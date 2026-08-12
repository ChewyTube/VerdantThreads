# 树外观瑕疵修复计划（P3-13）

> 状态：**计划定稿（2026-08-12），待玩家确认后实施**。仅外观修复，不改变树风格（香樟球冠树）。
> 来源：`docs/archive/REVIEW.md` P3-13（历史遗留，低优先级）；当前代码在 `World/Feature/TreeFeature.cs`
> （从旧 `World.cs` 1:1 搬迁，公式逐字保留，故 REVIEW 的问题描述仍然成立）。
> 关联：`FEATURE_SYSTEM.md`（地物框架与锚点逻辑）、`TODO_LIST.md`「其他待办」。

## 1. 背景

REVIEW 记录的四项外观瑕疵，全部位于树的生成公式，源自旧 `World.cs` 时代的地形/锚点语义：

| 项 | 现象 | 根因（当前代码） |
|---|---|---|
| (a) | baseHeight 小时树"悬空" | 树干起点比草皮高 1 格，地表矮时视觉更明显 |
| (b) | 草皮在 chunk 顶边界时树干留 1 格空隙 | 同 (a)：树干起点 = 锚点 = 地表上 1 格 |
| (c) | 冠层起始随地表奇偶变化 | 球心 Y 依赖 `realY` 奇偶 → 树冠形状周期摆动 |
| (d) | 树高/树冠用局部坐标 | 形态哈希用 `lx/lz`（chunk 内局部）而非世界坐标 |

## 2. 现状代码定位

- 地形：`TerrainGenerator.cs` 列循环——`baseHeight = (noise+1)*0.5*64`（[0,64]）；地表 Grass 在
  `y == baseHeight + 3`，Dirt 到 `baseHeight + 2`，Stone 到 `baseHeight`；锚点 `anchorY = baseHeight + 4`，
  判定 `anchorLocalY = anchorY - pos.Y*16 ∈ [0,16)` 才放地物（`TerrainGenerator.cs:67-102`）。
- 树：`TreeFeature.cs`——`realY = groundY % 16`（groundY = anchorY = baseHeight+4），树干
  `for (i = realY; i < trunkTop; i++)`（`:30-33`）；树冠球心 `crownCenterY = trunkTop + crownRadius - 2`、
  逐层 `layerRadius = Ceiling(Sqrt(r² - dy²))`（`:36-55`）。

## 3. 问题分析与修复方案

### 3.1 (a)+(b) 树干悬空 / 草皮 1 格空隙 —— 同根因，一起修

**根因**：锚点语义是"地表上方一格"（`anchorY = baseHeight + 4`，草皮在 `baseHeight + 3`）。
树干起点 `realY = groundY % 16` 即锚点局部 Y → 树干基部与草皮之间**恒有 1 格 Air 空隙**。
baseHeight 小（地表低）时这 1 格 + 大树冠使树显得"浮空"；草皮恰在 chunk 顶边界时表现为树干下留空。

**修复**：树干起点下移 1 格到草皮格（局部 `realY - 1`）：

```csharp
// TreeFeature.Place：树干从草皮格开始（下移 1 格，消除悬空/空隙）
int trunkBase = realY - 1;                 // 草皮格（地表）；realY==0 时跨界到下方 chunk，Setblock 自动处理
for (int i = trunkBase; i < trunkTop; i++) // 树干含草皮格与上方
    data.Setblock(BlockRegistry.Log, lx, i, lz);
```

- 跨 chunk：`trunkBase == -1`（锚点恰在本 chunk y=0）时树干第 1 格写到下方 chunk 的 y=15——`data.Setblock`
  自动进 pendingBlocks，主线程在下方 chunk 加载后重放，无额外处理 ✓
- 树冠内"树干格保留不盖"判断 `!(j==0 && k==0 && layerY < trunkTop)` 不变（树冠不会盖掉新增的树干第 1 格？——
  **注意**：树冠底部 `crownBottom = crownCenterY - crownRadius`，若 crownBottom ≤ trunkBase，树冠会覆盖树干基部
  ——需在实施时核对层高关系，必要时同步下移冠底或保留"树干格不盖"的判断覆盖范围）
- **视觉影响**：整棵树下移 1 格，树干贴地。这是本计划最大的可见变化，需玩家确认接受
  （若想保持"香樟树干基部抬起"的风格，则本项降级为"仅修跨 chunk 特例"）

### 3.2 (c) 冠层奇偶

**根因**：`crownCenterY = trunkTop + crownRadius - 2`，`trunkTop = realY + trunkHeight`——球心 Y 的
奇偶 = 地表高度（baseHeight+4）的奇偶。逐层半径 `Ceiling(Sqrt(r²-dy²))` 对整数 dy 的取整使
**奇数/偶数地表高度产生不同的逐层半径序列**，树冠形状随地表奇偶周期性变化。

**修复**（二选一）：
- **A（推荐，视觉连续）**：球心/层高用**绝对世界 Y** 计算（`worldY = groundY + i` 或
  `crownCenterWorldY = anchorY + trunkHeight + crownRadius - 2`），消除对 `realY` 奇偶的依赖——
  同高度地表树冠形状一致，不再随地表奇偶摆动
- **B（零视觉变化）**：保持现状，仅加注释说明（"冠层随地表奇偶微变"），不修

> 决策点：选 A 则树冠形状会整体微调（不同奇偶地表的新形状），但更一致；选 B 则保留现状。
> 推荐 A，与 (a)/(b) 的"贴地"修复同批做，一次性重生成对比。

### 3.3 (d) 局部坐标

**根因**：`trunkHeight = (lx*31 + lz*17) % 3 + 4`、`crownRadius = (lx*7 + lz*11) % 2 + 3`（`TreeFeature.cs:22-23`）
用 chunk 内局部坐标做形态哈希。chunk 布局固定（16³）时 `lx = blockX & 15` 与世界坐标一一对应，
**当前不产生视觉 bug**；真正的风险是：若未来 chunk 尺寸变化或树逻辑被复用，形态会意外改变。

**修复**（三选一）：
- **A（推荐，改世界坐标 + 接受新树形）**：`trunkHeight = (blockX*31 + blockZ*17) % 3 + 4`、
  `crownRadius = (blockX*7 + blockZ*11) % 2 + 3`——所有树的形态/分布重随机（哈希值全变），
  但确定性保持（同坐标同结果），且与世界坐标解耦
- **B（保持现树形，仅文档化）**：不改代码，注释说明"形态哈希用局部坐标是历史遗留，
  与 chunk 布局绑定，改动会重随机树形"
- **C（未来 chunk 尺寸变化时再改）**：挂到 TODO，本计划不动

> 决策点：A 会让全世界的树"换新发型"（树高/树冠半径重新分配）；B/C 零视觉变化。
> 若本轮目标是"修外观瑕疵"，建议 B（(d) 不是视觉 bug）；若想顺带让树更自然，选 A 与 3.1/3.2 同批。

## 4. 实施顺序（建议）

| 顺序 | 项 | 工作量 | 风险 | 说明 |
|---|---|---|---|---|
| 1 | 3.1 (a)+(b) 树干贴地 | 小（~5 行） | 中（树外观下移 1 格） | 需玩家确认接受贴地 |
| 2 | 3.2 (c) 冠层锚定世界 Y | 小（~3 行） | 低-中（树冠形状微调） | 选方案 A 时 |
| 3 | 3.3 (d) 局部坐标 | 极小 | 按所选方案 | 推荐 B（仅注释） |
| 4 | 验证 + 更新文档 | — | — | FEATURE_SYSTEM.md / AGENTS.md / TODO_LIST.md |

## 5. 验证方法（Play Mode）

1. **删档重生成**（`Application.persistentDataPath/world_saves/`）后启动，避免旧存档覆盖新外观
2. 对比修复前后：
   - 任意树的树干基部应**紧贴草皮**（无 1 格空隙 / 不悬空）
   - 连续地表高度（奇/偶相邻）的树冠形状应**一致**（不再随奇偶摆动）——仅方案 A
   - 树整体仍是香樟球冠风格（树干下部裸露、上部穿入球冠）
3. 跨 chunk 边界验证：树干第 1 格（trunkBase==-1 跨界）与树冠跨界格正常落位
4. 确定性：同一坐标重启重生成，树形完全一致
5. 豌豆丛不受影响（PeaClumpFeature 无改动，靠 Air 检查避让树干）

## 6. 风险与明确不做

- **外观变化**：3.1/3.2 的修复会改变树的外观（贴地、冠形微调）——这是"修复"的预期结果，
  但需玩家在实施前确认接受；不接受则降级（3.1 只修跨 chunk 特例、3.2 选 B）
- **存档兼容**：树是生成期纯逻辑，存档数据优先（读档命中跳过生成）——旧存档里的树不受影响，
  新生成区域（或删档）才看到新外观；零格式变化
- **确定性**：所有修复保持"同坐标 + 固定 seed 同结果"（只用坐标算术，无随机源）
- **明确不做**：树风格重设计（换树型/加变种）、地形高度公式改动、树逻辑搬到运行时更新
  （树仍为生成期一次性放置，不做生长/落叶等动态）
