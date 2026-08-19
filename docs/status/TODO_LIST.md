# VerdantThreads 任务列表

> 更新：2026-08-11　|　当前活跃任务列表（历史全绿任务总表已归档至 `docs/archive/TODO_HISTORY.md`）

## 已完成 ✅

- [x] 修复材质 `_MainTex` 悬空引用（guid 断裂导致全白）
- [x] 修复豌豆占位图画错像素位置（16px→24px cell 网格）
- [x] 修复豌豆邻居面被错误剔除（PeaStem 加入不剔除名单）
- [x] `Saver.cs` 增加读取/加载路径（`TryLoadVoxelChunk` 接入 `TerrainGenerator`）
- [x] 统一方向/面索引命名（单一 `Direction` 枚举，`FaceIndex` 已删除）
- [x] 清理死代码（`GenerateVoxelChunk` 重复体 / `AsyncSaver` / `BasicTree` 已删除）
- [x] `Saver.cs` 去魔数（已全部改用 `Constants`）
- [x] 更新 `AGENTS.md` 并归档旧版（`docs/archive/AGENTS_OLD.md`）
- [x] **Step 0** 存档续写修复：`SimpleRegionWriter` 改 `OpenOrCreate` + 读旧索引续写（含旧扇区复用防膨胀）
- [x] **物品栏与背包系统**（阶段二，`docs/design/INVENTORY_SYSTEM.md`）：`ItemInstance` + `Backpack`（选择状态唯一权威）+ `HotbarWindow`（9 槽/图集图标/左上角 1-9/选中高亮）+ `BackpackWindow`（E 键/点击选中）；`BlockInteraction` 放置改读 Backpack
- [x] **地物系统（Feature）**（`docs/design/FEATURE_SYSTEM.md`）：生成期地物抽象（`Feature` 基类 + `TerrainGenerator` 锚点装配）；树从 `TerrainGenerator` 内嵌代码 1:1 搬入 `TreeFeature`（外观不变）；新增 `PeaFeature` 豌豆自然生成（密度哈希 + 确定性基因 + `AddPendingTile` 通道，主线程 CreateChunk 后与存档读回统一回挂）
- [x] **豌豆丛生（PeaClumpFeature）**（`docs/design/PEA_CLUMP_FEATURE.md`）：豌豆单株生成改为丛生（每丛 14-18 株聚簇，中心密度哈希 + 半径内确定性 jitter；2026-08-11 调参：密度 64→256、株数 3-6→14-18、半径 2→3）；整丛共享母本基因 + 每株株坐标哈希确定性微变异（1-2 个等位基因位 0↔1 翻转）；tile 通道升级为世界坐标版 `pendingTileWrites`（`ChunkStreamer` 新增平行重试队列 `_pendingTileWritesQueue`，跨 chunk 块走 pendingBlocks / tile 走新通道，两条路在目标 chunk 汇合）；`PeaFeature.cs` 已删除、`PEA_FEATURE_DENSITY` → `PEA_CLUMP_DENSITY`/`MIN`/`MAX`/`RADIUS` 常量
- [x] **豌豆生长改随机刻（MC 式，存档 v3）**（`docs/design/GROWTH_RANDOM_TICK.md`）：生长由计时阈值（20/40/60s，过快）改为 MC 式随机刻——**20 tick/秒**（`PEA_GROWTH_TICK_INTERVAL=0.05f`，`World.Update` while 补 tick），每 chunk 每 tick 抽 `PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK=3` 个随机位置（MC 每 section 每 tick 3 次 1:1 对齐），命中 `PeaStem` 且阶段<3 时以 `PEA_GROWTH_ADVANCE_CHANCE=1/3` 推进（阶段只进不退，存方块状态位 StageMask）；期望单阶段 ≈ 205s、三阶段全熟 ≈ 10 分钟；**GrowthTime 退役**（`PeaTileData`/`TileSaveRecord`/`TickPeaGrowth` 全链路删除）；存档载荷升级 v3（tile 记录 14B→10B，magic `'V''3'`），读路径 v1/v2/v3 三版判别、旧档兼容；`PEA_STAGE_1/2/3_SECONDS` 已删除
- [x] **豌豆两格高植株**（阶段链 0 最小苗→1 苗→2 两格高植株→3 开花结果）：顶部格新方块 `BlockType.PeaPlantTop=9`（MC tall plant 式，可命中/存档/破坏联动，无 tile）；随机刻阶段 1→2 先占顶部格（`TryEnsurePlantTop`：上方必须 Air、跨 chunk 安全、成功才推进底部）后推进，顶部格不生长；贴图阶段 0/1 用 CellByStage((2,3)/(2,2))、阶段 2/3 底部 `PlantBottomCell(2,5)` / 顶部 `PlantTopCell(2,4)`（用户绘制，`PaintAtlasPlaceholders` 占位绘制退役）；破坏联动（打底→清顶+RemoveTile / 打顶→底部退回阶段 0 保留 tile）；旧档修复 `ChunkStore.RepairPeaPlants`（创建 chunk 后三向扫描补顶/清孤儿顶，跨 chunk 读邻居未加载跳过）

## 方块更新机制（Block Update System，设计见 docs/design/BLOCK_UPDATE_SYSTEM.md）✅

| 步骤 | 内容 | 状态 |
|------|------|------|
| Step A | 随机刻泛化：豌豆逻辑迁入 `BlockUpdateCenter.DispatchRandomTick` 类型分派，行为零变化 | ✅ |
| Step B | BlockUpdate 通知：`store.SetBlock` 变化检测 + 本位置/6 邻居通知 + 递归深度上限；破坏联动从 BlockInteraction 特判迁入更新中心 | ✅ |
| Step C | ScheduledTick 计划刻：tick 计数器 + 按 chunk 待执行列表 + `ScheduleTick(pos, delayTicks)` API；chunk 卸载丢弃；暂不接新方块 | ✅ |
| 支撑检查 | 豌豆下方支撑被挖（`NeighborChanged`）或空中放置（`Place`）→ 植株掉落（方块 + tile 移除，顶部格经 Break 联动清除） | ✅ |
| 阶段 3 花贴图 | 开花植株按基因（花色 紫/白 × 花位置 腋生/顶生）选 8 张花贴图；`PeaTextures.GetFlowerCells` + mesh 快照携带 `TileGenomes`/`TileGenomesBelow`（跨 chunk 顶部格） | ✅ |
| 验证 | Play Mode：A 后生长节奏不变；B 后破坏联动一致（含跨 chunk）；C 后计划刻到期触发；支撑掉落；阶段 3 四种花型 | ✅（玩家已确认） |

## 基因系统路线图（架构评审 @oracle 已定案）⏳

**核心结论：不引入完整 NBT。**

采纳 Minecraft「BlockState 轻量 uint + BlockEntity 稀疏 tile 数据」分离架构；NBT 仅是将来 tile 的序列化格式，不是块存储模型。

- **Block uint 保留**：类型（bit0-15）+ 生长阶段（bit16-17，`StageMask` 不变）
- **废除** bit18-24 的 7 个孟德尔预留位（1 bit 无法区分杂合 Aa/AA，错误设计），改为 bit18-31 共 14 bit 通用渲染状态预留
- **基因编码**：7 位点 × 2 等位 × 每等位 2 bit 打包进 uint32，封装 `Genome` struct（访问器 / Crossover 纯位运算 / Mutate）；2/3 等位值预留突变与稀有等位
- **碱基序列**：现阶段不设计，将来进 tile 变长 payload（`byte[]`），genome 作为其"表达投影"；tile 段格式需提前预留 `payloadLen` 字段
- **存档**：`.vrf` 版本 1→2，压缩载荷内嵌 tile 段，外层扇区格式不动；v1 旧档兼容读、写入时原地升级，零迁移

### 任务步骤

| 步骤 | 内容 | 状态 |
|------|------|------|
| Step 0（前置） | 修复 `SimpleRegionWriter` 用 `FileMode.Create` 整文件重建隐患 → 改 `OpenOrCreate` + 读旧索引续写（含旧扇区复用防膨胀）；已实现并验证 ✅ | ✅ |
| Step 1a | 废除 Block uint bit18-24 孟德尔预留位，改为 14 bit 通用渲染状态预留 | ✅（Block.cs 注释 + `WithStage`） |
| Step 1b | `Genome` struct（7 位点 × 2 等位 × 2 bit 打包 uint32 + 访问器 / Crossover / Mutate）+ `PeaTrait` 性状表（与批2 2a/2b 合并做） | ✅（`Genetics/Genome.cs` + `Genetics/PeaTrait.cs`） |
| Step 1c | `VoxelChunk` 挂 `Dictionary<ushort, PeaTileData>` tile 字典（key = 块内线性索引 `(x<<8)\|(y<<4)\|z`）；`PrepareForPool` / `ResetForReuse` 必须清空 | ✅ |
| Step 1d | 种植豌豆创建 tile（默认/随机 genome + 世代 0）；破坏移除 tile；写入路由镜像 `store.SetBlock` 的跨 chunk 分发 | ✅ |
| Step 1e | 生长 tick（主线程挂 `World.Update`）：遍历豌豆 tile 按时间推进 stage → 写回 Block uint → changed → 网格重建；结荚可采收（网格路径零改动） | ✅ |
| Step 1f | 存档 v2：`SaveVoxelChunk` 带 tile 段；读路径 v1/v2 分支；v1 自动升级 | ✅（载荷自描述，VRF1 文件头不动，v1 零迁移兼容） |
| Step 1g | 验证：种豌豆 → 存档 → 重启 → 阶段与基因保留 | ✅（玩家已确认 Play Mode 验证通过） |
| Step 2 | 遗传/育种：采收种子携带双亲基因 → 种植 Crossover；突变；世代参与表型（全在 tile 内，不动 Block/网格/存档格式） | ⏳ |
| Step 3 | 碱基序列：启用 tile 变长 payload（**HTT 载荷机制已定案**，见 `docs/design/HTT.md`）；碱基序列 `byte[]` + 序列级突变；genome 作为投影重算 | ⏳ |
| Step 2+ 泛化 | 等第 2 个复杂方块出现再做：tileType 标签 + 注册表分派 | ⏳ |

## 豌豆采收系统（设计定案，见 docs/design/HARVEST_SYSTEM.md）⏳

| Phase | 内容 | 状态 |
|-------|------|------|
| Phase 0 | **物品系统重构**：新增 `ItemType` 枚举（独立于 `BlockType`）；`ItemInstance` 持 `ItemType` + 可选 `PlaceableBlockType`；增加 `phenotypeTags`/`genotypeTags` 标签字段；重构 `Backpack` 构造器 | ✅（2026-08-14） |
| Phase 1 | **堆叠系统 + 背包存档**：背包支持堆叠（上限 64，按表型合并，内部分基因型计数）；`BackpackSaver`（二进制 `BPK1` 格式 → `backpack.dat`）；种子袋容器（右键打开，上限 1024）；`World.cs` 装配 | ✅（2026-08-14） |
| Phase 2 | **采收逻辑 + 表型推导**：右键拦截豌豆（阶段≥3，含中段/顶端）；青嫩豆荚 / 豌豆荚（**采摘次数与产量由 8 个新基因共同控制**，`HarvestGenome` 存 HTT 载荷，见 HARVEST_SYSTEM.md §5.2 + `docs/design/HTT.md`）；采收后回退阶段 2，次数耗尽整株枯萎（`PeaWithered` 方块，玩家破坏去除）；vrf v4 + BPK1 v2 存档升级 | ⏳ 设计已定案（2026-08-16 修订）；**Step 0（HTT 载荷机制）✅、Step 1（PeaHarvestCalculator + 常量 + 随机 HarvestGenome）✅、Step 2（HarvestMask + PeaWithered）✅、Step 3（ChunkMeshBuilder 枯萎十字面片 + 不剔除名单）✅、Step 4（BlockUpdateCenter RevertToStage2/WitherPeaPlant + 枯萎破坏联动）✅、Step 5（表型标签子集重载 + 显式标签构造器 + BackpackSaver 缺口修复）✅、Step 6（BlockInteraction 右键采收全流程）✅**，Step 7（Play Mode 验证）待玩家执行 |
| Phase 3 | **分解（后置）**：手持右键分解豌豆荚 → 4~8 粒豌豆种子；优先存入种子袋 | ✅（2026-08-19，见 WORKLOG_2026-08-19；Play Mode 验证待玩家执行） |

## 其他待办 ⏳

- [ ] 豌豆自然生成：草地上随机生成 `PeaStem`（生成规则后续再定）
- [ ] 树外观低概率瑕疵（悬空/草皮空隙/冠层奇偶/局部坐标，历史遗留 P3-13，低优先级）——**实施计划已定案**：`docs/design/TREE_APPEARANCE_FIX.md`（待玩家确认外观决策后实施）
