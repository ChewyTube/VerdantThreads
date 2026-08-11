// 物品实例：一种可持有/使用的"东西"的最小数据载体。
// 批2 扩展点已填上 genome（基因）字段：豌豆物品携带基因，非豌豆物品为 null。
public class ItemInstance
{
    // 对应方块类型（豌豆种子为 PeaStem，生长阶段 0=苗）
    public BlockType ItemType { get; }

    // 中文显示名（热栏 / 背包窗 / 左下角信息共用）
    public string DisplayName { get; }

    // 基因（豌豆物品携带；非豌豆物品为 null）。Genome 与 ItemInstance 同在全局命名空间，无需 using。
    public Genome? Genome { get; }

    public ItemInstance(BlockType itemType, string displayName)
    {
        ItemType = itemType;
        DisplayName = displayName;
        Genome = null;
    }

    // 携带基因的构造重载（豌豆种子等）
    public ItemInstance(BlockType itemType, string displayName, Genome genome)
    {
        ItemType = itemType;
        DisplayName = displayName;
        Genome = genome;
    }

    public override string ToString() => DisplayName;
}
