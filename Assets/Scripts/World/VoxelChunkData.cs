using System;
using System.Collections.Generic;
using System.Numerics;
using UnityEngine;

public class VoxelChunkData
{
    Block[,,] blocks;
    VCPosInWorld pos;
    List<(BlockPosInWorld, Block)> pendingBlocks;

    // 存档 v2 读回的 tile 快照（纯值数组）：后台生成线程构造，主线程消费，跨线程安全。
    // 默认空数组，读路径（GenerateVoxelChunkData 命中存档）用 SetLoadedTiles 注入。
    private TileSaveRecord[] loadedTiles = Array.Empty<TileSaveRecord>();

    // 生成期地物产出的 tile（豌豆丛）：后台生成线程写入，主线程按世界坐标路由到目标 chunk（跨 chunk 重试）。
    // 与 pendingBlocks 平行：块走 Setblock → pendingBlocks、tile 走本通道，两条路在目标 chunk 汇合。
    // 纯值元组（BlockPosInWorld, Genome, HarvestGenome）跨线程安全（第三项为采收基因，Phase 2 多基因数量性状）。
    private readonly List<(BlockPosInWorld, Genome, HarvestGenome)> pendingTileWrites = new List<(BlockPosInWorld, Genome, HarvestGenome)>();

    public VoxelChunkData(Block[,,] blocks, VCPosInWorld pos, List<(BlockPosInWorld, Block)> pendingBlocks, bool fillAir = true)
    {
        this.blocks = blocks;
        this.pos = pos;
        this.pendingBlocks = pendingBlocks;

        if (!fillAir) return; // 读路径（存档加载）数据已就绪，跳过 Air 填充

        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        for(int x=0; x<CHUNK_SIZE; x++)
            for(int y=0; y<CHUNK_SIZE; y++)
                for(int z=0; z<CHUNK_SIZE; z++)
                {
                    blocks[x, y, z] = BlockRegistry.Air;
                }

    }

    public void Setblock(Block block, int x, int y, int z)
    {
        if (
            x < 0 || x >= Constants.CHUNK_SIZE ||
            y < 0 || y >= Constants.CHUNK_SIZE ||
            z < 0 || z >= Constants.CHUNK_SIZE)
        {
            int X = x + Constants.CHUNK_SIZE * pos.X;
            int Y = y + Constants.CHUNK_SIZE * pos.Y;
            int Z = z + Constants.CHUNK_SIZE * pos.Z;

            pendingBlocks.Add((new(X, Y, Z), block));
            return;
        }

        blocks[x, y, z] = block;
    }

    public void Setblock(Block block, int x, int y, int z, int dx, int dy, int dz)
    {
        Setblock(block, x + dx, y + dy, z + dz);
    }

    public Block[,,] GetBlocksData()
    {
        return blocks;
    }

    // 设置存档读回的 tile 快照（后台生成线程调用，主线程稍后消费；纯值数组跨线程安全）
    public void SetLoadedTiles(TileSaveRecord[] tiles)
    {
        loadedTiles = tiles ?? Array.Empty<TileSaveRecord>();
    }

    // 读取 tile 快照（主线程消费：CreateChunk 成功后回挂到 chunk 的 tile 字典）
    public TileSaveRecord[] GetLoadedTiles()
    {
        return loadedTiles;
    }

    // 后台线程调用：登记一个豌豆 tile（世代 0、生长时间 0 = 阶段 0 苗；采收基因随株确定性派生/随机）
    public void AddPendingTileWrite(BlockPosInWorld pos, Genome genome, HarvestGenome harvestGenome)
    {
        pendingTileWrites.Add((pos, genome, harvestGenome));
    }

    // 主线程消费：返回全部待路由 tile 记录
    public (BlockPosInWorld, Genome, HarvestGenome)[] GetPendingTileWrites()
    {
        return pendingTileWrites.ToArray();
    }
    public VCPosInWorld GetPos()
    {
        return pos;
    }

    public List<(BlockPosInWorld, Block)> GetPendingBlocks()
    {
        return pendingBlocks;
    }

    // 判断该 chunk 数据是否全为空气（Air/Void），用于自动卸载空区块
    public bool IsEmpty()
    {
        for (int x = 0; x < Constants.CHUNK_SIZE; x++)
            for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                for (int z = 0; z < Constants.CHUNK_SIZE; z++)
                {
                    var t = blocks[x, y, z].GetBlockType();
                    if (t != BlockType.Air && t != BlockType.Void)
                        return false;
                }
        return true;
    }
}