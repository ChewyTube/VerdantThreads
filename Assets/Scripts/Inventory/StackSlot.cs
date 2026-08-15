using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 背包单槽：物品模板 + 堆叠数量 + 内部分基因型计数。
// 非堆叠物品（方块类）Count 恒为 1；可堆叠物品（豆荚/豌豆）Count 1~STACK_LIMIT。
// 同槽物品的 ItemType 与 PhenotypeTags 必须一致（堆叠依据）。
public class StackSlot
{
    public ItemInstance Item { get; }              // 模板物品（表型、类型、基因样本）
    public int Count { get; private set; }         // 当前数量（1~STACK_LIMIT）
    private readonly Dictionary<Genome, int> _genotypeCounts = new(); // 内部分基因型计数（仅携带基因物品使用）
    public IReadOnlyDictionary<Genome, int> GenotypeCounts => _genotypeCounts;

    public StackSlot(ItemInstance item, int count = 1)
    {
        Item = item;
        Count = count;
        if (item.Genome.HasValue) _genotypeCounts[item.Genome.Value] = count;
    }

    // 从存档恢复完整槽状态（含内部基因型分布；构造函数只处理单一基因样本）
    public static StackSlot FromSave(ItemInstance item, int count, Dictionary<Genome, int> genotypeCounts)
    {
        var slot = new StackSlot(item, count);
        if (genotypeCounts != null && genotypeCounts.Count > 0)
        {
            slot._genotypeCounts.Clear();
            foreach (var kv in genotypeCounts)
                slot._genotypeCounts[kv.Key] = kv.Value;
        }
        return slot;
    }

    // 能否合并：同 ItemType + 同 PhenotypeTags（逐项比较）
    public bool CanMergeWith(ItemInstance other)
    {
        if (Item.ItemType != other.ItemType) return false;
        if (Item.PhenotypeTags.Count != other.PhenotypeTags.Count) return false;
        for (int i = 0; i < Item.PhenotypeTags.Count; i++)
            if (Item.PhenotypeTags[i] != other.PhenotypeTags[i]) return false;
        return true;
    }

    // 合并 amount 个物品进本槽（调用方已通过 CanMergeWith；Count 上限 STACK_LIMIT，超出部分返回剩余未合并数）
    public int Merge(ItemInstance other, int amount)
    {
        int space = Constants.STACK_LIMIT - Count;
        int merged = Mathf.Min(space, amount);
        Count += merged;
        if (other.Genome.HasValue)
        {
            if (_genotypeCounts.ContainsKey(other.Genome.Value))
                _genotypeCounts[other.Genome.Value] += merged;
            else
                _genotypeCounts[other.Genome.Value] = merged;
        }
        return amount - merged; // 剩余未合并数量（0 表示全部合并）
    }

    // 取出 amount 个（从基因型计数中扣除；返回实际取出数）
    public int Take(int amount)
    {
        amount = Mathf.Min(amount, Count);
        if (amount <= 0) return 0;
        // 从基因型计数中扣除（简化：从第一个基因型扣）
        if (_genotypeCounts.Count > 0)
        {
            var first = _genotypeCounts.First();
            int takeFromFirst = Mathf.Min(amount, first.Value);
            if (takeFromFirst >= first.Value) _genotypeCounts.Remove(first.Key);
            else _genotypeCounts[first.Key] = first.Value - takeFromFirst;
        }
        Count -= amount;
        return amount;
    }
}
