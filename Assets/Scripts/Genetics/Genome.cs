using System;

// 豌豆基因组：7 个孟德尔性状位点 × 每对 2 个等位基因 × 每个等位基因 2 bit = 28 bit，打包进一个 uint32。
//
// 位布局（位点 i 的等位基因 j 占 bits (i*4 + j*2) 起 2 bit）：
//   bit0-1   位点0 等位基因0  种子形状（圆粒/皱粒）
//   bit2-3   位点0 等位基因1
//   bit4-5   位点1 等位基因0  子叶颜色（黄色/绿色）
//   bit6-7   位点1 等位基因1
//   bit8-9   位点2 等位基因0  花色（紫色/白色）
//   bit10-11 位点2 等位基因1
//   bit12-13 位点3 等位基因0  豆荚形状（饱满/皱缩）
//   bit14-15 位点3 等位基因1
//   bit16-17 位点4 等位基因0  豆荚颜色（绿色/黄色）
//   bit18-19 位点4 等位基因1
//   bit20-21 位点5 等位基因0  花位置（腋生/顶生）
//   bit22-23 位点5 等位基因1
//   bit24-25 位点6 等位基因0  茎高度（高茎/矮茎）
//   bit26-27 位点6 等位基因1
//   bit28-31 预留
//
// 等位基因编码：0=显性，1=隐性，2/3=预留（未来突变/稀有等位基因）。
public readonly struct Genome : IEquatable<Genome>
{
    public const int LocusCount = 7;       // 位点数
    public const int AllelesPerLocus = 2;  // 每位点等位基因数（一对：父方 + 母方）
    public const int BitsPerAllele = 2;    // 每个等位基因占位 bit 数

    // 随机源：持久字段实例（不要每次 new，避免同种子重复序列）
    private static readonly System.Random _random = new System.Random();

    public uint Value { get; }

    public Genome(uint value) => Value = value;

    // 读取某位点某等位基因（0=显性 1=隐性 2/3=预留）
    public int GetAllele(int locus, int alleleIndex)
    {
        int shift = locus * (AllelesPerLocus * BitsPerAllele) + alleleIndex * BitsPerAllele;
        return (int)((Value >> shift) & 0x3u);
    }

    // 返回替换某等位基因后的新 Genome（不修改自身）
    public Genome WithAllele(int locus, int alleleIndex, int value)
    {
        int shift = locus * (AllelesPerLocus * BitsPerAllele) + alleleIndex * BitsPerAllele;
        uint mask = 0x3u << shift;
        uint newValue = (Value & ~mask) | ((uint)(value & 0x3) << shift);
        return new Genome(newValue);
    }

    // 表型判定：任一等位基因为显性(0) → true（显性表型）；均为隐性(1) → false（隐性表型）
    public bool IsDominant(int locus) => GetAllele(locus, 0) == 0 || GetAllele(locus, 1) == 0;

    // 随机基因：14 个等位基因各随机 0/1
    public static Genome Random()
    {
        uint value = 0;
        for (int i = 0; i < LocusCount * AllelesPerLocus; i++)
        {
            value |= (uint)_random.Next(2) << (i * BitsPerAllele);
        }
        return new Genome(value);
    }

    // 杂交：每个位点随机决定哪个亲本贡献等位基因 0、哪个贡献等位基因 1（每对一父一母）
    public Genome Crossover(Genome other)
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
        return new Genome(value);
    }

    // 突变：每个等位基因以 rate 概率随机重掷为 0/1
    public Genome Mutate(float rate)
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
        return new Genome(value);
    }

    public bool Equals(Genome other) => Value == other.Value;
    public override bool Equals(object obj) => obj is Genome other && Equals(other);
    public override int GetHashCode() => (int)Value;

    // 14 字符等位基因串（每位点每等位基因各一字符，如 "0011..."），便于调试
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
