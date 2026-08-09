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
            World.Instance?.RequestMeshRebuild(pos);
        }
    }


    public void CreateSinglePlaneVoxelChunk()
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                SetBlock(BlockRegistry.Bedrock, x, 0, z);
                SetBlock(BlockRegistry.Dirt, x, 1, z);
                SetBlock(BlockRegistry.Dirt, x, 2, z);
                SetBlock(BlockRegistry.Grass, x, 3, z);
                //SetBlock(BlockRegistry.Grass, x, x * z % 16, z);
            }

        World.Instance?.RequestMeshRebuild(pos);
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
        pos = default;
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
    Up = 2, 
    Down = 3, 
    North = 5, 
    South = 4, 
    East = 0, 
    West = 1
}