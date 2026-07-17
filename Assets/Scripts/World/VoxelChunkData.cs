using System;
using System.Collections.Generic;
using System.Numerics;
using UnityEngine;

public struct VoxelChunkData
{
    Block[,,] blocks;
    VCPosInWorld pos;
    List<(BlockPosInWorld, Block)> pendingBlocks;

    public VoxelChunkData(Block[,,] blocks, VCPosInWorld pos, List<(BlockPosInWorld, Block)> pendingBlocks)
    {
        this.blocks = blocks;
        this.pos = pos;
        this.pendingBlocks = pendingBlocks;

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