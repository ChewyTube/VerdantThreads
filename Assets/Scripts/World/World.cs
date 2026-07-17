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
    private const int MAX_MESH_OPTIMIZE_COUNT_PER_FRAME = 8;  // 每帧 mesh 优化上限（可调）
    private const int MAX_BLOCKS_PER_FRAME = 64;
    private readonly ConcurrentQueue<VoxelChunkData> _pendingBuildQueue = new();
    private readonly ConcurrentQueue<List<(BlockPosInWorld, Block)>> _pendingSetBlocksQueue = new();
    private readonly ConcurrentQueue<VCPosInWorld> _pendingMeshOptimizeQueue = new();
    private const int MAX_FRAME_WORK_BUDGET_MS = 6; // 构建/优化每帧主线程耗时预算（毫秒，可调）
    private readonly System.Diagnostics.Stopwatch _frameWorkStopwatch = new();

    Dictionary<Vector3Int, VoxelChunk> world = new Dictionary<Vector3Int, VoxelChunk>();
    HashSet<Vector3Int> loadedVoxelChunks = new HashSet<Vector3Int>();

    private FastNoiseLite noise = new FastNoiseLite();
    private Saver saver = new Saver("world_saves");

    Camera cam;
    Vector3Int VCPosCam;
    Vector3Int lastVCPosCam;

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

        GenerateWorld();

        cam = Camera.main;

        cam.transform.position = new(0, 64, 0);

        Vector3 camPos = cam.transform.position;

        BlockPosInWorld camPosInt = new BlockPosInWorld((int)camPos.x, (int)camPos.y, (int)camPos.z);

        VCPosCam = camPosInt.GetCorrespondingVCPos();
        lastVCPosCam = VCPosCam;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        saver.Dispose(); // 释放 Saver 持有的 FileStream，防止泄漏
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
        while (builtCount < MAX_NEW_CHUNKS_PER_FRAME &&
               _frameWorkStopwatch.ElapsedMilliseconds < MAX_FRAME_WORK_BUDGET_MS &&
               _pendingBuildQueue.TryDequeue(out var chunkData))
        {
            var pos = chunkData.GetPos();

            // 已加载（含已标记的空区块）或已超出视距（在途生成）→ 跳过本数据
            if (loadedVoxelChunks.Contains(pos) || !IsWithinViewDistance(pos))
                continue;

            if (chunkData.IsEmpty())
            {
                // 自动卸载空区块：全空气 chunk 不创建对象，仅记录为已加载，节省对象/内存/draw call
                loadedVoxelChunks.Add(pos);
            }
            else
            {
                CreateVoxelChunk(pos, chunkData.GetBlocksData());
                builtCount++;
            }
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
        int optimizeCount = 0;
        while (optimizeCount < MAX_MESH_OPTIMIZE_COUNT_PER_FRAME &&
               _frameWorkStopwatch.ElapsedMilliseconds < MAX_FRAME_WORK_BUDGET_MS &&
               _pendingMeshOptimizeQueue.TryDequeue(out var vcPos))
        {
            world.TryGetValue(vcPos, out var vc);
            if(vc != null)
            {
                vc.MeshOptimize();
                optimizeCount++;
                // Debug.Log($"optimized vc({vcPos})");
            }
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

        // 2. 为视距内缺失的 chunk 启动后台生成（结果直接入队，由 Update 按帧消费）
        for (int X = x - l; X <= x + l; X++)
            for (int Y = Mathf.Max(y - verticalLineOfSight, 0); Y <= y + verticalLineOfSight; Y++)
                for (int Z = z - l; Z <= z + l; Z++)
                {
                    SpawnChunkDataGeneration(new VCPosInWorld(X, Y, Z));
                }
    }

    // 后台生成单个 chunk 数据并直接入队（跨线程安全）；异常仅记录，不中断其他生成
    private void SpawnChunkDataGeneration(VCPosInWorld pos)
    {
        if (loadedVoxelChunks.Contains(pos)) return;
        Task.Run(() =>
        {
            try
            {
                _pendingBuildQueue.Enqueue(GenerateVoxelChunkData(pos));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        });
    }

    // 请求重建 chunk mesh（走帧预算队列，避免 setblock 重放在单帧内同步重建过多 chunk）
    public void RequestMeshRebuild(VCPosInWorld vcPos)
    {
        _pendingMeshOptimizeQueue.Enqueue(vcPos);
    }

    // 判断 chunk 是否在相机的视距内（用于构建守卫与树冠写入重试）
    private bool IsWithinViewDistance(VCPosInWorld vcPos)
    {
        return Math.Abs(vcPos.X - VCPosCam.x) <= lineOfSight &&
               Math.Abs(vcPos.Y - VCPosCam.y) <= verticalLineOfSight &&
               Math.Abs(vcPos.Z - VCPosCam.z) <= lineOfSight;
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

        vc.DestroySelf();
        world.Remove(pos);

        // 邻居重新入队 MeshOptimize：卸载后重新评估边界剔除，修复世界边缘透空
        int[] offsets = { -1, 1 };
        foreach (int dx in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x + dx, pos.y, pos.z);
            if (world.ContainsKey(n)) _pendingMeshOptimizeQueue.Enqueue(n);
        }
        foreach (int dy in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x, pos.y + dy, pos.z);
            if (world.ContainsKey(n)) _pendingMeshOptimizeQueue.Enqueue(n);
        }
        foreach (int dz in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.x, pos.y, pos.z + dz);
            if (world.ContainsKey(n)) _pendingMeshOptimizeQueue.Enqueue(n);
        }
    }
    private VoxelChunkData GenerateVoxelChunkData(VCPosInWorld pos)
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        int baseHeight = 0;

        VoxelChunkData data = new VoxelChunkData(new Block[CHUNK_SIZE, CHUNK_SIZE, CHUNK_SIZE], pos, new List<(BlockPosInWorld, Block)>());

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
                    int treeHeight = x * z % 6 + 1;
                    int realY = (baseHeight + 4) % 16;


                    for (int i = realY; i < realY + treeHeight + 1; i++)
                    {
                        data.Setblock(BlockRegistry.Log, x, i, z);

                        if(i <= realY + realY % 2)
                        {
                            continue;
                        }

                        for(int j = -2; j<=2; j++) 
                            for(int k = -2; k<=2; k++)
                            {
                                if(!((j == 0 && k == 0) || (Math.Abs(j*k) == 4)))
                                {
                                    data.Setblock(BlockRegistry.Leaves, x, i, z, j, 0, k);
                                }
                            }
                    }

                    data.Setblock(BlockRegistry.Leaves, x, realY + treeHeight + 1, z);
                    data.Setblock(BlockRegistry.Leaves, x, realY + treeHeight + 1, z, -1, 0,  0);
                    data.Setblock(BlockRegistry.Leaves, x, realY + treeHeight + 1, z,  1, 0,  0);
                    data.Setblock(BlockRegistry.Leaves, x, realY + treeHeight + 1, z,  0, 0, -1);
                    data.Setblock(BlockRegistry.Leaves, x, realY + treeHeight + 1, z,  0, 0,  1);
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
        for (int x = -lineOfSight; x < lineOfSight; x++)
            for(int z = -lineOfSight; z < lineOfSight; z++)
                for (int y = 0; y < verticalLineOfSight; y++)
                {
                    SpawnChunkDataGeneration(new(x, y, z));
                }
    }

    private void GenerateVoxelChunk(VCPosInWorld pos)
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        int baseHeight = 0;

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int y = 0; y < CHUNK_SIZE; y++)
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    int blockX = pos.X * CHUNK_SIZE + x;
                    int blockY = pos.Y * CHUNK_SIZE + y;
                    int blockZ = pos.Z * CHUNK_SIZE + z;

                    baseHeight = (int)((noise.GetNoise(blockX, blockZ) + 1) * 0.5f * 20);

                    if (blockY == 0)
                    {
                        Setblock(BlockRegistry.Bedrock, blockX, 0, blockZ);
                    }
                    else if (blockY <= baseHeight && blockY > 0)
                    {
                        Setblock(BlockRegistry.Stone, blockX, blockY, blockZ);
                    }else if (blockY <= baseHeight + 2)
                    {
                        Setblock(BlockRegistry.Dirt, blockX, blockY, blockZ);
                    }else if (blockY <= baseHeight + 3)
                    {
                        Setblock(BlockRegistry.Grass, blockX, blockY, blockZ);
                    }
                    else
                    {
                        continue;
                    }

                }
    }
    private void Setblock(Block block, int x, int y, int z)
    {
        BlockPosInWorld posInWorld = new BlockPosInWorld(x, y, z);

        VCPosInWorld vcPos = posInWorld.GetCorrespondingVCPos();
        BlockPosInVoxelChunk bPos = posInWorld.GetCorrespondingPosInVC();

        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            // 目标 chunk 未加载：丢弃本次写入（跨 chunk 装饰性写入，避免主线程同步生成级联创建 chunk）
            return;
        }

        targetChunk.SetBlock(block, bPos.X, bPos.Y, bPos.Z);
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

    public void TryGetBlock(BlockPosInWorld pos, out BlockType bt)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        BlockPosInVoxelChunk blockLocalPos = pos.GetCorrespondingPosInVC();

        world.TryGetValue(vcPos, out VoxelChunk vc);

        if (vc != null)
        {
            bt = vc.GetBlock(blockLocalPos);
        }
        else
        {
            bt = BlockType.ERROR;
        }
    }

    private void CreateEmptyVoxelChunk(VCPosInWorld pos)
    {
        GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
        chunkGO.transform.SetParent(transform);
        VoxelChunk chunk = chunkGO.AddComponent<VoxelChunk>();
        // chunk.AddComponent<Rigidbody>().useGravity = false;

        chunk.Initialize(new VCPosInWorld(pos.X, pos.Y, pos.Z));

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

        GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
        chunkGO.transform.SetParent(transform);
        VoxelChunk chunk = chunkGO.AddComponent<VoxelChunk>();
        // chunk.AddComponent<Rigidbody>().useGravity = false;

        chunk.Initialize(new VCPosInWorld(pos.X, pos.Y, pos.Z), blockdata);

        world.Add(chunk.GetVCPosInWorld(), chunk);
        loadedVoxelChunks.Add(pos);
        _pendingMeshOptimizeQueue.Enqueue(pos);
    }
}
