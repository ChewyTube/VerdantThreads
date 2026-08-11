using System.Collections.Generic;
using UnityEngine;

// 背包：非堆叠物品列表 + 选中索引（选择状态的唯一权威）。
// 热栏、背包窗、放置逻辑全部从这里读当前选中，避免状态分裂。
// 普通 class（非 MonoBehaviour）：由 World 创建，经 Init 注入给 UI / 交互组件。
public class Backpack
{
    private readonly List<ItemInstance> items = new List<ItemInstance>();

    // 背包窗开关状态：BackpackWindow 切换，BlockInteraction 据此暂停世界操作
    public bool BackpackOpen { get; set; }

    // 当前选中索引（选择状态唯一权威，热栏 / 背包窗高亮均读此值）
    public int SelectedIndex { get; private set; }

    public int Count => items.Count;

    // 当前选中的物品；越界 / 空背包返回 null
    public ItemInstance CurrentSelected
    {
        get
        {
            if (SelectedIndex < 0 || SelectedIndex >= items.Count) return null;
            return items[SelectedIndex];
        }
    }

    // 按索引访问物品；越界返回 null（热栏只显示前 N 个时使用）
    public ItemInstance this[int index]
    {
        get
        {
            if (index < 0 || index >= items.Count) return null;
            return items[index];
        }
    }

    public Backpack()
    {
        // 初始装入当前全部可放置方块（顺序与 BlockInteraction 旧默认列表一致；数量无限，不做拾取/计数）
        items.Add(new ItemInstance(BlockType.Grass, "草方块"));
        items.Add(new ItemInstance(BlockType.Dirt, "泥土"));
        items.Add(new ItemInstance(BlockType.Stone, "石头"));
        items.Add(new ItemInstance(BlockType.Log, "原木"));
        items.Add(new ItemInstance(BlockType.Leaves, "树叶"));
        items.Add(new ItemInstance(BlockType.Bedrock, "基岩"));
        items.Add(new ItemInstance(BlockType.PeaStem, "豌豆种子")); // 豌豆种子对应 PeaStem（生长阶段 0=苗）

        // 默认选中索引 2（石头），保留原 defaultSelectedIndex=2 的默认选中行为
        SelectedIndex = 2;
    }

    // 选中指定索引（Clamp 到有效范围；空背包忽略）
    public void Select(int index)
    {
        if (items.Count == 0) return;
        SelectedIndex = Mathf.Clamp(index, 0, items.Count - 1);
    }
}
