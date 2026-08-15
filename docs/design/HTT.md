# HTT（Hierarchical Tag Tree，层级标签树）设计定案

> 创建：2026-08-16　|　状态：设计已定案（玩家认可），待实施（Phase 2 Step 0 前置）
> 关联：`docs/design/HARVEST_SYSTEM.md`（8 新基因载荷载体）、`docs/status/TODO_LIST.md`（Phase 2 计划）

## 一、定位与背景

- 需求：豌豆采收系统需要 **8 个新基因** 共同控制采收潜力（采摘次数）与产量；`Genome`（uint32，7 位点 × 4 bit = 28 bit）已满。玩家决策：**不改动 Genome 长度**，引入类 NBT 机制承载新基因。
- 架构依据：@oracle 定案「不引入完整 NBT」——NBT 仅是将来 tile 的序列化格式，不是块存储模型；基因路线图预留了 tile 变长 payload（`payloadLen` 字段）。HTT 即该预留机制的提前启用。
- 定位：**最小自研层级标签树**，非 Minecraft NBT 规范兼容实现；只做本项目需要的子集。

## 二、模型

一棵命名标签树：

- **容器两类**：`HTTCompound`（命名子节点，键唯一，Set 覆盖）+ `HTTList`（索引子节点，**同构元素类型**——NBT 语义，天然校验）
- **叶子六类**：Byte / Short / Int / Long / String / ByteArray
- 共 **9 种 tag 类型**（含 End 终止符），tagId 0-8，9-15 预留

Phase 2 实际载荷示例：

```
HTTCompound（根）
 └── HTTInt "harvestGenome"   ← 8 新基因打包 uint32
```

## 三、类型系统

| tagId | 类型 | 载荷 |
|---|---|---|
| 0 | End | —（Compound 终止符） |
| 1 | Byte | 1 字节 |
| 2 | Short | 2 字节大端 |
| 3 | Int | 4 字节大端 |
| 4 | Long | 8 字节大端 |
| 5 | String | ushort 长 + UTF-8 |
| 6 | ByteArray | int 长 + 字节 |
| 7 | List | 元素 tagId + int 数量 + 元素载荷×N |
| 8 | Compound | 命名子节点直至 End |

预留 9-15：Float/Double/IntArray 等将来按需补（格式自描述，加类型不破坏旧数据）。

## 四、API 设计（签名示意，实施时以代码为准）

```csharp
// 节点基类 + 叶子（值可变，主线程独享）
public abstract class HTTNode { public abstract HTTType Type { get; } }
public sealed class HTTInt : HTTNode { public int Value; }   // 其余叶子同构

// 容器
public sealed class HTTCompound : HTTNode {
    public HTTNode Get(string name);            // 无 → null
    public bool Has(string name);
    public void Set(string name, HTTNode node); // 同名覆盖
    public void Remove(string name);
    // 类型化便捷访问器（缺失/类型不符 → 返回默认值，不抛异常）
    public int GetInt(string name, int def = 0);
    public void SetInt(string name, int value);
    public string GetString(string name, string def = "");
    public HTTCompound GetOrCreateCompound(string name);
    public HTTList GetList(string name);
}
public sealed class HTTList : HTTNode {
    public HTTType ElementType { get; }   // 构造时固定
    public int Count { get; }
    public HTTNode this[int i] { get; }
    public void Add(HTTNode node);        // 元素类型不符 → 抛异常（编程错误，非脏数据）
}
```

## 五、二进制格式（大端，与 .vrf 惯例一致）

```
Compound:  重复 { byte tagId | ushort 名字长 | UTF-8 名字 | 载荷 } … byte 0x00(End)
List:      byte 元素tagId | int 数量 | 元素载荷×N（无名字）
Byte: 1B | Short: 2B | Int: 4B | Long: 8B
String: ushort 长 + UTF-8 | ByteArray: int 长 + 字节
```

## 六、防御性校验

反序列化失败一律返回 null（调用方回退基线，仅日志警告）：

- 深度 ≤ 32
- 名字 ≤ 255 字符
- String ≤ 32KB
- ByteArray ≤ 1MB
- List ≤ 65536 元素
- 未知 tagId / 截断 / 越界 → 拒绝

天然上限：tile 记录 payloadLen 用 ushort → 单株载荷 ≤ 64KB。

## 七、线程纪律

- **HTT 树：主线程独享**（与 tile 字典纪律一致）
- 序列化：主线程树 → `byte[]`（纯值，可跨线程）
- worker 线程**只搬运字节**（`TileSaveRecord.PayloadBytes`），绝不触碰 HTT 对象
- 反序列化：主线程（CreateChunk 回挂 tile 时）`byte[]` → 树

## 八、Phase 2 集成点

| 位置 | 改动 |
|---|---|
| `Assets/Scripts/Data/HTT.cs` + `HTTSerializer.cs`（新建） | 树模型 + 序列化/反序列化（含校验） |
| `World/PeaTileData.cs` | 增 `HTTCompound Payload`（可空）+ `GetHarvestGenome()/SetHarvestGenome()` 访问器（惰性建树）；Genome/Generation 字段不动（热路径零影响） |
| `World/Saver.cs` | vrf v4：tile 记录 `key 2B + genome 4B + 世代 4B + payloadLen 2B + payload`，magic `'V4'`；读路径 v1/v2/v3/v4 判别；**载荷为空不写段**（旧档与基线株天然同构，零迁移） |
| `Inventory/ItemInstance.cs` | 增 `HTTCompound Payload`（豌豆荚携带母本 8 基因） |
| `Inventory/BackpackSaver.cs` | BPK1 v2：槽序列化追加 payload 字节；v1 读路径兼容 |
| `Genetics/HarvestGenome.cs`（新建） | uint32 打包 8 位点 × 2 等位 × 2 bit（与 Genome 同编码）；`Random/Crossover/Mutate/IsHomozygousDominant` 镜像 API |
| `Genetics/PeaHarvestCalculator.cs`（新建） | k = 纯合显性数：次数 `min(2^(1+k), 64)`；产量 阶段4 `12+2k` / 阶段3 `3+k` |

## 九、未来扩展（零格式改动）

- 碱基序列 → `HTTByteArray "dna"` 进 payload（payloadLen 已预留）
- 世代谱系 / 实验记录 → 新 tag
- 物品载荷与 tile 载荷同一机制

## 十、文件布局

`Assets/Scripts/Data/`（新目录；全局命名空间，无 asmdef，符合项目约定）。