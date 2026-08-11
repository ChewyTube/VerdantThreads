# 豌豆记录与标签系统（批2）设计定案

> 创建：2026-08-11　|　依据讨论搭档与作者对话 + 实现者确认问答定案。
> 状态：已定案，待实现。

## 1. 玩法定位

豌豆遗传玩法核心 = 解密、推理兼收集。玩家观察植株性状 → 采集豌豆/豆荚 → 手动记录标签 → 杂交实验 → 推理真实基因型。

## 2. 已定决策

1. **表现型植株可见**：母本性状（花色、株高等）植株模型直接可见，无需 UI；暂用占位颜色区分（现有十字面片占位贴图已满足）。
2. **采集交互**：右键命中 `PeaStem` 时拦截（不再放置方块），按成熟度分产物：
   - 苗期（stage 0）：不可采，OnGUI 提示。
   - 开花期（stage 1）：获得 **1 个「青嫩豆荚」**（种子性状未定型，不可观察）。
   - 结荚期（stage 2）：获得 **3~5 个「成熟豆荚」**（种子圆/皱、黄/绿可观察）。
   - 豆荚数量多，保证杂交实验样本量。
3. **双重标签系统**：每颗豌豆/豆荚物品两类标签并存：
   - 表现型标签：观察所得，如「紫花」「高茎」「饱满豆荚」。
   - 基因型标签：推理所得，如「杂合」「显性纯合」。
4. **底层与认知层分离**：物品底层存**真实基因组**（`Genome`，uint32 打包），**绝不向玩家暴露**（提示框/日志/UI 均不可见，`#if UNITY_EDITOR` 除外）。标签是玩家认知层数据，与底层独立。
5. **标签输入**：完全自由文本 + 预设点选填入文本框（引导而非强制）。预设覆盖 7 对性状全部表现型（14 个）+ 4 个基因型分类。
6. **标签标准化**：后期种子库筛选前，将自由文本标签映射到系统预设分类；本次只做预留入口 + 调试向导（F9）。
7. **系统确认推理（预留接口）**：`bool ValidateGenotypeTag(ItemInstance item, string tag)`，支持多性状断言（如「紫花杂合,高茎显性纯合」），具体调用时机由后续关卡决定。

## 3. 技术确认结论

| 问题 | 结论 |
|---|---|
| 标签存储位置 | `ItemInstance` 实例挂载（phenotypeTags / genotypeTags 两个 `List<string>`），不做管理器映射 |
| UI 底座 | **IMGUI（OnGUI）**最小背包 + 标签编辑窗，零场景依赖（项目无任何 UI 基础设施） |
| 预设配置 | `TagPresetConfig : ScriptableObject`（CreateAssetMenu）+ 代码内建默认兜底 |
| 背包存档 | **本次做**，NBT 式极简 tag 树，独立文件 `world_saves/backpack.dat`，与 genome 一起序列化 |
| Genome 来源 | 本次实现 `Genome` struct；采摘时**每株随机生成一次**，同次采摘产物共享（植株 tile 系统落地后改为读 tile，接口预留） |

## 4. 实现清单

### 新增
- `Assets/Scripts/Genetics/Genome.cs` — uint32 打包 7 位点 × 2 等位 × 2bit；访问器 / 显性判定 / `Random()` / `Crossover` / `Mutate`（纯位运算，等位值 2/3 预留突变）
- `Assets/Scripts/Genetics/PeaTrait.cs` — 7 对性状定义表（名称/显性表现型/隐性表现型/关键词），预设与验证共用
- `Assets/Scripts/Inventory/ItemInstance.cs` — 物品实例（itemType / genome / 两类标签 / 标准化映射）
- `Assets/Scripts/Inventory/Backpack.cs` — 物品列表（**非堆叠**，每颗豆荚是个体，带自身 genome）
- `Assets/Scripts/Inventory/TagPresetConfig.cs` — 预设配置 ScriptableObject
- `Assets/Scripts/UI/BackpackWindow.cs` — OnGUI 背包窗（E 开关，键位已由 F 调整为 E，见 `docs/design/INVENTORY_SYSTEM.md`；右键行或「标签」按钮打开编辑窗）
- `Assets/Scripts/UI/TagEditorWindow.cs` — OnGUI 标签编辑（两类标签**分区+异色**；文本框 + 预设按钮填入）
- `Assets/Scripts/UI/StandardizeWindow.cs` — 调试标准化向导（F9）
- `Assets/Scripts/Save/BackpackSaver.cs` — NBT 式 tag 树序列化
- `Assets/Scripts/Genetics/GenomeValidator.cs` — 预留验证接口 + 多性状重载

### 修改
- `Assets/Scripts/Player/BlockInteraction.cs` — 右键分发：命中 PeaStem → 采摘；否则照旧放置
- `Assets/Scripts/World/World.cs` — 装配 Backpack + BackpackSaver（加载/退出保存）
- `Assets/Scripts/Constants.cs` — 背包存档名、采摘数量等常量

## 5. 约束

- 全代码全局命名空间、注释/日志中文、常量进 `Constants.cs`
- 真实 genome 绝不暴露（除 `#if UNITY_EDITOR`）
- 背包存档独立于 .vrf 地形存档
- 7 对性状（GDD §6）：圆粒/皱粒、黄子叶/绿子叶、紫花/白花、豆荚饱满/皱缩、绿豆荚/黄豆荚、花腋生/顶生、高茎/矮茎
