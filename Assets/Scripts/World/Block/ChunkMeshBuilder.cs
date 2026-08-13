using System;
using System.Collections.Generic;
using UnityEngine;

// 体素网格构建的纯数据快照（后台线程只读此快照，不触碰 World/VoxelChunk 实例）
public struct MeshBuildData
{
    public VCPosInWorld Pos;
    public Block[,,] Blocks;   // 本地块快照
    public long Seq;     // 构建代次（World 在主线程分配，单调递增）
    public long ChunkId; // 所属 chunk 实例 ID
    public int Seed;     // 世界种子（贴图随机旋转哈希用；后台线程不能访问 World 实例，必须随快照传递）

    // 豌豆 tile 基因快照（主线程拷贝，后台只读；阶段 3 开花植株按基因选花贴图）。
    // 键 = TileKey：(x<<8)|(y<<4)|z。null = 无 tile（本 chunk 无豌豆）
    public Dictionary<ushort, Genome> TileGenomes;
    // Y-1 邻居 chunk 的 tile 基因快照（PeaPlantTop 在 y=0 时跨 chunk 查下方 PeaStem 的基因）；null = 邻居无 tile
    public Dictionary<ushort, Genome> TileGenomesBelow;

    // 6 个方向邻居的 16×16 边界面（BlockType）；null = 邻居未加载/不存在（保留面）
    public BlockType[,] BorderNorth; // +Z 邻居 z=0 平面 [x,y]
    public BlockType[,] BorderSouth; // -Z 邻居 z=15 平面 [x,y]
    public BlockType[,] BorderEast;  // +X 邻居 x=0 平面 [z,y]
    public BlockType[,] BorderWest;  // -X 邻居 x=15 平面 [z,y]
    public BlockType[,] BorderUp;    // +Y 邻居 y=0 平面 [x,z]
    public BlockType[,] BorderDown;  // -Y 邻居 y=15 平面 [x,z]
}

// 在后台线程执行的纯计算：从块快照生成 MeshData
public static class ChunkMeshBuilder
{
    // 主线程调用：构建快照（getNeighborBlocks 由 World 提供，返回邻居 chunk 的 Block[,,] 或 null；
    // tiles 为本 chunk 的 tile 字典（可 null），getNeighborTiles 取邻居 chunk 的 tile 字典）
    public static MeshBuildData CreateSnapshot(VCPosInWorld pos, Block[,,] blocks,
        Func<VCPosInWorld, Block[,,]> getNeighborBlocks,
        Dictionary<ushort, PeaTileData> tiles,
        Func<VCPosInWorld, Dictionary<ushort, PeaTileData>> getNeighborTiles,
        int seed)
    {
        int s = Constants.CHUNK_SIZE;

        MeshBuildData d = new MeshBuildData
        {
            Pos = pos,
            Blocks = new Block[s, s, s],
            Seed = seed,
            // 基因快照：只拷值（Genome struct），不拷 PeaTileData 引用；后台线程只读副本，线程安全
            TileGenomes = SnapshotGenomes(tiles),
            TileGenomesBelow = SnapshotGenomes(getNeighborTiles(new VCPosInWorld(pos.X, pos.Y - 1, pos.Z))),
        };
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                    d.Blocks[x, y, z] = blocks[x, y, z];

        // 邻居边界面快照；邻居缺失（未加载/空区块未建对象）→ null → 保留面
        d.BorderNorth = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X, pos.Y, pos.Z + 1)), 2, 0);
        d.BorderSouth = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X, pos.Y, pos.Z - 1)), 2, s - 1);
        d.BorderEast  = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X + 1, pos.Y, pos.Z)), 0, 0);
        d.BorderWest  = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X - 1, pos.Y, pos.Z)), 0, s - 1);
        d.BorderUp    = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X, pos.Y + 1, pos.Z)), 1, 0);
        d.BorderDown  = CopyPlane(getNeighborBlocks(new VCPosInWorld(pos.X, pos.Y - 1, pos.Z)), 1, s - 1);

        return d;
    }

    // tile 字典 → 基因值字典（主线程拷贝，后台只读；空/空字典返回 null）
    private static Dictionary<ushort, Genome> SnapshotGenomes(Dictionary<ushort, PeaTileData> tiles)
    {
        if (tiles == null || tiles.Count == 0) return null;
        var snapshot = new Dictionary<ushort, Genome>(tiles.Count);
        foreach (var kv in tiles)
            snapshot[kv.Key] = kv.Value.Genome;
        return snapshot;
    }

    // axis: 0=X 1=Y 2=Z；fixedIndex: 该轴上取哪一面的局部坐标。平面索引规则：
    // Z 轴固定 → [x,y]；X 轴固定 → [z,y]；Y 轴固定 → [x,z]
    private static BlockType[,] CopyPlane(Block[,,] nb, int axis, int fixedIndex)
    {
        if (nb == null) return null; // 邻居未加载/不存在 → null → 保留面
        int s = Constants.CHUNK_SIZE;
        BlockType[,] plane = new BlockType[s, s];
        for (int a = 0; a < s; a++)
            for (int b = 0; b < s; b++)
            {
                if (axis == 2)      plane[a, b] = nb[a, b, fixedIndex].GetBlockType(); // [x,y]
                else if (axis == 0) plane[a, b] = nb[fixedIndex, b, a].GetBlockType(); // [z,y]
                else                plane[a, b] = nb[a, fixedIndex, b].GetBlockType(); // [x,z]
            }
        return plane;
    }

    // 后台线程调用：从快照生成 MeshData（纯计算，无 Unity API 依赖）
    public static MeshData Build(MeshBuildData d)
    {
        int s = Constants.CHUNK_SIZE;
        MeshData meshData = new MeshData(1024);
        meshData.Seq = d.Seq;
        meshData.ChunkId = d.ChunkId;

        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                {
                    var bt = d.Blocks[x, y, z].GetBlockType();
                    // 豌豆不走六面剔除，直接生成十字面片（高度/贴图随生长阶段）
                    if (bt == BlockType.PeaStem)
                    {
                        int stage = (int)(d.Blocks[x, y, z].GetBlockState() & BlockBits.StageMask);
                        int xw = x + d.Pos.X * s, yw = y + d.Pos.Y * s, zw = z + d.Pos.Z * s;
                        // 豌豆旋转（十字面片几何绕 Y 旋转，贴图随几何转；不对称贴图 → 可见朝向，见 TEXTURE_ROTATION.md 2.1）；
                        // 顶部格用同一 hash（yw-1）保证同株同朝向
                        int rot = TextureRotation.GetRotation(d.Seed, xw, yw, zw);
                        // 阶段 0/1：单格用阶段贴图；阶段 2：两格高无花植株；阶段 3：开花，按基因选 4 种花贴图之一
                        if (stage >= 3)
                        {
                            // 底部格基因查本 chunk tile（tile 挂在 PeaStem 上）；缺失则回退无花贴图（防御，正常必有）
                            ushort key = (ushort)((x << 8) | (y << 4) | z);
                            Vector2Int cell = PeaTextures.PlantBottomCell;
                            if (d.TileGenomes != null && d.TileGenomes.TryGetValue(key, out var genome))
                                PeaTextures.GetFlowerCells(genome, out cell, out _);
                            meshData.AddPeaQuadCell(xw, yw, zw, cell, rot);
                        }
                        else if (stage == 2)
                            meshData.AddPeaQuadCell(xw, yw, zw, PeaTextures.PlantBottomCell, rot);
                        else
                            meshData.AddPeaQuad(xw, yw, zw, stage, rot);
                        continue;
                    }
                    // 豌豆两格高植株顶部格：独立方块类型；阶段 3 花贴图随下方 PeaStem 基因（同株同基因，跨 chunk 由 TileGenomesBelow 兜底）
                    if (bt == BlockType.PeaPlantTop)
                    {
                        int xw = x + d.Pos.X * s, yw = y + d.Pos.Y * s, zw = z + d.Pos.Z * s;
                        // 与底部格同 hash（yw-1）→ 同株同朝向
                        int rot = TextureRotation.GetRotation(d.Seed, xw, yw - 1, zw);
                        Vector2Int cell = PeaTextures.PlantTopCell;
                        Dictionary<ushort, Genome> genes = y > 0 ? d.TileGenomes : d.TileGenomesBelow;
                        ushort key = (ushort)((x << 8) | ((y > 0 ? y - 1 : Constants.CHUNK_SIZE - 1) << 4) | z);
                        if (genes != null && genes.TryGetValue(key, out var genome))
                            PeaTextures.GetFlowerCells(genome, out _, out cell);
                        meshData.AddPeaQuadCell(xw, yw, zw, cell, rot);
                        continue;
                    }
                    if (bt != BlockType.Air && bt != BlockType.Void)
                    {
                        TryAddFace(meshData, d, x, y, z, Direction.Up);
                        TryAddFace(meshData, d, x, y, z, Direction.Down);
                        TryAddFace(meshData, d, x, y, z, Direction.North);
                        TryAddFace(meshData, d, x, y, z, Direction.South);
                        TryAddFace(meshData, d, x, y, z, Direction.East);
                        TryAddFace(meshData, d, x, y, z, Direction.West);
                    }
                }
        return meshData;
    }

    private static void TryAddFace(MeshData meshData, MeshBuildData d, int x, int y, int z, Direction dir)
    {
        if (ShouldBeEliminated(d, x, y, z, dir)) return;

        int s = Constants.CHUNK_SIZE;
        int xw = x + d.Pos.X * s;
        int yw = y + d.Pos.Y * s;
        int zw = z + d.Pos.Z * s;
        // 贴图随机旋转：白名单内的方块/面才转（默认仅草方块顶面），其余 rotation=0 不转（各向同性纹理无视觉差异）
        var bt = d.Blocks[x, y, z].GetBlockType();
        int rot = TextureRotation.ShouldRotateFace(bt, dir) ? TextureRotation.GetRotation(d.Seed, xw, yw, zw) : 0;
        meshData.AddFace(xw, yw, zw, dir, d.Blocks[x, y, z], rot);
    }

    // 面剔除（与原 VoxelChunk.ShouldBeEliminated 逻辑一致，改为读快照）
    private static bool ShouldBeEliminated(MeshBuildData d, int x, int y, int z, Direction dir)
    {
        int s = Constants.CHUNK_SIZE;
        BlockType neighborBt = BlockType.Air; // 初始值仅用于满足确定性赋值；cross=false 路径必然在 switch 中覆盖
        BlockType[,] plane = null;
        bool cross = false;
        int i0 = 0, i1 = 0;

        switch (dir)
        {
            case Direction.North: // +Z
                if (z == s - 1) { cross = true; plane = d.BorderNorth; i0 = x; i1 = y; }
                else neighborBt = d.Blocks[x, y, z + 1].GetBlockType();
                break;
            case Direction.South: // -Z
                if (z == 0) { cross = true; plane = d.BorderSouth; i0 = x; i1 = y; }
                else neighborBt = d.Blocks[x, y, z - 1].GetBlockType();
                break;
            case Direction.East: // +X
                if (x == s - 1) { cross = true; plane = d.BorderEast; i0 = z; i1 = y; }
                else neighborBt = d.Blocks[x + 1, y, z].GetBlockType();
                break;
            case Direction.West: // -X
                if (x == 0) { cross = true; plane = d.BorderWest; i0 = z; i1 = y; }
                else neighborBt = d.Blocks[x - 1, y, z].GetBlockType();
                break;
            case Direction.Up: // +Y
                if (y == s - 1) { cross = true; plane = d.BorderUp; i0 = x; i1 = z; }
                else neighborBt = d.Blocks[x, y + 1, z].GetBlockType();
                break;
            case Direction.Down: // -Y
                if (y == 0) { cross = true; plane = d.BorderDown; i0 = x; i1 = z; }
                else neighborBt = d.Blocks[x, y - 1, z].GetBlockType();
                break;
            default:
                neighborBt = BlockType.Air;
                break;
        }

        if (cross)
        {
            if (plane == null) return false; // 邻居未加载 → 保留面
            neighborBt = plane[i0, i1];
        }

        if (neighborBt == BlockType.Air || neighborBt == BlockType.Void) return false;

        var bt = d.Blocks[x, y, z].GetBlockType();
        // 半透明方块（树叶）与不占满格子的豌豆（十字面片，含两格高植株顶部格）不剔除邻居面：透过它们能看到相邻方块
        if (bt == BlockType.Leaves || neighborBt == BlockType.Leaves) return false;
        if (bt == BlockType.PeaStem || neighborBt == BlockType.PeaStem) return false;
        if (bt == BlockType.PeaPlantTop || neighborBt == BlockType.PeaPlantTop) return false;

        return true;
    }
}
