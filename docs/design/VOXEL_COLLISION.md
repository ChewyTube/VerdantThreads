# 方案 C：自定义体素碰撞系统设计

> 创建：2026-08-20　|　状态：**已实现（待 Play Mode 验收）**
> 关联：`docs/design/ITEM_DROP.md`（Q 键扔出物品，本方案为其物理底座）；`docs/design/INVENTORY_GRID.md`（背包网格，提供 TakeFromSelected/AddItem API）
> 决策：体素游戏不用 Unity 物理引擎（PhysX 为连续网格设计，非体素网格），采用 MC 同款自定义体素碰撞——长期可持续、性能最优、引擎无关（规避 Tuanjie fork 物理差异风险）。
> 实施记录（2026-08-20）：P1-P5 全部完成。P1 `VoxelCollision`；P2 `PlayerController`（CameraMove.cs 重命名保留 meta guid，场景引用不破）；P3 `DroppedItem` + `DroppedItemManager` + `ItemIcon` 提取（Backpack.AddItem 改为返回剩余数）；P4 Q/Shift+Q 扔出；P5 背包内 Q + 掉落物合并 + 玩家位置存档（掉落物持久化按 §8.6 暂不做）。

## 1. 总览

```
┌─────────────────────────────────────────────────┐
│  VoxelCollision（物理核心，静态类）              │
│  AABB vs 体素世界 · 逐轴分解移动 · 方块重叠采样  │
└──────────────┬──────────────────────────────────┘
               │ 复用
   ┌───────────┴───────────┐
   ▼                       ▼
PlayerController        DroppedItem
（玩家身体+重力+跳跃）   （掉落物实体+代码物理）
   │                       │
   └─────── 都走同一套体素碰撞 ───────┘
```

核心思想：**一套碰撞系统服务所有实体**（玩家、掉落物、未来生物/投射物），与现有 DDA 射线、`world.IsSolid` 同构。

## 2. 物理核心：`VoxelCollision`（新增 `Assets/Scripts/Physics/VoxelCollision.cs`）

### 2.1 方块重叠采样
AABB 覆盖的方块范围 = 对 AABB 各轴 min/max 取 `FloorToInt`，遍历检查 `isSolid(x, y, z)`：
- 玩家 0.6×1.8×0.6 → 最多 2×3×2 = **12 次方块查询/轴**，极廉价。
- 掉落物 0.25³ → 1×1×1 = 1 次查询。

### 2.2 逐轴分解移动（MC 同款，防卡角）
```
Move(center, halfExtents, ref velocity, dt, isSolid):
    1. X 轴：center.x += vx*dt → 若重叠固体 → 吸附到方块面，vx = 0
    2. Y 轴：center.y += vy*dt → 同上（落地/撞顶）
    3. Z 轴：center.z += vz*dt → 同上
```
逐轴解析保证斜向移动不卡方块棱角（MC 碰撞的核心技巧）。

### 2.3 接口设计
```csharp
public static class VoxelCollision
{
    // 单轴移动 + 解析：返回该轴实际位移与是否碰撞
    public static bool MoveAxis(Vector3 center, Vector3 halfExtents, int axis,
                                float delta, out float moved, Func<Vector3Int, bool> isSolid);

    // 完整移动（X→Y→Z 逐轴），velocity 会被碰撞归零
    public static void Move(Vector3 center, Vector3 halfExtents, ref Vector3 velocity,
                            float dt, Func<Vector3Int, bool> isSolid);
}
```
- `isSolid` 由调用方注入（World 提供 `IsSolid` 包装），**碰撞系统不依赖 World 具体实现**，可测试。
- 世界边界守卫：`y < 0` 或 `y >= 256` 视为固体（防掉出世界）。

## 3. 玩家角色：`PlayerController`（新增，替换 `CameraMove`）

### 3.1 身体模型（MC 同款尺寸）
| 参数 | 值 |
|---|---|
| AABB | 0.6 × 1.8 × 0.6（halfExtents 0.3/0.9/0.3） |
| 眼睛高度 | 1.62 |
| 重力 | ~28 blocks/s² |
| 跳跃初速 | ~8.5 blocks/s（跳高 ≈ 1.25 格，MC 同款） |
| 步行速度 | ~4.3 blocks/s |
| 台阶高度 | 0.5（水平被挡时自动上台阶） |

### 3.2 位置约定
`transform.position` = **眼睛位置**（相机直接渲染，无需子物体）；身体 AABB 中心 = `(x, y - 1.62 + 0.9, z)`，脚底在 `y - 1.62`。

### 3.3 行为
- **WASD**：相对相机 yaw 的水平移动（复用现有 `CameraMove` 的鼠标视角逻辑）。
- **Space**：跳跃（仅在地面时）；**Shift**：下蹲/下降（飞行模式）。
- **台阶**：水平移动碰撞时，尝试"上移 0.5 → 水平移动 → 下移"（MC 自动上台阶）。
- **飞行模式（Ctrl+F）**：无重力、可上下飞（Space/Shift），**仍有方块碰撞**（不能穿墙）。
- **调试模式（Ctrl+D）**：无重力 + **穿墙**（noclip，直接移动 transform，原型阶段测试地形必需）。
- 每帧：`velocity.y -= gravity*dt` → `VoxelCollision.Move(...)` → 落地时 `velocity.y = 0`。

### 3.4 与现有代码的关系
- `CameraMove.cs` 被 `PlayerController` 取代（或改造为飞行分支）。
- `BlockInteraction` 的 DDA 射线**保持不变**（本来就是体素射线，与方案 C 一致）。
- 相机出生点逻辑（`World.Start` 的 `cameraSpawnPos`）不变。

## 4. 掉落物：`DroppedItem`（新增 `Assets/Scripts/World/DroppedItem.cs`）

### 4.1 实体
```csharp
public class DroppedItem : MonoBehaviour
{
    public ItemInstance Item;   // 物品模板
    public int Count;           // 数量（Q 扔 1，Shift+Q 扔整组）
    private Vector3 velocity;   // 初速度（向前上方，MC"扔"手感）
    private float spawnTime;    // 出生时间（5 分钟消失）
}
```

### 4.2 代码物理（复用 VoxelCollision）
- AABB：0.25³。
- 每帧：`velocity.y -= gravity*dt` → `VoxelCollision.Move(...)`。
- 落地：`velocity.y = 0` + 小幅反弹（×0.3）+ 水平摩擦衰减；速度趋零后停止模拟（只渲染）。

### 4.3 渲染（billboard）
- Quad（两个三角形）子物体，材质 = `WorldManager.Instance.BlockMaterial`（图集材质）。
- UV = 物品图标 cell——**先把 `CalcIconUVRect` 提取为公共静态方法**（`ItemIcon.GetUVRect(ItemInstance)`），BackpackWindow / HotbarWindow / DroppedItem 三处共用。
- 每帧 Quad 旋转面向相机。

### 4.4 拾取
- 玩家身体 AABB 与掉落物 AABB 重叠（或距离 < 1.5）→ `Backpack.AddItem(item, count)`。
- 背包满放不下 → 不拾取（或剩余部分保留实体）。
- 消失：5 分钟（常量可配）。

### 4.5 数量上限
- 全场景掉落物上限 64 个，超出丢弃最老的（防刷爆）。

## 5. Q 键集成（`BlockInteraction.cs`）

- `Input.GetKeyDown(KeyCode.Q)` → 选中槽非空 → `TakeFromSelected(1)` → 生成掉落物：
  - 位置：`transform.position + transform.forward * 1.5f + Vector3.up * 0.5f`
  - 初速度：`forward * 3 + up * 2`（MC"扔"手感）
- **Shift+Q**：扔出整组（`GetSlotCount(SelectedIndex)`）。
- **背包窗内 Q**（Phase 2）：`BackpackWindow` 里按 Q 扔悬停槽物品（当前 `BlockInteraction` 在背包打开时早退）。

## 6. 装配与生命周期（`World.cs`）

- `World.Awake`：创建 `PlayerController`（挂相机 GameObject）、`DroppedItemManager`（普通 class，持有掉落物列表）。
- `World.Update`：驱动玩家物理（非飞行时）+ 掉落物 tick（物理/拾取/消失）。
- 掉落物生成入口：`DroppedItemManager.Spawn(item, count, position, velocity)`。

## 7. 分阶段实施

| 阶段 | 内容 | 验证点（Play Mode） |
|---|---|---|
| **P1 物理核心** | `VoxelCollision`（逐轴解析 + 方块采样 + 边界守卫） | 用测试 AABB 撞墙/落地，无穿透、无卡角 |
| **P2 玩家角色** | `PlayerController` 替换 `CameraMove`（行走 + 重力 + 跳跃 + 台阶 + F 飞行） | 走路不穿墙、跳上 1 格、自动上 0.5 台阶、飞行正常 |
| **P3 掉落物实体** | `DroppedItem` + 代码物理 + billboard 渲染 + `ItemIcon` 提取 | 生成掉落物落地不穿透、图标正确 |
| **P4 Q 键集成** | Q / Shift+Q 扔出 + 拾取 + 5 分钟消失 + 64 上限 | 扔出→走回→拾取回背包；堆叠正确 |
| **P5 打磨（可选）** | 背包内 Q、掉落物合并、玩家位置/掉落物存档 | 全链路无回归 |

## 8. 风险与注意

1. **`CameraMove` 替换**：自由飞行是原型测试刚需——必须保留 F 飞行模式，否则开发体验倒退。
2. **碰撞边界情况**：高速下落穿透 → 逐轴 + 必要时子步进（`MAX_SUBSTEPS`）；角落卡住 → 逐轴解析已解决大部分。
3. **性能**：方块采样是 O(1) 查询，无每帧分配（避免 LINQ/装箱）；掉落物上限防刷爆。
4. **`ItemIcon` 提取是重构**：需回归验证背包/热栏图标。
5. **世界边界**：Y<0 / Y≥256 视为固体，防玩家/掉落物掉出世界。
6. **存档**：玩家位置与掉落物持久化建议 Phase 5 再做（先不做则退出即重置/消失）。