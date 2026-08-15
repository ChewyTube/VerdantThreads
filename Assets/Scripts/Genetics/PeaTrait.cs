// 豌豆性状定义表：索引与 Genome 位点顺序一致（0-6，即孟德尔豌豆实验的 7 个性状）。
public static class PeaTraits
{
    // 单个性状条目：中文名、显性/隐性表型、检索关键词（中文 + 英文）
    public readonly struct PeaTrait
    {
        public readonly string Name;              // 中文名，如 "种子形状"
        public readonly string DominantPhenotype; // 显性表型，如 "圆粒"
        public readonly string RecessivePhenotype;// 隐性表型，如 "皱粒"
        public readonly string[] Keywords;        // 检索关键词（中文 + 英文），如 ["圆粒","round"]

        public PeaTrait(string name, string dominantPhenotype, string recessivePhenotype, string[] keywords)
        {
            Name = name;
            DominantPhenotype = dominantPhenotype;
            RecessivePhenotype = recessivePhenotype;
            Keywords = keywords;
        }
    }

    // 7 项，顺序 = 位点顺序（0 种子形状 → 6 茎高度）
    public static readonly PeaTrait[] All = new PeaTrait[]
    {
        new PeaTrait("种子形状", "圆粒", "皱粒",
            new string[] { "种子形状", "圆粒", "皱粒", "seed", "round", "wrinkled" }),
        new PeaTrait("子叶颜色", "黄色", "绿色",
            new string[] { "子叶颜色", "黄色", "绿色", "cotyledon", "yellow", "green" }),
        new PeaTrait("花色", "紫色", "白色",
            new string[] { "花色", "紫色", "白色", "flower", "purple", "white" }),
        new PeaTrait("豆荚形状", "饱满", "皱缩",
            new string[] { "豆荚形状", "饱满", "皱缩", "pod", "full", "constricted" }),
        new PeaTrait("豆荚颜色", "绿色", "黄色",
            new string[] { "豆荚颜色", "绿色", "黄色", "pod", "green", "yellow" }),
        new PeaTrait("花位置", "腋生", "顶生",
            new string[] { "花位置", "腋生", "顶生", "position", "axial", "terminal" }),
        new PeaTrait("茎高度", "高茎", "矮茎",
            new string[] { "茎高度", "高茎", "矮茎", "stem", "tall", "dwarf" }),
    };

    // 获取某位点性状；越界返回默认（空名/空数组，调用方需自行判空）
    public static PeaTrait Get(int locus)
    {
        if (locus < 0 || locus >= All.Length) return default;
        return All[locus];
    }

    // 计算表型标签数组：7 位点各取显性/隐性表型名（堆叠分组依据）
    public static string[] GetPhenotypeTags(Genome genome)
    {
        var tags = new string[All.Length];
        for (int i = 0; i < All.Length; i++)
            tags[i] = genome.IsDominant(i) ? All[i].DominantPhenotype : All[i].RecessivePhenotype;
        return tags;
    }
}
