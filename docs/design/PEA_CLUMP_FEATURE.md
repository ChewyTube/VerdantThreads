# 豌豆丛生（Pea Clump）设计定案

> 状态：**设计定案，待实施（2026-08-11）**。尚未动代码。
> 关联：`FEATURE_SYSTEM.md`（地物框架，本设计将替换其 §4.2 的 `PeaFeature` 单株生成）、`GAME_DESIGN.md`（环境装饰）。

## 1. 概念与目标

- **一丛豌豆 = 一个地物（Feature）**：不再逐列散点单株，而是以一个「丛」为单位聚集生成
- 一群豌豆（3-6 株）在中心周围一定半径内聚集，模拟真实豌豆的丛生/分蘖形态
- 天然一丛与玩家种植（单株）在玩法语义上区分：自然世界呈现为「一丛一丛」，后续采摘/育种围绕丛展开

## 2. 设计决策（玩家定案）

**整丛共享一个母本基因 + 每株微小变异（模拟一丛是同株分蘖）。**

基因派生规则：

- **母本基因**：由**丛中心世界坐标**确定性哈希派生（28 bit，`& 0x0FFFFFFF`，与现有 `PeaFeature` 同约束）
- **每株基因**：母本基因 + 由**该株自身世界坐标**哈希派生的确定性微小变异
  - 变异方式参考 `Genome.Mutate`（逐等位基因重掷），但**必须用位置哈希驱动，禁止 `System.Random` / `Genome.Random()`**（时间种子非确定 + 共享静态源后台多线程不安全，破坏地物确定性契约）
  - 变异率小（如单株 1-2 个等位基因位点翻转），使丛内株间基因高度相似但不完全相同——符合「同株分蘖」直觉
- 结果：丛内每株基因 = 母本 ± 微小差异；不同丛之间基因明显不同

## 3. 现状约束（为什么需要这些改动）

1. **Feature 契约是「单列」的**：`CanPlace/Place` 在 `TerrainGenerator` 列循环内每列调用一次，地物自身无跨列状态
2. **tile key 是 chunk 局部坐标**：`VoxelChunkData.AddPendingTile` 的 key 为 `(x<<8)|(y<<4)|z` 的 ushort，**只能登记本 chunk 内的格子**；一丛半径 2-3 格、chunk 宽 16，几乎必然跨 chunk 边界
3. **Feature 只知道本列 groundY**：丛内其他株落在邻列，斜坡上地面高度不同，需要按株取各自列高度

## 4. 改动设计

### 4.1 跨 chunk tile 路由通道（关键基础设施，通用可复用）

- `VoxelChunkData` 新增 **`pendingTileWrites`**：`List<(BlockPosInWorld pos, Genome genome)>`（世界坐标版 tile 记录），与 `pendingBlocks` 平行
- `ChunkStreamer` 新增平行重试队列 `_pendingTileWritesQueue`，**完全复用 `_pendingSetBlocksQueue` 语义**：
  - 目标 chunk 已加载 → 转局部 key `SetTile`
  - 目标 chunk 在视距内未加载 → 下帧重试
  - 视距外 → 丢弃（与现有 block 行为一致）
- 丛内每株 = **块走旧通道**（`Setblock → pendingBlocks`）+ **tile 走新通道**，两条路在目标 chunk 汇合
- 该通道是通用基础设施：未来带 tile 的地物（浆果丛、作物田等）直接复用
- 备选方案（**已否决**）：把丛限制在 chunk 内部（中心距边界 ≥ 半径）——浪费大半 chunk 面积

### 4.2 PeaClumpFeature（重写现有 PeaFeature）

- **CanPlace**：只在**丛中心列**返回 true：中心哈希 `(blockX·7 + blockZ·13 + groundY·29) % PEA_CLUMP_DENSITY == 0`；中心列锚点 Y 范围守卫沿用现有生成器逻辑，保证**每丛恰好被一个 chunk 生成一次**
- **Place**：由中心坐标确定性派生整丛：
  - 株数（如 3-6）、半径（如 2-3）、每株偏移（确定性 jitter）——全部由中心坐标哈希派生，同坐标重启结果一致
  - 每株高度取**它自己列**的 groundY + 1（见 §4.3）
  - 每株独立 Air 检查（避让树干/树冠，沿用现状的树先放、豌豆后放顺序）
  - 每株 `Setblock(PeaStem)` + 按 §2 规则派生基因并登记 tile
- 实施时定：类名 `PeaFeature` → `PeaClumpFeature`（或保留类名只改逻辑），`TerrainGenerator` 装配处同步更新

### 4.3 邻列高度获取（最小的契约扩展）

- 给 `PeaClumpFeature` 构造函数注入高度函数 `Func<int,int,int> heightAt`（或抽 `IHeightAwareFeature` 接口，实施时定）
- `TerrainGenerator` 装配时传入自己的高度公式：`(bx, bz) => (int)((noise.GetNoise(bx, bz) + 1) * 0.5f * 64)` —— 纯函数，后台线程安全，不破坏确定性
- `TreeFeature` 不需要（单列，直接用传入的 groundY），不动

### 4.4 常量（Constants.cs）

- `PEA_FEATURE_DENSITY` → `PEA_CLUMP_DENSITY`（丛中心频率，含多株故分母增大，实施时调参）
- 新增 `PEA_CLUMP_MIN_PLANTS` / `PEA_CLUMP_MAX_PLANTS`（株数范围）
- 新增 `PEA_CLUMP_RADIUS`（聚集半径）
- 新增丛内变异相关常量（变异位点数/概率）

## 5. 对既有系统的影响

- 丛内每株仍是普通 `PeaStem + tile`：**生长 tick、存档 v2、未来的采摘（批2 2h）全部照旧工作**，零格式变更
- 旧存档语义：存档命中的 chunk 走读路径不重新生成地物（既有行为，不涉及迁移）
- 采摘/育种影响（Step 2）：丛内采收的种子携带相近但不同的基因，天然构成「同一母本的分蘖后代」实验素材

## 6. 验证方法（Play Mode）

1. 删档 → 草地上出现**一丛一丛**的豌豆（3-6 株聚簇），不再是单株散点
2. 斜坡上：丛内每株都正确落在自己列的地表+1，无悬浮/埋地
3. 沿 chunk 边界走一圈：丛跨边界时两侧株都在、tile 齐全，生长正常
4. 丛内基因：各株 = 母本 ± 微小变异（差异位数符合设定），不同丛基因差异明显
5. 重启不删档 → 每丛位置、株数、每株基因与阶段完全一致
6. chunk 卸载重载 → 边界株不丢（新通道回放正确）、阶段不倒退

## 7. 实施衔接

- 实施时同步更新 `FEATURE_SYSTEM.md` §4.2（单株 → 丛生）与 §5（跨 chunk tile 路由通道）
- 实施时可复用 fix-2 会话（已含 TerrainGenerator / VoxelChunkData / ChunkStreamer / VoxelChunk / Saver / Genome 上下文）
