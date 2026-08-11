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
| Step 1a | 废除 Block uint bit18-24 孟德尔预留位，改为 14 bit 通用渲染状态预留 | ⏳ |
| Step 1b | `Genome` struct（7 位点 × 2 等位 × 2 bit 打包 uint32 + 访问器 / Crossover / Mutate） | ⏳ |
| Step 1c | `VoxelChunk` 挂 `Dictionary<ushort, PeaTileData>` tile 字典（key = 块内线性索引 `(x<<8)\|(y<<4)\|z`）；`PrepareForPool` / `ResetForReuse` 必须清空 | ⏳ |
| Step 1d | 种植豌豆创建 tile（默认/随机 genome + 世代 0）；破坏移除 tile；写入路由镜像 `store.SetBlock` 的跨 chunk 分发 | ⏳ |
| Step 1e | 生长 tick（主线程挂 `World.Update`）：遍历豌豆 tile 按时间推进 stage → 写回 Block uint → changed → 网格重建；结荚可采收（网格路径零改动） | ⏳ |
| Step 1f | 存档 v2：`SaveVoxelChunk` 带 tile 段；读路径 v1/v2 分支；v1 自动升级 | ⏳ |
| Step 1g | 验证：种豌豆 → 存档 → 重启 → 阶段与基因保留 | ⏳ |
| Step 2 | 遗传/育种：采收种子携带双亲基因 → 种植 Crossover；突变；世代参与表型（全在 tile 内，不动 Block/网格/存档格式） | ⏳ |
| Step 3 | 碱基序列：启用 tile 变长 payload；碱基序列 `byte[]` + 序列级突变；genome 作为投影重算 | ⏳ |
| Step 2+ 泛化 | 等第 2 个复杂方块出现再做：tileType 标签 + 注册表分派 | ⏳ |

## 批2：豌豆记录与标签系统（设计已定案，见 docs/design/TAG_SYSTEM.md）⏳

| 步骤 | 内容 | 状态 |
|------|------|------|
| 2a | `Genetics/Genome.cs`：uint32 打包 7 位点×2 等位×2bit，访问器/显性判定/Random/Crossover/Mutate | ⏳ |
| 2b | `Genetics/PeaTrait.cs`：7 对性状定义表（名称/显性/隐性表现型/关键词） | ⏳ |
| 2c | `Inventory/ItemInstance.cs` + `Inventory/Backpack.cs`：物品实例（itemType/显示名，genome/标签待 2a/2b 后扩展），非堆叠列表 | ✅ 基础已完成（物品系统） |
| 2d | `Inventory/TagPresetConfig.cs`：ScriptableObject 预设（14 表现型 + 4 基因型），代码内建默认兜底 | ⏳ |
| 2e | `Save/BackpackSaver.cs`：NBT 式极简 tag 树，落盘 `world_saves/backpack.dat` | ⏳ |
| 2f | `UI/BackpackWindow.cs`（E 开关，原定 F 已调整）+ `UI/TagEditorWindow.cs`（双分区异色）+ `UI/StandardizeWindow.cs`（F9 调试向导） | ⏳（BackpackWindow 基础已由物品系统完成，剩 TagEditor/Standardize） |
| 2g | `Genetics/GenomeValidator.cs`：`ValidateGenotypeTag` + 多性状重载（预留接口） | ⏳ |
| 2h | `BlockInteraction.cs`：右键分发（命中 PeaStem → 按成熟度采摘：开花 1 青嫩豆荚 / 结荚 3-5 成熟豆荚）；`World.cs` 装配 Backpack+Saver；`Constants.cs` 常量 | ⏳ |
| 2i | 验证：进 Play Mode 种豌豆→结荚→右键采摘→F 开背包看标签→编辑→F9 标准化→退出重进看背包存档 | ⏳ |

## 其他待办 ⏳

- [ ] 豌豆自然生成：草地上随机生成 `PeaStem`（生成规则后续再定）
- [ ] 树外观低概率瑕疵（悬空/草皮空隙/冠层奇偶/局部坐标，历史遗留 P3-13，低优先级）
