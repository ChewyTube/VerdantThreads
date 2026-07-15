using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Block
{
    BlockType type;

    public Block()
    {
        type = BlockType.Void;
    }
    public Block(BlockType type)
    {
        this.type = type;
    }

    public BlockType GetBlockType()
    {
        return type;
    }
}
public enum BlockType
{
    ERROR = -1,
    Void = 0,
    Air = 1,
    Grass = 2,
    Dirt = 3,
    Bedrock = 4,
    Stone = 5,
    Log = 6,
    Leaves = 7,
}

