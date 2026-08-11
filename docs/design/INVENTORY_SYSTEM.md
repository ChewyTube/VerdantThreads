# 物品栏（热栏）与背包系统设计（物品系统前置）

> 创建：2026-08-11　|　状态：**已实现（2026-08-11，Play Mode 验证通过）**
> 关联：批2 设计定案 `docs/design/TAG_SYSTEM.md` —— 本次提前实现其 2c（物品数据层）与 2f（背包窗）的基础部分。

## 1. 定位与目标

玩家随时能看到两件事：

1. **当前拿着什么** —— 热栏（选中槽位高亮）
2. **背包里有什么** —— 背包窗

方案：**通用物品系统**（作者拍板）。数据模型一步到位，为批2 豌豆豆荚铺路，避免批2 再做一遍。

## 2. 已定决策

1. **UI 底座**：IMGUI（OnGUI），零场景依赖（沿用项目已定案方案，与批2 一致，不建 Canvas/UGUI）。
2. **数据模型**：`ItemInstance` + `Backpack`（批2 2c 提前实现）。
   - Backpack 初始装入现有全部可放置方块（数量无限，不做拾取/计数，保持现有无限放置行为）。
   - ItemInstance 最小结构：`itemType` + 中文显示名；预留批2 扩展（genome / 标签后续再加）。
3. **热栏**：底部居中 9 槽；图集贴图图标（`GUI.DrawTextureWithTexCoords`，UV 按 24px cell 换算，row 从图集底部起算）；选中槽高亮；**槽左上方标数字 1-9**。
4. **背包窗**：**E 键开关**；列出全部物品（图标 + 中文名）；点击物品可选中并同步热栏。
5. **状态归属**：选择状态只放一处 —— `Backpack` 持有物品列表 + `selectedIndex`，热栏 / 背包窗 / 放置逻辑全部读同一来源，避免状态分裂。

## 3. 实现清单

### 新增

- `Assets/Scripts/Inventory/ItemInstance.cs` — 物品实例（itemType / 显示名）
- `Assets/Scripts/Inventory/Backpack.cs` — 非堆叠物品列表 + selectedIndex（普通 class，非 MonoBehaviour）
- `Assets/Scripts/UI/HotbarWindow.cs` — 常驻热栏（9 槽、图集图标、选中高亮、槽左上角 1-9）
- `Assets/Scripts/UI/BackpackWindow.cs` — E 键开关背包窗（物品列表、点击选中）

### 修改

- `Assets/Scripts/Player/BlockInteraction.cs` — 放置逻辑改为读新物品系统当前选中类型；数字键 1-9 选择保留；破坏/放置行为与现状完全一致
- `Assets/Scripts/World/World.cs`（或合适装配点） — 创建 Backpack 实例并装配 UI / 交互引用（沿用项目现有依赖注入风格）
- `Assets/Scripts/Constants.cs` — 按键（E）、槽位数等常量

## 4. 与批2 的关系

- 本次提前实现批2 的 2c（ItemInstance / Backpack）与 2f（BackpackWindow）基础部分。
- ⚠️ **键位变更**：背包窗开关由批2 原定的 **F 改为 E**（F 预留批2 其他用途或另行安排）。
- 豌豆豆荚等批2 物品后续直接加入 Backpack 列表即可，数据模型无需改动。

## 5. 约束

- 纯 IMGUI，不建 Canvas/EventSystem；全局命名空间、注释/日志中文、常量进 `Constants.cs`。
- 不动图集 UV 常量（768 虚拟网格 / 24px cell）、存档、地形生成、mesh 管线。
- 破坏/放置行为与现状完全一致（含 Y=0 Bedrock 不可破坏等守卫）。

## 6. 验证（Play Mode）

1. 热栏显示 9 个带图标的槽位，槽左上角标 1-9。
2. 按 1-9 切换选中高亮。
3. E 键开关背包窗，显示 9 个物品（图标 + 中文名）。
4. 点击背包物品可选中，与热栏同步。
5. 放置/破坏行为与之前一致。
