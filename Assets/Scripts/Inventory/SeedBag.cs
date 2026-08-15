using System.Collections.Generic;
using UnityEngine;

// 种子袋：豌豆容器物品的内部数据。容量上限 SEED_BAG_CAPACITY（1024），按基因型分组计数。
public class SeedBag
{
    private readonly Dictionary<Genome, int> _peas = new();
    public IReadOnlyDictionary<Genome, int> Peas => _peas;
    public int TotalCount { get; private set; }

    // 尝试加入 count 粒指定基因豌豆；超容量返回 false（不部分加入）
    public bool TryAdd(Genome genome, int count)
    {
        if (TotalCount + count > Constants.SEED_BAG_CAPACITY) return false;
        if (_peas.ContainsKey(genome)) _peas[genome] += count;
        else _peas[genome] = count;
        TotalCount += count;
        return true;
    }

    // 取出 count 粒指定基因豌豆；返回实际取出数
    public int Take(Genome genome, int count)
    {
        if (!_peas.TryGetValue(genome, out int have)) return 0;
        int taken = Mathf.Min(count, have);
        if (taken >= have) _peas.Remove(genome);
        else _peas[genome] = have - taken;
        TotalCount -= taken;
        return taken;
    }
}
