using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// 背包存档：极简二进制格式（magic "BPK1" + 版本 + 槽列表），独立于 .vrf 地形存档。
// 存档时机：World.OnApplicationQuit；读取时机：World.Awake（Backpack 创建后）。
// 槽序列化字段顺序（WriteSlot / ReadSlot 配套，务必保持一致）：
//   int ItemType / string DisplayName / bool hasPlaceable+int / bool hasGenome+uint /
//   int Count / 基因型分布 / 表型标签 / 基因型标签 / bool isSeedBag+种子袋内容 /
//   int payloadLen + payload（v2 新增：HTT 载荷字节；v1 旧档无此段，读路径按版本跳过）
public static class BackpackSaver
{
    private const string Magic = "BPK1";     // 魔数：格式标识
    private const byte Version = 2;          // 格式版本（v2 新增槽 HTT 载荷段）

    private static string GetPath() => Path.Combine(Application.persistentDataPath, "world_saves", Constants.BACKPACK_SAVE_FILE);

    // 保存：全量写槽列表（含堆叠数量、基因型分布、种子袋内容）。失败仅日志警告，不抛异常。
    public static void Save(Backpack backpack)
    {
        try
        {
            if (backpack == null) return;
            string path = GetPath();
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                writer.Write(Magic.ToCharArray());
                writer.Write(Version);
                writer.Write(backpack.Count);
                for (int i = 0; i < backpack.Count; i++)
                    WriteSlot(writer, i, backpack);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"背包存档失败：{e.Message}");
        }
    }

    // 加载：文件不存在返回 null；解析失败返回 null（日志警告，不抛异常）。
    // 返回的 Backpack 已用读回数据重建槽列表。
    public static Backpack Load()
    {
        string path = GetPath();
        if (!File.Exists(path)) return null;
        try
        {
            using (var reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                if (new string(reader.ReadChars(4)) != Magic)
                {
                    Debug.LogWarning("背包存档魔数不匹配，忽略存档");
                    return null;
                }
                byte version = reader.ReadByte();
                if (version < 1 || version > Version)
                {
                    Debug.LogWarning($"背包存档版本 {version} 不兼容，忽略存档");
                    return null;
                }
                int count = reader.ReadInt32();
                if (count < 0 || count > 512) // 防脏数据：槽数上限保护
                {
                    Debug.LogWarning("背包存档槽数异常，忽略存档");
                    return null;
                }
                var slots = new List<StackSlot>(count);
                for (int i = 0; i < count; i++)
                {
                    StackSlot slot = ReadSlot(reader, version);
                    if (slot == null) { Debug.LogWarning("背包存档槽解析失败，忽略存档"); return null; }
                    slots.Add(slot);
                }
                var backpack = new Backpack();
                backpack.ReplaceAll(slots);
                return backpack;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"背包存档加载失败：{e.Message}");
            return null;
        }
    }

    // 写单个槽（字段顺序与 ReadSlot 严格一致）
    private static void WriteSlot(BinaryWriter writer, int index, Backpack backpack)
    {
        ItemInstance item = backpack[index];
        if (item == null) { writer.Write(-1); return; } // 防御：空槽写非法 ItemType，加载时整档拒绝

        writer.Write((int)item.ItemType);
        writer.Write(item.DisplayName);

        writer.Write(item.PlaceableBlockType.HasValue);
        if (item.PlaceableBlockType.HasValue) writer.Write((int)item.PlaceableBlockType.Value);

        writer.Write(item.Genome.HasValue);
        if (item.Genome.HasValue) writer.Write(item.Genome.Value.Value);

        writer.Write(backpack.GetStackCount(index));

        // 内部分基因型分布（仅携带基因物品有内容；其余为空字典）
        IReadOnlyDictionary<Genome, int> genotypeCounts = backpack.GetGenotypeCounts(index);
        writer.Write(genotypeCounts != null ? genotypeCounts.Count : 0);
        if (genotypeCounts != null)
        {
            foreach (var kv in genotypeCounts) { writer.Write(kv.Key.Value); writer.Write(kv.Value); }
        }

        // 表型 / 基因型标签
        writer.Write(item.PhenotypeTags.Count);
        foreach (string t in item.PhenotypeTags) writer.Write(t);
        writer.Write(item.GenotypeTags.Count);
        foreach (string t in item.GenotypeTags) writer.Write(t);

        // 种子袋内容（仅 SeedBag 物品非 null）
        bool isSeedBag = item.SeedBag != null;
        writer.Write(isSeedBag);
        if (isSeedBag)
        {
            writer.Write(item.SeedBag.Peas.Count);
            foreach (var kv in item.SeedBag.Peas) { writer.Write(kv.Key.Value); writer.Write(kv.Value); }
        }

        // HTT 载荷段（v2 新增）：int payloadLen + 字节；空树（无 Payload 或 Count==0）只写 0，不写段
        byte[] payloadBytes = item.Payload != null && item.Payload.Count > 0 ? HTTSerializer.Serialize(item.Payload) : null;
        writer.Write(payloadBytes != null ? payloadBytes.Length : 0);
        if (payloadBytes != null) writer.Write(payloadBytes);
    }

    // 读单个槽（version 用于判别 v2 的 HTT 载荷段；v1 旧档无该段，Payload 保持 null）；
    // 字段非法返回 null（调用方据此拒绝整档）
    private static StackSlot ReadSlot(BinaryReader reader, byte version)
    {
        int itemTypeInt = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(ItemType), itemTypeInt)) return null;
        var itemType = (ItemType)itemTypeInt;
        string displayName = reader.ReadString();

        bool hasPlaceable = reader.ReadBoolean();
        BlockType? placeable = hasPlaceable ? (BlockType?)reader.ReadInt32() : null;

        bool hasGenome = reader.ReadBoolean();
        uint genomeValue = 0;
        if (hasGenome) genomeValue = reader.ReadUInt32();

        int count = reader.ReadInt32();
        if (count <= 0 || count > Constants.STACK_LIMIT) return null; // 防脏数据：堆叠数量越界

        // 内部分基因型分布
        int genotypeEntryCount = reader.ReadInt32();
        if (genotypeEntryCount < 0 || genotypeEntryCount > Constants.STACK_LIMIT) return null;
        var genotypeCounts = new Dictionary<Genome, int>(genotypeEntryCount);
        for (int i = 0; i < genotypeEntryCount; i++)
        {
            uint gv = reader.ReadUInt32();
            int gc = reader.ReadInt32();
            if (gc <= 0) continue; // 防御：非法数量条目跳过
            var g = new Genome(gv);
            genotypeCounts[g] = gc;
        }

        // 表型 / 基因型标签（仅冗余备份；重建物品时按需使用）
        int phenotypeCount = reader.ReadInt32();
        if (phenotypeCount < 0 || phenotypeCount > 64) return null;
        for (int i = 0; i < phenotypeCount; i++) reader.ReadString();
        int genotypeTagCount = reader.ReadInt32();
        if (genotypeTagCount < 0 || genotypeTagCount > 64) return null;
        for (int i = 0; i < genotypeTagCount; i++) reader.ReadString();

        // 种子袋内容
        bool isSeedBag = reader.ReadBoolean();
        int seedBagEntryCount = reader.ReadInt32();
        if (seedBagEntryCount < 0 || seedBagEntryCount > Constants.SEED_BAG_CAPACITY) return null;
        var seedBagGenomes = new Dictionary<Genome, int>(seedBagEntryCount);
        for (int i = 0; i < seedBagEntryCount; i++)
        {
            uint gv = reader.ReadUInt32();
            int gc = reader.ReadInt32();
            if (gc <= 0) continue;
            var g = new Genome(gv);
            seedBagGenomes[g] = gc;
        }

        // HTT 载荷段（v2 新增）：int payloadLen + 字节；v1 旧档无此段，直接跳过（Payload = null）
        HTTCompound payload = null;
        if (version >= 2)
        {
            int payloadLen = reader.ReadInt32();
            if (payloadLen < 0 || payloadLen > 65535) return null; // 防脏数据：长度越界 → 拒绝整档
            if (payloadLen > 0)
            {
                byte[] payloadBytes = reader.ReadBytes(payloadLen);
                // 反序列化失败仅日志警告，Payload 保持 null（回退基线），不拒绝整档
                payload = HTTSerializer.Deserialize(payloadBytes);
            }
        }

        // 重建物品：
        //   SeedBag → 非方块构造 + 逐个 TryAdd 读回的袋内豌豆
        //   携带基因 → genome 构造器（自动重算表型标签，与存档时一致）
        //   可放置 → 方块构造；其余 → 非方块构造
        ItemInstance item;
        if (itemType == ItemType.SeedBag)
        {
            item = new ItemInstance(itemType, displayName);
            foreach (var kv in seedBagGenomes) item.SeedBag.TryAdd(kv.Key, kv.Value);
        }
        else if (hasGenome)
        {
            item = new ItemInstance(itemType, displayName, new Genome(genomeValue));
        }
        else if (placeable.HasValue)
        {
            item = new ItemInstance(itemType, displayName, placeable.Value);
        }
        else
        {
            item = new ItemInstance(itemType, displayName);
        }

        // 重建槽（携带基因时 FromSave 会用读回的基因型分布覆盖构造默认的单基因样本计数）
        item.Payload = payload; // 重建物品后回填 HTT 载荷（v1 或反序列化失败时为 null）
        return StackSlot.FromSave(item, count, genotypeCounts);
    }
}
