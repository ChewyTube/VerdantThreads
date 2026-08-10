# VerdantThreads 项目认知总览

> 创建：2026-08-10　|　基于全部源码逐文件阅读整理（非文档转述），行号/结构以当日代码为准，改动后可能漂移。
> 关系：本文是"代码现状认知"，`docs/design/GAME_DESIGN.md` 是"设计目标"，`docs/status/TODO_LIST.md` 是"当前任务"。

---

## 一、项目定位

类《我的世界》的**体素分块地形原型**，运行于 **Unity 团结引擎（Tuanjie）**（Unity `2022.3.62t10` / Tuanjie Editor 1.9.2）。

- 按 GDD（`docs/design/GAME_DESIGN.md`）远期目标是"以孟德尔遗传定律→分子生物学知识为核心、体素世界为载体"的科技探索游戏，但**当前代码只有地形原型 + 豌豆渲染占位，无任何遗传/玩法系统**。
- 单场景 `Assets/Scenes/SampleScene.scene`（Tuanjie 用 `.scene`，不是 `.unity`）；**无 CLI 构建、无测试套件、无 CI——验证一律进 Play Mode**。
- 无 `.asmdef`：全部编译进 `Assembly-CSharp`，所有代码在**全局命名空间**（新脚本保持一致）。
- 根 `*.csproj`/`*.sln` 由编辑器重新生成并 gitignore。

## 二、核心架构：World facade + 三专职类

`World.cs`（~91 行，**非单例**，场景显式放置）在 `Awake` 组装三个专职类，自身只做生命周期、相机出生点、存档集成与公开 API 转发：

```
World (MonoBehaviour, facade)
 ├── TerrainGenerator —— 纯生成逻辑（噪声地形+树+存档读路径），后台线程可调用
 ├── ChunkStore      —— chunk 存储/对象池/跨 chunk 写入/卸载保存（仅主线程）
 └── ChunkStreamer   —— 流式调度：视距盒、后台 Task 调度、帧预算队列、就近排序
```

依赖注入方式：

- `ChunkStore` 构造时注入 mesh 重建回调 `pos => streamer.RequestMeshRebuild(pos)`（World 先建 store 再建 streamer，闭包捕获字段引用，调用时已就绪）。
- `BlockInteraction` 通过 `[SerializeField]` + `FindObjectOfType` 兜底拿 World。
- `VoxelChunk.onMeshRebuildRequested` 由 `ChunkStore.CreateChunk`/`CreateEmptyVoxelChunk` 创建时赋值。

## 三、数据模型

### 3.1 Block：一个 uint 包万物

`Block` 是 `readonly struct`，包装单个 `uint`（16³=4096 块/chunk，每块 4 字节）：

```
bit0–15  类型（TypeMask 0xFFFF）→ BlockType：Void=0/Air=1/Grass=2/Dirt=3/Bedrock=4/Stone=5/Log=6/Leaves=7/PeaStem=8
bit16–17 生长阶段（StageMask 0x3，豌豆 0=苗/1=开花/2=结荚）
bit18–24 预留"7 对孟德尔性状各 1 位" —— ⚠️ 架构评审已判定错误设计，将废除
bit25–31 预留
```

关键 API：

- `GetBlockType()` = `_value & TypeMask`
- `GetBlockState()` = `(_value & StateMask) >> StateShift` —— **括号已修复，勿去括号**
- `uint`↔`Block` 隐式/显式转换；存档序列化即 `(uint)Block` 整值流

`BlockRegistry` 持有全部方块单例；`PeaSeed = new Block((uint)PeaStem)`——**当前所有豌豆 stage 恒为 0**，无任何代码写 stage 位。

### 3.2 坐标系三件套（`World/Position/Postions.cs`，文件名拼写是真实的 typo）

| 类型 | 含义 | 换算 |
|---|---|---|
| `VCPosInWorld` | chunk 坐标 | `>>4` |
| `BlockPosInWorld` | 世界方块坐标 | `GetCorrespondingVCPos()` → `>>4` |
| `BlockPosInVoxelChunk` | chunk 内局部坐标 | `& 15` |

均实现 `IEquatable` + 稳定哈希（`X*73856093 ^ Y*19349663 ^ Z*83492791`）。局部坐标哈希即线性索引 `(x<<8)|(y<<4)|z`（`CHUNK_SIZE_LOG2*2`/`LOG2`），与存档压缩器索引**完全一致**——将来 tile 字典 key 直接复用。

### 3.3 常量（`Assets/Scripts/Constants.cs`）

`CHUNK_SIZE=16`、`CHUNK_SIZE_LOG2=4`、`REGION_SIZE=32`、`REGION_SIZE_LOG2=5`、`SECTOR_SIZE=4096`、`CHUNK_VOLUME=4096`。Saver/压缩器/位置换算已全部贯通（魔数清理完成）。

## 四、运行时管线（每帧数据流）

### 4.1 启动

`World.Start`：相机设到 `cameraSpawnPos`（SerializeField，(0,64,0)）→ `streamer.InitializeCamera`（记录当前 VC，`hasPrevViewBox=false`）→ `GenerateInitial`：只生成地形核心层（y∈[0,6)，水平 ±12 全铺）。

### 4.2 每帧 `World.Update` → `streamer.Tick`

1. **相机跨 chunk 检测**：`lastVCPosCam != VCPosCam` → `OnCameraChunkChanged`：
   - 先收集再卸载超视距 chunk（`store.UnloadChunk`：同步保存→还池→移除→触发 `OnChunkUnloaded` 事件让邻居重建边界面）。
   - 只补**新暴露环**（旧视距盒内跳过；首帧无旧盒=全暴露），按切比雪夫环距就近排序 spawn。
2. **帧预算消费**（`MAX_FRAME_WORK_BUDGET_MS=6`，Stopwatch 限制）4 条队列：
   - 生成数据队列 → 就近创建 chunk（24/帧；空区块走 `MarkEmptyChunkLoaded` 不建对象）。
   - 跨 chunk 写入队列 → 按块记账（64 块/帧）；目标未加载且在视距内则重试入队。
   - mesh 构建启动（24/帧）→ `SpawnMeshBuild`。
   - mesh 上传（8/帧）→ `vc.ApplyMeshData`。

### 4.3 线程模型（硬性不变量）

- **主线程独享**：两个对象池（Stack）、world 字典、loadedVoxelChunks、全部 Unity API。
- **后台线程**：`TerrainGenerator.GenerateVoxelChunkData`（Task.Run；主线程先取池化数组再传）与 `ChunkMeshBuilder.Build`（只读快照）。
- **快照隔离**：mesh 构建前主线程拍 `MeshBuildData`（16³ 整块拷贝 + 6 方向 16×16 边界面；邻居缺失→null→保留面）；worker 绝不碰活对象。
- **乱序守卫**：`VoxelChunk.ApplyMeshData` 用 `ChunkId`（实例 ID）丢弃已卸载 chunk 的过期上传 + `Seq`（`TakeBuildSeq` 递增代次）丢弃乱序完成的旧构建。

### 4.4 对象池（上限 8192 = 视距盒 25×13×25）

- chunk 池：`PrepareForPool`（清 mesh/断引用/隐藏，**保留材质**——Start 只跑一次）→ `ResetForReuse`（刷新 InstanceId、归零 seq，让在途上传作废）。
- 块数组池：`TakeBlockArray` 必须在 `Task.Run` 之前（Stack 非线程安全）。
- 池满回退 `DestroySelf` 封顶内存；worker 异常路径数组不进池（可接受，泄漏有界）。

## 五、地形生成（`World/TerrainGenerator.cs`，~130 行）

- 固定 seed `985211`，OpenSimplex2 + FBM（6 octave、frequency 0.002、lacunarity 2.0、gain 0.5），**确定性**：同坐标永远同结果，生成算法改动须保持同坐标同结果。
- **读存档优先**：`GenerateVoxelChunkData` 先 `saver.TryLoadVoxelChunk`，命中直接返回（保留玩家修改），miss 才重新生成。
- 分层：Y=0 Bedrock → `baseHeight=(noise+1)*0.5*64` 以下 Stone → +2 层 Dirt → +3 层 Grass。
- 树（香樟风球冠）：`HasTree` 确定性伪随机 `(x²·13+y·17+z²·19)%128==37`；树干 4-6 格 + 球状树冠（半径 3-4）；跨界树冠写入 `pendingBlocks` 由主线程按帧预算重放。

## 六、网格构建（后台：`ChunkMeshBuilder` + `MeshData`）

- `Build`：遍历 16³，PeaStem 走 `AddPeaQuad`（十字面片，跳过六面剔除）；非 Air/Void 走 6 方向 `TryAddFace`。
- **面剔除规则**：邻居 Air/Void → 保留面；邻居未加载 → 保留面；**当前块或邻居是 Leaves 或 PeaStem → 不剔除**（半透明/不占满格子，透过能看到）。
- `MeshData`：预分配 List；`FillMesh` 原地写复用 Mesh 实例（不 new Mesh/ToArray/RecalculateNormals，法线逐面写死）。
- **豌豆十字面片**：X/Z 两条对角 quad，高度随 stage（0.4/0.7/1.0），法线 ±Y 双面渲染；UV 取自 `PeaTextures.CellByStage`（苗绿/开花顶紫点/结荚中黄点）。
- **UV 数学**：假定虚拟网格 32×24px=768px（`atlasSize=512/16`、`padding=4`、`pixelPerTexture=16`、`totalSize=768`），与真实图集 `Atlas.png` 768×768 对应——**这些常量勿动**；cell 坐标 `(col,row)` 从图集底部起算。

## 七、存档系统（`World/Saver.cs`，~429 行，读写完备）

### 7.1 文件格式 `.vrf`

```
8B 版本头（"VRF1" + int32 version=1，big-endian）
→ 索引区（32³×4B：3B 扇区偏移 + 1B 扇区数）
→ 数据扇区（4096B 对齐：4B 压缩长度 + deflate 数据 + 补零）
```

路径：`Application.persistentDataPath/world_saves/r.{rx}.{ry}.{rz}.vrf`（region=32³ chunk）。

### 7.2 写路径

- 异步 worker（`Task.Run`）+ 信号量队列；入队前**拷贝**数据（防调用方复用数组）。
- **背压**：队列上限 1024（≈16MB），满则主线程同步兜底 `SaveSync`（与 worker 用 `_writeLock` 互斥）。
- **批量 flush**：每 32 chunk 才 flush 一次全部活跃 region（fsync 从每 chunk 1 次降到每 32 chunk 每 region 1 次）。
- **重试**：写失败重入队最多 3 次，放弃时 LogError + Dispose 汇总失败数。
- 退出流程：`OnApplicationQuit` → `SaveAllLoadedChunks`（先放开队列上限 int.MaxValue → 排空 pendingBlocks → 全量入队）→ `OnDestroy` → `saver.Dispose()` 排空落盘。

### 7.3 读路径

`TryLoadVoxelChunk`（后台线程可安全调用，独立开文件流 + FileShare.ReadWrite 与写并发）：校验版本头 → 索引定位 → 扇区读 → 越界防护 → 解压 → `uint→Block`。**任何失败返回 null，回退重新生成**（存档是优化，非正确性依赖）。

### 7.4 ⚠️ 已知隐患（架构评审确认，Step 0 必修）

`SimpleRegionWriter` 构造用 **`FileMode.Create`**——会话内首次写某 region 会整文件重建，**只包含本次会话加载过的 chunk**。对地形是"回退种子重新生成"，可忍受；对将来的遗传数据是**不可逆丢失**。改 `FileMode.OpenOrCreate` + 读旧索引续写。

## 八、玩家交互

### 8.1 `Camera/CameraMove.cs`（自由相机）

WASD+Space/Shift 移动（10 m/s，加速度/减速度插值，水平移动去 Y 分量），鼠标旋转（灵敏度 2，pitch ±89°），聚焦失焦自动解锁光标。

### 8.2 `Player/BlockInteraction.cs`（体素交互）

- **左键破坏**：Amanatides-Woo DDA 网格步进射线（返回命中块 + 进入面法线，8 格距离）；Y=0 的 Bedrock 不可破坏。
- **右键放置**：命中面外侧一格；守卫（不封自己、Y∈[0,256)、目标非固体）；数字键 1-9 切换方块。
- 默认放置列表：Grass/Dirt/Stone/Log/Leaves/Bedrock/**PeaSeed**（7 号键）。
- 破坏/放置后重建目标 + 6 邻居 chunk mesh（入帧预算队列，内部去重）。

## 九、材质 / 图集 / 豌豆占位贴图

- `WorldManager`（DontDestroyOnLoad 单例）持有 `blockMaterial`（Built-in Standard、Cutout `_Mode:1`、queue 2450）；Awake 调 `PeaTextures.InstallToMaterial` **运行时把占位图直接画进 Atlas.png 的 (2,0)(2,1)(2,2) 三个空闲 cell**（24px cell 公式 `ox=c.x*24+4`，16×16 像素，`Apply(true)` 重建 mip 链）。
- 材质/场景引用 guid 断裂（Tuanjie meta base64 guid vs 引用 32-hex guid 不一致）→ **白渲染无报错**——已修复过一次。
- `DataBuffer`：遗留单例（缓存 (blockType,face)→UV），mesh 管线已不再依赖，属死代码残留。

## 十、已知坑位

1. **Tuanjie meta guid**：`Atlas.png` 等资产重新导入可能重生成 meta guid → 静默断引用（白渲染无错）；`.meta` 是 base64、`.mat/.scene` 里是 32-hex，必须一致。
2. **24px cell 公式**：任何图集写入代码必须 `x*24+4`；用 16px 会画错位（豌豆 invisible 无报错）。
3. **`GetBlockState` 括号**：`(_value & StateMask) >> StateShift` 括号不能去。
4. **`FileMode.Create` 整文件重建**（见 7.4）。
5. 新 .cs 文件需 Unity 重导入才进 csproj，编辑器外 LSP 会短暂报"找不到类型"（陈旧诊断，非真实错误）。
6. 代码注释和 Debug.Log 全中文；常量进 `Constants.cs`。

## 十一、豌豆系统现状

- **数据**：`BlockType.PeaStem`，stage 存 bit16-17（**仅渲染读取，无写入/推进**）。
- **渲染**：十字面片 + 运行时占位贴图，高度/贴图随 stage。
- **放置**：玩家数字键 7 放置 `PeaSeed`（stage=0）。
- **无**：自然生成、生长逻辑、收获、遗传数据（基因型仅预留 7 bit 且判定为错误设计）。

## 十二、已定案的演进方向（见 `docs/status/TODO_LIST.md`）

1. **废除** uint 的 bit18-24 孟德尔预留位 → 改 14bit 通用渲染状态。
2. **不引入完整 NBT**，采纳 Minecraft「BlockState 轻量 uint + BlockEntity 稀疏 tile」分离：每 chunk 挂 `Dictionary<ushort, PeaTileData>`（key 复用局部坐标哈希）。
3. `Genome` struct：7 位点×2 等位×2bit 打包 uint32（Crossover 纯位运算，等位值 2/3 预留突变）。
4. 存档 v1→v2 内嵌 tile 段（提前预留 `payloadLen` 给碱基序列）；v1 零迁移兼容。
5. 碱基序列后期进 tile 变长 payload，genome 作表达投影。
6. Step 0 先修 `FileMode.Create` 隐患。
