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

        saver.SaveVoxelChunk(new(pos.x, pos.y, pos.z), vc.GetBlocksData());

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
