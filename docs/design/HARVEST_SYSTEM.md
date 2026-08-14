# 豌豆采收系统设计定案

> 创建：2026-08-13　|　依据玩家问答协商定案。
> 状态：已定案，待实施（Phase 0→1→2 分步推进）。

## 1. 采收触发

右键命中 `PeaStem` 时拦截（不再执行放置逻辑），按当前生长阶段分派产物。

| 阶段 | 对应状态 | 产物 | 数量 | 基因来源 |
|------|---------|------|------|---------|
| 0 | 最小苗 | 不可采，OnGUI 提示「未成熟」 | — | — |
| 1 | 苗 | 不可采，OnGUI 提示「未成熟」 | — | — |
| 2 | 植株（已长出 2/3 格高） | 不可采，OnGUI 提示「未成熟」 | — | — |
| 3 | 开花 | **青嫩豆荚** | 3~5 个 | 无基因（未定型），不可种植 |
| 4 | 结果 | **豌豆荚** | 12~16 个 | 读 tile `PeaTileData.Genome`（母本基因） |

## 2. 产物物品

### 2.1 物品类型

独立 `ItemType` 枚举（与 `BlockType` 分离），`ItemInstance` 同时持有 `ItemType` 和可选的 `PlaceableBlockType`：

```
ItemType {
    GrassBlock,        // 对应 BlockType.Grass → PlaceableBlockType.Grass
    DirtBlock,         // ...
    StoneBlock,
    LogBlock,
    LeavesBlock,
    BedrockBlock,
    PeaSeedBlock,      // 豌豆种子（背包第7格，无基因）
    GreenBeanPod,      // 青嫩豆荚（阶段3，不可种）
    PeaPod,            // 豌豆荚（阶段4，可分解）
    SeedBag,           // 种子袋（容器物品，右键打开）
}
```

### 2.2 青嫩豆荚

- 阶段 3 开花期采收所得
- **不可种植**（`PlaceableBlockType = null`）
- 无基因组
- 有表型（外观/描述基于母株花色等可见性状）
- 堆叠按表型合并（上限 64）

### 2.3 豌豆荚

- 阶段 4 结果期采收所得
- **不可直接种植**（`PlaceableBlockType = null`），需分解为豌豆粒
- 携带母本基因组（读 `PeaTileData.Genome`）
- 堆叠按表型合并（上限 64），内部记录不同基因型各自数量
- 分解（本期不做）：合成窗口 / 手持右键 → 4~8 粒豌豆种子（带各自母本基因）

## 3. 堆叠规则

### 3.1 堆叠物品
- 堆叠上限：**64**
- 堆叠依据：**表型相同**（同名 + 同可见性状）自动合并
  - 青嫩豆荚：表型相同 → 同一格
  - 豌豆荚：表型相同（同花色+同株高+同豆荚色等） → 同一格
- 堆叠内部记录：**每种基因型分别多少个**
- 非堆叠物品（方块类工具物品等）：每个独立占格（保留现有 `List<ItemInstance>` 的 flat 结构）

### 3.2 种子袋
- 独立物品 `ItemType.SeedBag`
- 右键打开：显示内部子背包界面
- 容量上限：**1024 粒豌豆**（总计，含不同基因型）
- 种子袋内豌豆按基因型拆分显示（每行一种基因型 + 数量）
- 分解豌豆粒时，若种子袋在背包中，优先存入种子袋；无种子袋则落入背包（以堆叠形式）

## 4. 背包存档

`BackpackSaver`（`Save/BackpackSaver.cs`）：
- 独立存档文件 `world_saves/backpack.dat`
- NBT 式极简 tag 树序列化
- 序列化内容：
  - 背包非堆叠物品列表（BlockItem 类物品 → `ItemType + PlaceableBlockType`）
  - 堆叠物品列表（青嫩豆荚/豌豆荚 → `ItemType + 表型 + 内部分基因型计数表`）
  - 种子袋内容（子背包内按基因型拆分的豌豆列表）
- 加载路径：`World.Awake` 内 `BackpackSaver.Load()`
- 保存路径：`OnApplicationQuit` / `OnDestroy` 或定期自动保存

## 5. 表型推导

`PeaTrait.GetPhenotype(Genome)` → 返回表型标识字符串（用于堆叠分组）：

| 位点 | 性状 | 显性表型 | 隐性表型 |
|------|------|---------|---------|
| 0 | 种子形状 | 圆粒 | 皱粒 |
| 1 | 子叶颜色 | 黄色 | 绿色 |
| 2 | 花色 | 紫色 | 白色 |
| 3 | 豆荚形状 | 饱满 | 皱缩 |
| 4 | 豆荚颜色 | 绿色 | 黄色 |
| 5 | 花位置 | 腋生 | 顶生 |
| 6 | 茎高度 | 高茎 | 矮茎 |

青嫩豆荚的表型：只取**花色**（位点 2）和**花位置**（位点 5）作为外观区分（开花期可见性状），其余位点不参与。

## 6. 实施计划

### Phase 0 — 物品系统重构
- 新增 `ItemType` 枚举（独立于 `BlockType`）
- `ItemInstance` 持有 `ItemType` + `PlaceableBlockType`（可选）
- 增加 `phenotypeTags` / `genotypeTags` 标签字段（`List<string>`）
- 重构 `Backpack` 构造器与初始化逻辑

### Phase 1 — 堆叠系统 + 背包存档
- 背包从纯 `List<ItemInstance>` 改为支持堆叠仓储结构
- 按表型分组 + 内部分基因型计数
- `BackpackSaver`（NBT 式 tag 树 → `backpack.dat`）
- 种子袋容器（右键打开子背包，上限 1024）
- `World.cs` 装配 `BackpackSaver`（加载/退出保存）

### Phase 2 — 采收逻辑 + 表型推导
- `BlockInteraction` 右键拦截：命中 `PeaStem` 且阶段 ≥ 3 时走采收
- `PeaTrait.GetPhenotype(Genome)` 表型推导函数
- 阶段 3 → `AddItem(GreenBeanPod × 3~5, phenotypeFromTile)`
- 阶段 4 → `AddItem(PeaPod × 12~16, genome = GetTile(pos).Genome)`
- 堆叠合并（同表型自动叠加）
- 相关性 mesh 重建

### Phase 3 — 分解（后置）
- 合成窗口 / 手持右键分解豌豆荚
- 每荚 → 4~8 粒豌豆种子（携带各自基因组）
- 优先存入种子袋，无种子袋落入背包

## 7. 对应旧文档

- `docs/design/TAG_SYSTEM.md` — 部分内容已过时（阶段映射、产物数量、标签范围为批2前置）。采收设计以本文档为准。
- `docs/design/INVENTORY_SYSTEM.md` — 物品基础框架延续，堆叠扩展。
- `docs/status/TODO_LIST.md` 批2-2h — 以本文档 Phase 0-2 替代。
