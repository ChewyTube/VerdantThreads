using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoxelChunk : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Material blockMaterial;

    public VCPosInWorld pos{
        get; private set;
    }

    Block[,,] blocks;

    Mesh mesh;

    private static long _nextInstanceId;
    public long InstanceId { get; private set; }   // 实例唯一 ID（用于丢弃过期上传）
    private long _buildSeq;                         // 下次构建代次
    private long _appliedMeshSeq;                   // 已应用的最大构建代次

    bool initialized = false;
    bool changed = false;

    bool isEmpty = false;

    // ---- tile 字典（豌豆生长数据）----
    // tile 仅主线程访问；与 blocks 无强绑定，破坏/种植由 BlockInteraction 联动维护。
    // 存档 v2 之前 tile 不单独序列化：chunk 卸载即丢失（预期行为）。
    private Dictionary<ushort, PeaTileData> _tiles;

    // 惰性初始化：首次访问时 new，避免空 chunk 白占字典
    public Dictionary<ushort, PeaTileData> Tiles
    {
        get
        {
            if (_tiles == null) _tiles = new Dictionary<ushort, PeaTileData>();
            return _tiles;
        }
    }

    // 仅主线程只读访问底层字典（可能为 null）：供生长扫描等遍历场景，避免惰性创建空字典
    public Dictionary<ushort, PeaTileData> TilesRaw => _tiles;

    // 注入的 mesh 重建请求回调（去单例化：由 ChunkStore 创建时赋值，World 组装时转发给 ChunkStreamer）
    public Action<VCPosInWorld> onMeshRebuildRequested;

    void Awake()
    {
        InstanceId = ++_nextInstanceId;

        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        blocks = new Block[
            Constants.CHUNK_SIZE, 
            Constants.CHUNK_SIZE,
            Constants.CHUNK_SIZE];

        // CreateSinglePlaneVoxelChunk();

        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();

        // struct 数组元素判 null 恒为 false（装箱比较），原 if 分支永不执行，未初始化数据保持默认 BlockType.Void
        // 这里无条件填 Air：保证按需恢复的空区块（CreateEmptyVoxelChunk）以空气为基底，不渲染成实心
        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int y = 0; y < CHUNK_SIZE; y++)
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    blocks[x, y, z] = BlockRegistry.Air;
                }
    }

    // Start is called before the first frame update
    void Start()
    {
        if (!initialized)
        {
            throw new Exception("uninitialized voxelchunk");
            //return;
        }



        if (WorldManager.Instance == null)
        {
            throw new Exception("WorldManager is null");
        }

        blockMaterial = WorldManager.Instance.BlockMaterial;

        if (blockMaterial == null)
        {
            throw new Exception("BlockMaterial is null");
        }

        meshRenderer.sharedMaterial = blockMaterial;

        // mesh 构建已交给 World 的预算队列，此处不再同步构建
    }

    void Update()
    {
        if (changed)
        {
            changed = false;
            onMeshRebuildRequested?.Invoke(pos);
        }
    }


    // 主线程调用：为本次后台 mesh 构建分配递增代次
    public long TakeBuildSeq() => ++_buildSeq;

    // 主线程调用：把后台生成的 MeshData 写入复用的 Mesh 实例
    public void ApplyMeshData(MeshData meshData)
    {
        // 丢弃已卸载/已重建 chunk 的过期上传（实例 ID 不匹配）
        if (meshData.ChunkId != InstanceId) return;
        // 丢弃乱序完成的旧代次构建，保证 mesh 收敛到最新数据
        if (meshData.Seq <= _appliedMeshSeq) return;
        _appliedMeshSeq = meshData.Seq;

        if (mesh == null) mesh = new Mesh();
        meshData.FillMesh(mesh);
        meshFilter.mesh = mesh;
    }

    public void Initialize(VCPosInWorld p)
    {
        pos = p;

        initialized = true;
    }
    public void Initialize(VCPosInWorld p, Block[,,] blockdata)
    {
        pos = p;

        blocks = blockdata;

        initialized = true;
    }

    // 池化复用：新生命周期新身份（让上一世在途上传被 ChunkId 守卫丢弃），并重置状态
    public void ResetForReuse(VCPosInWorld p, Block[,,] blockdata)
    {
        InstanceId = ++_nextInstanceId;
        _buildSeq = 0;
        _appliedMeshSeq = 0;

        pos = p;
        blocks = blockdata;
        initialized = true;
        changed = false;
        isEmpty = false;
        _tiles = null; // 池化复用：清空上一世 tile（tile 仅主线程访问；存档 v2 之前卸载即丢失，预期行为）

        if (mesh != null) mesh.Clear(); // 清掉上一世残留 mesh，防止复用为空区块时渲染旧内容
    }

    // 归还池前清理：断数组引用、隐藏 GO；材质保留（Start 只跑一次，复用后必须保留）
    public void PrepareForPool()
    {
        if (mesh != null) mesh.Clear();
        gameObject.SetActive(false);
        blocks = null;
        initialized = false;
        changed = false;
        isEmpty = false;
        _tiles = null; // 归还池前释放 tile 字典，防止复用残留上一世 tile
        pos = default;
        onMeshRebuildRequested = null; // 断注入回调引用，复用时由 ChunkStore 重新赋值
    }

    // 静态 Air 填充辅助（供 CreateEmptyVoxelChunk 复用池化数组）
    public static void FillAir(Block[,,] arr)
    {
        for (int x = 0; x < Constants.CHUNK_SIZE; x++)
            for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                for (int z = 0; z < Constants.CHUNK_SIZE; z++)
                    arr[x, y, z] = BlockRegistry.Air;
    }
    public void SetBlock(Block block, int x,  int y, int z)
    {
        if (x < 0 || x >= Constants.CHUNK_SIZE) { throw new Exception($"Invalid input x={x}"); }
        if (y < 0 || y >= Constants.CHUNK_SIZE) { throw new Exception($"Invalid input y={y}"); }
        if (z < 0 || z >= Constants.CHUNK_SIZE) { throw new Exception($"Invalid input z={z}"); }

        blocks[x, y, z] = block;
        changed = true;
    }

    // ---- tile 读写（仅主线程）----

    // 写入 tile（惰性建字典）
    public void SetTile(ushort key, PeaTileData tile)
    {
        if (_tiles == null) _tiles = new Dictionary<ushort, PeaTileData>();
        _tiles[key] = tile;
    }

    // 移除 tile：存在才移除；移除后字典空则置 null 释放
    public void RemoveTile(ushort key)
    {
        if (_tiles == null) return;
        _tiles.Remove(key);
        if (_tiles.Count == 0) _tiles = null;
    }

    // 读取 tile：无则返回 null
    public PeaTileData GetTile(ushort key)
    {
        if (_tiles == null) return null;
        return _tiles.TryGetValue(key, out var tile) ? tile : null;
    }

    public BlockType GetBlock(BlockPosInVoxelChunk pos)
    {
        int x = pos.X;
        int y = pos.Y;
        int z = pos.Z;

        return blocks[x, y, z].GetBlockType();
    }
    public Block[,,] GetBlocksData()
    {
        return blocks;
    }

    public VCPosInWorld GetVCPosInWorld()
    {
        return pos;
    }

    public void EmptyCheck()
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int y = 0; y < CHUNK_SIZE; y++)
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    if(blocks[x, y, z].GetBlockType() != BlockType.Air)
                    {
                        isEmpty = false;
                        return;
                    }
                }
        isEmpty = true;
    }

    public void DestroySelf()
    {
        if (meshFilter != null && meshFilter.mesh != null)
        {
            Destroy(meshFilter.mesh);
            meshFilter.sharedMesh = null;
        }

        if (meshRenderer != null)
        {
            meshRenderer.sharedMaterial = null;
        }


        Destroy(gameObject);
    }
}

public enum Direction
{
    East = 0,  // +X
    West = 1,  // -X
    Up = 2,    // +Y
    Down = 3,  // -Y
    South = 4, // -Z
    North = 5, // +Z
    Count = 6, // 方向总数
}