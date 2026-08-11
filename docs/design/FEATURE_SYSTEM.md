# 地物系统（Feature System）

> 状态：**已实现（2026-08-11）**。生成期地物框架 + 两个地物（树、豌豆丛生）。
> 关联：`GAME_DESIGN.md`（环境装饰）、`TODO_LIST.md`「其他待办 - 豌豆自然生成」、`PEA_CLUMP_FEATURE.md`（丛生设计定案）。

## 1. 概念

地物（Feature）= 生成期在已填充地形上放置装饰物的**纯生成逻辑单元**。原树生成内嵌在
`TerrainGenerator.GenerateVoxelChunkData` 的列循环里（`HasTree` + 球冠树代码），现统一搬入
`World/Feature/` 抽象框架，新增地物只需写一个子类并装配。

## 2. Feature 契约（`World/Feature/Feature.cs`）

```csharp
public abstract class Feature
{
    // 判定是否在本列放置（确定性哈希 + 目标格可用性检查）
    public abstract bool CanPlace(VoxelChunkData data, int blockX, int groundY, int blockZ);
    // 执行放置：通过 data.Setblock 写方块；需要 tile 时记入 data
    public abstract void Place(VoxelChunkData data, int blockX, int groundY, int blockZ);
}
```

- **纯生成逻辑**：只读输入参数 + 写 `VoxelChunkData`，不碰任何 Unity 主线程对象
  （GameObject/Time/Transform 等），可在后台生成线程安全调用。
- **确定性**：同坐标 + 固定 seed 必须产生同结果；**禁止 `System.Random` / `Genome.Random()` / `DateTime`
  等非确定源**（`Genome.Random()` 时间种子非确定，且共享静态 Random 后台多线程不安全）。
- **跨界写入**：一律走 `data.Setblock`（块外坐标自动进 `pendingBlocks`，由主线程在目标 chunk
  加载后重放）；地物自身不得直接访问 `ChunkStore`/`world`。
- **签名约束**：纯值参数、返回 void、无 Unity 对象。

## 3. 文件结构

```
Assets/Scripts/World/Feature/
├── Feature.cs         # 抽象基类（契约见 §2）
├── TreeFeature.cs     # 香樟风球冠树（从 TerrainGenerator 1:1 搬迁）
└── PeaClumpFeature.cs # 豌豆丛生（整丛母本基因 + 每株微变异，见 PEA_CLUMP_FEATURE.md）
```

装配点：`TerrainGenerator` 构造函数 `features = new Feature[] { new TreeFeature(), new PeaClumpFeature(heightAt) };`
顺序即放置优先级（树先、豌豆丛后；豌豆丛靠 Air 检查避让树干）；`heightAt` 为列高度纯函数
（与地形填充同公式，后台线程安全）。

锚点逻辑（`GenerateVoxelChunkData` 列循环尾部，地形填充完成后）：

```csharp
int anchorY = baseHeight + 4;                    // 地表上方一格（树干基部/豌豆落脚格）
int anchorLocalY = anchorY - pos.Y * CHUNK_SIZE; // 锚点必须落在本 chunk 的 Y 范围内
if (anchorLocalY >= 0 && anchorLocalY < CHUNK_SIZE)
{
    foreach (var feature in features)
    {
        if (feature.CanPlace(data, blockX, anchorY, blockZ))
            feature.Place(data, blockX, anchorY, blockZ);
    }
}
```

> 锚点 Y 范围判定等价于原树生成的 `(baseHeight+4 < maxY) && (baseHeight+4) >= (maxY - CHUNK_SIZE)`
> 守卫，树外观行为不变。

## 4. 现有地物

### 4.1 树（TreeFeature）—— 1:1 搬迁

- `CanPlace`：`(blockX²·13 + groundY·17 + blockZ²·19) % 128 == 37`（即原 `HasTree`）。
- `Place`：块内坐标 `lx = blockX & 15`、`lz = blockZ & 15`（原列循环局部 x/z，等价），
  `realY = groundY % 16`（原 `(baseHeight+4)%16`），其余公式（trunkHeight / crownRadius /
  trunkTop / crownCenterY / 球方程逐层半径 / 树干格跳过 / 边缘缺角）逐字保留。
- 跨界写入（树干/树冠越出本 chunk）由 `Setblock → pendingBlocks` 处理，与原先一致。

### 4.2 豌豆丛生（PeaClumpFeature，详见 `PEA_CLUMP_FEATURE.md`）

- `CanPlace`：只在**丛中心列**返回 true
  - 密度哈希：`(blockX·7 + blockZ·13 + groundY·29) % PEA_CLUMP_DENSITY == 0`
    （`PEA_CLUMP_DENSITY = 256` → 约 1/256 列一丛，越小越密）；
  - 中心格 Air 检查：`data.GetBlocksData()[lx, anchorLocalY, lz]` 为 Air 才可放（避让树干）。
- `Place`：由中心坐标确定性派生整丛（14-18 株聚簇）
  - 母本基因：丛中心世界坐标哈希派生（28 bit）；
  - 株数、半径内每株偏移（确定性 jitter）——全部由中心哈希派生，同坐标重启结果一致；
  - 每株高度取**它自己列**地表+1（构造函数注入 `heightAt` 纯函数）；
  - 每株基因 = 母本 + 株坐标哈希驱动的 1-2 个等位基因位 0↔1 翻转（确定性微变异）；
  - 每株独立 Air 检查（仅本 chunk 内有效）；跨 chunk 株：块走 `Setblock → pendingBlocks`、
    tile 走 `AddPendingTileWrite → pendingTileWrites`，两条路在目标 chunk 汇合。

## 5. tile 出站通道（pendingTileWrites，世界坐标版）

生成期地物若需要 tile（如豌豆丛），后台线程通过 `VoxelChunkData.AddPendingTileWrite(BlockPosInWorld pos, Genome genome)`
登记**世界坐标**纯值记录；主线程在 `ChunkStreamer.Tick` 的平行队列 `_pendingTileWritesQueue` 按帧预算路由：
目标 chunk 已加载 → `store.SetTile` 转局部 key 写入；视距内未加载 → 下帧重试；视距外丢弃
（与 `_pendingSetBlocksQueue` 语义完全一致）。丛内每株 = 块走旧通道 + tile 走新通道，两条路在
目标 chunk 汇合。存档读回（loadedTiles）仍在 `CreateChunk` 成功后直接回挂，与本通道互不影响。

## 6. 如何新增地物（三步）

1. 在 `Assets/Scripts/World/Feature/` 写子类：`CanPlace` 做确定性哈希 + 可用性检查，
   `Place` 用 `data.Setblock` 写方块（跨界自动进 pendingBlocks）；需要 tile 就
   `data.AddPendingTileWrite(worldPos, genome)`（世界坐标通道，主线程自动路由到目标 chunk）。
2. 在 `TerrainGenerator` 构造函数装配进 `features` 数组（顺序 = 放置优先级）。
3. 需要新参数（如密度）时加进 `Constants.cs`，不硬编码魔数。

## 7. 线程与确定性注意事项

- 后台生成线程**绝不碰**：tile 字典、GameObject、`Application.*`、`Time.*`、`Genome.Random()`、
  `System.Random`（共享静态源多线程竞争会破坏确定性）。
- 基因派生只用列坐标算术（纯函数），无任何共享可变状态。
- 读存档命中的 chunk **跳过地物生成**（存档数据优先，玩家修改不丢失）。

## 8. 验证方法（Play Mode）

1. 删除存档（`Application.persistentDataPath/world_saves/`）后启动 → 树外观与原版完全一致；
   草地上随机出现**一丛一丛**的阶段 0 豌豆苗（14-18 株聚簇，详见 `PEA_CLUMP_FEATURE.md` §6）。
2. 记下某丛豌豆的世界坐标 → 退出重启（不删档）→ 同坐标豌豆分布/株数/每株基因完全一致，
   生长阶段保留（存档 v2 读回 + pendingTileWrites 路由链路）。
3. 豌豆苗随时间进入 苗→开花→结果（贴图/十字面片高度逐阶段变化），卸载重载后阶段不倒退。
