# 贴图随机旋转方案（2026-08-13）

> 状态：**已实施**（2026-08-13）。本期实现：草方块顶面旋转（UV 循环位移）+ 豌豆十字面片 **XZ 几何旋转**（贴图粘在 quad 上随几何转 → 不对称贴图可见朝向，见 2.1/2.6），白名单见 2.4；方案细节与验证方式见下。
> 关联：`docs/status/WORKLOG_2026-08-13.md`、`AGENTS.md`（Atlas/UV 数学约定）、`MeshData.cs`/`ChunkMeshBuilder.cs`/`TextureRotation.cs`（渲染管线）。

## 1. 目标

消除大面积重复贴图的枯燥感：**豌豆可朝向东南西北四个方向，草方块等有方向性的贴图随机旋转**。朝向由 `(x, y, z) 世界坐标 + 固定 seed` 确定性决定，同一位置永远同一朝向。

## 2. 核心设计

### 2.1 旋转实现：六面体 = UV 角点循环位移；豌豆十字面片 = XZ 几何旋转

当前渲染管线在后台线程构建 mesh（`ChunkMeshBuilder.Build` → `MeshData.AddFace` / `AddPeaQuadCell`），UV 由 `GetFaceUVs`（六面体）或 `AddPeaQuadCell`（十字面片）生成 4 个角点。

- **六面体面（草顶面等）**：旋转 90° 的倍数的实现点是 UV——`AddFace` 的 `rotation` 参数让 `GetFaceUVs` 对 4 个 UV 角点做 `rotation` 次循环位移（水平面旋转观感正确）。
- **豌豆十字面片**：`AddPeaQuadCell` 的 `rotation` 参数改为**几何旋转**——quad a/b 的 8 个顶点绕格心 (x+0.5, z+0.5) 绕 Y 轴旋转（`RotateQuadXZ`，Unity 旋转约定：+90° 时 (x,z)→(z,-x) 方向），**UV 数组不动**（贴图粘在 quad 上随几何转）。

**安全性**：图集 cell 是 24px（16px 内容 + 四周 4px padding）。UV 4 角始终落在 `[u0, u0+s] × [v0, v0+s]` 的 16×16 内容区内（豌豆旋转不动 UV，六面体旋转只是角点重排），**采样范围不越出内容区**，不会采到 padding 或相邻 cell——与 AGENTS.md 的 24px-cell 公式约束无冲突。

**注意**：豌豆十字面片是 X/Z 两对角交叉（`AddPeaQuadCell` 的 quad a/b）。几何绕 Y 旋转 90° 时两片互换对角线，但贴图粘在各自 quad 上、UV 按顶点索引不动——不对称贴图（花/豆荚在贴图一侧）的可见侧随 rotation 换向：rot 0→1→2→3 时复合外观的"强可见侧"绕植株转一圈（北→东→南→西，方向取决于旋转约定）。贴图始终 v=world-up 直立，**不翻转不躺倒**——这是几何旋转相对 UV 旋转的关键区别（UV 旋转在垂直面片上读作翻转/镜像，已否决，见 2.6）。

### 2.2 确定性朝向：世界坐标哈希

```
rotation = Hash(seed, wx, wy, wz) & 3   // 0~3 → 0°/90°/180°/270°
```

- **必须用世界坐标** `(wx, wy, wz)`（= chunk 坐标 ×16 + 局部坐标），不用 chunk 局部坐标：
  - 跨 chunk 边界的一致性（同一格从不同路径/重建时朝向不变）；
  - chunk 卸载重载后朝向不变；
  - 树冠、豌豆顶格等跨 chunk 写入位置朝向稳定。
- 哈希函数沿用项目已有的 golden-ratio 混合风格（参考 `PeaClumpFeature.cs:54` 的 `2654435761u`）：
  ```
  uint h = (uint)seed;
  h = (h ^ (uint)(wx * 0x9E3779B9u)) ^ (uint)(wy * 0x85EBCA77u) ^ (uint)(wz * 0xC2B2AE3Du);
  h ^= h >> 15; h *= 0x85EBCA77u; h ^= h >> 13;
  return (int)(h & 3);
  ```
  纯整数运算，后台线程可安全调用，无共享状态。

### 2.3 seed 注入后台线程

`ChunkMeshBuilder.Build` 在 `Task.Run` 后台线程执行，不能访问 World 实例。方案：

- `MeshBuildData` 增加 `public int Seed;` 字段；
- `ChunkMeshBuilder.CreateSnapshot` 签名增加 `int seed` 参数（或由 World/ChunkStreamer 在快照上赋值）；
- `World.Awake` 已有的 `seed = 985211` 沿 `World → ChunkStreamer.SpawnMeshBuild → CreateSnapshot → MeshBuildData.Seed` 传递。

### 2.4 旋转白名单（哪些方块/面旋转）

各向同性纹理（石头、泥土、基岩等）旋转无视觉差异，**默认不转**；有方向性的纹理才转。白名单建议：

| 方块/面 | 是否旋转 | 说明 |
|---|---|---|
| PeaStem / PeaPlantTop（十字面片） | ✅ | XZ 几何旋转（贴图随几何转 → 可见朝向、不翻转）；见 2.1/2.6 |
| 草方块顶面（Grass, Up） | ✅ | 用户点名 |
| 树叶（Leaves） | ✅（可选） | 有斑驳方向感 |
| 原木端面/侧面（Log） | ✅（可选） | 端面年轮有方向 |
| 石头/泥土/基岩 | ❌ | 各向同性 |
| 草方块侧面/底面 | ❌ | 泥土纹理各向同性 |

实现：在 `BlockUVMap` 或新常量里加一个"可旋转方块集合"（`HashSet<BlockType>`）+ 面维度（顶面 vs 全部），`Build` 时查表决定是否传入非零 rotation。**默认白名单先只开豌豆 + 草顶**，其余可选。

### 2.5 豌豆顶/底格同朝向（跨 chunk 安全）

阶段 2/3 是两格高植株：底部格 `PeaStem` + 顶部格 `PeaPlantTop`，两格**可能不在同一 chunk**（y=15 底 + y=0 顶）。为保证同株两格旋转一致（复合观感是同一朝向）：

- **底部格**：`rotation = Hash(seed, xw, yw, zw)`；
- **顶部格**：`rotation = Hash(seed, xw, yw-1, zw)`（即底部格的世界坐标），无需读邻居数据，纯坐标运算。

### 2.6 豌豆方案 B：单面朝向 quad（2026-08-13 尝试，已回退）

用户曾要求：俯视豌豆有可见正面、随机朝东南西北、永不翻转。几何约束：垂直面片绕 Y 轴 180° 与自身重合 → N/S 同平面、E/W 同平面，任何垂直面片几何**最多 2 种朝向**；十字面片四面同观、无正面可言。

- **方案 B 实施**：`MeshData.AddPeaQuadFacing(x, y, z, cell, facing)` 画一片垂直 quad，居中于格子，facing 0/1/2/3 = 北/东/南/西（四向顶点表已核对绕序，正面贴图正立不镜像）；UV **不做循环位移**（v=world-up → 永不翻转）；双面绘制（`AddQuad`，法线朝上，光照均匀）。
- **已回退（2026-08-13 用户否决）**：用户认为豌豆应为十字面片，单面 quad 观感不对。已恢复**十字面片 + UV 循环位移旋转**（`AddPeaQuadCell` + `GetRotation` 哈希），俯视读作翻转/镜像。
- **最终方案（2026-08-13 用户确认）**：`AddPeaQuadCell` 的 rotation 改为 **XZ 几何旋转**（`RotateQuadXZ`，绕格心绕 Y 轴，UV 不动）——贴图粘在 quad 上随几何转，不对称贴图 → 4 个旋转态互不相同、"强可见侧"绕植株旋转（rot 0→1→2→3 = 北→东→南→西），贴图始终 v=world-up 直立不翻转。`ChunkMeshBuilder` 恢复 `GetRotation` 哈希（底部格 `(xw,yw,zw)`、顶部格 `(xw,yw-1,zw)`）。草顶面 UV 旋转保留（水平面观感正确）。
- `AddPeaQuadFacing` 暂留作回退，验证后清理。

## 3. 实施步骤（按依赖顺序）

| 步骤 | 内容 | 涉及文件 | 验证 |
|---|---|---|---|
| 1 | `MeshData.AddFace` 加 `rotation` 参数，`GetFaceUVs` 支持 UV 循环位移（4 种旋转全对齐） | `MeshData.cs` | 单元推演：四角重排后采样区仍在 16×16 内 |
| 2 | `AddPeaQuadCell` 加 `rotation` 参数 | `MeshData.cs` | 同上 |
| 3 | `MeshBuildData` 加 `Seed`；`CreateSnapshot` 签名加 seed；`World`/`ChunkStreamer` 传递 | `ChunkMeshBuilder.cs` `ChunkStreamer.cs` `World.cs` | 编译通过 |
| 4 | 哈希函数 + 旋转白名单（豌豆、草顶） | `ChunkMeshBuilder.cs` 或新 `TextureRotation.cs` | Play Mode |
| 5 | `Build` 中：普通面按方块查表算 rotation；PeaStem/PeaPlantTop 按 2.5 规则传 rotation | `ChunkMeshBuilder.cs` | Play Mode |
| 6 | 文档同步（AGENTS.md / WORKLOG） | `AGENTS.md` | — |

## 4. 验证方案（Play Mode）

- 豌豆阶段 2/3 植株**顶/底格朝向一致**（同株同向、跨 chunk 也一致）；
- 豌豆植株之间朝向有随机差异（哈希 4 向分布）；花/豆荚的可见侧随朝向换向、**贴图直立不翻转**；
- 草方块顶面随机旋转、相邻格纹理不再完全对齐；
- **确定性**：同 seed 重进游戏 / 移动相机触发 chunk 卸载重载后，同一位置的朝向不变；
- 图集无错位（旋转后无采样到 padding/相邻格的色块）；
- 性能：后台线程纯整数哈希，每格一次，可忽略。

## 5. 明确不做（本期范围外）

- 非 90° 整数倍旋转（会采样 padding，破坏图集布局，需要每格独立 padding 技术，成本高）；
- 方块几何旋转（如苔藓石朝向用独立贴图集，不在此方案内）；
- 运行时动画旋转（随风摆动等，无动画系统）；
- 存档格式变更（朝向是纯渲染层，块值/存档不动）。

## 6. 风险与注意

- `GetFaceUVs` 各方向的 UV 角点顺序与顶点顺序绑定，循环位移时必须**保持四边形环绕方向**（三角形索引不变，仅 UV 重排），否则贴图翻转。
- 白名单若扩大（如原木端面），需确认端面贴图有足够方向性；侧壁旋转意义有限，默认不转侧壁。
- 图集重新生成不影响本功能（rotation 只影响 UV 角点重排，不依赖具体像素）。
