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

    // 返回替换高茎标志后的新 Block（保留类型与其他状态位；仿 WithStage，只改状态位第 3 位 = 块值 bit19）
    public Block WithTall(bool tall) => new Block((_value & ~(BlockBits.TallMask << BlockBits.StateShift)) | ((tall ? BlockBits.TallMask : 0) << BlockBits.StateShift));
    // 高茎标志（状态位第 3 位 = 块值 bit19；仅高茎豌豆中部/顶部格使用，PeaStem 不用）
    public bool IsTallPlant() => (GetBlockState() & BlockBits.TallMask) != 0;

    // 返回替换采收剩余次数后的新 Block（保留类型与其他状态位；仿 WithStage，只改状态位第 4-10 位 = 块值 bit20-26）
    public Block WithHarvests(int harvests) => new Block((_value & ~(BlockBits.HarvestMask << BlockBits.StateShift)) | ((uint)(harvests & BlockBits.HarvestMask) << BlockBits.StateShift));
    // 采收剩余次数（状态位第 4-10 位 = 块值 bit20-26；0=未初始化，1-64=剩余次数；仅豌豆底部格 PeaStem 使用）
    public int GetHarvests() => (int)(GetBlockState() & BlockBits.HarvestMask);
}

// 块值位布局：
//   bit0-15  类型（TypeMask）
//   bit16-18 生长阶段（0=最小苗 1=苗 2=植株 3=开花 4=结果，豌豆 PeaStem 用；PeaPlantMiddle/PeaPlantTop 中部顶部格也用）
//   bit19    高茎标志（TallMask；仅高茎豌豆中部/顶部格用，PeaStem 不用）
//   bit20-26 采收剩余次数（HarvestMask；仅豌豆底部格 PeaStem 用，0=未初始化，1-64=剩余次数）
//   bit27-31 通用渲染状态预留（5 bit；原 7 位孟德尔性状预留已废除，基因数据移入 Genome）
public static class BlockBits
{
    public const uint TypeMask   = 0x0000_FFFF; // 类型位（bit0-15）

    public const int StateShift  = 16;          // 状态位起始位（bit16）
    public const uint StateMask  = 0xFFFF_0000; // 状态位（bit16 起 16 位）
    public const uint StageMask  = 0x7;         // 生长阶段（状态位低 3 位，bit16-18）
    public const uint TallMask   = 0x8;         // 高茎标志（状态位第 3 位 = 块值 bit19；仅高茎豌豆中部/顶部格用，PeaStem 不用）
    public const uint HarvestMask = 0x7F;       // 采收剩余次数（状态位第 4-10 位 = 块值 bit20-26；仅豌豆底部格用，0=未初始化，1-64=剩余次数）
    // bit27-31（5 bit）为通用渲染状态预留（原 7 位孟德尔性状预留已废除，基因数据移入 Genome）
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
    PeaPlantTop = 9,   // 豌豆两格高植株的顶部格（MC tall plant 式：可被射线命中/存档/破坏联动；无 tile）
    PeaPlantMiddle = 10, // 高茎豌豆中部格（MC tall plant 式；无 tile，状态位带阶段 + 高茎标志）
    PeaWithered = 11,  // 豌豆枯萎植株（采收次数耗尽：矮茎 2 格 / 高茎 3 格全变枯萎；玩家左键破坏去除，无掉落；贴图列 8 用户已绘制）
}

