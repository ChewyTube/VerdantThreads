# VerdantThreads 任务总表（历史）

> 归档：2026-08-11　|　**历史记录，内容已全部完成**。当前活跃任务见 `docs/status/TODO_LIST.md`。
> 原始更新：2026-08-10　|　旧计划文档已归档至 `docs/archive/`（REVIEW.md、VIEW_DISTANCE_PLAN.md）

## 一、已完成 ✅

### 视距提升计划（VIEW_DISTANCE_PLAN.md，7/7 全部落地）

| # | 内容 | 状态 |
|---|------|------|
| ① | 垂直视距独立于水平视距（`verticalLineOfSight`） | ✅ |
| ② | mesh 数据生成移出主线程（`ChunkMeshBuilder` + 快照 + 乱序守卫） | ✅ |
| ③ | Chunk 对象池（`_chunkPool` / `_blockArrayPool`，上限 8192） | ✅ |
| ④ | 近处优先 + 只补新暴露环（切比雪夫环排序、`_generationInFlight` 去重、脏标记） | ✅ |
| ⑤ | 存档背压（队列上限 1024 + 满则主线程同步兜底 + 每 32 chunk 批量 flush + `SetQueueLimit`） | ✅ |
| ⑥ | 常数微调（`lineOfSight = 12`，加载积压时预算放宽） | ✅ |
| ⑦ | 自动卸载空区块（`loadedVoxelChunks` HashSet + 跨界树冠按需恢复） | ✅ |

### 代码审查修复（REVIEW.md，阶段一~三全部完成）

| 阶段 | 项 | 状态 |
|------|-----|------|
| 一 | P0-1 删 `using static UnityEditor.PlayerSettings` | ✅ |
| 一 | P0-2 修 Saver uint 重载 region key | ✅ |
| 一 | P1-6 `CreateVoxelChunk` 改幂等 | ✅ |
| 一 | P2-2 删 `Setblock` 重复 enqueue | ✅ |
| 一 | P2-1 64 块/帧按块记账 | ✅ |
| 一 | P2-5 `World.OnDestroy` Dispose Saver writers | ✅ |
| 二 | P1-1 + P1-2 重做 `OnCameraChunkChanged`（不丢事件、解耦、帧预算） | ✅ |
| 二 | P1-3 卸载时邻居重新入队 MeshOptimize（修边界透空） | ✅ |
| 二 | P1-4 `GenerateWorld` 只产数据入队 | ✅ |
| 二 | P1-5 pendingBlocks 不再同步级联创建 | ✅ |
| 三 | P2-3 chunk 复用 Mesh 实例 + 原地写 | ✅ |
| 三 | P2-4 后台异步保存（worker + 队列） | ✅ |
| 三 | P2-6 帧耗时预算动态调整 | ✅ |

### 本次会话新增修复 ✅

| 内容 | 说明 |
|------|------|
| 退出全量保存 | `OnApplicationQuit` → `SaveAllLoadedChunks`：先应用 pendingBlocks，再全量入队，由 `Dispose` 排空落盘 |
| 保存失败重试 + 显式报错 | `SaveTask.RetryCount` 上限 3 次重试；放弃时 LogError 带 region/local 坐标；Dispose 汇总失败数 |
| ⑤ 存档背压 | 见上表（队列上限/同步兜底/批量 flush/退出放开上限） |

## 二、未完成 ⏳

### 阶段四：重构（可延后，不影响当前功能）

| # | 内容 | 来源 | 备注 |
|---|------|------|------|
| 14 | Saver 补读路径 + .vrf 版本头 | P3-11 | ✅ 已完成（2026-08-09）：.vrf 加 "VRF1"+version=1 版本头；新增 `TryLoadVoxelChunk` 读路径（版本校验/索引定位/扇区读取/解压，任何失败回退重新生成）；`GenerateVoxelChunkData` 先查存档命中即用（含玩家修改）；`VoxelChunkData` 构造器加 `fillAir` 参数 |
| 15 | World.cs 拆分：ChunkStreamer / TerrainGenerator / ChunkStore | P3-12 | ✅ 代码完成（2026-08-09，待 Play Mode 验证）：World 瘦身为 facade（~110 行），新增 TerrainGenerator（生成+读存档）、ChunkStore（存储/池/写入/卸载保存+回调）、ChunkStreamer（队列调度/排序/预算/视距环） |
| 16 | 单例 → 场景显式引用/注入 | P3-10 | ✅ 已完成（2026-08-09）：相机出生点改 SerializeField（先前）；去单例化：删 World.Instance，BlockInteraction 改序列化引用 + FindObjectOfType 兜底，VoxelChunk 改注入 mesh 重建回调（ChunkStore 构造注入、创建时透传、归还池清空），顺带删除死代码 CreateSinglePlaneVoxelChunk |
| 17 | 死代码清理：`GenerateVoxelChunk`、`Decompress`、注释掉的 AsyncSaver、`BasicTree` stub、`uvIndex == null`、`TryGetBlock`、`Setblock(int,int,int)` 死重载 | P3-1/3-6/3-11/3-12 | ✅ 已完成（2026-08-09）；`uvIndex == null` 此前已不存在 |
| 18 | P3-2 状态位 / P3-3 方向语义 / P3-4 ERROR 魔数修复 | P3-2/3-3/3-4 | ✅ 已完成（2026-08-10）：P3-2 括号修复（`(_value & StateMask) >> StateShift`）；P3-3 方向语义统一（删 `FaceIndex`，`Direction` 唯一权威 + 语义注释 + `Count`，UV 接口 Direction 化，行为零变化）；P3-4 ERROR 全清理（`BlockUVMap` fallback 改 `FallbackUV`，`GetBlock` fallback 改 throw） |
| 19 | `Constants.CHUNK_SIZE` 贯通 Saver/压缩器/region 位移 | P3-14 | ✅ 已完成（2026-08-09）：新增 `REGION_SIZE/REGION_SIZE_LOG2/SECTOR_SIZE/CHUNK_VOLUME` 常量；顺带删除无调用方的 `Compress(Block[,,])` 重载 |

### 低优先级 / 已知遗留

| 项 | 来源 | 备注 |
|----|------|------|
| P1-7 共享 FastNoiseLite 并发读——当前只读安全，建议加注释声明 | P1-7 | ✅ 已完成（2026-08-10）：`TerrainGenerator.noise` 加并发只读声明注释 |
| P3-5 `VoxelChunkData` 可变 struct 值语义被引用语义破坏 | P3-5 | ✅ 已完成（2026-08-10）：改 class（8 处引用全为引用式用法，无值语义依赖） |
| P3-7 `MeshData` 首次 UV miss 的 `Debug.Log` 刷屏（~48 行） | P3-7 | ✅ 已完成（2026-08-10）：全项目 Debug.Log 仅剩错误上报与注释行，无刷屏 |
| P3-8 UV 数学 768px 虚拟网格 vs 512px 图集 | P3-8 | ✅ 已完成（2026-08-10）：MeshData 常量区加 768/512 勿改注释 |
| P3-13 树外观低概率瑕疵（悬空/草皮空隙/冠层奇偶/局部坐标） | P3-13 | ⏳ 低优先级外观 |

## 三、待实测验证（Play Mode，静态审查无法确认）

| # | 验证项 | 归属 |
|---|--------|------|
| 1 | 退出全量保存：放置方块 → 停止播放 → `world_saves/` 下 .vrf 含修改 chunk | 本次 |
| 2 | 保存失败重试：非法目录 → 3 次重试后 LogError 带坐标 + Dispose 汇总 | 本次 |
| 3 | ⑤ 背压：快速移动跨多 chunk → 队列不无界增长、同步兜底不卡帧 | 本次 |
| 4 | await 续体线程归属（若 `UnloadVoxelChunk` 报跨线程错误则 P1-1/P1-6 升级为 P0） | REVIEW |
| 5 | 启动冻结时长（首帧数据入队后的实际观感） | REVIEW |
| 6 | 相机高速移动下地形缺失观感（④ 近处优先效果） | REVIEW |
| 7 | 图集 UV 采样是否落在 24px 网格单元内 | REVIEW |

## 四、存档目录（备忘）

```
C:\Users\ChewyTube\AppData\LocalLow\DefaultCompany\VerdantThreads\world_saves\r.{x}.{y}.{z}.vrf
```
（`Application.persistentDataPath` + `world_saves` 子目录；region = 32³ chunk）
