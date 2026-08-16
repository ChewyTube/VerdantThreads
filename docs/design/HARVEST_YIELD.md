# 豌豆采收产量说明（收获个数由什么决定）

> 记录：2026-08-16　|　关联：`docs/design/HARVEST_SYSTEM.md`（§5.2 多基因数量性状模型）、`docs/design/HTT.md`（载荷机制）、`Assets/Scripts/Genetics/PeaHarvestCalculator.cs`（公式实现）。

## 一、单次收获个数（产量）

由 **`PeaHarvestCalculator.GetYield(harvestGenome, stage)`** 决定（`Assets/Scripts/Genetics/PeaHarvestCalculator.cs:29-35`）：

| 阶段 | 产出物品 | 公式 |
|------|---------|------|
| 阶段 4（结果期） | 豌豆荚 | `12 + 2k` 个 |
| 阶段 3（开花期） | 青嫩豆荚 | `3 + k` 个 |

- **k** = 8 个采收基因位点中**纯合显性**（两等位皆 0）的数量，由 `CountHomozygousDominant` 统计（:11-19）
- 每位点纯合显性概率 1/4，期望 k = 2 → 典型产量：阶段 4 约 16 个、阶段 3 约 5 个

## 二、采摘次数上限（寿命）

由 **`PeaHarvestCalculator.GetHarvestLimit(harvestGenome)`** 决定（:22-26）：

```
采摘次数上限 = min(2^(1 + k), 64)     // 2~64，典型 8 次，期望 ≈ 12
```

- 存于方块 `HarvestMask`（bit20-26，`BlockBits.HarvestMask = 0x7F`），每次采收 -1，归 0 整株枯萎（`WitherPeaPlant`）
- 未初始化（0）时按公式惰性初始化

## 三、采收基因（harvestGenome）来源

- 植株 tile 的 **HTT 载荷** `"harvestGenome"` 键（`PeaTileData.GetHarvestGenome()`，`Assets/Scripts/World/PeaTileData.cs:21`）
- **自然生成株**：`PeaClumpFeature.cs:93` 用株坐标哈希 `new HarvestGenome(plantHash)` 确定性派生（全 32 位，无 System.Random）
- **玩家种植株**：`BlockInteraction` 种植处 `tile.SetHarvestGenome(HarvestGenome.Random())`（非确定性契约）
- **无载荷**（旧档/基线）→ 默认全隐性，k = 0 基线

## 四、调用链

```
BlockInteraction.TryHarvestPea()          // Assets/Scripts/Player/BlockInteraction.cs:122
  ├─ tile.GetHarvestGenome()              // :155  读采收基因（无载荷 → 默认 k=0）
  ├─ PeaHarvestCalculator.GetYield()      // :159  按阶段算产量
  ├─ 阶段4 → ItemInstance(PeaPod, 母本Genome) + Payload.SetInt("harvestGenome", ...)  // :163-165
  ├─ 阶段3 → ItemInstance(GreenBeanPod, 表型标签{2,5})                                // :169
  ├─ Backpack.AddItem(item, yield)        // :171  入背包
  ├─ 次数 -1（未初始化按 GetHarvestLimit 惰性初始化）                                  // :174-176
  └─ 归 0 → WitherPeaPlant / 否则 WithHarvests + RevertToStage2 + 网格重建             // :177-186
```

## 五、公式常量（`Constants.cs`「豌豆采收」段）

| 常量 | 值 | 用途 |
|------|-----|------|
| `HARVEST_LIMIT_BASE_EXPONENT` | 1 | 次数上限指数基数（`1 << (1 + k)`） |
| `HARVEST_LIMIT_CAP` | 64 | 次数上限封顶 |
| `YIELD_BASE_STAGE4` | 12 | 阶段 4 产量基数 |
| `YIELD_PER_DOMINANT_STAGE4` | 2 | 阶段 4 每纯合显性位点增量 |
| `YIELD_BASE_STAGE3` | 3 | 阶段 3 产量基数 |
| `YIELD_PER_DOMINANT_STAGE3` | 1 | 阶段 3 每纯合显性位点增量 |

调参只需改 `Constants.cs` 与 `PeaHarvestCalculator.cs`，其余链路（物品/存档/交互）零改动。

## 六、相关代码位置速查

| 文件 | 位置 | 内容 |
|------|------|------|
| `Assets/Scripts/Genetics/PeaHarvestCalculator.cs` | :11-19 / :22-26 / :29-35 | k 统计 / 次数上限 / 产量公式 |
| `Assets/Scripts/Genetics/HarvestGenome.cs` | 全文件 | 8 位点 × 2 等位 × 2 bit 打包 uint32 |
| `Assets/Scripts/World/PeaTileData.cs` | :21 / :28 | `GetHarvestGenome` / `SetHarvestGenome`（HTT 载荷访问器） |
| `Assets/Scripts/World/Feature/PeaClumpFeature.cs` | :93-94 | 自然生成株确定性采收基因 |
| `Assets/Scripts/Player/BlockInteraction.cs` | :122-188 | `TryHarvestPea` 采收全流程 |
| `Assets/Scripts/World/Block/Block.cs` | `BlockBits.HarvestMask` | 采摘次数状态位（bit20-26） |
| `Assets/Scripts/Constants.cs` | 「豌豆采收」段 | 6 个公式常量 |