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

    // 遍历所有已创建的 chunk（含 tile 字典，可能为 null；仅供主线程只读遍历；存档 v3 全量保存用）。
    // 调用方须在主线程内对 tiles 做快照（Saver.SnapshotTiles），worker 只读快照数组。
    public void ForEachLoadedChunk(Action<VCPosInWorld, Block[,,], Dictionary<ushort, PeaTileData>> action)
    {
        foreach (var (pos, vc) in world)
            action(new(pos.x, pos.y, pos.z), vc.GetBlocksData(), vc.TilesRaw);
    }

    // 跨 chunk 方块写入：目标未创建但为已标记的空区块时按需创建；目标不在视距盒内返回 false。
    // 写入成功且旧块 != 新块、未 suppress → 触发 OnBlockWritten（BlockUpdateCenter 分派本位置 + 6 邻居联动）。
    public bool SetBlock(Block block, BlockPosInWorld pos)
    {
        return SetBlock(block, pos, suppressUpdate: false);
    }

    // suppressUpdate=true：写入但不触发方块更新通知（生成期 pendingBlocks 重放 / 存档修复用，
    // 世界刚生成/修复无需联动）。变化检测：旧块 == 新块时跳过写入与通知（优化 + 防循环）。
    public bool SetBlock(Block block, BlockPosInWorld pos, bool suppressUpdate)
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

        Block[,,] blocks = targetChunk.GetBlocksData();
        Block oldBlock = blocks[bPos.X, bPos.Y, bPos.Z];
        if (oldBlock == block) return true; // 无变化：跳过写入与通知（优化 + 防循环）

        targetChunk.SetBlock(block, bPos.X, bPos.Y, bPos.Z);

        // 写入成功且未 suppress → 通知方块更新中心（联动判定用旧块，位置与 6 邻居分派在其中完成）
        if (!suppressUpdate)
            OnBlockWritten?.Invoke(pos, oldBlock, block);

        return true;
    }

    // 方块写入通知（主线程）：由 World 装配时订阅到 BlockUpdateCenter（本位置 + 6 邻居联动分派）
    public event Action<BlockPosInWorld, Block, Block> OnBlockWritten;

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

    // 旧档完整性修复：扫描该 chunk 全部 16³ 格，修复两格高豌豆（PeaStem 阶段≥2 缺顶部 /
    // 孤儿 PeaPlantTop / 跨 chunk「底部在 y=15、顶部在邻居 y=0」）。
    // 主线程调用（ChunkStreamer 创建 chunk 成功后）；读邻居用 GetChunkBlocks（null=未加载跳过，
    // 等邻居 chunk 创建时它自己跑修复轮兜底），写用 SetBlock（false=目标不在视距内跳过）。
    public void RepairPeaPlants(VCPosInWorld vcPos)
    {
        Block[,,] blocks = GetChunkBlocks(vcPos);
        if (blocks == null) return;

        int s = Constants.CHUNK_SIZE;
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                {
                    Block b = blocks[x, y, z];
                    if (b.GetBlockType() == BlockType.PeaStem)
                    {
                        // 阶段 ≥2 的两格高植株：上方缺 PeaPlantTop → 补顶
                        if ((int)(b.GetBlockState() & BlockBits.StageMask) >= 2)
                            RepairEnsureTop(vcPos, blocks, x, y, z);
                    }
                    else if (b.GetBlockType() == BlockType.PeaPlantTop)
                    {
                        RepairRemoveOrphanTop(vcPos, blocks, x, y, z); // 下方不是阶段≥2 的 PeaStem → 清孤儿顶
                    }
                    else if (y == 0 && b.GetBlockType() == BlockType.Air)
                    {
                        RepairTopFromBelow(vcPos, x, z); // y=0 Air：下方（邻居 y=15）若是阶段≥2 豌豆 → 补顶
                    }
                }
    }

    // 阶段≥2 的 PeaStem 上方格非 PeaPlantTop 时：上方为 Air 则补顶部（PeaPlantTop）
    private void RepairEnsureTop(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        int topLocalY = y + 1;

        BlockType topType;
        if (topLocalY < s)
        {
            topType = blocks[x, topLocalY, z].GetBlockType();
        }
        else
        {
            Block[,,] up = GetChunkBlocks(new VCPosInWorld(vcPos.X, vcPos.Y + 1, vcPos.Z));
            if (up == null) return; // 上方 chunk 未加载：跳过，等它加载后由自身修复轮处理
            topType = up[x, 0, z].GetBlockType();
        }

        if (topType == BlockType.PeaPlantTop) return; // 顶部已存在
        if (topType != BlockType.Air) return;         // 上方被其他方块占：保持现状，不破坏既有内容

        SetBlock(BlockRegistry.PeaPlantTop, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + topLocalY, vcPos.Z * s + z), suppressUpdate: true); // 补顶部（存档修复不触发方块更新；false=目标未加载则跳过）
    }

    // 孤儿顶部：下方不是 PeaStem（或阶段<2）→ 顶部置 Air 清除
    private void RepairRemoveOrphanTop(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        int belowLocalY = y - 1;

        Block below = BlockRegistry.Air;
        if (belowLocalY >= 0)
        {
            below = blocks[x, belowLocalY, z];
        }
        else
        {
            Block[,,] down = GetChunkBlocks(new VCPosInWorld(vcPos.X, vcPos.Y - 1, vcPos.Z));
            if (down == null) return; // 下方 chunk 未加载：跳过
            below = down[x, s - 1, z];
        }

        // 合法底部 = PeaStem 且阶段 ≥ 2（两格高植株的顶部格才有存在意义）
        bool validBottom = below.GetBlockType() == BlockType.PeaStem
            && (int)(below.GetBlockState() & BlockBits.StageMask) >= 2;
        if (!validBottom)
        {
            SetBlock(BlockRegistry.Air, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z), suppressUpdate: true); // 清除孤儿顶（存档修复不触发方块更新）
        }
    }

    // y=0 层 Air 格：下方格（邻居 chunk y=15）若是阶段≥2 的 PeaStem → 补顶部（覆盖跨 chunk 情形）
    private void RepairTopFromBelow(VCPosInWorld vcPos, int x, int z)
    {
        int s = Constants.CHUNK_SIZE;
        Block[,,] down = GetChunkBlocks(new VCPosInWorld(vcPos.X, vcPos.Y - 1, vcPos.Z));
        if (down == null) return; // 下方 chunk 未加载：跳过

        Block below = down[x, s - 1, z];
        if (below.GetBlockType() != BlockType.PeaStem) return;
        if ((int)(below.GetBlockState() & BlockBits.StageMask) < 2) return;

        SetBlock(BlockRegistry.PeaPlantTop, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s, vcPos.Z * s + z), suppressUpdate: true); // 补顶部（存档修复不触发方块更新）
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

        // 存档 v3：主线程内先快照 tile（Saver.SnapshotTiles 纯复制，worker 只读快照数组），随块数据一起保存
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
