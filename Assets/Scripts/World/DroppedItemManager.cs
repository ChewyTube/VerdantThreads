using System.Collections.Generic;
using UnityEngine;

// 掉落物管理器（普通 class，由 World 创建）：持有全场景掉落物列表，
// 统一驱动物理 / billboard / 拾取 / 消失 / 数量上限（64，超出丢弃最老的防刷爆）。
public class DroppedItemManager
{
    private readonly List<DroppedItem> items = new List<DroppedItem>();
    private readonly System.Func<Vector3Int, bool> isSolid; // 体素碰撞查询（World 注入）

    public DroppedItemManager(System.Func<Vector3Int, bool> isSolid)
    {
        this.isSolid = isSolid;
    }

    // 生成掉落物实体（Q 扔出 / 未来其他来源）
    public void Spawn(ItemInstance item, int count, Vector3 position, Vector3 velocity)
    {
        if (item == null || count <= 0) return;

        // 数量上限：超出丢弃最老的（防刷爆）
        if (items.Count >= Constants.DROPPED_ITEM_CAP)
        {
            DroppedItem oldest = items[0];
            items.RemoveAt(0);
            if (oldest != null) Object.Destroy(oldest.gameObject);
        }

        GameObject go = new GameObject("DroppedItem");
        DroppedItem di = go.AddComponent<DroppedItem>();
        di.Init(item, count, position, velocity, isSolid);
        items.Add(di);
    }

    // 每帧驱动：物理 / billboard / 拾取 / 消失
    public void Tick(Vector3 playerEyePos, Backpack backpack)
    {
        // 玩家身体 AABB 中心（与 PlayerController 同约定：眼睛下方 EYE_HEIGHT - HALF_HEIGHT）
        Vector3 playerCenter = playerEyePos - Vector3.up * (Constants.PLAYER_EYE_HEIGHT - Constants.PLAYER_HALF_HEIGHT);

        // 掉落物合并：同物品（表型一致）且相邻 → 数量合并（不超 STACK_LIMIT）
        MergeNearby();

        for (int i = items.Count - 1; i >= 0; i--)
        {
            DroppedItem di = items[i];
            if (di == null) { items.RemoveAt(i); continue; }

            // 消失：出生 5 分钟后
            if (di.Age >= Constants.DROPPED_ITEM_LIFETIME)
            {
                Object.Destroy(di.gameObject);
                items.RemoveAt(i);
                continue;
            }

            // 物理 + billboard
            di.TickPhysics();
            di.TickBillboard();

            // 拾取：与玩家距离 < 拾取半径 → 尝试放入背包（放不下的剩余保留在实体上）
            if (Vector3.Distance(di.transform.position, playerCenter) < Constants.DROPPED_ITEM_PICKUP_RADIUS)
            {
                int remaining = backpack.AddItem(di.Item, di.Count);
                if (remaining <= 0)
                {
                    Object.Destroy(di.gameObject);
                    items.RemoveAt(i);
                }
                else
                {
                    di.Count = remaining; // 部分拾取：实体保留剩余
                }
            }
        }
    }

    // 清空全部掉落物（退出时）
    public void Clear()
    {
        foreach (DroppedItem di in items)
            if (di != null) Object.Destroy(di.gameObject);
        items.Clear();
    }

    // 掉落物合并：同物品（ItemType + 表型一致）且距离 < 0.5 时合并数量（不超 STACK_LIMIT）。
    // 仅可堆叠物品参与（方块/种子袋不可堆叠，Count 恒 1）。
    private void MergeNearby()
    {
        for (int i = 0; i < items.Count; i++)
        {
            DroppedItem a = items[i];
            if (a == null) continue;
            for (int j = i + 1; j < items.Count; j++)
            {
                DroppedItem b = items[j];
                if (b == null) continue;
                if (!CanMerge(a, b)) continue;
                if (Vector3.Distance(a.transform.position, b.transform.position) > 0.5f) continue;

                int space = Constants.STACK_LIMIT - a.Count;
                if (space <= 0) break;
                int move = Mathf.Min(space, b.Count);
                a.Count += move;
                b.Count -= move;
                if (b.Count <= 0)
                {
                    Object.Destroy(b.gameObject);
                    items.RemoveAt(j);
                    j--;
                }
            }
        }
    }

    // 两个掉落物能否合并：均可堆叠 + 同 ItemType + 同表型标签（与 StackSlot.CanMergeWith 同语义）
    private static bool CanMerge(DroppedItem a, DroppedItem b)
    {
        if (!a.Item.IsStackable || !b.Item.IsStackable) return false;
        if (a.Item.ItemType != b.Item.ItemType) return false;
        if (a.Item.PhenotypeTags.Count != b.Item.PhenotypeTags.Count) return false;
        for (int i = 0; i < a.Item.PhenotypeTags.Count; i++)
            if (a.Item.PhenotypeTags[i] != b.Item.PhenotypeTags[i]) return false;
        return true;
    }
}