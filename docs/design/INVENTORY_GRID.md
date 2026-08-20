# MC 风格背包（4 行 × 9 列）设计计划

> 创建：2026-08-20　|　状态：**已实现（2026-08-20，P1-P5 全部完成，待 Play Mode 验收）**
> 关联：`docs/design/INVENTORY_SYSTEM.md`（现有物品栏/背包系统，本计划为其升级改造）

## 1. 目标

把现有纵向列表背包窗升级为 MC 风格固定网格背包：

- **4 行 × 9 列 = 36 格**，第 1 行（row 0，index 0-8）即热栏，row 1-3（index 9-35）为主背包。
- 背包窗内主背包 3 行在上、热栏 1 行在下（中间留 gap），与 MC 一致。
- 槽位持久化：物品放在哪格，重启后仍在哪格。

## 2. 现状盘点

| 组件 | 现状 | 与目标的关系 |
|---|---|---|
| `Backpack` | 动态 `List<StackSlot>`，槽随增删伸缩；`SelectedIndex` 唯一权威 | 需改为固定 36 格 |
| `StackSlot` | 模板 + Count + 内部分基因型计数 | 基本不动 |
| `HotbarWindow` | 底部 9 槽，显示 `slots[0..8]`，数字键 1-9 选中 | 天然对应 row 0，几乎不动 |
| `BackpackWindow` | 纵向列表（E 开关、右键种子袋/豆荚、拖拽交换） | 重写为 4×9 网格 |
| `BackpackSaver` | `BPK1` v2 二进制，动态槽列表，空槽写 -1 整档拒绝 | 升 v3 + 旧档迁移 |
| `BlockInteraction` | 数字键选热栏、右键分解/放置 | 小适配 |

## 3. 核心设计决策

1. **固定 36 格扁平数组**（与 MC 内部模型一致）：`index = row * 9 + col`，**row 0 = 热栏**（index 0-8），row 1-3 = 主背包（index 9-35）。空槽用 `null` 表示。
2. **`SelectedIndex` 保持 0-35 唯一权威**，选中槽永远存在（空槽选中 = 无物品），不再因移除槽而位移——反而简化现有 Shift 分解循环。
3. **存档升 v3**：写满 36 格（含空槽标记），槽位持久化；v1/v2 旧档顺序填入网格迁移。

## 4. 数据层改造

### 4.1 `Constants.cs` 新增

```csharp
public const int INVENTORY_ROWS = 4;        // 背包总行数（含热栏行）
public const int INVENTORY_COLUMNS = 9;     // 每行格数（= HOTBAR_SLOT_COUNT）
public const int INVENTORY_SLOT_COUNT = 36; // 总格数 = ROWS * COLUMNS
```

### 4.2 `Backpack.cs` 改造

- `slots`：`List<StackSlot>` → `StackSlot[INVENTORY_SLOT_COUNT]`，`null` = 空槽。
- **`Count` 语义变更**：从"当前槽数"改为"总格数 36"。影响面逐一核对：
  - `BackpackWindow` 窗口高度/行循环 → 网格重写后自然适配；
  - `HotbarWindow` 的 `i < backpack.Count` → 恒真，无害；
  - `BlockInteraction` 的 `Mathf.Min(9, Count)` → 恒 9，无害；
  - `BackpackSaver` 写 `Count` → 写 36，配合 v3 空槽标记。
  - 新增 `OccupiedCount`（非空格数）备用。
- `AddItem`：先遍历合并同表型未满槽（跳过 null），剩余找**第一个空槽**新建。
- `TakeFromSlot`：扣到 0 → `slots[index] = null`（**不再 `RemoveAt`，`SelectedIndex` 不再位移**）。
- `SwapSlots`：支持与空槽交换（null 参与交换）。
- `Select`：`Clamp(0, INVENTORY_SLOT_COUNT - 1)`。
- 新增辅助：`RowOf(index)`、`ColOf(index)`、`IndexAt(row, col)`。
- `ReplaceAll`：改为填充固定数组（存档加载用）。
- 构造器默认物品（草/泥土/石头/原木/树叶/基岩/豌豆种子/种子袋）放入 `slots[0..7]`（热栏），`SelectedIndex = 2` 不变。
- `AddPeaSeeds` / `DecomposePeaPod` 逻辑不变，但 `foreach (slots)` 需跳过 null。

### 4.3 `StackSlot.cs`

- 基本不变。空槽统一用 `null` 表示，不引入 `IsEmpty`。

## 5. 存档兼容（`BackpackSaver.cs`）

- `Version = 3`。
- **Save**：写 `INVENTORY_SLOT_COUNT`（36），每格先写 `bool isEmpty`，非空再写原字段序列（ItemType/DisplayName/Placeable/Genome/Count/基因型分布/标签/种子袋/HTT 载荷）。
- **Load**：
  - v3：读 36 格，空槽跳过；
  - v1/v2：读动态列表 → 顺序填入 `slots[0..n-1]`，其余空（旧档槽数少，无溢出风险；上限保护 512 改为 36 截断）。
- 删除"空槽写 -1 → 整档拒绝"的旧逻辑。

## 6. UI 层改造

### 6.1 `BackpackWindow.cs` 重写 `OnGUI` 为 4×9 网格

- 布局（MC 风格）：**主背包 3 行在上，热栏 1 行在下，中间留 gap**；窗口居中。
- 尺寸：`slotSize ≈ 44px`、间距 4px、margin 8px；窗口宽 ≈ `9*44 + 8*4 + 16`，高 ≈ `4*44 + 3*4 + gap + 标题`。
- 每格绘制：`GUI.Box` 边框 + 图标（复用现有 `CalcIconUVRect`）+ 右下角 `xN` 数量 + 选中高亮（沿用暖黄）。
- 交互（沿用现有逻辑，坐标换算 `row/col → index`）：
  - 左键点击 → `Select(index)`；
  - 左键拖拽 → 源槽/目标槽交换（含空槽）；
  - 右键种子袋 → 打开种子袋内容子面板（保留）；
  - 右键豌豆荚 → 分解（保留）。
- 种子袋内容子面板：位置改为贴新窗口右侧，行名"豌豆粒" + Tab 表型显示**保留**。
- 可选增强：悬停槽位显示物品名（`DisplayName`），按住 Tab 时对豌豆粒显示表型（与种子袋一致）。

### 6.2 `HotbarWindow.cs`

- 基本不动（`slots[0..8]` 即热栏）。
- 可选：鼠标滚轮切换热栏选中（`Input.mouseScrollDelta.y` → `Select(SelectedIndex ± 1)`，Clamp 到 0-8）。

## 7. 交互层适配（`BlockInteraction.cs`）

- 数字键 1-9 → `Select(0-8)`：不变。
- Shift 右键分解循环：槽清空后 `SelectedIndex` 不再位移，但保留 `CurrentSelected` 逐次校验（防御，逻辑不变）。
- 破坏/放置/采收：不变。

## 8. 分阶段实施

| 阶段 | 内容 | 验证点（Play Mode） |
|---|---|---|
| **P1 数据层** | Constants + Backpack 固定 36 格 + StackSlot 判空适配 | 编译通过；热栏仍显示默认 8 物品 |
| **P2 存档** | BackpackSaver v3 + 旧档迁移 | 放物品 → 重启 → 槽位/数量/种子袋内容保持 |
| **P3 网格 UI** | BackpackWindow 4×9 重写 | E 开背包见 4×9 网格；点击/拖拽/右键分解/种子袋子面板正常 |
| **P4 交互** | 滚轮切热栏（可选）+ BlockInteraction 适配 | 数字键/滚轮选中、Shift 全分解正常 |
| **P5 回归** | 全流程回归 | 放置/破坏/采收/分解/存档全链路无回归 |

## 9. 风险与注意

1. **`Count` 语义变更**是最大影响面——所有读 `Count` 的地方都要过一遍（见 4.2）。
2. **旧档迁移**：v1/v2 动态列表 → 固定网格，槽位顺序保留、超出 36 截断。
3. **空槽 null 遍历**：`AddPeaSeeds`/`AddItem` 的 foreach 必须判空，否则 NRE。
4. **拖拽/右键事件**：网格坐标换算（`row = (mouseY - gridTop) / (slotSize+spacing)`）要处理 margin/gap 偏移。
5. **种子袋子面板**：与主窗口的 z 序（后画覆盖）和位置（贴右侧）需适配新窗口尺寸。
6. **IMGUI 每帧多次调用**：`Input.GetKey`/`Event.current` 用法保持现状即可。