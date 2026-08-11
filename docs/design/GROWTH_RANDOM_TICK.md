# 豌豆生长机制：计时制 → MC 随机刻制（存档 v3）＋ 两格高植株

> 状态：**已实现（2026-08-11）**。生长由计时阈值改为 MC 式随机刻推进，存档载荷升级 v3；
> 阶段链扩展为「最小苗→苗→两格高植株→开花结果」，阶段 2/3 为两格高植株（新增 PeaPlantTop 顶部格）。
> 关联：`FEATURE_SYSTEM.md`（地物）、`TODO_LIST.md`（豌豆生长条目）、`AGENTS.md`（豌豆系统说明）。

## 1. 决策背景

原计时制：`PEA_STAGE_1/2/3_SECONDS = 20/40/60s`，单阶段 20s、三阶段全熟 60s，**太快**（不自然）。
用户定案：三阶段全熟 10~20 分钟合理 → 采用 **MC 随机刻模型**。

## 2. MC 随机刻模型对照

MC（Java 1.17+）的随机刻以 **section（sub-chunk，16³ = 4096 方块）** 为单位：每个 section 每
游戏 tick（1/20s）执行 **3 次**随机刻（满列 chunk 有 16 个 section，但作物只在自己所在 section
内被抽）。小麦在被抽中时以 **1/3** 概率推进一生长阶段。

本作 voxelchunk 同为 16³ = 4096 方块，**体积恰好等于 MC 的一个 section**；本作按 MC 同款
**20 tick/秒**节奏执行（`PEA_GROWTH_TICK_INTERVAL = 0.05f`，`World.Update` deltaTime 累加 +
while 补 tick，低帧率不丢 tick）：每 chunk 每 tick 抽 `PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK = 3`
个位置 —— **与 MC 每 section 每 tick 3 次 1:1 对齐**（不是整列 chunk 48 次/tick 那个口径）。

**节奏公式**（单阶段期望时间）：

```
期望单阶段 ≈ CHUNK_VOLUME / (RANDOM_TICKS × tick 频率) / ADVANCE_CHANCE
           ≈ 4096 / (3 × 20) / (1/3) ≈ 205 秒
三阶段全熟 ≈ 3 × 205s ≈ 10 分钟
```

## 3. 设计定案

- **随机刻**：**20 tick/秒**（`PEA_GROWTH_TICK_INTERVAL = 0.05f`），每 chunk 每 tick 抽
  `PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK = 3` 个随机位置（MC 每 section 每 tick 3 次，1:1 对齐；
  `System.Random`，主线程游玩随机，与生成确定性契约无关——地物/地形生成不用它）；
  命中 `PeaStem` 且阶段 < 3 时以 `PEA_GROWTH_ADVANCE_CHANCE = 1/3` 概率推进阶段（阶段只进不退）。
- **阶段即状态**：生长阶段存方块状态位（`BlockBits.StageMask` bit16-17，0-3），
  随机刻只碰方块（`WithStage` → `SetBlock` 置 changed → 下一帧自动重建 mesh），**不碰 tile 字典**。
- **GrowthTime 退役**：MC 无进度计数器，删除 `GrowthTime` 全链路（`PeaTileData` / `TileSaveRecord` /
  `TickPeaGrowth` 阈值逻辑 / 回挂构造），存档载荷 tile 记录 14B → 10B。
- **存档升级 v3**：写入恒为 v3（magic `'V''3'`，记录 `ushort key | uint genome | int generation`）；
  读路径 v1/v2/v3 三版判别，v1/v2 旧档仍可读（v2 的 GrowthTime 段读出后丢弃）。

## 4. 两格高植株（阶段 2/3）

阶段链：**0 最小苗（单格）→ 1 苗（单格）→ 2 两格高植株 → 3 开花结果（两格高）**。

- **顶部格是新方块 `BlockType.PeaPlantTop = 9`**（MC tall plant 式：可被射线命中、可存档、参与破坏联动）；
  顶部格**无 tile**（tile 只在底部 PeaStem 上）。
- **贴图**：底部格（PeaStem 阶段 2/3）用 `PeaTextures.PlantBottomCell = (2,5)`，
  顶部格（PeaPlantTop）用 `PlantTopCell = (2,4)`（用户已绘制，绝不运行时覆盖）；
  原 (2,1)/(2,0) 运行时占位退役（`PaintAtlasPlaceholders` 已停用绘制）。
- **随机刻 1→2 补顶**（`ChunkStore.TickPeaRandomTicks` + `TryEnsurePlantTop`）：
  - 上方格必须为 Air（MC tall plant 式空间检查；顶部在相邻 chunk 时读邻居，未加载则本次跳过）；
  - 先 `SetBlock(PeaPlantTop)`（跨 chunk 安全，未加载返回 false），**写入成功才推进底部到阶段 2**；
  - 上方被占 → 卡住不推进，下次随机刻再试。
- **阶段 2→3**：只推进底部阶段，顶部格不动（视觉暂与阶段 2 相同，花/荚贴图待后续替换）。
- **顶部格不生长**：随机刻抽到 `PeaPlantTop` 直接 continue。
- **破坏联动**（`BlockInteraction.TryBreakBlock`）：
  - 破坏底部（阶段≥2）→ 置 Air + RemoveTile（底部持 tile），上方 PeaPlantTop 一并置 Air；
  - 破坏顶部 → 顶部置 Air + 下方 PeaStem 退回阶段 0（**不 RemoveTile**，基因保留可继续生长）。
- **旧档修复**（`ChunkStore.RepairPeaPlants`，ChunkStreamer 创建 chunk 后调用）：
  旧档阶段≥2 的 PeaStem 无顶部格，加载后三向扫描修复——阶段≥2 底部缺顶补顶 /
  孤儿顶部（下方非阶段≥2 PeaStem）置 Air / y=0 层 Air 格若下方（邻居 y=15）是阶段≥2 底部则补顶；
  跨 chunk 读用 `GetChunkBlocks`（null=未加载跳过，等邻居创建时自身修复轮兜底）、写用 `SetBlock`。

## 4. 文件改动清单

| 文件 | 改动 |
|------|------|
| `World/ChunkStore.cs` | `TickPeaGrowth(dt)` → `TickPeaRandomTicks()`（无参）；新增主线程 `System.Random _random` |
| `World/World.cs` | `Update()` 随机刻调用点（保留 1s 累加器，不再传 dt） |
| `World/PeaTileData.cs` | 删除 `GrowthTime` 字段与构造函数赋值 |
| `World/ChunkStreamer.cs` | tile 回挂去掉 `{ GrowthTime = ... }`；创建 chunk 后调 `store.RepairPeaPlants(pos)` |
| `World/Saver.cs` | `TileSaveRecord` 删 GrowthTime；`Compress` 产 v3；`DecompressPayload` 三版判别；常量 `V2_TILE_RECORD_SIZE=14` / `V3_TILE_RECORD_SIZE=10` |
| `Constants.cs` | 删 `PEA_STAGE_1/2/3_SECONDS`；`PEA_GROWTH_TICK_INTERVAL=0.05f`（20 tick/s，MC 同款）；`PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK=3`；`PEA_GROWTH_ADVANCE_CHANCE=1/3` |
| `World/Block/Block.cs` | `BlockType.PeaPlantTop=9`；BlockBits 阶段语义注释更新 |
| `World/Block/BlockRegistry.cs` | 新增 `PeaPlantTop` 单例 + GetBlock 分支 |
| `World/Block/PeaTextures.cs` | 新增 `PlantTopCell(2,4)`/`PlantBottomCell(2,5)`；阶段 2/3 占位绘制退役 |
| `World/Block/MeshData.cs` | 新增 `AddPeaQuadCell(x,y,z,cell)`；`AddPeaQuad(stage)` 阶段 2/3 改走 PlantBottomCell |
| `World/Block/ChunkMeshBuilder.cs` | PeaPlantTop 分支（PlantTopCell）+ 不剔除名单；PeaStem 阶段≥2 用 PlantBottomCell |
| `Player/BlockInteraction.cs` | 破坏联动两分支（底→清顶 / 顶→底部退回阶段 0）；新增 `GetBlockAt` |

## 5. 验证方法（Play Mode）

1. 删档起新世界 → 豌豆丛生成（阶段 0 最小苗）。
2. 停留观察：豌豆在随机刻下逐阶段推进（约 3~3.5 分钟一阶段、10 分钟全熟），阶段只进不退；
   阶段 2 起变为**两格高**（底 (2,5) / 顶 (2,4) 两段十字面片），阶段 3 顶部不动。
3. 阶段 1→2 空间卡住：在豌豆正上方一格放方块 → 到达阶段 1 后不再长高（卡住），移开方块后恢复生长。
4. 破坏联动：打掉两格高植株底部 → 顶部一并消失、tile 移除；打掉顶部 → 底部退回阶段 0（tile 保留可再长）。
5. 中途退出重启（不删档）→ 已推进的阶段与顶部格保留（阶段存方块状态位，PeaPlantTop 随块数据自然存档）。
6. 用 v2/v3 旧档启动 → 正常读回（tile 基因/世代保留，GrowthTime 丢弃）；旧档阶段≥2 豌豆经
   `RepairPeaPlants` 自动补出顶部格，孤儿顶部被清除。
7. 全熟豌豆不再生长（阶段 3 判停）。

## 6. 存档兼容性

- **v1**（纯块数据 16384B）：读回正常，tiles 空。
- **v2**（magic `'V''2'`，记录 14B）：读回正常，GrowthTime 段读出后丢弃。
- **v3**（magic `'V''3'`，记录 10B）：新写格式。
- 判别依据：解压长度 + magic + 各版本记录尺寸长度校验，损坏抛 `InvalidDataException` → 回退重新生成。
