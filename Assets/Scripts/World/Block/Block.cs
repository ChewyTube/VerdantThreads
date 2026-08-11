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
    public uint GetBlockState() => (_value & BlockBits.StateMask) >> BlockBits.StateShift;

    // 返回替换生长阶段后的新 Block（保留类型与其他状态位）
    public Block WithStage(uint stage) => new Block((_value & ~(BlockBits.StageMask << BlockBits.StateShift)) | ((stage & BlockBits.StageMask) << BlockBits.StateShift));
}

// 块值位布局：
//   bit0-15  类型（TypeMask）
//   bit16-17 生长阶段（0=最小苗 1=苗 2=两格高植株 3=开花结果，豌豆 PeaStem 用；PeaPlantTop 顶部格无阶段）
//   bit18-31 通用渲染状态预留（14 bit；原 7 位孟德尔性状预留已废除，基因数据移入 Genome）
public static class BlockBits
{
    public const uint TypeMask   = 0x0000_FFFF; // 类型位（bit0-15）

    public const int StateShift  = 16;          // 状态位起始位（bit16）
    public const uint StateMask  = 0xFFFF_0000; // 状态位（bit16 起 16 位）
    public const uint StageMask  = 0x3;         // 生长阶段（状态位低 2 位，bit16-17）
    // bit18-31（14 bit）为通用渲染状态预留（原 7 位孟德尔性状预留已废除，基因数据移入 Genome）
}

public enum BlockType : uint
{
    Void = 0,
    Air = 1,
    Grass = 2,
    Dirt = 3,
    Bedrock = 4,
    Stone = 5,
    Log = 6,
    Leaves = 7,
    PeaStem = 8,
    PeaPlantTop = 9, // 豌豆两格高植株的顶部格（MC tall plant 式：可被射线命中/存档/破坏联动；无 tile）
}

