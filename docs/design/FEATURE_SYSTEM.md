# 地物系统（Feature System）

> 状态：**已实现（2026-08-11）**。生成期地物框架 + 两个地物（树、豌豆自然生成）。
> 关联：`GAME_DESIGN.md`（环境装饰）、`TODO_LIST.md`「其他待办 - 豌豆自然生成」。

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
├── Feature.cs      # 抽象基类（契约见 §2）
├── TreeFeature.cs  # 香樟风球冠树（从 TerrainGenerator 1:1 搬迁）
└── PeaFeature.cs   # 豌豆自然生成（密度哈希 + 确定性基因 + tile 登记）
```

装配点：`TerrainGenerator` 构造函数 `features = new Feature[] { new TreeFeature(), new PeaFeature() };`
顺序即放置优先级（树先、豌豆后；豌豆靠 Air 检查避让树干）。

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

### 4.2 豌豆自然生成（PeaFeature）

- `CanPlace`：
  - 密度哈希：`(blockX·7 + blockZ·13 + groundY·29) % PEA_FEATURE_DENSITY == 0`
    （`PEA_FEATURE_DENSITY = 64` → 约 1/64 列一棵，越小越密）；
  - 目标格 Air 检查：`data.GetBlocksData()[lx, anchorLocalY, lz]` 为 Air 才可放（避让树干）。
- `Place`：
  - `data.Setblock(BlockRegistry.GetBlock(BlockType.PeaStem), lx, anchorLocalY, lz)`（默认状态 = 阶段 0）；
  - 登记 tile：`data.AddPendingTile(key, genome)`，key 公式与 `ChunkStore.TileKey` 一致
    `(x<<8)|(y<<4)|z`，世代 0、生长时间 0；
  - **确定性基因**：`(blockX·73856093 ^ groundY·19349663 ^ blockZ·83492791) & 0x0FFFFFFF`
    （28 bit 装得下），保证同坐标重启后基因一致。

## 5. tile 出站通道（pendingTiles）

生成期地物若需要 tile（如豌豆），后台线程通过 `VoxelChunkData.AddPendingTile(ushort key, Genome genome)`
登记纯值记录（`TileSaveRecord`，已在 `Saver.cs` 定义）；主线程在 `ChunkStreamer.Tick` 的
`store.CreateChunk` 成功后，把 **loadedTiles（存档读回）与 pendingTiles（地物生成）两来源统一回挂**
到 chunk 的 tile 字典（同一转换路径），再进入既有生长/存档流程。

## 6. 如何新增地物（三步）

1. 在 `Assets/Scripts/World/Feature/` 写子类：`CanPlace` 做确定性哈希 + 可用性检查，
   `Place` 用 `data.Setblock` 写方块（跨界自动进 pendingBlocks）；需要 tile 就
   `data.AddPendingTile(key, genome)`。
2. 在 `TerrainGenerator` 构造函数装配进 `features` 数组（顺序 = 放置优先级）。
3. 需要新参数（如密度）时加进 `Constants.cs`，不硬编码魔数。

## 7. 线程与确定性注意事项

- 后台生成线程**绝不碰**：tile 字典、GameObject、`Application.*`、`Time.*`、`Genome.Random()`、
  `System.Random`（共享静态源多线程竞争会破坏确定性）。
- 基因派生只用列坐标算术（纯函数），无任何共享可变状态。
- 读存档命中的 chunk **跳过地物生成**（存档数据优先，玩家修改不丢失）。

## 8. 验证方法（Play Mode）

1. 删除存档（`Application.persistentDataPath/world_saves/`）后启动 → 树外观与原版完全一致；
   草地上随机出现阶段 0 豌豆苗（最小苗贴图）。
2. 记下某棵豌豆的世界坐标 → 退出重启（不删档）→ 同坐标豌豆分布一致、基因/生长阶段保留
   （存档 v2 读回 + pendingTiles 回挂链路）。
3. 豌豆苗随时间进入 苗→开花→结果（贴图/十字面片高度逐阶段变化），卸载重载后阶段不倒退。
