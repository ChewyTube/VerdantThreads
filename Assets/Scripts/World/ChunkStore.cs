using System;
using System.Collections.Generic;
using UnityEngine;

// chunk 存储层：world 字典、已加载记录、对象池、chunk 创建/卸载、跨 chunk 方块写入。
// 仅主线程访问；不依赖任何调度队列。卸载时负责同步保存并触发 OnChunkUnloaded 回调
// （由调度方订阅做邻居 mesh 重建，保持本类与 mesh 队列解耦）。
public class ChunkStore
{
    private readonly Dictionary<Vector3Int, VoxelChunk> world = new Dictionary<Vector3Int, VoxelChunk>();
    private readonly HashSet<Vector3Int> loadedVoxelChunks = new HashSet<Vector3Int>();

    // ③ 对象池（主线程独享，不用并发容器）
    private readonly Stack<VoxelChunk> _chunkPool = new Stack<VoxelChunk>();
    private readonly Stack<Block[,,]> _blockArrayPool = new Stack<Block[,,]>();
    private const int MAX_POOLED_CHUNKS = 8192;          // 视距盒上限 25×13×25=8125
    private const int MAX_POOLED_BLOCK_ARRAYS = 8192;

    private readonly Transform parentTransform; // chunk GameObject 的父级（World 的 transform）
    private readonly Saver saver;               // 卸载时同步保存（写路径）
    private readonly Action<VCPosInWorld> requestMeshRebuild; // 注入的 mesh 重建请求回调（由 World 组装，转发给 ChunkStreamer）

    // chunk 卸载回调：参数为已卸载位置（调度方用于邻居 mesh 重建）
    public event Action<VCPosInWorld> OnChunkUnloaded;

    public ChunkStore(Transform parentTransform, Saver saver, Action<VCPosInWorld> requestMeshRebuild)
    {
        this.parentTransform = parentTransform;
        this.saver = saver;
        this.requestMeshRebuild = requestMeshRebuild;
    }

    // 已加载（含已标记的空区块）
    public bool IsLoaded(VCPosInWorld pos) => loadedVoxelChunks.Contains(pos);

    // chunk 对象是否存在（已创建的非空区块）
    public bool ContainsChunk(VCPosInWorld pos) => world.ContainsKey(pos);

    // 已加载位置集合（仅供主线程只读遍历；遍历期间不可修改集合）
    public IEnumerable<Vector3Int> LoadedPositions => loadedVoxelChunks;

    // 获取 chunk 对象（仅供主线程只读使用）；未创建返回 null
    public VoxelChunk GetChunk(VCPosInWorld pos)
    {
        return world.TryGetValue(pos, out var vc) ? vc : null;
    }

    // 供快照构建读取邻居块数据（仅主线程调用）；未加载返回 null
    public Block[,,] GetChunkBlocks(VCPosInWorld vcPos)
    {
        return world.TryGetValue(vcPos, out var vc) ? vc.GetBlocksData() : null;
    }

    // 遍历所有已创建的 chunk（仅供主线程只读遍历；退出全量保存用）
    public void ForEachLoadedChunk(Action<VCPosInWorld, Block[,,]> action)
    {
        foreach (var (pos, vc) in world)
            action(new(pos.x, pos.y, pos.z), vc.GetBlocksData());
    }

    // 遍历所有已创建的 chunk（含 tile 字典，可能为 null；仅供主线程只读遍历；存档 v2 全量保存用）。
    // 调用方须在主线程内对 tiles 做快照（Saver.SnapshotTiles），worker 只读快照数组。
    public void ForEachLoadedChunk(Action<VCPosInWorld, Block[,,], Dictionary<ushort, PeaTileData>> action)
    {
        foreach (var (pos, vc) in world)
            action(new(pos.x, pos.y, pos.z), vc.GetBlocksData(), vc.TilesRaw);
    }

    // 跨 chunk 方块写入：目标未创建但为已标记的空区块时按需创建；目标不在视距盒内返回 false
    public bool SetBlock(Block block, BlockPosInWorld pos)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        BlockPosInVoxelChunk bPos = pos.GetCorrespondingPosInVC();

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

    // ---- tile 跨 chunk 路由（与 SetBlock 同构；仅主线程访问）----

    // 跨 chunk tile 写入：目标未创建但为已标记的空区块时按需创建；目标不在视距盒内返回 false
    public bool SetTile(BlockPosInWorld pos, PeaTileData tile)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        BlockPosInVoxelChunk bPos = pos.GetCorrespondingPosInVC();

        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            // 目标 chunk 未创建：若为已标记的空区块，则按需创建（与 SetBlock 一致，保证跨界种植落盘）
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

        targetChunk.SetTile(TileKey(bPos), tile);
        return true;
    }

    // 移除 tile：目标 chunk 不存在返回 false；存在则移除并返回 true
    public bool RemoveTile(BlockPosInWorld pos)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            return false;
        }

        targetChunk.RemoveTile(TileKey(pos.GetCorrespondingPosInVC()));
        return true;
    }

    // 读取 tile：目标 chunk 不存在返回 null
    public PeaTileData GetTile(BlockPosInWorld pos)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            return null;
        }

        return targetChunk.GetTile(TileKey(pos.GetCorrespondingPosInVC()));
    }

    // 块内坐标 → 线性 tile key：(x<<8)|(y<<4)|z（与 CHUNK_SIZE_LOG2 一致，16 位 ushort 装得下）
    private static ushort TileKey(BlockPosInVoxelChunk pos)
        => (ushort)((pos.X << (Constants.CHUNK_SIZE_LOG2 * 2)) | (pos.Y << Constants.CHUNK_SIZE_LOG2) | pos.Z);

    // 豌豆生长扫描：遍历所有已创建 chunk 的 tile，累加真实生长时间并推进阶段（仅主线程）。
    // tile 的 GrowthTime 是唯一时间源，阶段只进不退；阶段推进走 SetBlock 置 changed，
    // 由 VoxelChunk.Update 下一帧自动请求 mesh 重建，贴图随阶段切换。
    public void TickPeaGrowth(float dt)
    {
        foreach (var (_, vc) in world)
        {
            // 直接读底层字典避免惰性创建空字典；无 tile 的 chunk 跳过
            var tiles = vc.TilesRaw;
            if (tiles == null || tiles.Count == 0) continue;

            Block[,,] blocks = vc.GetBlocksData();
            foreach (var kv in tiles)
            {
                PeaTileData tile = kv.Value;
                tile.GrowthTime += dt;

                // 目标阶段：由累计生长时间决定（最小苗→苗→开花→结果，阶段只进不退）
                int newStage = tile.GrowthTime >= Constants.PEA_STAGE_3_SECONDS ? 3
                    : tile.GrowthTime >= Constants.PEA_STAGE_2_SECONDS ? 2
                    : tile.GrowthTime >= Constants.PEA_STAGE_1_SECONDS ? 1
                    : 0;

                // 当前阶段：从 chunk 块数组读（GetBlockState() 低 2 位状态位）
                ushort key = kv.Key;
                int x = key >> (Constants.CHUNK_SIZE_LOG2 * 2);
                int y = (key >> Constants.CHUNK_SIZE_LOG2) & (Constants.CHUNK_SIZE - 1);
                int z = key & (Constants.CHUNK_SIZE - 1);

                int currentStage = (int)(blocks[x, y, z].GetBlockState() & BlockBits.StageMask);

                // 阶段只进不退：目标 > 当前才更新方块状态位
                if (newStage > currentStage)
                {
                    vc.SetBlock(blocks[x, y, z].WithStage((uint)newStage), x, y, z);
                }
            }
        }
    }

    // 创建非空 chunk 对象；重复创建返回 false（不负责入队 mesh 构建，由调用方处理）
    public bool CreateChunk(VCPosInWorld pos, Block[,,] blockdata)
    {
        if (world.ContainsKey(pos))
        {
            Debug.LogWarning($"Repeatedly adding chunk{pos}");
            return false;
        }

        VoxelChunk chunk = GetChunkFromPool();
        if (chunk == null)
        {
            GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
            chunkGO.transform.SetParent(parentTransform);
            chunk = chunkGO.AddComponent<VoxelChunk>();
            // chunk.AddComponent<Rigidbody>().useGravity = false;
        }
        else
        {
            chunk.gameObject.SetActive(true);
            chunk.gameObject.name = $"Chunk_{pos.X}_{pos.Y}_{pos.Z}";
        }

        chunk.ResetForReuse(pos, blockdata);
        chunk.onMeshRebuildRequested = requestMeshRebuild; // 注入 mesh 重建请求（去单例化后由注入回调转发）

        world.Add(chunk.GetVCPosInWorld(), chunk);
        loadedVoxelChunks.Add(pos);
        return true;
    }

    // 空区块登记：全空气 chunk 不创建对象，仅记录为已加载（节省对象/内存/draw call）
    public void MarkEmptyChunkLoaded(VCPosInWorld pos)
    {
        loadedVoxelChunks.Add(pos);
    }

    // 卸载 chunk：同步保存（若有对象）、归还池、移除两个集合记录，并触发 OnChunkUnloaded 回调
    public void UnloadChunk(Vector3Int pos)
    {
        loadedVoxelChunks.Remove(pos); // 空区块（未创建对象）也在这里移除记录

        if (!world.TryGetValue(pos, out var vc))
        {
            // 空区块（未创建对象）：无需保存/销毁，记录已在上面移除
            return;
        }

        // 存档 v2：主线程内先快照 tile（Saver.SnapshotTiles 纯复制，worker 只读快照数组），随块数据一起保存
        saver.SaveVoxelChunk(new(pos.x, pos.y, pos.z), vc.GetBlocksData(), Saver.SnapshotTiles(vc.TilesRaw));

        ReturnChunkToPool(vc);
        world.Remove(pos);

        OnChunkUnloaded?.Invoke(new(pos.x, pos.y, pos.z));
    }

    // 取块数组：池空则新分配（主线程独享，绝不能在后台线程碰）
    public Block[,,] TakeBlockArray()
    {
        return _blockArrayPool.Count > 0
            ? _blockArrayPool.Pop()
            : new Block[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
    }

    // 归还块数组到池（池满溢出丢弃）
    public void ReturnBlockArray(Block[,,] arr)
    {
        if (arr != null && _blockArrayPool.Count < MAX_POOLED_BLOCK_ARRAYS)
            _blockArrayPool.Push(arr);
    }

    // 按需创建空区块对象（接收跨界写入用）
    private void CreateEmptyVoxelChunk(VCPosInWorld pos)
    {
        var arr = TakeBlockArray();
        VoxelChunk.FillAir(arr);

        VoxelChunk chunk = GetChunkFromPool();
        if (chunk == null)
        {
            GameObject chunkGO = new GameObject($"Chunk_{pos.X}_{pos.Y}_{pos.Z}");
            chunkGO.transform.SetParent(parentTransform);
            chunk = chunkGO.AddComponent<VoxelChunk>();
            // chunk.AddComponent<Rigidbody>().useGravity = false;
        }
        else
        {
            chunk.gameObject.SetActive(true);
            chunk.gameObject.name = $"Chunk_{pos.X}_{pos.Y}_{pos.Z}";
        }

        chunk.ResetForReuse(pos, arr);
        chunk.onMeshRebuildRequested = requestMeshRebuild; // 空区块按需恢复后同样需要重建请求能力

        world.Add(chunk.GetVCPosInWorld(), chunk);
        loadedVoxelChunks.Add(pos);
    }

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
}
