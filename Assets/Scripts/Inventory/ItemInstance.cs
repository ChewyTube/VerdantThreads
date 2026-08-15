using System.Collections.Generic;

// 物品实例：一种可持有/使用的"东西"的最小数据载体。
// 批2 扩展点已填上 genome（基因）字段：豌豆物品携带基因，非豌豆物品为 null。
// 携带基因物品在构造时自动填表型标签（PhenotypeTags），作为堆叠分组依据。
public class ItemInstance
{
    // 物品类型（独立于 BlockType 枚举；可放置方块物品通过 PlaceableBlockType 关联对应 BlockType）
    public ItemType ItemType { get; }

    // 可放置的对应方块类型；非方块物品（豆荚/种子袋等）为 null
    public BlockType? PlaceableBlockType { get; }

    // 中文显示名（热栏 / 背包窗 / 左下角信息共用）
    public string DisplayName { get; }

    // 基因（豌豆物品携带；非豌豆物品为 null）。Genome 与 ItemInstance 同在全局命名空间，无需 using。
    public Genome? Genome { get; }

    // 表型标签（堆叠分组依据；携带基因物品在构造时按 7 位点自动填充）
    public List<string> PhenotypeTags { get; }

    // 基因型标签（Phase 2/批2 扩展点：基因位点描述，初始为空列表）
    public List<string> GenotypeTags { get; }

    // 种子袋内容（仅 ItemType.SeedBag 物品持有；其余为 null）
    public SeedBag SeedBag { get; }

    // 可放置方块物品构造：绑定对应 BlockType
    public ItemInstance(ItemType itemType, string displayName, BlockType placeableBlockType)
    {
        ItemType = itemType;
        DisplayName = displayName;
        PlaceableBlockType = placeableBlockType;
        Genome = null;
        PhenotypeTags = new List<string>();
        GenotypeTags = new List<string>();
    }

    // 非方块物品构造（豆荚/种子袋等，不可放置）
    public ItemInstance(ItemType itemType, string displayName)
    {
        ItemType = itemType;
        DisplayName = displayName;
        PlaceableBlockType = null;
        Genome = null;
        PhenotypeTags = new List<string>();
        GenotypeTags = new List<string>();
        if (itemType == ItemType.SeedBag) SeedBag = new SeedBag(); // 种子袋物品内部持有容器数据
    }

    // 携带基因的构造重载（豌豆荚等，非方块物品）：自动填表型标签（堆叠分组依据）
    public ItemInstance(ItemType itemType, string displayName, Genome genome)
    {
        ItemType = itemType;
        DisplayName = displayName;
        PlaceableBlockType = null;
        Genome = genome;
        PhenotypeTags = new List<string>();
        PhenotypeTags.AddRange(PeaTraits.GetPhenotypeTags(genome));
        GenotypeTags = new List<string>();
    }

    public override string ToString() => DisplayName;
}
