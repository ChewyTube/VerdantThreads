# Q 键扔出物品功能设计计划

> 创建：2026-08-20　|　状态：**计划（未实施）**
> 关联：`docs/design/INVENTORY_GRID.md`（背包网格，本功能依赖其 `TakeFromSelected`/`AddItem` API）

## 1. 目标

按 Q 键把当前选中物品"扔出"到世界中，形成可见、可拾取的掉落物（MC 风格）。

## 2. 现状盘点（关键约束）

- **项目没有掉落物实体系统**（AGENTS.md 明确："there is no item-drop entity system"）——豌豆植株被破坏是直接消失。
- **地形是体素网格，没有物理 Collider**——`BlockInteraction` 的射线是 DDA 体素步进，不是 Physics.Raycast。所以掉落物**不能用 Rigidbody 自由落体**（会穿透地面）。
- 物品图标 UV 计算（`CalcIconUVRect`）目前**重复存在于** `BackpackWindow` 和 `HotbarWindow` 两个 UI 类里，掉落物渲染需要复用。
- 背包 API 已就绪：`TakeFromSelected(1)` 扣物品、`AddItem` 加物品、`CurrentSelected` 读选中。

## 3. 方案对比

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **A. 掉落物实体系统（推荐）** | 生成世界空间掉落物实体，代码模拟物理，走近拾取 | 最接近 MC，物品可见可捡 | 工作量最大 |
| B. 简化版 | Q 只从背包移除 1 个 + 日志 | 极简 | 不是真正的"扔出"，玩家看不到 |
| C. 折中版 | 可放置物品 Q 时直接放置为方块；非方块物品移除+日志 | 无需实体系统 | 非 MC 形态，豆荚/种子袋无法处理 |

**推荐方案 A**。注意：**地形无 Collider**，物理必须用代码模拟（体素碰撞），不能依赖 Unity 物理引擎。

## 4. 方案 A 详细设计

### 4.1 数据与实体 — 新增 `DroppedItem.cs`（MonoBehaviour）
- 字段：`ItemInstance item`、`int count`、`Vector3 velocity`、出生时间。
- 由 `World` 统一管理（列表），或独立 `ItemDropManager`（普通 class，World 持有）。

### 4.2 渲染 — billboard 十字面
- 掉落物 GameObject 挂一个 Quad（两个三角形），材质复用 `WorldManager.Instance.BlockMaterial`（图集材质）。
- UV 用物品图标 cell——**先把 `CalcIconUVRect` 提取为公共静态方法**（如 `ItemIcon.GetUVRect(ItemInstance)`，放 `Assets/Scripts/Inventory/` 或 `UI/`），三个调用方（BackpackWindow / HotbarWindow / DroppedItem）共用，消除重复。
- 每帧把 Quad 旋转面向相机（billboard），MC 掉落物是小方块，billboard 图标更简单且清晰。

### 4.3 物理 — 代码模拟（关键，因为地形无 Collider）
- 每帧：`velocity.y -= gravity * dt`（重力）；`position += velocity * dt`。
- 体素碰撞：查询脚下方块（`world.GetBlockAt` / `IsSolid`），落地则 `velocity.y = 0`、水平速度衰减（摩擦）、小幅反弹。
- 简单实现约 20-30 行，无需 Rigidbody/Collider。

### 4.4 拾取
- 每帧检测玩家与掉落物距离（< 1.5 格）→ `Backpack.AddItem(item, count)` → 销毁实体。
- 背包满时放不下 → 不拾取（或拾取后剩余部分重新生成实体）。
- 消失：5 分钟（MC 同款）后销毁，常量可配。

### 4.5 按键与交互
- `BlockInteraction.Update`：`Input.GetKeyDown(KeyCode.Q)` → `TakeFromSelected(1)` → 在玩家面前 1.5 格生成掉落物（初速度向前上方，MC 的"扔"手感）。
- **Shift+Q**：扔出整组（与 Shift 分解交互一致）——可选。
- **背包窗内 Q**：MC 支持在背包里按 Q 扔悬停物品。当前 `BlockInteraction` 在背包打开时早退，需在 `BackpackWindow` 里加 Q 处理（扔悬停槽物品）——建议 Phase 2 再做。

### 4.6 装配
- `World.Awake` 创建 `ItemDropManager`（或 World 直接持有列表），`World.Update` 驱动掉落物 tick（物理 + 拾取 + 消失）。
- 掉落物生成位置：`transform.position + transform.forward * 1.5f + Vector3.up * 0.5f`。

### 4.7 存档（可选）
- MC 会保存掉落物。本项目建议 **Phase 2 再做**：保存位置 + 物品 + 数量，加载时重建。先不做则退出即消失（作为已知限制记录）。

## 5. 分阶段实施

| 阶段 | 内容 | 验证点（Play Mode） |
|---|---|---|
| **P1 图标提取** | `CalcIconUVRect` 提取为公共方法，三处共用 | 背包/热栏图标无回归 |
| **P2 掉落物实体** | `DroppedItem` + 代码物理 + billboard 渲染 | Q 扔出物品，落地不穿透，图标正确 |
| **P3 拾取与消失** | 走近拾取 + 5 分钟消失 | 扔出→走回→拾取回背包；堆叠正确 |
| **P4 交互完善** | Shift+Q 整组扔、背包内 Q（可选） | 两种 Q 行为正确 |
| **P5 存档（可选）** | 掉落物持久化 | 重启后掉落物仍在 |

## 6. 风险与注意

1. **地形无 Collider**——物理必须代码模拟，这是最大的架构约束。
2. **图标 UV 提取**是重构，需回归验证背包/热栏图标。
3. **背包满时拾取**要处理（放不下不拾取或剩余回退实体）。
4. **掉落物数量**：Q 扔 1 个，实体 count=1；Shift+Q 扔整组 count=N，拾取时 `AddItem(item, N)`。
5. **性能**：掉落物数量上限（如 64 个）防刷爆，超出丢弃最老的。
6. **Q 键冲突**：确认 Q 未被占用（当前未占用）。