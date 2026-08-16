using System;
using System.Collections;
using System.Collections.Generic;

// HTT（Hierarchical Tag Tree，层级标签树）：最小自研层级标签树模型，非 Minecraft NBT 规范兼容实现，
// 只实现本项目需要的子集：容器两类（Compound/List）+ 叶子六类（Byte/Short/Int/Long/String/ByteArray）。
// 定位与二进制格式详见 docs/design/HTT.md；编解码见 HTTSerializer。
// 线程纪律：HTT 树主线程独享（与 tile 字典纪律一致），序列化/反序列化均在主线程进行。
// 本文件为纯 C#，不依赖 UnityEngine。

// tag 类型：0=End 终止符，1-8 为节点类型，9-15 预留（将来按需补 Float/Double/IntArray 等）
public enum HTTType : byte
{
    End = 0,       // Compound 终止符
    Byte = 1,      // 1 字节
    Short = 2,     // 2 字节大端
    Int = 3,       // 4 字节大端
    Long = 4,      // 8 字节大端
    String = 5,    // ushort 长 + UTF-8
    ByteArray = 6, // int 长 + 字节
    List = 7,      // 元素 tagId + int 数量 + 元素载荷×N（元素无名字）
    Compound = 8,  // 命名子节点直至 End
}

// 节点基类：所有 HTT 节点共用的类型接口
public abstract class HTTNode
{
    public abstract HTTType Type { get; }
}

// 叶子：值可变（public 字段），主线程独享
public sealed class HTTByte : HTTNode
{
    public sbyte Value;
    public override HTTType Type => HTTType.Byte;
    public HTTByte(sbyte value) => Value = value;
}

public sealed class HTTShort : HTTNode
{
    public short Value;
    public override HTTType Type => HTTType.Short;
    public HTTShort(short value) => Value = value;
}

public sealed class HTTInt : HTTNode
{
    public int Value;
    public override HTTType Type => HTTType.Int;
    public HTTInt(int value) => Value = value;
}

public sealed class HTTLong : HTTNode
{
    public long Value;
    public override HTTType Type => HTTType.Long;
    public HTTLong(long value) => Value = value;
}

public sealed class HTTString : HTTNode
{
    public string Value;
    public override HTTType Type => HTTType.String;
    public HTTString(string value) => Value = value;
}

public sealed class HTTByteArray : HTTNode
{
    public byte[] Value;
    public override HTTType Type => HTTType.ByteArray;
    public HTTByteArray(byte[] value) => Value = value;
}

// 命名子节点容器：键唯一（同名 Set 覆盖），遍历按插入序。
// 实现 IEnumerable 供 HTTSerializer 遍历序列化。
public sealed class HTTCompound : HTTNode, IEnumerable<KeyValuePair<string, HTTNode>>
{
    private readonly Dictionary<string, HTTNode> _children = new Dictionary<string, HTTNode>();

    public override HTTType Type => HTTType.Compound;

    // 子节点数
    public int Count => _children.Count;

    // 取子节点；无 → null
    public HTTNode Get(string name)
    {
        _children.TryGetValue(name, out var node);
        return node;
    }

    public bool Has(string name) => _children.ContainsKey(name);

    // 设置子节点（同名覆盖）
    public void Set(string name, HTTNode node) => _children[name] = node;

    public void Remove(string name) => _children.Remove(name);

    // 类型化便捷访问器（缺失/类型不符 → 返回默认值，不抛异常）
    public int GetInt(string name, int def = 0) => Get(name) is HTTInt n ? n.Value : def;

    public void SetInt(string name, int value) => Set(name, new HTTInt(value));

    public string GetString(string name, string def = "") => Get(name) is HTTString n ? n.Value : def;

    // 无则建空 Compound 并 Set 后返回（惰性建树入口）
    public HTTCompound GetOrCreateCompound(string name)
    {
        if (Get(name) is HTTCompound c) return c;
        var compound = new HTTCompound();
        Set(name, compound);
        return compound;
    }

    // 无/类型不符 → null
    public HTTList GetList(string name) => Get(name) as HTTList;

    public IEnumerator<KeyValuePair<string, HTTNode>> GetEnumerator() => _children.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// 索引子节点容器：同构元素类型（构造时固定，NBT 语义，天然校验）。
// 元素类型仅限叶子六类（End/List/Compound 不合法——List 内出现 End 属脏数据，见 HTTSerializer 校验）。
public sealed class HTTList : HTTNode
{
    private readonly List<HTTNode> _items = new List<HTTNode>();

    // 元素类型（构造时固定）
    public HTTType ElementType { get; }

    public override HTTType Type => HTTType.List;

    public HTTList(HTTType elementType)
    {
        // 防御：List 元素类型限定叶子六类（End/List/Compound 不合法，构造即视为编程错误）
        if (elementType == HTTType.End || elementType == HTTType.List || elementType == HTTType.Compound)
            throw new ArgumentException($"非法 List 元素类型：{elementType}（编程错误）");
        ElementType = elementType;
    }

    public int Count => _items.Count;

    // 越界抛 IndexOutOfRangeException（List 原生语义）
    public HTTNode this[int i] => _items[i];

    // 元素类型与 ElementType 不符 → 抛 ArgumentException（编程错误，非脏数据）
    public void Add(HTTNode node)
    {
        if (node.Type != ElementType)
            throw new ArgumentException($"List 元素类型不符：期望 {ElementType}，实际 {node.Type}（编程错误）");
        _items.Add(node);
    }
}
