using System;

// 豌豆采收计算器：8 新基因（HarvestGenome）多基因数量性状公式集中，便于调参。
// 见 docs/design/HARVEST_SYSTEM.md §5.2：
//   采摘次数上限（指数模型）= min(2^(1+k), 64)，k = 纯合显性位点数（每位点概率 1/4，期望 2）
//   产量（累加模型）：阶段 4 豌豆荚 = 12+2k；阶段 3 青嫩豆荚 = 3+k
// 公式常量集中在 Constants「豌豆采收」段；8 新基因为纯数量性状，不参与表型/堆叠分组/渲染。
public static class PeaHarvestCalculator
{
    // 统计 8 个采收基因位点的纯合显性（两等位皆 0）数量 k
    public static int CountHomozygousDominant(HarvestGenome genome)
    {
        int k = 0;
        for (int i = 0; i < HarvestGenome.LocusCount; i++)
        {
            if (genome.IsHomozygousDominant(i)) k++;
        }
        return k;
    }

    // 采摘次数上限 = min(2^(1+k), CAP)；k 最大 8 → 1<<9 = 512，无溢出
    public static int GetHarvestLimit(HarvestGenome genome)
    {
        int k = CountHomozygousDominant(genome);
        return Math.Min(1 << (Constants.HARVEST_LIMIT_BASE_EXPONENT + k), Constants.HARVEST_LIMIT_CAP);
    }

    // 单次产量：阶段 ≥4 → 结果期豌豆荚；否则（阶段 3 开花期）→ 青嫩豆荚
    public static int GetYield(HarvestGenome genome, int stage)
    {
        int k = CountHomozygousDominant(genome);
        return stage >= 4
            ? Constants.YIELD_BASE_STAGE4 + k * Constants.YIELD_PER_DOMINANT_STAGE4
            : Constants.YIELD_BASE_STAGE3 + k * Constants.YIELD_PER_DOMINANT_STAGE3;
    }
}
