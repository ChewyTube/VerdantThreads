// 物品类型枚举：独立于 BlockType，区分可放置方块物品与非方块物品（豆荚/种子袋等）。
// 可放置方块物品通过 PlaceableBlockType 关联对应 BlockType。
public enum ItemType
{
    GrassBlock,
    DirtBlock,
    StoneBlock,
    LogBlock,
    LeavesBlock,
    BedrockBlock,
    PeaSeedBlock,    // 豌豆种子（背包第7格，可放置为 PeaStem）
    PeaSeed,         // 豌豆粒（分解后，预留 Phase 3）
    GreenBeanPod,    // 青嫩豆荚（阶段3，不可放置）
    PeaPod,          // 豌豆荚（阶段4，不可放置）
    SeedBag,         // 种子袋（容器物品）
}
