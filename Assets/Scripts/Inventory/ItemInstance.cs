// 物品实例：一种可持有/使用的"东西"的最小数据载体。
// 批2 将在此扩展 genome（基因）/ 标签等字段，当前仅方块类型 + 中文显示名。
public class ItemInstance
{
    // 对应方块类型（豌豆种子为 PeaStem，生长阶段 0=苗）
    public BlockType ItemType { get; }

    // 中文显示名（热栏 / 背包窗 / 左下角信息共用）
    public string DisplayName { get; }

    public ItemInstance(BlockType itemType, string displayName)
    {
        ItemType = itemType;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;
}
