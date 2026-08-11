# 方块更新机制（Block Update System）

> 状态：**设计定案（2026-08-11），分步实施中**。目标：把"随机刻 / 方块联动 / 计划刻"三类更新从硬编码特判收敛为统一分派中心，未来新方块（沙子、树苗、水流等）只需加分派分支。
> 关联：`GROWTH_RANDOM_TICK.md`（随机刻现状）、`FEATURE_SYSTEM.md`（地物）、`TODO_LIST.md`（实施条目）、`AGENTS.md`（架构说明）。

## 1. 背景

实施前，"更新"均为硬编码：

| 机制 | 现状 | 问题 |
|---|---|---|
| 随机刻 Random Tick | `ChunkStore.TickPeaRandomTicks`（20Hz，MC 同款） | 豌豆专用，新方块只能继续加 if |
| 方块联动 Block Update | 破坏顶/底联动写在 `BlockInteraction.TryBreakBlock` 特判 | 无通用机制，逻辑散在交互层 |
| 计划刻 Scheduled Tick | 无 | 无 |

MC 的三类更新（Random Tick / Block Update / Scheduled Tick）本作按需对齐。渲染层 mesh 重建（changed → 下一帧 rebuild）与逻辑更新**解耦**，不并入本机制。

## 2. 架构

### 2.1 分派中心 `BlockUpdateCenter`（主线程，非 MonoBehaviour，World 持有，注入 store 引用）

```
方块写入（运行时唯一入口 store.SetBlock）
   │
   ├─ 变化检测：old != new，相等则跳过（优化 + 防循环）
   ├─ 写入 + changed（mesh rebuild 走现有链路，解耦）
   └─ 未 suppressUpdate → 触发 BlockUpdate 通知：
        ① 本位置     DispatchBlockUpdate(pos, source)
        ② 6 邻居     DispatchBlockUpdate(neighborPos, source)  // 跨 chunk 安全，未加载跳过
              └─ 联动写入再走 store.SetBlock → 递归通知，深度上限 MAX_BLOCK_UPDATE_DEPTH 防爆栈
```

### 2.2 三类触发

- **Random Tick**：20Hz（`World.Update` while 补 tick），每 chunk 每 tick 抽 `PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK=3` 位 → `DispatchRandomTick(pos)` 按方块类型分派
- **Block Update**：方块变化（Place/Break/StateChange/NeighborChanged）→ 本位置 + 6 邻居分派
- **Scheduled Tick**：`ScheduleTick(pos, delayTicks)` → tick 计数器到期执行 `DispatchScheduledTick(pos)`（按 chunk 分组存储，chunk 卸载丢弃；存档计划刻后置）

事件源枚举：`BlockUpdateSource { Place, Break, StateChange, NeighborChanged }`。

### 2.3 分派表（集中式 switch，不引入 OOP 注册表）

```csharp
// BlockUpdateCenter 静态分派，按 BlockType switch：
//   PeaStem      → 随机刻推进（含阶段 1→2 补顶）/ 邻居变化（顶部消失→退阶段 0）
//   PeaPlantTop  → 随机刻无操作 / 自身被破坏→下方退阶段 0
// 未来方块：switch 加分支
```

### 2.4 写入路径收敛

- **运行时**：一律 `store.SetBlock`（已支持跨 chunk，未加载返回 false），在其中统一发通知；`vc.SetBlock` 降为底层，仅供 store 内部
- **生成期**（后台线程 `data.Setblock` → pendingBlocks 主线程重放）：**不触发更新**（世界刚生成无需联动）——重放路径须经带 suppress 标记的写入（如 `store.SetBlock(..., suppressUpdate: true)` 重载或专用内部入口）

### 2.5 线程纪律

全部主线程（延续现有纪律）。后台生成线程绝不触碰 BlockUpdateCenter。

## 3. 实施步骤

### Step A：随机刻泛化（行为零变化）
- `TickPeaRandomTicks` 豌豆逻辑迁入 `BlockUpdateCenter.DispatchRandomTick` 类型分派；豌豆路径原样保留（补顶原子性、顶部格跳过）
- 验证：Play Mode 豌豆生长节奏与实施前完全一致

### Step B：BlockUpdate 通知 + 联动迁移（核心）
- `store.SetBlock` 加变化检测 + 本位置/6 邻居通知；递归深度上限
- 破坏联动从 `BlockInteraction` 特判迁入更新中心：破坏 PeaPlantTop → 下方 PeaStem 退阶段 0（tile 保留）；破坏 PeaStem（阶段≥2）→ 上方 PeaPlantTop 清除
- `BlockInteraction` 只剩"置 Air + 请求 mesh 重建"
- 验证：破坏顶/底行为与现在完全一致；跨 chunk 边界联动正常

### Step C：ScheduledTick 计划刻（机制，暂不接方块）
- tick 计数器 + 按 chunk 的待执行列表（位置 + 触发 tick），到 tick 执行 `DispatchScheduledTick`
- 提供 `ScheduleTick(pos, delayTicks)` API；chunk 卸载丢弃对应计划刻
- 暂不接入任何新方块（沙/水流后置，需新方块与贴图）

## 4. 明确不做（本期范围外）

- 存档计划刻/更新队列（等需求出现再扩展存档格式）
- 红石级高频更新
- 后台线程更新

## 5. 边界与风险

- **性能**：破坏一个方块触发 7 通知 + 可能递归，视距 12 规模可忽略；深度上限防失控
- **跨 chunk**：邻居未加载跳过通知（现状 RepairPeaPlants 等兜底不变）
- **旧档**：纯机制重构，存档格式零变化
- **行为回归**：Step A/B 行为不变重构，Play Mode 对比验证
- **已知遗留**：生成期重放 suppress 语义需实现时确认（pendingBlocks 主线程重放路径不得触发通知）

## 6. 验证方法（Play Mode）

1. Step A 后：豌豆生长节奏与之前一致（单阶段 ≈ 205s）
2. Step B 后：破坏顶 → 底退阶段 0（tile 保留可再长）；破坏底 → 顶清除；跨 chunk（y=15/16）同验
3. Step C 后：`ScheduleTick` 到期触发（调试日志验证）；卸载 chunk 后计划刻丢弃不报错
