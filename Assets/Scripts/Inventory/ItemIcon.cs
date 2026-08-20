using UnityEngine;

// 物品图标 UV 提取（BackpackWindow / HotbarWindow / DroppedItem 三处共用，原 CalcIconUVRect 去重）。
// 图集 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），row 从图集底部起算。
public static class ItemIcon
{
    // 豌豆种子/豌豆粒特判（两者 PlaceableBlockType 均为 PeaStem，而 PeaStem 无 BlockUVMap 条目，
    // 走 Fallback 会显示错误图标）；其余可放置方块取 Up 面 cell；非方块物品（豆荚/种子袋等）按类型+基因选图集 cell。
    public static Rect GetUVRect(ItemInstance item)
    {
        Vector2Int cell;
        if (item.ItemType == ItemType.PeaSeedBlock || item.ItemType == ItemType.PeaSeed)
        {
            // 豌豆种子 → 最小苗图标；豌豆粒 → 按基因选表型图标（子叶色+种子形状），无基因兜底最小苗。
            // 均不随可放置方块走 BlockUVMap（PeaStem 无条目，走 Fallback 会显示错误图标）
            cell = item.ItemType == ItemType.PeaSeed && item.Genome.HasValue
                ? PeaTextures.GetItemSeedCell(item.Genome.Value)
                : PeaTextures.CellByStage[0];
        }
        else if (item.PlaceableBlockType.HasValue)
        {
            // 可放置方块 → 从 BlockUVMap 取 Up 面 cell
            cell = BlockUVMap.GetUV(item.PlaceableBlockType.Value, Direction.Up);
        }
        else
        {
            // 非方块物品 → 按类型选图集 cell（豌豆荚按基因选表型图标；青嫩豆荚暂用占位）
            cell = item.ItemType switch
            {
                ItemType.PeaPod when item.Genome.HasValue => PeaTextures.GetItemPodCell(item.Genome.Value),
                ItemType.PeaPod => new Vector2Int(0, 0),        // 无基因兜底（占位）
                ItemType.SeedBag => PeaTextures.ItemSeedBagCell,
                ItemType.GreenBeanPod => PeaTextures.ItemGreenBeanPodCell,
                _ => new Vector2Int(0, 0),
            };
        }
        // Rect 不支持 / 运算符，逐分量除以 768 得到归一化 UV
        Rect uv = new Rect(cell.x * 24 + 4, cell.y * 24 + 4, 16, 16);
        return new Rect(uv.x / 768f, uv.y / 768f, uv.width / 768f, uv.height / 768f);
    }
}