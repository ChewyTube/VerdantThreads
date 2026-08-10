# VerdantThreads 项目状态快照（2026-08-09，历史）

> 归档：2026-08-11　|　**过期快照，内容不再更新**。最新代码认知见 `docs/PROJECT_UNDERSTANDING.md`，当前任务见 `docs/status/TODO_LIST.md`。
> 原始更新时间：2026-08-09（③ 对象池完成后，Play Mode 复测通过）
> 用途：供后续会话快速恢复上下文。详细审查与计划见 `docs/archive/REVIEW.md`、`docs/archive/VIEW_DISTANCE_PLAN.md`。
> 行号为当时版本近似值（World.cs 617 行 / VoxelChunk.cs ~250 行），改动后可能漂移。

---

## 1. 项目概览

- 体素/Minecraft 式分块地形原型，运行于 **Unity Tuanjie（团结引擎）**，`2022.3.62t10` / Tuanjie Editor 1.9.2。
- 单场景 `Assets/Scenes/SampleScene.unity`；**无 CLI 构建、无测试套件**——验证一律进 Play Mode。
- 视距目标已达成：`lineOfSight=12`（X/Z）、`verticalLineOfSight=6`（Y），视距盒 25×13×25 ≈ 8125 chunk。
- 固定种子 `985211`（OpenSimplex2 + FBM，6 层，frequency 0.002）→ 地形确定性可复现。

## 2. 当前架构（均已实现并复核）

### 2.1 运行流水线（`World.cs`）
- **主线程 Update 分帧预算**（`MAX_FRAME_WORK_BUDGET_MS=6`）排空 4 个并发队列：
  - `_pendingBuildQueue`(VoxelChunkData) — 24 chunk/帧
  - `_pendingSetBlocksQueue`(List<(BlockPosInWorld,Block)>) — 64 块/帧（按块记账）
  - `_pendingMeshBuildQueue`(VCPosInWorld) — 24 启动/帧
  - `_pendingMeshUploadQueue`((VCPosInWorld,MeshData)) — 8 上传/帧
- 相机跨 chunk → `OnCameraChunkChanged`：同步卸载超视距 + 整盒扫描 spawn 缺失 chunk 生成（**④ 待优化点**）。
- 数据持有：`world` Dictionary<Vector3Int,VoxelChunk>；`loadedVoxelChunks` HashSet<Vector3Int>。

### 2.2 mesh 管线（②，A2 完成）
- `ChunkMeshBuilder.CreateSnapshot`（主线程）：**整块复制**本地块 + 6 方向邻居边界面（`MeshBuildData`，纯数据快照）。
- `Task.Run(ChunkMeshBuilder.Build)`：worker 只读快照做剔除 + 顶点/UV/法线 → `MeshData`。
- 上传队列 → `VoxelChunk.ApplyMeshData`：**ChunkId + Seq 双守卫**丢弃过期上传（乱序完成收敛到最新快照）。
- 每个 chunk 复用单个 `Mesh` 实例（P2-3），`MeshData` 已去除 `DataBuffer` 依赖。

### 2.3 对象池（③）
- `Stack<VoxelChunk> _chunkPool` + `Stack<Block[,,]> _blockArrayPool`，上限各 8192；池满回退 `DestroySelf`。
- 创建池优先（`SetActive(true)`+改名+`ResetForReuse`）；卸载 `ReturnChunkToPool`（`PrepareForPool` 清残留/断引用/**保留材质**）。
- `ResetForReuse` **刷新 InstanceId + seq 归零** → 上一世在途 mesh 上传被 ChunkId 守卫丢弃。
- 数组所有权：spawn 主线程 `TakeBlockArray` → worker 写入 → 构建循环三分支（跳过/空区块=还池，创建=转移）→ 卸载归还。

### 2.4 空区块（⑦）
- 全空气 chunk 不建对象，仅记入 `loadedVoxelChunks`；跨界树冠写入按需 `CreateEmptyVoxelChunk` 恢复（FillAir + 应用写入）。

### 2.5 存档（Saver，write-only）
- `.vrf` region 文件（32³ chunk、4096B 扇区、deflate、`uint[4096]` 索引 `(x<<8)|(y<<4)|z`），`Application.persistentDataPath/world_saves/`。
- 卸载时主线程同步拷贝保存；**无读路径**。`World.OnDestroy` 已收敛 Dispose（P2-5）。

### 2.6 线程安全不变量（硬性约束，勿破坏）
1. **两个池/数组所有读写严格主线程**（Stack 非线程安全；`TakeBlockArray` 必须在 `Task.Run` 之前）。
2. worker 线程绝不触碰 `world`/`blocks`/Unity 对象——只读快照。
3. `MeshBuildData.Blocks` 是快照拷贝，不是活数组引用。
4. 保存（同步拷贝）先于数组还池。
5. 池化复用必须刷新 InstanceId（否则 A2 守卫失效）。

## 3. 已完成工作时间线

| 轮次 | 内容 | 状态 |
|---|---|---|
| R1 | REVIEW.md 阶段一/二：P0-1 删编辑器引用、P0-2 Saver key 修复、P1-1 事件不丢弃、P1-2 帧预算消费、P1-3 卸载重建邻居、P1-4 启动分帧、P1-5 级联创建收敛、P2-1/2/5 | ✅ |
| R1 | 卡顿修复：帧耗时预算 + 树冠重试 + 视距守卫 | ✅ |
| R1 | P2-3 chunk 复用单 Mesh 实例 | ✅ |
| R2 | **② mesh 生成移出主线程**（快照 + 双队列 + 守卫），ora-1 审查发现 P1 乱序上传 → 已修复 | ✅ |
| R3 | **③ 对象池**（GO 池 + 数组池），ora-1 审查无 P0/P1/P2，Play Mode 复测通过 | ✅ |
| 其他 | ① 垂直视距、⑥ 常数微调、⑦ 空区块自动卸载 | ✅ |

## 4. 已知问题与遗留（P3 / 开放项）

**开放项：**
- **P2-6** mesh 优化队列积压（已缓解，未彻底）。
- **P1-7** 共享 `FastNoiseLite` 实例并发只读——已核实安全（配置仅在初始化期写入），勿加运行时 SetXxx。
- **P3-1** worker 生成异常路径数组不进池——有意不修：catch 在后台线程，直接 Push 违反不变量 1；路径实际不可达、泄漏有界。

**死代码（阶段四清理清单，REVIEW.md:129）：** `GenerateVoxelChunk`（P3-1 重复实现）、`Decompress`（P3-11）、注释掉的 `AsyncSaver`、`BasicTree`、`uvIndex == null` 恒假分支（P3-6）、`TryGetBlock`（A2 后无调用方）、`Setblock(int,int,int)` 死重载、`VoxelChunk.Initialize`（③ 后无调用方）。

**坑（AGENTS.md 延续）：**
- `Block.GetBlockState()` 运算符优先级 bug（`&` 与 `>>`）——当前无人调用（P3-2）。
- `Direction.North`=+Z 与 `FaceIndex.North`=-Z 语义相反；`MeshData` 直接 `(int)dir` 当 UV 索引，数值恰好一致、当前无方向纹理所以无害（P3-3）。
- MeshData UV 数学假定 32×24px 虚拟网格 vs 真实 512×512 图集——**常量勿动**（P3-8）。
- `BlockType.ERROR = 114514` 魔数（P3-4）。
- Saver 硬编码 `CHUNK_SIZE=16` 等魔数（P3-14）；`SimpleRegionWriter` `FileMode.Create` 覆写 + 每 chunk fsync。
- 无 `.asmdef`，全代码在**全局命名空间**，注释/日志用**中文**。
- 新增 .cs 文件后 LSP 可能报"找不到类型"，Unity 重编译收录后自动消除（csproj 由编辑器重新生成）。

## 5. 下一步计划

### ④ 近处优先 + 只补新暴露环（已出方案，待实施）
- 近处优先：`_pendingMeshBuildQueue` 从 ConcurrentQueue 改主线程 `List` 按 `dist²` 排序（该队列全部主线程写入）；生成 spawn 顺序按距离排序。上传队列排序可选。顺带加 `_meshBuildPending` 去重。
- 只补新暴露环：`OnCameraChunkChanged` 只 spawn `viewBox(C1)−viewBox(C0)−loaded−_generating`；新增 `_generatingChunks` 集合（spawn 时加、构建消费所有分支移除）——**顺手修掉当前整盒扫描重复 Task.Run 的潜伏 bug**。
- 新加载 chunk 的已存在邻居补重建边界（当前只重建自身）；遥传加 spawn 每帧上限。

### ⑤ 存档背压
- 保存队列上限 + worker 按区域批量 flush 减少 fsync，防连续移动内存无界增长。

### 阶段四重构（可延后）
- Saver 明确 write-only 定位或补读路径；World 拆分；单例→注入；死代码清理（见上）；P3-2/3/4 等方向纹理需求出现前可拖。

## 6. Play Mode 复测清单（当前基线）

1. 快速往返飞行：帧时间平稳，无启动冻结、无地形缺失/透空。
2. 复用 chunk 无残影/材质丢失；Hierarchy 对象数收敛（池封顶）。
3. 树冠跨界完整；控制台无 null / 重复创建警告。

## 7. 文档索引与惯例

- `docs/archive/VIEW_DISTANCE_PLAN.md` — 视距提升计划（①②③⑥⑦ ✅ / ④⑤ 待实施）。
- `docs/archive/REVIEW.md` — 代码审查报告（P0-P3 清单 + 修改计划阶段一二完成）。
- `AGENTS.md` — 编辑器/工具链约定、代码布局、坑位清单（本项目入口文档）。
- 本文件 gitignore 除外不入库；其他 docs 入库存档。
