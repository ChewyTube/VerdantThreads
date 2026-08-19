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

    // 指定槽的堆叠数量；越界返回 0（供日志/调试反馈）
    public int GetSlotCount(int index)
    {
        if (index < 0 || index >= slots.Count) return 0;
        return slots[index].Count;
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

        // 非堆叠物品：直接新建槽（Count=1）。
        // 豌豆粒例外：可堆叠（分解产物，按表型分组合并，见 Phase 3）
        if ((item.PlaceableBlockType.HasValue && item.ItemType != ItemType.PeaSeed) || item.ItemType == ItemType.SeedBag)
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

    // 分解豌豆荚：消耗指定槽 1 个 PeaPod，产出 4~8 粒豌豆粒（携带母本基因组 + 采收基因载荷），
    // 优先存入种子袋，剩余落入背包。返回产出的种子粒数；槽无效/非豌豆荚返回 -1（未消费）。
    // 供 BlockInteraction（手持右键）与 BackpackWindow（背包窗右键）共用。
    public int DecomposePeaPod(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= slots.Count)
        {
            Debug.Log($"[分解] DecomposePeaPod 槽越界：slotIndex={slotIndex}，槽数={slots.Count}");
            return -1;
        }
        ItemInstance item = slots[slotIndex].Item;
        if (item == null || item.ItemType != ItemType.PeaPod)
        {
            Debug.Log($"[分解] DecomposePeaPod 槽内物品非豌豆荚：类型={item?.ItemType}，名称={item?.DisplayName}");
            return -1;
        }

        int taken = TakeFromSlot(slotIndex, 1);
        if (taken <= 0)
        {
            Debug.Log($"[分解] DecomposePeaPod 扣除失败：taken={taken}");
            return -1;
        }

        // 4~8 粒豌豆种子，带母本基因组 + 采收基因 HTT 载荷（如有）
        Genome genome = item.Genome ?? Genome.Random(); // 防御：无基因兜底随机
        int seedCount = UnityEngine.Random.Range(4, 9); // [4, 8]
        // AddPeaSeeds 返回落入背包的粒数 → 种子袋粒数 = 总粒数 - 背包粒数
        LastBaggedSeedCount = seedCount - AddPeaSeeds(genome, seedCount, item.GetHarvestGenome());
        Debug.Log($"[分解] DecomposePeaPod 成功：消耗 1 个豌豆荚，产出 {seedCount} 粒（种子袋 {LastBaggedSeedCount} 粒）");
        return seedCount;
    }

    // 最近一次 DecomposePeaPod 中存入种子袋的粒数（供调用方日志反馈去向）
    public int LastBaggedSeedCount { get; private set; }

    // 从选中槽扣除 amount 个物品（用于分解/使用等右键消费）；
    // 槽空则自动移除并调整选中索引。返回实际扣除数。
    public int TakeFromSelected(int amount) => TakeFromSlot(SelectedIndex, amount);

    // 从指定槽扣除 amount 个物品（背包窗右键分解等按行操作）；
    // 槽空则自动移除并调整选中索引。返回实际扣除数。
    public int TakeFromSlot(int index, int amount)
    {
        if (index < 0 || index >= slots.Count) return 0;
        StackSlot slot = slots[index];
        int taken = slot.Take(amount);
        if (slot.Count <= 0)
        {
            slots.RemoveAt(index);
            // 移除的槽在选中槽之前 → 选中项下移一格；移除即选中槽 → 后继顶位；之后 → 不变
            if (index < SelectedIndex) SelectedIndex--;
            else if (SelectedIndex >= slots.Count) SelectedIndex = slots.Count - 1;
        }
        return taken;
    }

    // 添加豌豆种子：优先存入已有种子袋，剩余再以 PeaSeed 物品形式落入背包。
    // 所有种子携带相同 genome；harvestGenome 非空时写入 HTT 载荷（种植继承采收潜力）。
    // 返回落入背包的粒数（种子袋部分 = count - 返回值），便于调用方反馈种子去向。
    public int AddPeaSeeds(Genome genome, int count, HarvestGenome? harvestGenome = null)
    {
        int remaining = count;

        // Step 1: 尝试存入已有种子袋（按容量逐个填充）
        foreach (StackSlot slot in slots)
        {
            if (remaining <= 0) break;
            if (slot.Item.ItemType == ItemType.SeedBag && slot.Item.SeedBag != null)
            {
                int space = Constants.SEED_BAG_CAPACITY - slot.Item.SeedBag.TotalCount;
                if (space > 0)
                {
                    int addCount = Mathf.Min(space, remaining);
                    slot.Item.SeedBag.TryAdd(genome, addCount);
                    remaining -= addCount;
                }
            }
        }

        // Step 2: 剩余种子以豌豆粒形式落入背包（可堆叠、可种植，堆叠按表型合并）
        if (remaining > 0)
        {
            var seedItem = new ItemInstance(ItemType.PeaSeed, "豌豆粒", genome, BlockType.PeaStem);
            // 子集表型标签 {0,1}（子叶色+种子形状）与豌豆粒图标 GetItemSeedCell 一致，作为堆叠分组依据
            seedItem.PhenotypeTags.Clear();
            seedItem.PhenotypeTags.AddRange(PeaTraits.GetPhenotypeTags(genome, 0, 1));
            if (harvestGenome.HasValue)
            {
                seedItem.Payload = new HTTCompound();
                seedItem.Payload.SetInt("harvestGenome", (int)harvestGenome.Value.Value);
            }
            AddItem(seedItem, remaining);
        }

        // 返回落入背包的粒数（种子袋部分 = count - 返回值）；全部入袋时为 0
        return remaining;
    }

    // 存档加载专用：清空并用读回数据重建槽列表（SelectedIndex 归 0）
    public void ReplaceAll(List<StackSlot> newSlots)
    {
        slots.Clear();
        if (newSlots != null) slots.AddRange(newSlots);
        SelectedIndex = 0;
    }
}
