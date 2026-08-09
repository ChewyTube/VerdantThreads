# VerdantThreads 视距提升计划（lineOfSight 6 → 12）

> 目标：视距从 6 提升到 12；保持平时 ~200fps；地形加载时帧率不掉到 30 以下。
> 前提判断：平时 200fps 说明渲染 / draw call 余量巨大；30fps 仅出现在加载新地形时 → 瓶颈是**加载路径的主线程工作量**（chunk 对象创建、mesh 上传、GC）。因此本期**不做网格合并 / LOD**。
> 实施状态：2026-08-09 —— ①②③⑥⑦ 已实现并复核；④ 已实现（2026-08-09）；⑤ 待实施。

## 现状量化

| 项 | l=6（现状） | l=12（目标） | 说明 |
|---|---|---|---|
| 视距盒体积 | 13×13×~11 层 ≈ 1.9k chunk | 25×25×17 层 ≈ 10.6k | 约 5.7× |
| 每跨 chunk 新暴露 | ~143 | ~425 | 生成/构建压力同比例上升 |
| 平时帧率 | ~200 | 渲染余量充足 | 无需动渲染架构 |
| 加载时帧率 | 30 | ≥60（目标） | 主线程加载工作量需削减 |

关键事实：相机在 chunk y=4，l=12 会加载 y∈[0,16] 共 17 层，而地形最高约 chunk 5——上空约 10 层是纯空气，白加载。

## 措施

### ① 垂直视距独立于水平视距 ✅ 已实现
新增 `verticalLineOfSight`（默认 6），Y 方向只加载到地面以上一小段；X/Z 仍用 `lineOfSight`。
- 改动点：`OnCameraChunkChanged` 卸载判定与 spawn 循环、`IsWithinViewDistance`、`GenerateWorld` 的 Y 循环。
- 效果：加载量 10.6k → ~4.4k，每次跨 chunk 的 spawn/卸载量减半以上，加载压力接近 l=6 水平。

### ② mesh 数据生成移出主线程 ✅ 已实现
新增 `ChunkMeshBuilder`：主线程对本地块 + 6 方向邻居边界面拍纯数据快照（`MeshBuildData`），worker 线程只读快照生成 `MeshData`（剔除扫描 + 顶点/UV/法线），主线程仅做 `Mesh.SetVertices/SetTriangles/SetUVs/SetNormals` 上传（帧预算队列内）。`MeshData` 移除 `DataBuffer` 缓存依赖（UV 改纯计算）。
- 线程安全：worker 不触碰 `world`/`blocks`/Unity 对象；乱序上传用"实例 ID + 构建代次"守卫丢弃过期 mesh。
- 效果：单 chunk 主线程成本降到只剩上传 ~0.1-0.2ms，加载帧不再被单个 chunk 的完整构建卡住。

### ③ Chunk 对象池 ✅ 已实现
`World` 持两个主线程独享的池：`_chunkPool`（chunk GameObject + `VoxelChunk` 组件）与 `_blockArrayPool`（16³ `Block[,,]` 数组），上限 8192（= 视距盒 25×13×25）。
- Phase1：创建路径池优先（命中则 `SetActive(true)` + 改名 + `ResetForReuse`），卸载路径 `ReturnChunkToPool`（`PrepareForPool` 清残留 mesh/断引用/置空状态，但**保留材质**——Start 只跑一次，池化复用后不重跑）。
- Phase2：`SpawnChunkDataGeneration` 主线程取池化数组、worker 直接写入（`VoxelChunkData` 构造器自带 Air 填充）；构建循环的"已加载/超视距/空区块"三个分支归还数组、创建分支转移所有权、卸载路径归还。
- 关键不变量：池与数组所有读写严格主线程（`TakeBlockArray` 在 `Task.Run` 之前）；`ResetForReuse` 刷新 `InstanceId` 并归零 seq，上一世在途 mesh 上传被 A2 的 ChunkId 守卫丢弃；池满回退 `DestroySelf` 封顶内存。
- 效果：消除加载/卸载风暴的 `new GameObject`/`AddComponent`/`Destroy` 与 16KB×N 数组 GC 轮换，高速移动帧更稳。
- 遗留 P3（可接受）：worker 生成异常路径数组不进池（catch 在后台线程，不能碰非线程安全 Stack；该路径实际不可达、泄漏有界）；池满溢出的数组由 GC 回收。

### ④ 近处优先 + 只补新暴露环 ✅ 已实现
- 构建/上传队列按到相机距离排序（按距离环分桶），视野内先填满，pop-in 从近到远。
- `OnCameraChunkChanged` 只 spawn 新暴露的位置，并用"已 spawn 未构建"集合避免对同一位置重复 `Task.Run`（当前整盒扫描 + `ContainsKey` 只看已创建，会造成重复生成）。

### ⑤ 存档背压（待实施）
l=12 时每次跨 chunk 卸载 ~175-425 个，入队 16KB×N；worker 受 fsync 限制可能落后。恢复保存队列上限（满则阻塞/同步兜底），worker 改按区域批量 flush 减少 fsync 次数，防止连续移动时内存无界增长。

### ⑥ 常数微调 ✅ 已实现（随①落地）
`lineOfSight = 12`；加载 catch-up 积压大时构建预算可临时放宽到 ~8-10ms、追平后回落（平时帧率不受影响，移动时少掉帧）。

### ⑦ 自动卸载空区块 ✅ 已实现
全空气（Air/Void）chunk 在构建时**不创建对象**，仅记录为已加载位置（`loadedVoxelChunks` 由 List 改为 `HashSet`，O(1) 查询）；卸载时无对象直接清理记录，不保存、不建 mesh。
- 跨界树冠写入（高树顶部进入上方空 chunk）会**按需恢复**该空区块：用全空气数据创建对象并应用写入（复用 `CreateEmptyVoxelChunk`），保证树冠完整。
- 安全性：空气邻居在面剔除中本就该保留面，`TryGetBlock` 返回 ERROR ≈ Air，剔除语义不受影响。

## 实施顺序

① → ⑦ → ② → ③/④ → ⑤（①②③⑥⑦ 已落地）

## 明确不做（本期）

- 网格合并 / 远距离 LOD：平时 200fps 说明 draw call 余量巨大，留到 l=16 再评估。
