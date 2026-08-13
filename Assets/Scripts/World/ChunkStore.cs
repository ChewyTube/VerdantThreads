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

    // 旧档完整性修复：扫描该 chunk 全部 16³ 格，修复豌豆植株结构——
    //   PeaStem 阶段≥2：按基因（位点6 高茎/矮茎）补齐中部+顶部 / 顶部（跨 chunk 到 Y+1）；
    //   孤儿 PeaPlantTop/PeaPlantMiddle：结构不合法则清除；
    //   跨 chunk「底部在 y=15、中部/顶部在邻居 y=0/1」与「底部 y=14、顶部在邻居 y=0」。
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
                    switch (b.GetBlockType())
                    {
                        case BlockType.PeaStem:
                            // 阶段 ≥2 的植株：按基因高/矮补齐结构（高茎 3 格 / 矮茎 2 格）
                            if ((int)(b.GetBlockState() & BlockBits.StageMask) >= 2)
                                RepairEnsurePlantStructure(vcPos, blocks, x, y, z);
                            break;
                        case BlockType.PeaPlantMiddle:
                            RepairRemoveOrphanMiddle(vcPos, blocks, x, y, z);
                            break;
                        case BlockType.PeaPlantTop:
                            RepairRemoveOrphanTop(vcPos, blocks, x, y, z);
                            break;
                        default:
                            // 跨 chunk 补位：下方植株缺格则补（y=0 中部/顶部、y=1 顶部）
                            if (b.GetBlockType() == BlockType.Air)
                                RepairFillFromBelow(vcPos, blocks, x, y, z);
                            break;
                    }
                }
    }

    // 阶段≥2 的 PeaStem：按基因高/矮补齐上方结构。高茎 → 中部(y+1)+顶部(y+2)；矮茎 → 顶部(y+1)。
    // 目标格非 Air 则不动（不覆盖既有内容）；已有中部/顶部则同步阶段与高/矮标志（旧档阶段为 0）。
    // 目标在 Y+1 邻居 chunk 时由 GetChunkBlocks/SetBlock 处理（未加载返回 false，等它自己跑修复轮兜底）。
    private void RepairEnsurePlantStructure(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        Block bottom = blocks[x, y, z];
        uint stage = (uint)(bottom.GetBlockState() & BlockBits.StageMask);

        // 高茎判定：读底部 tile 基因（位点 6 显性）；tile 缺失按矮茎（旧档两格结构保持原样）
        PeaTileData tile = GetTile(new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z));
        bool tall = tile != null && PeaTextures.IsTall(tile.Genome);

        if (tall)
        {
            // 中部格 y+1（可能跨 chunk 到 Y+1 的 y=0）
            BlockPosInWorld midPos = new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y + 1, vcPos.Z * s + z);
            if (TryGetBlockAt(midPos, out Block mid))
            {
                if (mid.GetBlockType() == BlockType.Air)
                    SetBlock(BlockRegistry.PeaPlantMiddle.WithStage(stage).WithTall(true), midPos, suppressUpdate: true);
                else if (mid.GetBlockType() == BlockType.PeaPlantMiddle)
                    SetBlock(mid.WithStage(stage).WithTall(true), midPos, suppressUpdate: true); // 阶段同步
            }

            // 顶部格 y+2（可能跨 chunk 到 Y+1 的 y=0/1）
            BlockPosInWorld topPos = new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y + 2, vcPos.Z * s + z);
            if (TryGetBlockAt(topPos, out Block top))
            {
                if (top.GetBlockType() == BlockType.Air)
                    SetBlock(BlockRegistry.PeaPlantTop.WithStage(stage).WithTall(true), topPos, suppressUpdate: true);
                else if (top.GetBlockType() == BlockType.PeaPlantTop)
                    SetBlock(top.WithStage(stage).WithTall(true), topPos, suppressUpdate: true); // 阶段同步
            }
        }
        else
        {
            // 矮茎：顶部格 y+1（可能跨 chunk 到 Y+1 的 y=0）
            BlockPosInWorld topPos = new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y + 1, vcPos.Z * s + z);
            if (TryGetBlockAt(topPos, out Block top))
            {
                if (top.GetBlockType() == BlockType.Air)
                    SetBlock(BlockRegistry.PeaPlantTop.WithStage(stage).WithTall(false), topPos, suppressUpdate: true);
                else if (top.GetBlockType() == BlockType.PeaPlantTop)
                    SetBlock(top.WithStage(stage).WithTall(false), topPos, suppressUpdate: true); // 阶段同步
            }
        }
    }

    // 孤儿中部：下方（y-1，y=0 时下邻居 y=15）不是「阶段≥2 的 PeaStem」→ 清除；合法则同步阶段 + 高茎标志
    private void RepairRemoveOrphanMiddle(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        Block below = ReadCellAt(vcPos, blocks, x, y - 1, z);
        bool valid = below.GetBlockType() == BlockType.PeaStem &&
            (int)(below.GetBlockState() & BlockBits.StageMask) >= 2;
        if (!valid)
        {
            SetBlock(BlockRegistry.Air, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z), suppressUpdate: true);
            return;
        }
        uint stage = (uint)(below.GetBlockState() & BlockBits.StageMask);
        SetBlock(blocks[x, y, z].WithStage(stage).WithTall(true), new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z), suppressUpdate: true);
    }

    // 孤儿顶部：合法 = 下方是「阶段≥2 矮茎 PeaStem」（矮茎：顶部紧贴底部）
    //  或 下方是 PeaPlantMiddle 且其下是「阶段≥2 高茎 PeaStem」（高茎：底部+中部+顶部）。
    // 合法则同步阶段与高/矮标志；不合法则清除。
    private void RepairRemoveOrphanTop(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        Block below = ReadCellAt(vcPos, blocks, x, y - 1, z);

        Block bottom = default;
        bool tallTop = false;
        if (below.GetBlockType() == BlockType.PeaStem)
        {
            bottom = below;             // 矮茎顶部：下方即底部
        }
        else if (below.GetBlockType() == BlockType.PeaPlantMiddle)
        {
            bottom = ReadCellAt(vcPos, blocks, x, y - 2, z); // 高茎顶部：下方是中部、再下是底部
            tallTop = true;
        }

        bool valid = bottom.GetBlockType() == BlockType.PeaStem &&
            (int)(bottom.GetBlockState() & BlockBits.StageMask) >= 2;
        if (!valid)
        {
            SetBlock(BlockRegistry.Air, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z), suppressUpdate: true);
            return;
        }

        uint stage = (uint)(bottom.GetBlockState() & BlockBits.StageMask);
        SetBlock(blocks[x, y, z].WithStage(stage).WithTall(tallTop), new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z), suppressUpdate: true);
    }

    // 跨 chunk 补位：本格是 Air，但下方（Y-1 邻居）是阶段≥2 豌豆、且上方结构缺失 → 补中部/顶部。
    // 覆盖：y=0 Air + 下方矮茎 → 补顶部(y=0)；y=0 Air + 下方高茎 → 补中部(y=0)；
    //      y=1 Air + 下方已补/已存在中部 → 补顶部(y=1)。循环按 y 升序，先补 y=0 再衔接 y=1。
    private void RepairFillFromBelow(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        if (y == 0)
        {
            // 下方（Y-1 邻居 y=15）是阶段≥2 豌豆 → 高茎补中部 / 矮茎补顶部
            if (TryGetBlockAt(new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s - 1, vcPos.Z * s + z), out Block below) &&
                below.GetBlockType() == BlockType.PeaStem &&
                (int)(below.GetBlockState() & BlockBits.StageMask) >= 2)
            {
                uint stage = (uint)(below.GetBlockState() & BlockBits.StageMask);
                PeaTileData tile = GetTile(new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s - 1, vcPos.Z * s + z));
                bool tall = tile != null && PeaTextures.IsTall(tile.Genome);
                Block cell = tall ? BlockRegistry.PeaPlantMiddle : BlockRegistry.PeaPlantTop;
                SetBlock(cell.WithStage(stage).WithTall(tall), new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s, vcPos.Z * s + z), suppressUpdate: true);
            }
        }
        else if (y == 1)
        {
            // 下方（本 chunk y=0）是高茎中部 → 补顶部(y=1)
            if (TryGetBlockAt(new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s, vcPos.Z * s + z), out Block below) &&
                below.GetBlockType() == BlockType.PeaPlantMiddle)
            {
                uint stage = (uint)(below.GetBlockState() & BlockBits.StageMask);
                SetBlock(BlockRegistry.PeaPlantTop.WithStage(stage).WithTall(true), new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + 1, vcPos.Z * s + z), suppressUpdate: true);
            }
        }
    }

    // 读取世界坐标处方块（跨 chunk）；chunk 未加载返回 false
    private bool TryGetBlockAt(BlockPosInWorld pos, out Block block)
    {
        Block[,,] b = GetChunkBlocks(pos.GetCorrespondingVCPos());
        if (b == null) { block = default; return false; }
        int m = Constants.CHUNK_SIZE - 1;
        block = b[pos.X & m, pos.Y & m, pos.Z & m];
        return true;
    }

    // 读取局部偏移格（y 越界时落到 Y±1 邻居 chunk；未加载返回 Air）
    private Block ReadCellAt(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        int m = s - 1;
        int ly = y & m;
        int cy = y >> Constants.CHUNK_SIZE_LOG2;
        if (cy == 0) return blocks[x, ly, z]; // 同 chunk
        Block[,,] n = GetChunkBlocks(new VCPosInWorld(vcPos.X, vcPos.Y + cy, vcPos.Z));
        if (n == null) return BlockRegistry.Air;
        return n[x, ly, z];
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
