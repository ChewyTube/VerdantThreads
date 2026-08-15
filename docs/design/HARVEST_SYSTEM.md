# 豌豆采收系统设计定案

> 创建：2026-08-13　|　修订：2026-08-16（Phase 2 设计修订：8 新基因 HarvestGenome + HTT 载荷，替代位点 7 方案）
> 状态：Phase 0/1 已实施 ✅；Phase 2 设计已定案（2026-08-16 修订），待实施。

## 1. 采收触发

右键命中豌豆植株（`PeaStem` 底部格，或 `PeaPlantMiddle`/`PeaPlantTop` 中段/顶端格——命中后向下找底部格）时拦截（不再执行放置逻辑），按当前生长阶段分派产物。

| 阶段 | 对应状态 | 产物 | 数量 | 基因来源 |
|------|---------|------|------|---------|
| 0 | 最小苗 | 不可采，OnGUI 提示「未成熟」 | — | — |
| 1 | 苗 | 不可采，OnGUI 提示「未成熟」 | — | — |
| 2 | 植株（已长出 2/3 格高） | 不可采，OnGUI 提示「未成熟」 | — | — |
| 3 | 开花 | **青嫩豆荚** | 3~7 个（按基因） | 无基因（未定型），不可种植 |
| 4 | 结果 | **豌豆荚** | 12~20 个（按基因） | 读 tile `PeaTileData.Genome`（母本基因） |

### 1.1 采收后植株状态

- 采收成功 → **回退到阶段 2**（植株），可再次生长 2→3→4 循环采收
- **采摘次数由基因型决定**（多基因共同控制，见 §5.2），每次采收（阶段 3 或 4）次数 -1
- 次数耗尽 → **整株枯萎**：矮茎 2 格 / 高茎 3 格全部变为 `BlockType.PeaWithered`（新方块），移除 tile；玩家左键破坏枯萎方块去除（无掉落）。破坏任一枯萎格 → 联动清除同株其余枯萎格
- 剩余次数存储：底部格方块状态位 bit20-26（`HarvestMask = 0x7F`，7 bit；0 = 未初始化 → 首次采收按基因型写入上限；1-64 = 剩余次数）。块值随 `.vrf` 存档，零格式改动；`WithStage`/`WithTall` 只改各自掩码位，互不干扰

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
    PeaSeed,           // 豌豆粒（分解后，预留 Phase 3）
    GreenBeanPod,      // 青嫩豆荚（阶段3，不可种）
    PeaPod,            // 豌豆荚（阶段4，可分解）
    SeedBag,           // 种子袋（容器物品，右键打开）
}
```

### 2.2 青嫩豆荚

- 阶段 3 开花期采收所得
- **不可种植**（`PlaceableBlockType = null`）
- 无基因组
- 表型标签：仅取花色（位点 2）+ 花位置（位点 5）（开花期可见性状），作为堆叠分组依据
- 显示名保持简单名「青嫩豆荚」（玩家确认，表型标签仅内部使用）
- 堆叠按表型合并（上限 64）

### 2.3 豌豆荚

- 阶段 4 结果期采收所得
- **不可直接种植**（`PlaceableBlockType = null`），需分解为豌豆粒
- 携带母本基因组 + 采收基因（读 `PeaTileData.Genome` + `HarvestGenome`，经 HTT 载荷，见 `docs/design/HTT.md`）
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
- 极简二进制格式（magic `BPK1` + 版本 + 槽列表；已实施，非 NBT tag 树——定案时 NBT 为示意，实现取更简方案）
- **BPK1 v2（Phase 2）**：槽序列化追加 HTT payload 字节（豌豆荚携带采收基因）；v1 读路径兼容（无 payload → null）
- 序列化内容：
  - 背包非堆叠物品列表（BlockItem 类物品 → `ItemType + PlaceableBlockType`）
  - 堆叠物品列表（青嫩豆荚/豌豆荚 → `ItemType + 表型 + 内部分基因型计数表`）
  - 种子袋内容（子背包内按基因型拆分的豌豆列表）
- 加载路径：`World.Awake` 内 `BackpackSaver.Load()`
- 保存路径：`OnApplicationQuit`（`World` 装配，`BackpackSaver.Save`）

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

> 注：8 个新基因（采收潜力/产量）为**纯数量性状**，不参与表型标签/堆叠分组/渲染（见 §5.2 与 `docs/design/HTT.md`）。

青嫩豆荚的表型：只取**花色**（位点 2）和**花位置**（位点 5）作为外观区分（开花期可见性状），其余位点不参与。

### 5.2 多基因数量性状模型（采摘次数与产量，2026-08-16 定案）

**硬约束**：`Genome`（uint32，7 位点 × 4 bit = 28 bit）已满，玩家决策**不改动 Genome 长度**。8 个新基因打包为独立 `HarvestGenome`（uint32，8 位点 × 2 等位 × 2 bit，与 Genome 同编码），存于 tile 的 **HTT 载荷**（`docs/design/HTT.md`）——**纯数量性状**：不参与表型标签、堆叠分组与渲染。

**寿命（采摘次数）**——指数模型，8 个新基因共同控制（k = 纯合显性位点数，每位点概率 1/4，期望 2）：

```
采摘次数上限 = min(2^(1 + k), 64)     // 2~64，典型 8 次，期望 ≈ 12
```

**产量（单次豆荚数）**——累加模型，同一批 8 基因共同控制：

```
阶段 4 豌豆荚 = 12 + 2k     // 12~28，期望 16
阶段 3 青嫩豆荚 = 3 + k     // 3~11，期望 5
```

生物学叙事：8 个新基因 = 多基因数量性状（产量/寿命由众多微效基因共同控制，单个基因不可见）——与经典 7 对孟德尔性状（可见、参与表型）分离。

公式集中在 `PeaHarvestCalculator` 工具类（待实施），便于调参。

## 6. 实施计划

### Phase 0 — 物品系统重构 ✅（2026-08-14）
- ✅ 新增 `ItemType` 枚举（独立于 `BlockType`）
- ✅ `ItemInstance` 持有 `ItemType` + `PlaceableBlockType`（可选）+ `PhenotypeTags`/`GenotypeTags`
- ✅ 重构 `Backpack` 构造器与初始化逻辑
- ✅ 调用点适配（BlockInteraction 放置守卫、UI 图标 UV）

### Phase 1 — 堆叠系统 + 背包存档 ✅（2026-08-14）
- ✅ 背包改为堆叠仓储结构（`StackSlot`：模板物品 + 数量 + 内部分基因型计数）
- ✅ 按表型分组合并（`AddItem` 自动合并，上限 64）
- ✅ `BackpackSaver`（`BPK1` 二进制 → `backpack.dat`）
- ✅ 种子袋容器（`SeedBag`，右键打开子面板，上限 1024）
- ✅ `World.cs` 装配（`Awake` 加载 / `OnApplicationQuit` 保存）
- ✅ Play Mode 验证修复：鼠标锁定状态下 IMGUI 无法点击（背包开→解锁光标、CameraMove 暂停输入、锁定状态右键按选中槽判断）

### Phase 2 — 采收逻辑 + 表型推导（待实施，2026-08-16 修订）
- **Step 0（前置）HTT 载荷机制**（`docs/design/HTT.md`）：`Data/HTT.cs` + `HTTSerializer.cs`（9 种 tag、大端、防御校验）；`PeaTileData.Payload` + `GetHarvestGenome()/SetHarvestGenome()` 访问器；vrf v4（tile 记录追加 payloadLen + payload，空载荷不写段，v1/v2/v3 读路径兼容）；`ItemInstance.Payload`；BPK1 v2（v1 兼容）
- `Genetics/HarvestGenome.cs`（新建）：8 位点 × 2 等位 × 2 bit 打包 uint32（与 Genome 同编码）+ `Random/Crossover/Mutate/IsHomozygousDominant` 镜像 API
- `Genetics/PeaHarvestCalculator.cs`（新建）：次数 `min(2^(1+k), 64)`；产量 阶段4 `12+2k` / 阶段3 `3+k`（k = 纯合显性数）
- `Constants`：产量/次数公式常量
- `Block.HarvestMask = 0x7F`（bit20-26）+ `WithHarvests(int)`/`GetHarvests()`（0=未初始化）
- `PeaTrait.GetPhenotypeTags(Genome, params int[] loci)` 位点子集重载（青嫩豆荚取 {2,5}）+ `ItemInstance` 显式表型标签构造器（无基因）
- `BlockUpdateCenter`：公开 `RevertToStage2(bottomPos)`（复用 `SyncUpperStage`）；`PeaWithered` 破坏联动（破坏任一格 → 清除同株其余枯萎格）
- `Block.cs`/`BlockRegistry`：`PeaWithered = 11` 注册；**贴图先占位**（复用豌豆底格 tile），玩家跑 `main.py` 生成后替换
- `ChunkMeshBuilder`：`PeaWithered` 十字面片分支 + `ShouldBeEliminated` 不剔除名单
- `BlockInteraction` 右键拦截采收：命中豌豆（含中段/顶端向下找底部）→ 阶段 <3 提示「未成熟」→ 阶段 3/4 按公式产出入背包（`AddItem` 自动堆叠）→ 次数 -1 → 归 0 则整株变 `PeaWithered`（移除 tile），否则回退阶段 2 + 同步上部 + mesh 重建
- 种植/自然生成：随机 `Genome` + `HarvestGenome`（`PeaClumpFeature` 通道扩展）
- 修复 `BackpackSaver` 读档丢弃无基因物品表型标签的缺口（青嫩豆荚重启后堆叠分组失效）

### Phase 3 — 分解（后置）
- 合成窗口 / 手持右键分解豌豆荚
- 每荚 → 4~8 粒豌豆种子（携带各自基因组）
- 优先存入种子袋，无种子袋落入背包

## 7. 对应旧文档

- `docs/design/TAG_SYSTEM.md` — 部分内容已过时（阶段映射、产物数量、标签范围为批2前置）。采收设计以本文档为准。
- `docs/design/INVENTORY_SYSTEM.md` — 物品基础框架延续，堆叠扩展。
- `docs/status/TODO_LIST.md` 批2-2h — 以本文档 Phase 0-2 替代。
