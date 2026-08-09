# VerdantThreads 代码审查报告

> 审查日期：2026-08-09　|　审查方式：只读静态审查（全部 15 个脚本 + FastNoiseLite 内部实现）　|　**未修改任何代码**
>
> 行号基于审查时的源码，代码改动后可能漂移。每项均可独立对照源码核实。
>
> **实施进度：阶段一、阶段二已完成（2026-08-09）；卡顿修复与 P2 残留修复（异步保存/帧耗时预算/树冠重试/视距守卫）已完成。**

---

## 一、问题清单

### P0 — 崩溃 / 数据损坏 / 构建阻断

- [x] **P0-1 运行时脚本引用编辑器程序集（构建阻断）**
  - `World.cs:10`：`using static UnityEditor.PlayerSettings;`
  - `Assembly-CSharp`（运行时程序集）依赖仅编辑器存在的 `UnityEditor` 命名空间，任何独立构建（Standalone/IL2CPP）直接编译失败。全文未使用任何 `PlayerSettings.*` 成员，纯多余引用。
  - 建议：删除该行。零风险，最先修。

- [x] **P0-2 `Saver.SaveVoxelChunk(VCPosInWorld, uint[,,])` 字典 key 用错 + 覆写截断（潜在数据损坏）**
  - `Saver.cs:128-144`，关键在 `:132`：`_regionWriters.TryGetValue(vcPos, ...)` 用 **chunk 坐标**查字典，而字典语义上以 **region 坐标**为 key → 必然 miss → 每次新建 `SimpleRegionWriter`（`Saver.cs:190` `FileMode.Create` 直接截断整个区域文件）→ 同区域内此前保存的 chunk 全部丢失；writer 也永不 Dispose。
  - 触发场景：当前该重载**无调用方**（只有 Block[,,] 重载在 `World.cs:226` 使用），属"埋雷"，任何人调用即数据损坏。
  - 建议：参照 Block 重载（`Saver.cs:149-151`）把 key 改为 region 坐标；或直接删除该死重载。

### P1 — 功能错误

- [x] **P1-1 相机跨 chunk 事件被丢弃 → 地形缺失（可能"永不加载"）**
  - `World.cs:140`：`if (!await _chunkUpdateLock.WaitAsync(0)) return;` — 锁忙时本次 chunk 变化被静默丢弃。
  - `OnCameraChunkChanged` 单次执行含 `await Task.WhenAll`（可能跨多帧，尤其大量 chunk 待生成时），期间相机每跨一个 chunk 就触发一次（`World.cs:100` `_ = ...`），几乎必然丢事件；而 `lastVCPosCam` 已更新到最新位置（`World.cs:103`），停稳后不再补触发。
  - 触发场景：相机快速直线移动跨多个 chunk，最终停下的位置周围一圈 chunk 缺失。
  - 建议方向：锁忙不丢弃，记录最新 `VCPosCam` 待锁释放后重跑；并把"标记卸载"与"生成新 chunk"两阶段解耦。

- [x] **P1-2 chunk 流式补建上限过低 + 单次事件任务量过大 → 地形持续落后**
  - `World.cs:20`（`MAX_NEW_CHUNKS_PER_FRAME = 2`）与 `World.cs:178-188`（每次事件可 spawn (2·6+1)³ ≈ 2197 个位置，移动一步实际新增约 528 个）。
  - 补完一轮约需 250+ 帧（约 4 秒 @60fps），相机 moveSpeed=10 约 1.6 秒跨一个 chunk → 生成追不上移动，持续 pop-in/空洞。
  - 建议：提高每帧上限或按帧耗时预算动态消费（如 30-50 chunk/帧）；事件粒度只生成新暴露的环，不整盒扫描。

- [x] **P1-3 边界剔除是"快照"，邻居卸载后透空 / 内部可见**
  - `VoxelChunk.cs:203-256`（`ShouldBeEliminated`）+ `World.cs:219-232`（卸载）+ `World.cs:458`（MeshOptimize 仅创建时入队一次）。
  - 相邻 chunk 都加载时边界两面都剔除（正确）；但邻居卸载后剩余 chunk 的边界仍是"已剔除"状态 → 世界边缘呈中空、能看到地形内部横截面。反之邻居晚加载时先优化的 chunk 会保留多余隐藏面。
  - 建议：卸载时把相邻 chunk 重新入队 `_pendingMeshOptimizeQueue`（`ShouldBeEliminated` 已能处理"邻居 ERROR → 保留面"，`VoxelChunk.cs:233-237`），只需重新触发。

- [x] **P1-4 启动同步生成 864 chunk，同帧构建全部 mesh → 启动冻结**
  - `World.cs:317-327`（`GenerateWorld`）→ `CreateVoxelChunk` → 每个 `VoxelChunk.Start()`（`VoxelChunk.cs:51-76`）**同一帧**执行 `UpdateOrCreateMesh(true)`（含 `RecalculateNormals` + 3 次 ToArray）。
  - 864 个 GameObject + 864 次全量 mesh 构建集中在首帧，且随后 `World.Update` 还同步排空 `_pendingSetBlocksQueue`（树冠跨边界写入 → `Setblock` 同步再创建边缘 chunk）。预计 0.5~2s 卡死。
  - 建议：`GenerateWorld` 改为只产数据入队 `_pendingBuildQueue`（与流式同路径），或分帧创建。

- [x] **P1-5 `Setblock` 对未加载 chunk 的同步级联创建（主线程风暴）**
  - `World.cs:373-382` / `:397-406`：目标 chunk 缺失时**同步**执行 `GenerateVoxelChunkData`（4096 次块写入 + 256 次噪声）+ `CreateVoxelChunk`（建 GO + mesh）。
  - 树冠跨 chunk 写入（`World.cs:293-302` 的 ±2 叶子范围）进 `pendingBlocks`，主线程重放时若邻居未加载即触发同步生成；新 chunk 自己又可能带跨界树冠 → 再入队 → 级联。且新 chunk 直接 `world.Add`，不检查 lineOfSight，会超范围膨胀世界。
  - 建议方向：pending 写入目标缺失时丢弃（树冠本质是装饰）或仅入队数据；World 层加"只创建线内 chunk"守卫。

- [x] **P1-6 `CreateVoxelChunk` 硬 throw（"Repeatedly adding chunk"）**
  - `World.cs:442-447`。防御依赖"仅主线程改 `world` + await 续体回主线程"的隐式约定；`GenerateWorld`（`:325`）是唯一无防护调用点，仅靠"只调一次"维系。一旦续体落线程池或新增创建入口即爆炸。
  - 建议：改幂等（`if (world.ContainsKey(pos)) { Debug.LogWarning; return; }`），收敛所有创建入口到唯一函数。

- [ ] **P1-7 共享 `FastNoiseLite` 实例被多线程并发读（隐式约定，非当前 bug）**
  - `World.cs:32` + `:186`（`Task.Run`）+ `:249`（`noise.GetNoise`）。
  - 已逐行核实：`GetNoise → TransformNoiseCoordinate / GenFractalFBm / GenNoiseSingle` 只读实例字段、全部计算在局部栈上完成，无字段写入；`SetSeed/SetFrequency` 等写操作仅在 `World.Start` 的 `InitializeNoise` 一次，早于任何后台任务。**当前并发只读安全**。
  - 建议：加注释声明"配置仅在初始化期设置，此后只读"，避免未来有人加 `SetFractalXxx` 踩雷。

### P2 — 性能

- [x] **P2-1 每帧"64 块"上限实际按 List 条数计数** — `World.cs:116-124`：`setCount++` 在外层 while，内层 `foreach` 整条 List 无上限。一条 pendingBlocks List 可含数十个跨界叶子写入，一帧最多 64 条 List ≈ 上千次 `Setblock`（每次可能触发同步建 chunk）。建议按块数截断，队列尾部留到下帧。
- [x] **P2-2 `Setblock` 路径双重 enqueue pendingBlocks** — `World.cs:306-307`（生成内部已入队）与 `:377`/`:401`（又入队同一 List 引用）→ 写入全部翻倍（幂等但浪费）。删掉 `Setblock` 里那一次即可。
- [x] **P2-3 mesh 每重建 = 3 次 ToArray + `new Mesh` + `RecalculateNormals`，每 chunk 最多重建 3 次** — `MeshData.cs:114-126` + `VoxelChunk.cs:110-133`：受树影响的 chunk 经历 Start 首次构建 → pendingBlocks 重放 → MeshOptimize 共 3 次全量重建；旧 Mesh 悬挂到 GC（Unity native 对象回收不确定）。建议复用同一 Mesh 实例，`SetVertices/SetTriangles/SetUVs` 原地写，`RecalculateNormals` 按需。
- [x] **P2-4 卸载时主线程同步保存 + 每 chunk fsync** — `World.cs:226` → `Saver.cs:145-161` → `Saver.cs:221-226`（`Flush(true)` 即 fsync + 每次重写 128KB header）。相机大步移动时一次卸载数十 chunk = 主线程数十次磁盘 fsync。建议恢复被注释的 `AsyncSaver`（`Saver.cs:22-120`）或批量 flush。
- [x] **P2-5 FileStream 永不 Dispose** — `Saver.cs:123`（`_regionWriters`）+ `Saver.cs:228-236`（`Dispose` 从未被调用）。每探索一个新 region 泄漏一个 `FileStream`。建议 `World.OnDestroy` 遍历 Dispose。
- [ ] **P2-6 MeshOptimize 队列积压** — `World.cs:21`（2/帧）+ `:458`：初始 864 chunk 全入队，2/帧消费 → 初始积压约 7 秒（@60fps），期间边界不剔除，世界显"臃肿"。与 P1-2 一并按帧耗时预算调整。

### P3 — 设计 / 整洁度

| # | 位置 | 问题 | 备注 |
|---|------|------|------|
| P3-1 | `World.cs:329-365` | `GenerateVoxelChunk` 是 `GenerateVoxelChunkData` 的重复死代码，且地形高度因子 **20 vs 64 不一致**（`:343`）——高理解成本陷阱 | 删除 |
| P3-2 | `Block.cs:49` | `GetBlockState()`：`_value & StateMask >> StateShift` 因 `>>` 优先级高于 `&`，实为 `_value & 0xF`（取类型低 4 位），State 位取不到 | 当前无人调用；加括号或重写 |
| P3-3 | `VoxelChunk.cs:299-306` vs `BlockUVMap.cs:4-13` | `Direction.North`=+Z，`FaceIndex.North`=-Z，语义相反；`MeshData.cs:49` 直接 `(int)dir` 当 UV 索引。**数值恰好一致**（North 都=5），当前因侧面纹理相同而无害 | 做方向性纹理前必须处理 |
| P3-4 | `Block.cs:64` | `BlockType.ERROR = 114514` 魔数 | 改合法小值或专用标志位 |
| P3-5 | `VoxelChunkData.cs:6-65` | 含 `Block[,,]` + `List<>` 的可变 struct（值语义被引用语义破坏）；构造时先填满 Air 再覆盖 | 改 class 或文档化 |
| P3-6 | `MeshData.cs:73` | `if (uvIndex == null)` 对 `Vector2Int`（struct）恒假，死代码 | 删除 |
| P3-7 | `MeshData.cs:88` | 首次 UV miss 时 `Debug.Log` 刷屏（8 类型 × 6 面 ≈ 48 行起） | 一次性或删除 |
| P3-8 | `MeshData.cs:13-23` | UV 数学假定虚拟 32×24px=768px 网格 vs 真实 512×512 图集——已知脆弱约定（AGENTS.md 已声明"勿动"） | 不改动，仅代码内注释 |
| P3-9 | `World.cs:209-213` | `catch { Debug.LogException(ex); throw ex; }` 双日志 + `throw ex` 重置堆栈；fire-and-forget 任务 fault 未观察（`:100` `_ =`） | 改 `throw;` 或只 LogException |
| P3-10 | `World.cs:60` | Start 强制相机到 (0,64,0)，覆盖场景摆放 | 用 SerializeField |
| P3-11 | `Saver.cs` | 只写不读 + `FileMode.Create` 跨会话覆写 → 存档形同虚设；`Decompress`（`:298-324`）是无入口残片；`AsyncSaver`（`:22-120`）整段注释 | 明确写-only 定位或补读路径 |
| P3-12 | 全局 | 单例靠场景手动摆放（World/WorldManager/DataBuffer）无 DI；`World.cs` 460 行混合相机追踪/地形生成/线程调度/存档/树生成 5 类职责；Player 上 Rigidbody 未用；`BasicTree.cs` 空 stub；`Postions.cs` 文件名拼写 | 重构可延后 |
| P3-13 | `World.cs:273-303` | 树逻辑：主干条件与 `realY=(baseHeight+4)%16` 经核实**正确**；仅低概率外观瑕疵：(a) baseHeight 小时树悬空；(b) 草皮在 chunk 顶边界时树干留 1 格空隙；(c) 冠层起始依赖 `realY%2` 随地面奇偶变化；(d) 树高 `x*z%6+1` 用局部坐标 | 低优先级外观问题 |
| P3-14 | `Saver.cs:167/170/241-250` | `CHUNK_SIZE=16`、`SECTOR_SIZE=4096`、`32³` region、`(x<<8)|(y<<4)|z` 位移全部硬编码，未贯通 `Constants.cs` | 常量收敛 |

---

## 二、修改计划

### 阶段一：紧急修复（小而独立，建议立即做）

| 顺序 | 内容 | 风险 | 工作量 | 独立 |
|---|---|---|---|---|
| 1 | **P0-1** 删 `World.cs:10` ✅ | 零 | 1 行 | ✅ |
| 2 | **P0-2** 修 `Saver` uint 重载 key 为 region 坐标，或删除该重载 ✅ | 零（死代码） | 10 行 | ✅ |
| 3 | **P1-6** `CreateVoxelChunk` 改幂等（throw→LogWarning+return），`GenerateWorld` 走同一防护 ✅ | 低 | 10 行 | ✅ |
| 4 | **P2-2** 删 `Setblock` 内重复 enqueue ✅ | 低（行为不变） | 2 行 | ✅ |
| 5 | **P2-1** 修正 64 块/帧预算为按块记账 ✅ | 低 | 10 行 | ✅ |
| 6 | **P2-5** `World.OnDestroy` 遍历 Dispose `Saver` writers ✅ | 低 | 15 行 | ✅ |

### 阶段二：正确性（中工作量，互相有耦合）

| 顺序 | 内容 | 风险 | 工作量 | 独立 |
|---|---|---|---|---|
| 7 | **P1-1 + P1-2** 重做 `OnCameraChunkChanged`：锁忙不丢弃（记录最新位置待补）、事件与流式消费解耦、按帧预算消费 ✅ | 中（并发逻辑重构） | 中 | 部分（改 World 核心） |
| 8 | **P1-3** 卸载时对相邻 chunk 重新入队 MeshOptimize，修边界透空 ✅ | 低（复用现有机制） | 20 行 | ✅ |
| 9 | **P1-4** `GenerateWorld` 改为只产数据入队（消灭首帧峰值） ✅ | 低-中（与 7 共用队列） | 中 | 依赖 7 |
| 10 | **P1-5** pendingBlocks 目标 chunk 缺失时不再同步级联创建（丢弃或入队） ✅ | 中（可能影响树的观感） | 中 | ✅ |

### 阶段三：性能（依赖阶段二稳定后再做）

| 顺序 | 内容 | 风险 | 工作量 |
|---|---|---|---|
| 11 | **P2-3** chunk 复用单个 Mesh 实例 + 原地写顶点/索引 + 按需 `RecalculateNormals` ✅ | 中（mesh 生命周期管理） | 中 |
| 12 | **P2-4** 恢复后台 `AsyncSaver`（注意 FileStream 线程安全与 Dispose 收敛）或批量 flush | 中 | 中 |
| 13 | **P2-6** 按帧耗时预算动态调整构建/优化上限，替代固定 2/帧 ✅ | 低 | 小 |

### 阶段四：重构（可延后，不影响当前功能）

| 顺序 | 内容 |
|---|---|
| 14 | Saver 明确"写-only"定位或补读路径；.vrf 格式加版本头 |
| 15 | World.cs 拆分：ChunkStreamer / TerrainGenerator / ChunkStore |
| 16 | 单例 → 场景显式引用/注入；相机出生点改 SerializeField（P3-10） |
| 17 | 清理死代码：`GenerateVoxelChunk`（P3-1）、`Decompress`（P3-11）、注释掉的 AsyncSaver、`BasicTree`（P3-12）、`uvIndex == null`（P3-6）、`TryGetBlock`（A2 后已无调用方）、`Setblock(int,int,int)` 死重载等 |
| 18 | P3-2 / P3-3 / P3-4 修复（状态位、方向语义、ERROR 魔数）——**等方向性纹理需求出现前可一直拖着** |
| 19 | `Constants.CHUNK_SIZE` 贯通 Saver/压缩器（P3-14） |

---

## 三、待实测验证（静态审查无法确认，需 Play Mode）

1. **await 续体线程归属**：默认 Unity 安装 `UnitySynchronizationContext`、续体回主线程——大部分"主线程安全"结论建立在此之上。若实测 `UnloadVoxelChunk` 里的 `DestroySelf`/`world.Remove` 报跨线程错误，P1-6 / P1-1 严重性整体上调为 P0。
2. **启动冻结时长**：864 chunk 首帧建 mesh 的实际卡顿秒数（与机器配置相关）。
3. **相机高速移动下的地形缺失观感**：P1-1 与 P1-2 叠加效果的主次关系。
4. **图集 UV（768 vs 512 虚拟网格）**：当前"看起来正常"依赖采样落在 24px 网格单元内，未做像素级比对；不计划改动，仅记录。
