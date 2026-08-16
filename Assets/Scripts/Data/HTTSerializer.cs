using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using UnityEngine;

// HTT 二进制编解码（大端，与 .vrf 惯例一致），格式详见 docs/design/HTT.md：
//   Compound:  重复 { byte tagId | ushort 名字长 | UTF-8 名字 | 载荷 } … byte 0x00(End)
//   List:      byte 元素tagId | int 数量 | 元素载荷×N（元素无名字）
//   Byte: 1B | Short: 2B | Int: 4B | Long: 8B（均大端）
//   String: ushort 长 + UTF-8 | ByteArray: int 长 + 字节
//
// 线程纪律：序列化（主线程树 → byte[]）与反序列化（byte[] → 主线程树）均须在主线程进行；
// byte[] 为纯值，可交给 worker 只读搬运。
public static class HTTSerializer
{
    private const int MAX_DEPTH = 32;            // 嵌套深度上限（防御性校验）
    private const int MAX_NAME_CHARS = 255;      // 名字字符数上限
    private const int MAX_STRING_BYTES = 32768;  // String 载荷上限（32KB）
    private const int MAX_BYTEARRAY_BYTES = 1048576; // ByteArray 载荷上限（1MB）
    private const int MAX_LIST_ELEMENTS = 65536; // List 元素数上限

    // 序列化：root 为 null → 返回 null；否则返回含 End 终止符的完整字节（空树 = 1 字节 End）。
    // 防御性检查仅在编程错误时抛异常（String 超 ushort 长 / 名字超限），不用于拒绝合法数据。
    public static byte[] Serialize(HTTCompound root)
    {
        if (root == null) return null;

        using var ms = new MemoryStream();
        WriteCompound(ms, root);
        return ms.ToArray();
    }

    // 反序列化：data 为 null/空 → 返回 null；任何校验失败（深度/名字/String/ByteArray/List 上限、
    // 未知 tagId、截断/越界、尾部残留）→ Debug.LogWarning + 返回 null，调用方回退基线。
    // 空载荷（仅 1 字节 End）→ 返回空 Compound（合法）。
    public static HTTCompound Deserialize(byte[] data)
    {
        if (data == null || data.Length == 0) return null;

        try
        {
            int pos = 0;
            HTTCompound root = ReadCompound(data, ref pos, 0);
            if (pos != data.Length)
                throw new InvalidDataException($"HTT 载荷尾部残留 {data.Length - pos} 字节");
            return root;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"HTT 反序列化失败，回退基线：{e.Message}");
            return null;
        }
    }

    // ---- 序列化（写路径） ----

    private static void WriteCompound(MemoryStream ms, HTTCompound compound)
    {
        foreach (var kv in compound)
        {
            if (kv.Value == null) continue; // 防御：跳过 null 子节点
            if (kv.Key.Length > MAX_NAME_CHARS)
                throw new ArgumentException($"HTT 子节点名过长：{kv.Key.Length} 字符（上限 {MAX_NAME_CHARS}，编程错误）");

            ms.WriteByte((byte)kv.Value.Type);
            byte[] nameBytes = Encoding.UTF8.GetBytes(kv.Key);
            WriteU16(ms, (ushort)nameBytes.Length); // 名字长用字节数（大端 ushort）
            ms.Write(nameBytes, 0, nameBytes.Length);
            WriteNode(ms, kv.Value);
        }
        ms.WriteByte((byte)HTTType.End); // Compound 终止符
    }

    private static void WriteNode(MemoryStream ms, HTTNode node)
    {
        switch (node.Type)
        {
            case HTTType.Byte:
                ms.WriteByte(unchecked((byte)((HTTByte)node).Value));
                break;
            case HTTType.Short:
                WriteU16(ms, unchecked((ushort)((HTTShort)node).Value));
                break;
            case HTTType.Int:
                WriteI32(ms, ((HTTInt)node).Value);
                break;
            case HTTType.Long:
                WriteI64(ms, ((HTTLong)node).Value);
                break;
            case HTTType.String:
                WriteString(ms, ((HTTString)node).Value);
                break;
            case HTTType.ByteArray:
                WriteByteArray(ms, ((HTTByteArray)node).Value);
                break;
            case HTTType.List:
                WriteList(ms, (HTTList)node);
                break;
            case HTTType.Compound:
                WriteCompound(ms, (HTTCompound)node);
                break;
            default:
                throw new ArgumentException($"未知 HTT 节点类型：{node.Type}（编程错误）");
        }
    }

    private static void WriteString(MemoryStream ms, string value)
    {
        if (value == null) value = ""; // 防御：null 串按空串处理
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException($"HTT String 载荷过长：{bytes.Length} 字节（ushort 上限 {ushort.MaxValue}，编程错误）");
        WriteU16(ms, (ushort)bytes.Length);
        ms.Write(bytes, 0, bytes.Length);
    }

    private static void WriteByteArray(MemoryStream ms, byte[] value)
    {
        if (value == null) value = Array.Empty<byte>(); // 防御：null 数组按空数组处理
        WriteI32(ms, value.Length);
        ms.Write(value, 0, value.Length);
    }

    private static void WriteList(MemoryStream ms, HTTList list)
    {
        ms.WriteByte((byte)list.ElementType);
        WriteI32(ms, list.Count);
        for (int i = 0; i < list.Count; i++)
            WriteNode(ms, list[i]); // 元素无名字
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        ms.Write(buf);
    }

    private static void WriteI32(MemoryStream ms, int v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, v);
        ms.Write(buf);
    }

    private static void WriteI64(MemoryStream ms, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, v);
        ms.Write(buf);
    }

    // ---- 反序列化（读路径） ----

    // 读取 Compound：重复 tagId + 名字 + 载荷直至 End；深度超限抛异常
    private static HTTCompound ReadCompound(byte[] data, ref int pos, int depth)
    {
        if (depth > MAX_DEPTH)
            throw new InvalidDataException($"HTT 嵌套深度超限：{depth}（上限 {MAX_DEPTH}）");

        var compound = new HTTCompound();
        while (true)
        {
            byte tagId = ReadByte(data, ref pos);
            if (tagId == (byte)HTTType.End) break;

            HTTType type = (HTTType)tagId;
            if (type < HTTType.Byte || type > HTTType.Compound) // 未知 tagId（9-15 预留等）
                throw new InvalidDataException($"未知 HTT tagId：{tagId}");

            string name = ReadName(data, ref pos);
            HTTNode node = ReadNodePayload(data, ref pos, type, depth);
            compound.Set(name, node);
        }
        return compound;
    }

    // 读取名字：ushort 字节长 + UTF-8；解码后字符数超限抛异常
    private static string ReadName(byte[] data, ref int pos)
    {
        int nameLen = ReadU16(data, ref pos);
        if (pos + nameLen > data.Length)
            throw new InvalidDataException("HTT 名字数据截断");
        string name = Encoding.UTF8.GetString(data, pos, nameLen);
        if (name.Length > MAX_NAME_CHARS)
            throw new InvalidDataException($"HTT 名字过长：{name.Length} 字符（上限 {MAX_NAME_CHARS}）");
        pos += nameLen;
        return name;
    }

    // 按已判定的类型读取节点载荷
    private static HTTNode ReadNodePayload(byte[] data, ref int pos, HTTType type, int depth)
    {
        switch (type)
        {
            case HTTType.Byte:
                return new HTTByte(unchecked((sbyte)ReadByte(data, ref pos)));

            case HTTType.Short:
                return new HTTShort(unchecked((short)ReadU16(data, ref pos)));

            case HTTType.Int:
                return new HTTInt(ReadI32(data, ref pos));

            case HTTType.Long:
                return new HTTLong(ReadI64(data, ref pos));

            case HTTType.String:
            {
                int len = ReadU16(data, ref pos);
                if (len > MAX_STRING_BYTES)
                    throw new InvalidDataException($"HTT String 载荷过长：{len} 字节（上限 {MAX_STRING_BYTES}）");
                if (pos + len > data.Length)
                    throw new InvalidDataException("HTT String 数据截断");
                string s = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
                return new HTTString(s);
            }

            case HTTType.ByteArray:
            {
                int len = ReadI32(data, ref pos);
                if (len < 0 || len > MAX_BYTEARRAY_BYTES)
                    throw new InvalidDataException($"HTT ByteArray 载荷长度非法：{len}（上限 {MAX_BYTEARRAY_BYTES}）");
                if (pos + len > data.Length)
                    throw new InvalidDataException("HTT ByteArray 数据截断");
                byte[] bytes = new byte[len];
                Buffer.BlockCopy(data, pos, bytes, 0, len);
                pos += len;
                return new HTTByteArray(bytes);
            }

            case HTTType.List:
                return ReadList(data, ref pos, depth);

            case HTTType.Compound:
                return ReadCompound(data, ref pos, depth + 1);

            default:
                throw new InvalidDataException($"未知 HTT tagId：{(byte)type}");
        }
    }

    // 读取 List：byte 元素类型 + int 数量 + 元素载荷×N（元素无名字）。
    // List 内出现 End（元素类型为 End）→ 失败；元素类型非叶子六类由 HTTList 构造器拒绝。
    private static HTTList ReadList(byte[] data, ref int pos, int depth)
    {
        byte elementTagId = ReadByte(data, ref pos);
        HTTType elementType = (HTTType)elementTagId;
        if (elementType < HTTType.Byte || elementType > HTTType.ByteArray)
            throw new InvalidDataException($"非法 List 元素类型：{elementTagId}（含 End，属脏数据）");

        int count = ReadI32(data, ref pos);
        if (count < 0 || count > MAX_LIST_ELEMENTS)
            throw new InvalidDataException($"List 元素数非法：{count}（上限 {MAX_LIST_ELEMENTS}）");

        var list = new HTTList(elementType);
        for (int i = 0; i < count; i++)
        {
            HTTNode element = ReadNodePayload(data, ref pos, elementType, depth);
            list.Add(element); // 类型不符在此属脏数据（防御），会抛 ArgumentException 被上层捕获
        }
        return list;
    }

    private static byte ReadByte(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new InvalidDataException("HTT 数据截断（ReadByte）");
        return data[pos++];
    }

    private static ushort ReadU16(byte[] data, ref int pos)
    {
        if (pos + 2 > data.Length)
            throw new InvalidDataException("HTT 数据截断（ReadU16）");
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
        pos += 2;
        return v;
    }

    private static int ReadI32(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length)
            throw new InvalidDataException("HTT 数据截断（ReadI32）");
        int v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
        pos += 4;
        return v;
    }

    private static long ReadI64(byte[] data, ref int pos)
    {
        if (pos + 8 > data.Length)
            throw new InvalidDataException("HTT 数据截断（ReadI64）");
        long v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos, 8));
        pos += 8;
        return v;
    }
}
