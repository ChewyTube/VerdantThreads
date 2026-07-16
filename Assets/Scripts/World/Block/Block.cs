using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//public class Block
//{
//    BlockType type;

//    public Block()
//    {
//        type = BlockType.Void;
//    }
//    public Block(BlockType type)
//    {
//        this.type = type;
//    }

//    public BlockType GetBlockType()
//    {
//        return type;
//    }
//}

public readonly struct Block : IEquatable<Block>
{
    private readonly uint _value;

    public Block(uint value) => _value = value;
    public Block(BlockType value) => _value = (uint)value;

    public static implicit operator Block(uint value) => new(value);
    public static explicit operator uint(Block block) => block._value;

    public bool Equals(Block other) => _value == other._value;
    public override bool Equals(object obj) => obj is Block other && Equals(other);
    public override int GetHashCode() => (int)_value;
    public override string ToString() => $"Block({_value})";

    public static bool operator ==(Block left, Block right) => left._value == right._value;
    public static bool operator !=(Block left, Block right) => left._value != right._value;

    public uint GetUintBlockType() => _value & BlockBits.TypeMask;
    public BlockType GetBlockType()
    {
        uint bt = _value & BlockBits.TypeMask;
        return (BlockType)bt;
    }
    public uint GetBlockState() => _value & BlockBits.StateMask >> BlockBits.StateShift;


}

public static class BlockBits
{
    public const uint TypeMask   = 0x0000_FFFF;

    public const int StateShift  = 16;
    public const uint StateMask  = 0x000F_0000;
}

public enum BlockType : uint
{
    ERROR = 114514,
    Void = 0,
    Air = 1,
    Grass = 2,
    Dirt = 3,
    Bedrock = 4,
    Stone = 5,
    Log = 6,
    Leaves = 7,
}

