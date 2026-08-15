using System.Collections.Generic;
using UnityEngine;

// 背包：堆叠槽列表 + 选中索引（选择状态的唯一权威）。
// 热栏、背包窗、放置逻辑全部从这里读当前选中，避免状态分裂。
// 普通 class（非 MonoBehaviour）：由 World 创建，经 Init 注入给 UI / 交互组件。
public class Backpack
{
    private readonly List<StackSlot> slots = new List<StackSlot>();

    // 背包窗开关状态：BackpackWindow 切换，BlockInteraction 据此暂停世界操作
    public bool BackpackOpen { get; set; }

    // 种子袋内容子面板开关 + 打开时对应的槽索引（BackpackWindow 右键种子袋行时设置）
    public bool IsSeedBagOpen { get; set; }
    public int OpenSeedBagSlotIndex { get; set; }

    // 当前选中索引（选择状态唯一权威，热栏 / 背包窗高亮均读此值）
    public int SelectedIndex { get; private set; }

    // 槽数量（非堆叠物品一格一槽，可堆叠物品按表型合并成槽）
    public int Count => slots.Count;

    // 当前选中的物品；越界 / 空背包返回 null
    public ItemInstance CurrentSelected
    {
        get
        {
            if (SelectedIndex < 0 || SelectedIndex >= slots.Count) return null;
            return slots[SelectedIndex].Item;
        }
    }

    // 按索引访问物品（槽模板）；越界返回 null（热栏只显示前 N 个时使用）
    public ItemInstance this[int index]
    {
        get
        {
            if (index < 0 || index >= slots.Count) return null;
            return slots[index].Item;
        }
    }

    public Backpack()
    {
        // 初始装入当前全部可放置方块（顺序与 BlockInteraction 旧默认列表一致；数量无限，不做拾取/计数）
        slots.Add(new StackSlot(new ItemInstance(ItemType.GrassBlock, "草方块", BlockType.Grass)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.DirtBlock, "泥土", BlockType.Dirt)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.StoneBlock, "石头", BlockType.Stone)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.LogBlock, "原木", BlockType.Log)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.LeavesBlock, "树叶", BlockType.Leaves)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.BedrockBlock, "基岩", BlockType.Bedrock)));
        slots.Add(new StackSlot(new ItemInstance(ItemType.PeaSeedBlock, "豌豆种子", BlockType.PeaStem))); // 豌豆种子对应 PeaStem（生长阶段 0=苗）
        slots.Add(new StackSlot(new ItemInstance(ItemType.SeedBag, "种子袋"))); // 测试用：默认给一个空种子袋，后续可移除

        // 默认选中索引 2（石头），保留原 defaultSelectedIndex=2 的默认选中行为
        SelectedIndex = 2;
    }

    // 选中指定索引（Clamp 到有效范围；空背包忽略）
    public void Select(int index)
    {
        if (slots.Count == 0) return;
        SelectedIndex = Mathf.Clamp(index, 0, slots.Count - 1);
    }

    // 槽内堆叠数量（越界 / 空槽返回 0）
    public int GetStackCount(int index)
    {
        if (index < 0 || index >= slots.Count) return 0;
        return slots[index].Count;
    }

    // 槽内基因型分布（越界返回 null；非堆叠物品槽为空字典）
    public IReadOnlyDictionary<Genome, int> GetGenotypeCounts(int index)
    {
        if (index < 0 || index >= slots.Count) return null;
        return slots[index].GenotypeCounts;
    }

    // 加入物品：非堆叠物品（可放置方块 / 种子袋）直接新建槽（Count=1）；
    // 可堆叠物品（豆荚/豌豆）遍历现有槽找同表型未满槽合并，剩余再开新槽
    public void AddItem(ItemInstance item, int count = 1)
    {
        if (item == null || count <= 0) return;

        // 非堆叠物品：直接新建槽（Count=1）
        if (item.PlaceableBlockType.HasValue || item.ItemType == ItemType.SeedBag)
        {
            slots.Add(new StackSlot(item));
            return;
        }

        // 可堆叠物品：先尝试合并到已有同表型未满槽，剩余再开新槽
        int remaining = count;
        foreach (StackSlot slot in slots)
        {
            if (remaining <= 0) break;
            if (!slot.CanMergeWith(item)) continue;
            remaining = slot.Merge(item, remaining);
        }
        if (remaining > 0)
        {
            slots.Add(new StackSlot(item, remaining));
        }
    }

    // 存档加载专用：清空并用读回数据重建槽列表（SelectedIndex 归 0）
    public void ReplaceAll(List<StackSlot> newSlots)
    {
        slots.Clear();
        if (newSlots != null) slots.AddRange(newSlots);
        SelectedIndex = 0;
    }
}
