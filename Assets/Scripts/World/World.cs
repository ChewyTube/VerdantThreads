using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class World : MonoBehaviour
{
    public static World Instance { get; private set; }

    int lineOfSight = 12; // 水平视距（可调）
    int verticalLineOfSight = 6; // 垂直视距，独立于水平视距，减少高空空气 chunk 加载

    int seed = 985211;

    private const int MAX_NEW_CHUNKS_PER_FRAME = 24;          // 每帧构建 chunk 上限（可调，保证生成追上相机移动）
    private const int MAX_MESH_UPLOAD_PER_FRAME = 8;    // 每帧 mesh 上传上限（可调）
    private const int MAX_MESH_BUILD_SPAWN_PER_FRAME = 24; // 每帧启动后台 mesh 构建的上限（可调）
    private const int MAX_BLOCKS_PER_FRAME = 64;
    private readonly ConcurrentQueue<VoxelChunkData> _pendingBuildQueue = new();
    private readonly ConcurrentQueue<List<(BlockPosInWorld, Block)>> _pendingSetBlocksQueue = new();
    private readonly ConcurrentQueue<(VCPosInWorld, MeshData)> _pendingMeshUploadQueue = new();        // 后台已生成的 MeshData，待主线程就近上传

    // ④ 近处优先 + 只补新暴露环
    private readonly ConcurrentDictionary<Vector3Int, byte> _generationInFlight = new(); // 已 spawn 尚未消费的 chunk（避免重复 Task.Run；跨线程安全，失败路径也要清理）
    private readonly List<VoxelChunkData> _pendingBuildData = new();         // 已完成生成的数据（含上帧遗留），按到相机距离就近创建
    private readonly List<(VCPosInWorld, MeshData)> _pendingMeshUploadData = new(); // 已完成 mesh（含上帧遗留），按到相机距离就近上传
    private readonly List<VCPosInWorld> _pendingMeshBuild = new();           // 待后台构建 mesh 的位置（仅主线程入队，按距离就近构建）
    private readonly HashSet<VCPosInWorld> _pendingMeshBuildSet = new();     // 去重镜像：同一 chunk 未消费前只允许入队一次
    private Vector3Int _lastSortCam = new Vector3Int(int.MinValue, int.MinValue, int.MinValue); // 上次排序时的相机位置（相机不动则不重排）
    private bool _buildDataDirty, _meshBuildDirty, _uploadDirty;             // 有新增待排数据才重排
    private const int MAX_FRAME_WORK_BUDGET_MS = 6; // 构建/优化每帧主线程耗时预算（毫秒，可调）
    private readonly System.Diagnostics.Stopwatch _frameWorkStopwatch = new();

    Dictionary<Vector3Int, VoxelChunk> world = new Dictionary<Vector3Int, VoxelChunk>();
    HashSet<Vector3Int> loadedVoxelChunks = new HashSet<Vector3Int>();

    // ③ 对象池（主线程独享，不用并发容器）
    private readonly Stack<VoxelChunk> _chunkPool = new();
    private readonly Stack<Block[,,]> _blockArrayPool = new();
    private const int MAX_POOLED_CHUNKS = 8192;          // 视距盒上限 25×13×25=8125
    private const int MAX_POOLED_BLOCK_ARRAYS = 8192;

    private FastNoiseLite noise = new FastNoiseLite();
    private Saver saver = new Saver("world_saves");

    [SerializeField] private Vector3 cameraSpawnPos = new(0, 64, 0); // 相机出生点（可在 Inspector 覆盖，不再硬编码覆盖场景摆放）

    Camera cam;
    Vector3Int VCPosCam;
    Vector3Int lastVCPosCam;
    bool hasPrevViewBox = false; // 是否已有上一轮视距盒（首帧为 false，旧盒视为空，避免坐标哨兵溢出）

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[World] Duplicate instance detected, destroying new one.");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        saver.Initialize(); // 主线程解析保存根目录（Application.persistentDataPath 不能从后台线程读取）

        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        InitializeNoise();

        cam = Camera.main;
        cam.transform.position = cameraSpawnPos; // 出生点可配，不再强制 (0,64,0)

        VCPosCam = new BlockPosInWorld((int)cameraSpawnPos.x, (int)cameraSpawnPos.y, (int)cameraSpawnPos.z).GetCorrespondingVCPos();
        lastVCPosCam = VCPosCam;
        // hasPrevViewBox 保持 false：首次相机变化时"旧盒视为空"，新暴露 = 完整视距盒，
        // 由环逻辑补齐 GenerateWorld 只生成的地形核心层之外的其余层（不遗漏、不重复）
        GenerateWorld();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        saver.Dispose(); // 释放 Saver 持有的 FileStream，防止泄漏（内部会排空保存队列后退出）
    }

    void OnApplicationQuit()
    {
        SaveAllLoadedChunks();
    }

    // 退出兜底：卸载路径只保存被卸载的 chunk，仍在内存的 chunk 若不主动入队会丢修改
    private void SaveAllLoadedChunks()
    {
        saver.SetQueueLimit(int.MaxValue); // ⑤ 退出前放开背压：全量入队由 Dispose 排空落盘，不触发同步兜底

        // 先尽力应用一次跨 chunk 挂起写入（树冠等），避免退出时丢 pendingBlocks
        while (_pendingSetBlocksQueue.TryDequeue(out var blockList))
        {
            foreach (var (pos, block) in blockList)
                Setblock(block, pos);
        }

        // 全量入队（空 chunk 无对象不在 world 字典里，无需保存）；
        // OnApplicationQuit 先于 OnDestroy 触发，入队任务由随后 saver.Dispose() 排空落盘
        foreach (var (pos, vc) in world)
            saver.SaveVoxelChunk(new(pos.x, pos.y, pos.z), vc.GetBlocksData());
    }

    private void InitializeNoise()
    {
        noise.SetSeed(seed);                          // 固定种子保证结果可复现
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(0.002f);                   // 基础频率，控制地形尺度
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(6);                   // 叠加层数，越多细节越丰富
        noise.SetFractalLacunarity(2.0f);             // 每层频率倍增系数
        noise.SetFractalGain(0.5f);                   // 每层振幅衰减系数
    }

    void Update()
    {
        Vector3 camPos = cam.transform.position;

        BlockPosInWorld camPosInt = new BlockPosInWorld((int)camPos.x, (int)camPos.y, (int)camPos.z);

        // 摄像机所在VC的Position
        VCPosCam = camPosInt.GetCorrespondingVCPos(); 

        if (lastVCPosCam != VCPosCam)
        {
            OnCameraChunkChanged(VCPosCam);
            lastVCPosCam = VCPosCam;
        }

        // 帧耗时预算：限制构建/优化在主线程的耗时，避免单帧卡顿
        _frameWorkStopwatch.Restart();

        int builtCount = 0;
        while (_pendingBuildQueue.TryDequeue(out var chunkData))
        {
            _pendingBuildData.Add(chunkData); // 全部取出，统一按距离就近消费
            _buildDataDirty = true;
        }
        if (_pendingBuildData.Count > 0)
        {
            // ④ 近处优先：按到相机的切比雪夫距离排序，先填满视野内（有新数据或相机移动才重排）
            if (_buildDataDirty || _lastSortCam != VCPosCam)
            {
                _pendingBuildData.Sort((a, b) =>
                    DistRingToCam(a.GetPos(), VCPosCam).CompareTo(DistRingToCam(b.GetPos(), VCPosCam)));
                _buildDataDirty = false;
                _lastSortCam = VCPosCam;
            }

            int buildDataIndex = 0;
            while (builtCount < MAX_NEW_CHUNKS_PER_FRAME &&
                   _frameWorkStopwatch.ElapsedMilliseconds < MAX_FRAME_WORK_BUDGET_MS &&
                   buildDataIndex < _pendingBuildData.Count)
            {
                var d = _pendingBuildData[buildDataIndex++];
                var pos = d.GetPos();
                _generationInFlight.TryRemove(pos, out _); // 数据已到达，解除在途标记

                // 已加载（含已标记的空区块）或已超出视距（在途生成）→ 跳过本数据，块数组归还池
                if (loadedVoxelChunks.Contains(pos) || !IsWithinViewDistance(pos))
                {
                    var ba = d.GetBlocksData();
                    if (ba != null && _blockArrayPool.Count < MAX_POOLED_BLOCK_ARRAYS)
                        _blockArrayPool.Push(ba);
                    continue;
                }

                if (d.IsEmpty())
                {
                    // 自动卸载空区块：全空气 chunk 不创建对象，仅记录为已加载，节省对象/内存/draw call
                    var ba = d.GetBlocksData();
                    if (ba != null && _blockArrayPool.Count < MAX_POOLED_BLOCK_ARRAYS)
                        _blockArrayPool.Push(ba);
                    loadedVoxelChunks.Add(pos);
                }
                else
                {
                    CreateVoxelChunk(pos, d.GetBlocksData());
                    builtCount++;
                }
            }
            // 未消费的数据保留（已排序），下帧继续就近消费
            if (buildDataIndex > 0)
                _pendingBuildData.RemoveRange(0, buildDataIndex);
        }
        int setCount = 0;
        // 按块数记账：一帧最多处理 MAX_BLOCKS_PER_FRAME 次 Setblock，防止单个列表超预算重放
        var retryBlocks = new List<(BlockPosInWorld, Block)>();
        while (setCount < MAX_BLOCKS_PER_FRAME && _pendingSetBlocksQueue.TryDequeue(out var blockList))
        {
            int consumed = 0;
            foreach (var (pos, block) in blockList)
            {
                if (setCount >= MAX_BLOCKS_PER_FRAME) break; // 预算耗尽，停止本列表

                if (Setblock(block, pos))
                {
                    setCount++;
                }
                else if (IsWithinViewDistance(pos.GetCorrespondingVCPos()))
                {
                    // 目标 chunk 在视距内但尚未加载：稍后重试（树冠跨界写入，等邻居加载后补齐）
                    retryBlocks.Add((pos, block));
                }
                consumed++;
            }

            // 列表被部分消费时，剩余部分重新入队，留到下一帧处理，避免丢数据
            if (consumed < blockList.Count)
            {
                _pendingSetBlocksQueue.Enqueue(blockList.GetRange(consumed, blockList.Count - consumed));
            }
        }
        if (retryBlocks.Count > 0)
        {
            _pendingSetBlocksQueue.Enqueue(retryBlocks); // 视距内的失败写入重新入队，下一帧重试
        }
        int spawnCount = 0;
        if (_pendingMeshBuild.Count > 0)
        {
            // ④ 近处优先：按距离排序后，先启动离相机近的 chunk 的 mesh 构建（有新数据或相机移动才重排）
            if (_meshBuildDirty || _lastSortCam != VCPosCam)
            {
                _pendingMeshBuild.Sort((a, b) =>
                    DistRingToCam(a, VCPosCam).CompareTo(DistRingToCam(b, VCPosCam)));
                _meshBuildDirty = false;
                _lastSortCam = VCPosCam;
            }

            int meshBuildIndex = 0;
            while (spawnCount < MAX_MESH_BUILD_SPAWN_PER_FRAME &&
                   _frameWorkStopwatch.ElapsedMilliseconds < MAX_FRAME_WORK_BUDGET_MS &&
                   meshBuildIndex < _pendingMeshBuild.Count)
            {
                var buildPos = _pendingMeshBuild[meshBuildIndex++];
                _pendingMeshBuildSet.Remove(buildPos); // 出队即解除去重标记（chunk 可能再次被改块入队）
                if (world.ContainsKey(buildPos))
                {
                    SpawnMeshBuild(buildPos);
                    spawnCount++;
                }
            }
            if (meshBuildIndex > 0)
                _pendingMeshBuild.RemoveRange(0, meshBuildIndex);
        }
        int uploadCount = 0;
        while (_pendingMeshUploadQueue.TryDequeue(out var upload))
        {
            _pendingMeshUploadData.Add(upload); // 全部取出，统一按距离就近上传
            _uploadDirty = true;
        }
        if (_pendingMeshUploadData.Count > 0)
        {
            // ④ 近处优先：先上传离相机近的 mesh（有新数据或相机移动才重排）
            if (_uploadDirty || _lastSortCam != VCPosCam)
            {
                _pendingMeshUploadData.Sort((a, b) =>
                    DistRingToCam(a.Item1, VCPosCam).CompareTo(DistRingToCam(b.Item1, VCPosCam)));
                _uploadDirty = false;
                _lastSortCam = VCPosCam;
            }

            int uploadIndex = 0;
            while (uploadCount < MAX_MESH_UPLOAD_PER_FRAME &&
                   _frameWorkStopwatch.ElapsedMilliseconds < MAX_FRAME_WORK_BUDGET_MS &&
                   uploadIndex < _pendingMeshUploadData.Count)
            {
                var uploadItem = _pendingMeshUploadData[uploadIndex++];
                if (world.TryGetValue(uploadItem.Item1, out var vc))
                {
                    vc.ApplyMeshData(uploadItem.Item2);
                    uploadCount++;
                }
            }
            // 未上传的数据保留（已排序），下帧继续就近上传
            if (uploadIndex > 0)
                _pendingMeshUploadData.RemoveRange(0, uploadIndex);
        }
    }

    // 相机所在 chunk 变化：同步卸载超视距 chunk，并后台生成缺失 chunk 数据（不再丢弃事件）
    private void OnCameraChunkChanged(Vector3Int camPos)
    {
        int x = camPos.x;
        int y = camPos.y;
        int z = camPos.z;
        int l = lineOfSight;

        // 1. 标记并卸载超出视距的 chunk（主线程同步）
        var toUnload = new HashSet<Vector3Int>();
        foreach (var chunkPos in loadedVoxelChunks.ToArray())
        {
            if (Math.Abs(chunkPos.x - x) > l ||
                Math.Abs(chunkPos.y - y) > verticalLineOfSight ||
                Math.Abs(chunkPos.z - z) > l)
            {
                toUnload.Add(chunkPos);
            }
        }
        loadedVoxelChunks.ExceptWith(toUnload); // O(n) 差集，避免 RemoveWhere 的 O(n×m) 主线程尖峰
        foreach (var p in toUnload)
        {
            UnloadVoxelChunk(p);
        }

        // 2. 只补新暴露的位置：仅生成新视距盒相对旧视距盒新增的环，并按到相机距离就近排序
        //    （lastVCPosCam 为上一轮中心；相机跳变时新旧盒相差多环，全部覆盖）
        var newExposed = new List<VCPosInWorld>();
        for (int X = x - l; X <= x + l; X++)
            for (int Y = Mathf.Max(y - verticalLineOfSight, 0); Y <= y + verticalLineOfSight; Y++)
                for (int Z = z - l; Z <= z + l; Z++)
                {
                    // 位于旧视距盒内 → 上一轮已处理过，跳过（首轮无旧盒，全部视为新暴露）
                    if (hasPrevViewBox &&
                        Math.Abs(X - lastVCPosCam.x) <= l &&
                        Math.Abs(Y - lastVCPosCam.y) <= verticalLineOfSight &&
                        Math.Abs(Z - lastVCPosCam.z) <= l) continue;
                    newExposed.Add(new VCPosInWorld(X, Y, Z));
                }
        // 近处优先：先 spawn 离相机近的新暴露位置，配合队列就近消费，视野内先填满
        newExposed.Sort((a, b) => DistRingToCam(a, camPos).CompareTo(DistRingToCam(b, camPos)));
        foreach (var p in newExposed)
        {
            SpawnChunkDataGeneration(p);
        }
        hasPrevViewBox = true;

        // 相机已移动 → 三个就近队列的相对距离全部失效，统一标记重排
        _buildDataDirty = _meshBuildDirty = _uploadDirty = true;
    }

    // 后台生成单个 chunk 数据并直接入队（跨线程安全）；异常仅记录，不中断其他生成
    private void SpawnChunkDataGeneration(VCPosInWorld pos)
    {
        // 已加载（含空区块记录）或已在途生成 → 跳过，避免对同一位置重复 Task.Run
        if (loadedVoxelChunks.Contains(pos) || !_generationInFlight.TryAdd(pos, 0)) return;

        // 主线程取池化块数组（池是普通 Stack，非线程安全，绝不能在 Task.Run 内碰）
        Block[,,] arr = TakeBlockArray();
        Task.Run(() =>
        {
            try
            {
                _pendingBuildQueue.Enqueue(GenerateVoxelChunkData(pos, arr));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _generationInFlight.TryRemove(pos, out _); // 生成失败也要解除在途标记，允许后续重试
            }
        });
    }

    // 启动单个 chunk 的后台 mesh 构建：主线程先拍快照，worker 生成 MeshData，完成后入上传队列
    private void SpawnMeshBuild(VCPosInWorld vcPos)
    {
        if (!world.TryGetValue(vcPos, out var vc)) return;

        MeshBuildData snapshot = ChunkMeshBuilder.CreateSnapshot(vcPos, vc.GetBlocksData(), GetChunkBlocks);
        snapshot.Seq = vc.TakeBuildSeq();
        snapshot.ChunkId = vc.InstanceId;

        Task.Run(() =>
        {
            try
            {
                _pendingMeshUploadQueue.Enqueue((vcPos, ChunkMeshBuilder.Build(snapshot)));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        });
    }

    // 供快照构建读取邻居块数据（仅主线程调用）；未加载返回 null
    public Block[,,] GetChunkBlocks(VCPosInWorld vcPos)
    {
        return world.TryGetValue(vcPos, out var vc) ? vc.GetBlocksData() : null;
    }

    // 请求重建 chunk mesh（走帧预算队列，避免 setblock 重放在单帧内同步重建过多 chunk）
    public void RequestMeshRebuild(VCPosInWorld vcPos)
    {
        EnqueueMeshBuild(vcPos);
    }

    // mesh 重建入队（去重：未消费前同一 chunk 只入队一次，避免重复快照 + 重复 Task.Run）
    private void EnqueueMeshBuild(VCPosInWorld vcPos)
    {
        if (_pendingMeshBuildSet.Add(vcPos))
        {
            _pendingMeshBuild.Add(vcPos);
            _meshBuildDirty = true;
        }
    }

    // 判断 chunk 是否在相机的视距内（用于构建守卫与树冠写入重试）
    private bool IsWithinViewDistance(VCPosInWorld vcPos)
    {
        return Math.Abs(vcPos.X - VCPosCam.x) <= lineOfSight &&
               Math.Abs(vcPos.Y - VCPosCam.y) <= verticalLineOfSight &&
               Math.Abs(vcPos.Z - VCPosCam.z) <= lineOfSight;
    }

    // 切比雪夫距离（视距盒为立方体，用 max 轴向差衡量"环"）用于就近优先排序
    private static int DistRingToCam(VCPosInWorld vcPos, Vector3Int camPos)
    {
        return Math.Max(
            Math.Max(Math.Abs(vcPos.X - camPos.x), Math.Abs(vcPos.Y - camPos.y)),
            Math.Abs(vcPos.Z - camPos.z));
    }

    private void UnloadVoxelChunk(Vector3Int pos)
    {
        //Debug.Log($"Unloading VoxelChunk at {pos}");
        if (!world.TryGetValue(pos, out var vc))
        {
            // 空区块（未创建对象）：无需保存/销毁，位置已在调用方从 loadedVoxelChunks 移除
            return;
        }

        //Debug.Log($"Saving VoxelChunk at {pos}");
        // _ = saver.EnqueueSaveAsync(new(pos.x, pos.y, pos.z), vc.GetBlocksData());
        saver.SaveVoxelChunk(new(pos.x, pos.y, pos.z), vc.GetBlocksData());
        // Debug.Log($"Saved VoxelChunk at {pos}");

        ReturnChunkToPool(vc);
        world.Remove(pos);

        // 邻居重新入队 mesh 构建：卸载后重新评估边界剔除，修复世界边缘透空
        int[] offsets = { -1, 1 };
        foreach (int dx in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x + dx, pos.y, pos.z);
            if (world.ContainsKey(n)) EnqueueMeshBuild(n);
        }
        foreach (int dy in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x, pos.y + dy, pos.z);
            if (world.ContainsKey(n)) EnqueueMeshBuild(n);
        }
        foreach (int dz in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x, pos.y, pos.z + dz);
            if (world.ContainsKey(n)) EnqueueMeshBuild(n);
        }
    }
    private VoxelChunkData GenerateVoxelChunkData(VCPosInWorld pos, Block[,,] blocks)
    {
        // 读路径：#14 先查存档，命中则直接用已保存数据（含玩家修改），跳过重新生成
        Block[,,] loaded = saver.TryLoadVoxelChunk(pos);
        if (loaded != null)
        {
            return new VoxelChunkData(loaded, pos, new List<(BlockPosInWorld, Block)>(), fillAir: false);
        }

        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        int baseHeight = 0;

        VoxelChunkData data = new VoxelChunkData(blocks, pos, new List<(BlockPosInWorld, Block)>());

        int maxY = (pos.Y + 1) * CHUNK_SIZE;

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                int blockX = pos.X * CHUNK_SIZE + x;
                int blockZ = pos.Z * CHUNK_SIZE + z;

                baseHeight = (int)((noise.GetNoise(blockX, blockZ) + 1) * 0.5f * 64);

                for (int y = 0; y < CHUNK_SIZE; y++)
                {
                    int blockY = pos.Y * CHUNK_SIZE + y;

                    if (blockY == 0)
                    {
                        data.Setblock(BlockRegistry.Bedrock, x, y, z);
                    }
                    else if (blockY > 0 && blockY <= baseHeight)
                    {
                        data.Setblock(BlockRegistry.Stone, x, y, z);
                    }
                    else if (blockY <= baseHeight + 2)
                    {
                        data.Setblock(BlockRegistry.Dirt, x, y, z);
                    }
                    else if (blockY == baseHeight + 3)
                    {
                        data.Setblock(BlockRegistry.Grass, x, y, z);
                    }
                }

                if (HasTree(blockX, baseHeight + 4, blockZ) && (baseHeight + 4 < maxY) && (baseHeight + 4) >= (maxY - CHUNK_SIZE))
                {
                    // 香樟风球冠树：树干下部裸露、上部穿入球状树冠（确定性伪随机，保持固定 seed 的确定性）
                    int realY = (baseHeight + 4) % 16;

                    int trunkHeight = (x * 31 + z * 17) % 3 + 4;  // 树干 4-6 格
                    int crownRadius = (x * 7 + z * 11) % 2 + 3;   // 树冠水平半径 3-4 格
                    int trunkTop = realY + trunkHeight;           // 树干顶
                    int crownCenterY = trunkTop + crownRadius - 2; // 球心：树干顶深入球内 2 格
                    int crownBottom = crownCenterY - crownRadius;  // 树冠底（树干下部裸露 2 格后展开）
                    int crownTop = crownCenterY + crownRadius;

                    // 树干（下部裸露，上部被树冠包裹）
                    for (int i = realY; i < trunkTop; i++)
                    {
                        data.Setblock(BlockRegistry.Log, x, i, z);
                    }

                    // 球状树冠：逐层按球方程取水平半径（向上取整保证饱满），树冠内的树干格保留不盖
                    for (int layerY = crownBottom; layerY <= crownTop; layerY++)
                    {
                        float dy = layerY - crownCenterY;
                        int layerRadius = (int)Math.Ceiling(Math.Sqrt(crownRadius * crownRadius - dy * dy));

                        for (int j = -layerRadius; j <= layerRadius; j++)
                        {
                            for (int k = -layerRadius; k <= layerRadius; k++)
                            {
                                int d2 = j * j + k * k;
                                // 球内填充；树冠内的树干格跳过；边缘格按确定性扰动少量缺角增加自然感
                                if (d2 <= layerRadius * layerRadius &&
                                    !(j == 0 && k == 0 && layerY < trunkTop) &&
                                    !(d2 == layerRadius * layerRadius && (x * 13 + z * 7 + layerY * 3) % 4 == 0))
                                {
                                    data.Setblock(BlockRegistry.Leaves, x, layerY, z, j, 0, k);
                                }
                            }
                        }
                    }
                }
            }
        
        var blocksToBeSet = data.GetPendingBlocks();
        _pendingSetBlocksQueue.Enqueue(blocksToBeSet);

        return data;
    }

    private bool HasTree(int x, int y, int z)
    {
        return (x * x * 13 + y * 17 + z * z * 19)%128 == 37;
    }

    private void GenerateWorld()
    {
        // 只生成地形核心层（y∈[0,verticalLineOfSight) 覆盖全部地形高度），量小；
        // 完整视距盒（含上空各层）由首次相机变化的"新暴露=全盒"环逻辑补齐，
        // _generationInFlight 保证与这里已 spawn 的位置不重复
        for (int x = VCPosCam.x - lineOfSight; x <= VCPosCam.x + lineOfSight; x++)
            for (int z = VCPosCam.z - lineOfSight; z <= VCPosCam.z + lineOfSight; z++)
                for (int y = 0; y < verticalLineOfSight; y++)
                {
                    SpawnChunkDataGeneration(new(x, y, z));
                }
    }

    private bool Setblock(Block block, BlockPosInWorld pos)
    {
        int x = pos.X;
        int y = pos.Y;
        int z = pos.Z;

        BlockPosInWorld posInWorld = new BlockPosInWorld(x, y, z);

        VCPosInWorld vcPos = posInWorld.GetCorrespondingVCPos();
        BlockPosInVoxelChunk bPos = posInWorld.GetCorrespondingPosInVC();

        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            // 目标 chunk 未创建：若为已标记的空区块，则按需创建（接收跨界树冠写入，保证树冠完整）
            if (loadedVoxelChunks.Contains(vcPos))
            {
                CreateEmptyVoxelChunk(vcPos);
                world.TryGetValue(vcPos, out targetChunk);
            }

            if (targetChunk == null)
            {
                // 目标不在视距盒内：返回 false，由调用方决定重试或丢弃
                return false;
            }
        }

        targetChunk.SetBlock(block, bPos.X, bPos.Y, bPos.Z);
        return true;
    }

    // 玩家交互等外部模块的公开写入口：复用跨 chunk 写入逻辑
    public bool SetBlock(Block block, BlockPosInWorld pos) => Setblock(block, pos);

    // 从 chunk 池取复用对象；池空返回 null（防御 Unity 侧销毁的 null 项）
    private VoxelChunk GetChunkFromPool()
    {
        while (_chunkPool.Count > 0)
        {
            var vc = _chunkPool.Pop();
            if (vc != null) return vc;
        }
        return null;
    }

    // 取块数组：池空则新分配
    private Block[,,] TakeBlockArray()
    {
        return _blockArrayPool.Count > 0
            ? _blockArrayPool.Pop()
            : new Block[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
    }

    // 归还 chunk 到池：先归还其块数组，再清理并隐藏；池满则溢出回退（DestroySelf）
    private void ReturnChunkToPool(VoxelChunk vc)
    {
        if (_chunkPool.Count >= MAX_POOLED_CHUNKS)
        {
            vc.DestroySelf();
            return;
        }

        var arr = vc.GetBlocksData();
        if (arr != null && _blockArrayPool.Count < MAX_POOLED_BLOCK_ARRAYS)
            _blockArrayPool.Push(arr);

        vc.PrepareForPool();
        _chunkPool.Push(vc);
    }

    private void CreateEmptyVoxelChunk(VCPosInWorld pos)
    {
        var arr = TakeBlockArray();
        VoxelChunk.FillAir(arr);

        VoxelChunk chunk = GetChunkFromPool();
        if (chunk == null)
        {
            GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
            chunkGO.transform.SetParent(transform);
            chunk = chunkGO.AddComponent<VoxelChunk>();
            // chunk.AddComponent<Rigidbody>().useGravity = false;
        }
        else
        {
            chunk.gameObject.SetActive(true);
            chunk.gameObject.name = $"Chunk_{pos.X}_{pos.Y}_{pos.Z}";
        }

        chunk.ResetForReuse(pos, arr);

        world.Add(chunk.GetVCPosInWorld(), chunk);
        loadedVoxelChunks.Add(pos);
    }
    private void CreateVoxelChunk(VCPosInWorld pos, Block[,,] blockdata)
    {
        if (world.ContainsKey(pos))
        {
            Debug.LogWarning($"Repeatedly adding chunk{pos}");
            return;
        }

        VoxelChunk chunk = GetChunkFromPool();
        if (chunk == null)
        {
            GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
            chunkGO.transform.SetParent(transform);
            chunk = chunkGO.AddComponent<VoxelChunk>();
            // chunk.AddComponent<Rigidbody>().useGravity = false;
        }
        else
        {
            chunk.gameObject.SetActive(true);
            chunk.gameObject.name = $"Chunk_{pos.X}_{pos.Y}_{pos.Z}";
        }

        chunk.ResetForReuse(pos, blockdata);

        world.Add(chunk.GetVCPosInWorld(), chunk);
        loadedVoxelChunks.Add(pos);
        EnqueueMeshBuild(pos);
    }
}
