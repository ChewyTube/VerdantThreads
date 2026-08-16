using System;

// 采收基因组：8 个多基因数量性状位点 × 每对 2 个等位基因 × 每个等位基因 2 bit = 32 bit，打包进一个 uint32。
// 与 Genome（7 位点 × 4 bit = 28 bit，孟德尔离散性状）编码完全同构：
//   位点 i 的等位基因 j 占 bits (i*4 + j*2) 起 2 bit；0=显性，1=隐性，2/3=预留。
// 位布局（位点 i 的等位基因 j 占 bits (i*4 + j*2) 起 2 bit）：
//   bit0-1   位点0 等位基因0  多基因数量性状：采收潜力 / 产量
//   bit2-3   位点0 等位基因1
//   ...      位点1-7 同上（共 8 位点 × 2 等位 × 2 bit = 32 bit，无预留位）
//
// 定位：8 个新基因共同控制采收潜力（采摘次数）与产量，属纯数量性状、不参与表型，
// 不进入 Genome（其 28 bit 已满），通过 HTT 载荷（"harvestGenome" 键）随 tile / 物品存续。
// 详见 docs/design/HTT.md 与 docs/design/HARVEST_SYSTEM.md。
public readonly struct HarvestGenome : IEquatable<HarvestGenome>
{
    public const int LocusCount = 8;       // 位点数
    public const int AllelesPerLocus = 2;  // 每位点等位基因数（一对：父方 + 母方）
    public const int BitsPerAllele = 2;    // 每个等位基因占位 bit 数

    // 随机源：本类独立的持久字段实例（与 Genome 同款写法，但不共享 Genome 的私有字段）
    private static readonly System.Random _random = new System.Random();

    public uint Value { get; }

    public HarvestGenome(uint value) => Value = value;

    // 读取某位点某等位基因（0=显性 1=隐性 2/3=预留）
    public int GetAllele(int locus, int alleleIndex)
    {
        int shift = locus * (AllelesPerLocus * BitsPerAllele) + alleleIndex * BitsPerAllele;
        return (int)((Value >> shift) & 0x3u);
    }

    // 返回替换某等位基因后的新 HarvestGenome（不修改自身）
    public HarvestGenome WithAllele(int locus, int alleleIndex, int value)
    {
        int shift = locus * (AllelesPerLocus * BitsPerAllele) + alleleIndex * BitsPerAllele;
        uint mask = 0x3u << shift;
        uint newValue = (Value & ~mask) | ((uint)(value & 0x3) << shift);
        return new HarvestGenome(newValue);
    }

    // 表型判定：任一等位基因为显性(0) → true（与 Genome.IsDominant 语义一致）
    public bool IsDominant(int locus) => GetAllele(locus, 0) == 0 || GetAllele(locus, 1) == 0;

    // 纯合显性判定：两个等位基因皆为显性(0) → true（数量性状加性效应计数用）
    public bool IsHomozygousDominant(int locus) => GetAllele(locus, 0) == 0 && GetAllele(locus, 1) == 0;

    // 随机基因：16 个等位基因各随机 0/1
    public static HarvestGenome Random()
    {
        uint value = 0;
        for (int i = 0; i < LocusCount * AllelesPerLocus; i++)
        {
            value |= (uint)_random.Next(2) << (i * BitsPerAllele);
        }
        return new HarvestGenome(value);
    }

    // 杂交：每个位点随机决定哪个亲本贡献等位基因 0、哪个贡献等位基因 1（每对一父一母）
    public HarvestGenome Crossover(HarvestGenome other)
    {
        uint value = 0;
        for (int locus = 0; locus < LocusCount; locus++)
        {
            // 抛硬币决定本亲本贡献等位基因 0 还是 1（另一亲本贡献对侧等位基因）
            bool swap = _random.Next(2) == 1;
            int allele0 = swap ? other.GetAllele(locus, 0) : GetAllele(locus, 0);
            int allele1 = swap ? GetAllele(locus, 1) : other.GetAllele(locus, 1);
            value |= (uint)((allele0 << 2) | allele1) << (locus * AllelesPerLocus * BitsPerAllele);
        }
        return new HarvestGenome(value);
    }

    // 突变：每个等位基因以 rate 概率随机重掷为 0/1
    public HarvestGenome Mutate(float rate)
    {
        uint value = Value;
        for (int i = 0; i < LocusCount * AllelesPerLocus; i++)
        {
            if (_random.NextDouble() < rate)
            {
                int shift = i * BitsPerAllele;
                value = (value & ~(0x3u << shift)) | ((uint)_random.Next(2) << shift);
            }
        }
        return new HarvestGenome(value);
    }

    public bool Equals(HarvestGenome other) => Value == other.Value;
    public override bool Equals(object obj) => obj is HarvestGenome other && Equals(other);
    public override int GetHashCode() => (int)Value;

    // 16 字符等位基因串（8 位点 × 2 等位基因各一字符，如 "0011..."），便于调试
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(LocusCount * AllelesPerLocus);
        for (int i = 0; i < LocusCount * AllelesPerLocus; i++)
        {
            sb.Append((char)('0' + ((Value >> (i * BitsPerAllele)) & 0x3u)));
        }
        return sb.ToString();
    }
}
