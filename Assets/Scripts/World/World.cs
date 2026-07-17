using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static UnityEditor.PlayerSettings;

public class World : MonoBehaviour
{
    public static World Instance { get; private set; }

    int lineOfSight = 6;

    int seed = 985211;

    private const int MAX_NEW_CHUNKS_PER_FRAME = 2;
    private const int MAX_MESH_OPTIMIZE_COUNT_PER_FRAME = 2;
    private const int MAX_BLOCKS_PER_FRAME = 64;
    private SemaphoreSlim _chunkUpdateLock = new(1, 1);
    private readonly ConcurrentQueue<VoxelChunkData> _pendingBuildQueue = new();
    private readonly ConcurrentQueue<List<(BlockPosInWorld, Block)>> _pendingSetBlocksQueue = new();
    private readonly ConcurrentQueue<VCPosInWorld> _pendingMeshOptimizeQueue = new();

    Dictionary<Vector3Int, VoxelChunk> world = new Dictionary<Vector3Int, VoxelChunk>();
    List<Vector3Int> loadedVoxelChunks = new List<Vector3Int>();
    List<Vector3Int> VCShouldBeUnloaded = new List<Vector3Int>();

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

        if(lastVCPosCam != VCPosCam)
        {
            _ = OnCameraChunkChanged(VCPosCam);
        }

        lastVCPosCam = VCPosCam;

        int builtCount = 0;
        while (builtCount < MAX_NEW_CHUNKS_PER_FRAME && _pendingBuildQueue.TryDequeue(out var chunkData))
        {
            var pos = chunkData.GetPos();

            if (!world.ContainsKey(pos))
            {
                CreateVoxelChunk(pos, chunkData.GetBlocksData());
                builtCount++;
            }
        }
        int setCount = 0;
        while(setCount < MAX_BLOCKS_PER_FRAME && _pendingSetBlocksQueue.TryDequeue(out var blockList))
        {
            foreach(var (pos, block) in blockList)
            {
                Setblock(block, pos);
            }
            setCount++;
        }
        int optimizeCount = 0;
        while(optimizeCount < MAX_MESH_OPTIMIZE_COUNT_PER_FRAME && _pendingMeshOptimizeQueue.TryDequeue(out var vcPos))
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

    async Task OnCameraChunkChanged(Vector3Int camPos)
    {
        if (!await _chunkUpdateLock.WaitAsync(0)) return;
        try
        {
            var tasks = new List<Task<VoxelChunkData>>();

            int x = VCPosCam.x;
            int y = VCPosCam.y;
            int z = VCPosCam.z;

            int l = lineOfSight;

            foreach (var chunkPos in loadedVoxelChunks.ToArray())
            {
                int VCx = chunkPos.x;
                int VCy = chunkPos.y;
                int VCz = chunkPos.z;

                int dx = VCx - x;
                int dy = VCy - y;
                int dz = VCz - z;

                if (Math.Abs(dx) > lineOfSight ||
                    Math.Abs(dy) > lineOfSight ||
                    Math.Abs(dz) > lineOfSight)
                {
                    world.TryGetValue(chunkPos, out var vc);

                    if (vc != null)
                    {
                        VCShouldBeUnloaded.Add(chunkPos);
                    }
                    else
                    {
                        throw new Exception($"Tried to destroy unexist voxelchunk at {chunkPos}");
                    }
                }
            }

            for (int X = x - l; X <= x + l; X++)
                for (int Y = Mathf.Max(y - l, 0); Y <= y + l; Y++)
                    for (int Z = z - l; Z <= z + l; Z++)
                    {
                        VCPosInWorld pos = new VCPosInWorld(X, Y, Z);

                        if (!world.ContainsKey(pos))
                        {
                            tasks.Add(Task.Run(() => GenerateVoxelChunkData(pos)));
                        }
                    }

            var chunks = await Task.WhenAll(tasks);


            foreach (var chunk in chunks)
            {
                _pendingBuildQueue.Enqueue(chunk);
            }

            loadedVoxelChunks.RemoveAll(p => VCShouldBeUnloaded.Contains(p));
            foreach(var p in VCShouldBeUnloaded)
            {
                UnloadVoxelChunk(p);
            }

            VCShouldBeUnloaded.Clear();

            // Debug.Log($"World Keys: l={world.Keys.Count}, {string.Join(", ", world.Keys)}");
            // Debug.Log($"Loaded Chunks: l={loadedVoxelChunks.Count}, {string.Join(", ", loadedVoxelChunks)}");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            throw ex;
        }
        finally
        {
            if (this != null) _chunkUpdateLock.Release();
        }
    }
    private void UnloadVoxelChunk(Vector3Int pos)
    {
        //Debug.Log($"Unloading VoxelChunk at {pos}");
        world.TryGetValue(pos, out var vc);

        //Debug.Log($"Saving VoxelChunk at {pos}");
        saver.SaveVoxelChunk(new(pos.x, pos.y, pos.z), vc.GetBlocksData());
        Debug.Log($"Saved VoxelChunk at {pos}");

        vc.DestroySelf();
        world.Remove(pos);

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
                for (int y = 0; y < lineOfSight; y++)
                {
                    VCPosInWorld pos = new(x, y, z);
                    Block[,,] data = GenerateVoxelChunkData(pos).GetBlocksData();
                    CreateVoxelChunk(pos, data);
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
            var data = GenerateVoxelChunkData(vcPos);
            CreateVoxelChunk(vcPos, data.GetBlocksData());
            _pendingSetBlocksQueue.Enqueue(data.GetPendingBlocks());
            if(!world.TryGetValue(vcPos, out targetChunk))
            {
                throw new Exception("Failed to create VoxelChunk at setblock!");
            }
        }

        targetChunk.SetBlock(block, bPos.X, bPos.Y, bPos.Z);
    }
    private void Setblock(Block block, BlockPosInWorld pos)
    {
        int x = pos.X;
        int y = pos.Y;
        int z = pos.Z;

        BlockPosInWorld posInWorld = new BlockPosInWorld(x, y, z);

        VCPosInWorld vcPos = posInWorld.GetCorrespondingVCPos();
        BlockPosInVoxelChunk bPos = posInWorld.GetCorrespondingPosInVC();

        if (!world.TryGetValue(vcPos, out VoxelChunk targetChunk))
        {
            var data = GenerateVoxelChunkData(vcPos);
            CreateVoxelChunk(vcPos, data.GetBlocksData());
            _pendingSetBlocksQueue.Enqueue(data.GetPendingBlocks());
            if (!world.TryGetValue(vcPos, out targetChunk))
            {
                throw new Exception("Failed to create VoxelChunk at setblock!");
            }
        }

        targetChunk.SetBlock(block, bPos.X, bPos.Y, bPos.Z);
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
            throw new Exception($"Repeatedly adding chunk{pos}");
            // Debug.LogError($"Repeatedly adding chunk{pos}");
            // return;
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
