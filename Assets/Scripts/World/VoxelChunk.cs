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

    MeshData meshData;

    Mesh mesh;

    bool initialized = false;
    bool changed = false;

    bool isEmpty = false;

    void Awake()
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        blocks = new Block[
            Constants.CHUNK_SIZE, 
            Constants.CHUNK_SIZE,
            Constants.CHUNK_SIZE];

        // CreateSinglePlaneVoxelChunk();

        meshData = new MeshData();

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

        // mesh 构建已交给 World 的预算队列（MeshOptimize），此处不再同步构建
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

        UpdateOrCreateMesh(true);
    }

    public void MeshOptimize()
    {
        UpdateOrCreateMesh(false);
    }

    private void UpdateOrCreateMesh(bool firstLoad)
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        meshData.Clear();

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int y = 0; y < CHUNK_SIZE; y++)
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    var blockType = blocks[x, y, z].GetBlockType();
                    if (blockType != BlockType.Air && blockType != BlockType.Void)
                    {
                        TryAddFace(meshData, x, y, z, Direction.Up      , firstLoad);
                        TryAddFace(meshData, x, y, z, Direction.Down    , firstLoad);
                        TryAddFace(meshData, x, y, z, Direction.North   , firstLoad);
                        TryAddFace(meshData, x, y, z, Direction.South   , firstLoad);
                        TryAddFace(meshData, x, y, z, Direction.East    , firstLoad);
                        TryAddFace(meshData, x, y, z, Direction.West    , firstLoad);
                    }
                }

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

    private void TryAddFace(MeshData meshData, int x, int y, int z, Direction dir, bool firstLoad)
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        // Vector3Int neighborPos = GetNeighborPos(x, y, z, dir);
        
        if (!ShouldBeEliminated(x, y, z, dir, firstLoad))
        {
            int xInWorld = x + pos.X * CHUNK_SIZE;
            int yInWorld = y + pos.Y * CHUNK_SIZE;
            int zInWorld = z + pos.Z * CHUNK_SIZE;

            meshData.AddFace(xInWorld, yInWorld, zInWorld, dir, blocks[x, y, z]);
        }
    }

    private Vector3Int GetNeighborPos(int x, int y, int z, Direction dir)
    {
        return dir switch
        {
            Direction.Up => new Vector3Int(x, y + 1, z),
            Direction.Down => new Vector3Int(x, y - 1, z),
            Direction.North => new Vector3Int(x, y, z + 1),
            Direction.South => new Vector3Int(x, y, z - 1),
            Direction.East => new Vector3Int(x + 1, y, z),
            Direction.West => new Vector3Int(x - 1, y, z),
            _ => new Vector3Int(x, y, z)
        };
    }

    private bool ShouldBeEliminated(int x, int y, int z, Direction dir, bool firstLoad)
    {
        int CHUNK_SIZE = Constants.CHUNK_SIZE;
        Vector3Int neighborPos = GetNeighborPos(x, y, z, dir);

        int nx = neighborPos.x;
        int ny = neighborPos.y;
        int nz = neighborPos.z;

        BlockType bt = blocks[x, y, z].GetBlockType();
        BlockType neighborBt;

        if
            (
            nx < 0 || nx >= CHUNK_SIZE ||
            ny < 0 || ny >= CHUNK_SIZE ||
            nz < 0 || nz >= CHUNK_SIZE
            )
        {
            if (firstLoad)
            {
                return false;
            }

            int nxInWorld = nx + pos.X * CHUNK_SIZE;
            int nyInWorld = ny + pos.Y * CHUNK_SIZE;
            int nzInWorld = nz + pos.Z * CHUNK_SIZE;

            World.Instance.TryGetBlock(new(nxInWorld, nyInWorld, nzInWorld), out neighborBt);

            if(neighborBt == BlockType.ERROR)
            {
                // Debug.Log($"failed to get block at{nx}, {ny}, {nz}");
                return false;
            }
            // Debug.Log($"successed to get block at {nx}, {ny}, {nz} -> {neighborBt}");
        }
        else
        {
            neighborBt = blocks[nx, ny, nz].GetBlockType();
        }


        if(neighborBt == BlockType.Air || neighborBt == BlockType.Void)
        {
            return false;
        }
        if(bt == BlockType.Leaves || neighborBt == BlockType.Leaves)
        {
            return false;
        }
        
        return true;
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