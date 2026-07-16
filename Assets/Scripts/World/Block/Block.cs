using System;
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

public readonly struct Blockv2 : IEquatable<Blockv2>
{
    private readonly uint _value;

    public Blockv2(uint value) => _value = value;

    public static implicit operator Blockv2(uint value) => new(value);
    public static explicit operator uint(Blockv2 block) => block._value;

    public bool Equals(Blockv2 other) => _value == other._value;
    public override bool Equals(object obj) => obj is Blockv2 other && Equals(other);
    public override int GetHashCode() => (int)_value;
    public override string ToString() => $"Block({_value})";

    public static bool operator ==(Blockv2 left, Blockv2 right) => left._value == right._value;
    public static bool operator !=(Blockv2 left, Blockv2 right) => left._value != right._value;

    public uint GetBlockType() => _value & BlockBits.TypeMask;
    public uint GetBlockState() => _value & BlockBits.StateMask >> BlockBits.StateShift;


}

public static class BlockBits
{
    public const uint TypeMask   = 0x0000_FFFF;

    public const int StateShift  = 16;
    public const uint StateMask  = 0x000F_0000;
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

