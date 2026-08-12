using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// 流式调度器：相机视距盒管理、后台 chunk 数据生成/mesh 构建调度、帧预算队列消费、就近优先排序。
// 唯一持有各并发队列与就近列表；TerrainGenerator（生成）与 ChunkStore（存储）均不依赖本类。
// 非 MonoBehaviour：由 World 每帧调用 Tick 驱动，主线程访问。
public class ChunkStreamer
{
    private readonly TerrainGenerator terrainGen;
    private readonly ChunkStore store;

    private readonly int lineOfSight;         // 水平视距
    private readonly int verticalLineOfSight; // 垂直视距，独立于水平视距，减少高空空气 chunk 加载

    private const int MAX_NEW_CHUNKS_PER_FRAME = 24;          // 每帧构建 chunk 上限（可调，保证生成追上相机移动）
    private const int MAX_MESH_UPLOAD_PER_FRAME = 8;    // 每帧 mesh 上传上限（可调）
    private const int MAX_MESH_BUILD_SPAWN_PER_FRAME = 24; // 每帧启动后台 mesh 构建的上限（可调）
    private const int MAX_BLOCKS_PER_FRAME = 64;
    private const int MAX_TILES_PER_FRAME = 64;      // 每帧 tile 路由上限（地物豌豆丛跨 chunk tile，单丛 14-18 株）
    private const int MAX_FRAME_WORK_BUDGET_MS = 6; // 构建/优化每帧主线程耗时预算（毫秒，可调）
    private readonly System.Diagnostics.Stopwatch _frameWorkStopwatch = new();

    private readonly ConcurrentQueue<VoxelChunkData> _pendingBuildQueue = new();
    private readonly ConcurrentQueue<List<(BlockPosInWorld, Block)>> _pendingSetBlocksQueue = new();
    private readonly ConcurrentQueue<List<(BlockPosInWorld, Genome)>> _pendingTileWritesQueue = new(); // 地物 tile 跨 chunk 路由（与 pendingBlocks 语义一致，主线程重放）
    private readonly ConcurrentQueue<(VCPosInWorld, MeshData)> _pendingMeshUploadQueue = new();        // 后台已生成的 MeshData，待主线程就近上传

    // ④ 近处优先 + 只补新暴露环
    private readonly ConcurrentDictionary<Vector3Int, byte> _generationInFlight = new(); // 已 spawn 尚未消费的 chunk（避免重复 Task.Run；跨线程安全，失败路径也要清理）
    private readonly List<VoxelChunkData> _pendingBuildData = new();         // 已完成生成的数据（含上帧遗留），按到相机距离就近创建
    private readonly List<(VCPosInWorld, MeshData)> _pendingMeshUploadData = new(); // 已完成 mesh（含上帧遗留），按到相机距离就近上传
    private readonly List<VCPosInWorld> _pendingMeshBuild = new();           // 待后台构建 mesh 的位置（仅主线程入队，按距离就近构建）
    private readonly HashSet<VCPosInWorld> _pendingMeshBuildSet = new();     // 去重镜像：同一 chunk 未消费前只允许入队一次
    private Vector3Int _lastSortCam = new Vector3Int(int.MinValue, int.MinValue, int.MinValue); // 上次排序时的相机位置（相机不动则不重排）
    private bool _buildDataDirty, _meshBuildDirty, _uploadDirty;             // 有新增待排数据才重排

    private Vector3Int VCPosCam;         // 相机所在 chunk 坐标
    private Vector3Int lastVCPosCam;     // 上一轮相机所在 chunk 坐标
    private bool hasPrevViewBox = false; // 是否已有上一轮视距盒（首帧为 false，旧盒视为空，避免坐标哨兵溢出）

    public ChunkStreamer(TerrainGenerator terrainGen, ChunkStore store, int lineOfSight, int verticalLineOfSight)
    {
        this.terrainGen = terrainGen;
        this.store = store;
        this.lineOfSight = lineOfSight;
        this.verticalLineOfSight = verticalLineOfSight;
        store.OnChunkUnloaded += HandleChunkUnloaded;
    }

    // 初始化相机位置（Start 调用）：与首帧相同则不触发环逻辑，由 GenerateInitial 负责初始生成
    public void InitializeCamera(Vector3Int camVCPos)
    {
        VCPosCam = camVCPos;
        lastVCPosCam = camVCPos;
        // hasPrevViewBox 保持 false：首次相机变化时"旧盒视为空"，新暴露 = 完整视距盒，
        // 由环逻辑补齐 GenerateInitial 只生成的地形核心层之外的其余层（不遗漏、不重复）
    }

    // 初始生成（Start 调用）：只生成地形核心层（y∈[0,verticalLineOfSight) 覆盖全部地形高度），量小；
    // 完整视距盒（含上空各层）由首次相机变化的"新暴露=全盒"环逻辑补齐，
    // _generationInFlight 保证与这里已 spawn 的位置不重复
    public void GenerateInitial(Vector3Int camPos)
    {
        for (int x = camPos.x - lineOfSight; x <= camPos.x + lineOfSight; x++)
            for (int z = camPos.z - lineOfSight; z <= camPos.z + lineOfSight; z++)
                for (int y = 0; y < verticalLineOfSight; y++)
                {
                    SpawnChunkDataGeneration(new(x, y, z));
                }
    }

    // 每帧调度入口（主线程）：相机 chunk 变化 → 视距盒环逻辑；随后按帧预算消费各队列
    public void Tick(Vector3Int camVCPos)
    {
        VCPosCam = camVCPos;

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
                if (store.IsLoaded(pos) || !IsWithinViewDistance(pos))
                {
                    store.ReturnBlockArray(d.GetBlocksData());
                    continue;
                }

                if (d.IsEmpty())
                {
                    // 自动卸载空区块：全空气 chunk 不创建对象，仅记录为已加载，节省对象/内存/draw call
                    store.ReturnBlockArray(d.GetBlocksData());
                    store.MarkEmptyChunkLoaded(pos);
                }
                else
                {
                    if (store.CreateChunk(pos, d.GetBlocksData()))
                    {
                        builtCount++;
                        EnqueueMeshBuild(pos);

                        // 存档 v3：把读回的 tile 快照回挂到新创建的 chunk（纯值数组，主线程消费）。
                        // 地物产出的 tile（豌豆丛）走独立 _pendingTileWritesQueue 世界坐标通道，不在此回挂
                        var vc = store.GetChunk(pos);
                        var loadedTiles = d.GetLoadedTiles();
                        if (vc != null && loadedTiles.Length > 0)
                        {
                            foreach (var r in loadedTiles)
                                vc.SetTile(r.Key, new PeaTileData(new Genome(r.GenomeValue), r.Generation));
                        }

                        // 旧档修复：两格高豌豆缺顶/孤儿顶/跨 chunk 顶补齐（新生成世界阶段全为 0，天然无操作）
                        store.RepairPeaPlants(pos);
                    }
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

                // 生成期重放（树冠/地物块）：suppress 方块更新通知（世界刚生成无需联动）
                if (store.SetBlock(block, pos, suppressUpdate: true))
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
        // 地物 tile 路由：主线程按世界坐标 SetTile；目标 chunk 已加载即成功，未加载且在视距内则下帧重试（与 pendingBlocks 语义一致）
        int tileSetCount = 0;
        var retryTiles = new List<(BlockPosInWorld, Genome)>();
        while (tileSetCount < MAX_TILES_PER_FRAME && _pendingTileWritesQueue.TryDequeue(out var tileList))
        {
            int consumed = 0;
            foreach (var (pos, genome) in tileList)
            {
                if (tileSetCount >= MAX_TILES_PER_FRAME) break; // 预算耗尽，停止本列表

                if (store.SetTile(pos, new PeaTileData(genome, 0))) // 目标 chunk 已加载 → 成功
                {
                    tileSetCount++;
                }
                else if (IsWithinViewDistance(pos.GetCorrespondingVCPos()))
                {
                    retryTiles.Add((pos, genome)); // 视距内未加载：下帧重试（等邻居 chunk 加载后补齐）
                }
                consumed++;
            }

            // 列表被部分消费时，剩余部分重新入队，留到下一帧处理，避免丢数据
            if (consumed < tileList.Count)
            {
                _pendingTileWritesQueue.Enqueue(tileList.GetRange(consumed, tileList.Count - consumed));
            }
        }
        if (retryTiles.Count > 0)
        {
            _pendingTileWritesQueue.Enqueue(retryTiles); // 视距内的失败写入重新入队，下一帧重试
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
                if (store.ContainsChunk(buildPos))
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
                VoxelChunk vc = store.GetChunk(uploadItem.Item1);
                if (vc != null)
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

    // 退出前排空跨 chunk 挂起写入（World.SaveAllLoadedChunks 调用：同步重放，不计数）
    public void DrainPendingSetBlocks()
    {
        while (_pendingSetBlocksQueue.TryDequeue(out var blockList))
        {
            // 生成期重放：suppress 方块更新通知
            foreach (var (pos, block) in blockList)
                store.SetBlock(block, pos, suppressUpdate: true);
        }
        // 地物 tile 同样排空（避免退出时丢跨 chunk 豌豆 tile；块与 tile 保持一致）
        while (_pendingTileWritesQueue.TryDequeue(out var tileList))
        {
            foreach (var (pos, genome) in tileList)
                store.SetTile(pos, new PeaTileData(genome, 0));
        }
    }

    // 请求重建 chunk mesh（公开，供玩家交互等外部模块调用；走帧预算队列，避免同步重建过多 chunk）
    public void RequestMeshRebuild(VCPosInWorld vcPos)
    {
        EnqueueMeshBuild(vcPos);
    }

    // 相机所在 chunk 变化：同步卸载超视距 chunk，并后台生成缺失 chunk 数据（不再丢弃事件）
    private void OnCameraChunkChanged(Vector3Int camPos)
    {
        int x = camPos.x;
        int y = camPos.y;
        int z = camPos.z;
        int l = lineOfSight;

        // 1. 标记并卸载超出视距的 chunk（主线程同步；先收集后卸载，避免遍历时修改集合）
        var toUnload = new HashSet<Vector3Int>();
        foreach (var chunkPos in store.LoadedPositions)
        {
            if (Math.Abs(chunkPos.x - x) > l ||
                Math.Abs(chunkPos.y - y) > verticalLineOfSight ||
                Math.Abs(chunkPos.z - z) > l)
            {
                toUnload.Add(chunkPos);
            }
        }
        foreach (var p in toUnload)
        {
            store.UnloadChunk(p); // 内部同步保存 + 归还池 + 移除记录，并触发邻居重建回调
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
        if (store.IsLoaded(pos) || !_generationInFlight.TryAdd(pos, 0)) return;

        // 主线程取池化块数组（池是普通 Stack，非线程安全，绝不能在 Task.Run 内碰）
        Block[,,] arr = store.TakeBlockArray();
        Task.Run(() =>
        {
            try
            {
                VoxelChunkData data = terrainGen.GenerateVoxelChunkData(pos, arr);
                _pendingBuildQueue.Enqueue(data);
                // 跨 chunk 挂起写入（树冠等）随数据一起入队，由主线程按帧预算重放
                if (data.GetPendingBlocks().Count > 0)
                    _pendingSetBlocksQueue.Enqueue(data.GetPendingBlocks());
                // 地物 tile（豌豆丛跨 chunk）入平行队列，主线程按帧预算路由到目标 chunk
                var tileWrites = data.GetPendingTileWrites();
                if (tileWrites.Length > 0)
                    _pendingTileWritesQueue.Enqueue(new List<(BlockPosInWorld, Genome)>(tileWrites));
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
        VoxelChunk vc = store.GetChunk(vcPos);
        if (vc == null) return;

        // 快照含豌豆 tile 基因（阶段 3 花贴图按基因选）；Y-1 邻居 tile 供顶部格跨 chunk 查基因
        MeshBuildData snapshot = ChunkMeshBuilder.CreateSnapshot(
            vcPos, vc.GetBlocksData(), store.GetChunkBlocks,
            vc.TilesRaw, pos => store.GetChunk(pos)?.TilesRaw);
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

    // 卸载回调：邻居重新入队 mesh 构建，卸载后重新评估边界剔除，修复世界边缘透空
    private void HandleChunkUnloaded(VCPosInWorld pos)
    {
        int[] offsets = { -1, 1 };
        foreach (int dx in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.X + dx, pos.Y, pos.Z);
            if (store.ContainsChunk(n)) EnqueueMeshBuild(n);
        }
        foreach (int dy in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.X, pos.Y + dy, pos.Z);
            if (store.ContainsChunk(n)) EnqueueMeshBuild(n);
        }
        foreach (int dz in offsets)
        {
            VCPosInWorld n = new VCPosInWorld(pos.X, pos.Y, pos.Z + dz);
            if (store.ContainsChunk(n)) EnqueueMeshBuild(n);
        }
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
}
