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
    // 主线程调用：构建快照（getNeighborBlocks 由 World 提供，返回邻居 chunk 的 Block[,,] 或 null）
    public static MeshBuildData CreateSnapshot(VCPosInWorld pos, Block[,,] blocks, Func<VCPosInWorld, Block[,,]> getNeighborBlocks)
    {
        int s = Constants.CHUNK_SIZE;

        MeshBuildData d = new MeshBuildData
        {
            Pos = pos,
            Blocks = new Block[s, s, s]
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

    // axis: 0=X 1=Y 2=Z；fixedIndex: 该轴上取哪一面的局部坐标。平面索引规则：
    // Z 轴固定 → [x,y]；X 轴固定 → [z,y]；Y 轴固定 → [x,z]
    private static BlockType[,] CopyPlane(Block[,,] nb, int axis, int fixedIndex)
    {
        if (nb == null) return null;
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
                        // 阶段 0/1：单格十字用阶段贴图；阶段 2/3：两格高植株底部格贴图（顶部格由 PeaPlantTop 画）
                        if (stage >= 2)
                            meshData.AddPeaQuadCell(xw, yw, zw, PeaTextures.PlantBottomCell);
                        else
                            meshData.AddPeaQuad(xw, yw, zw, stage);
                        continue;
                    }
                    // 豌豆两格高植株顶部格：独立方块类型，固定顶部贴图
                    if (bt == BlockType.PeaPlantTop)
                    {
                        int xw = x + d.Pos.X * s, yw = y + d.Pos.Y * s, zw = z + d.Pos.Z * s;
                        meshData.AddPeaQuadCell(xw, yw, zw, PeaTextures.PlantTopCell);
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
        meshData.AddFace(xw, yw, zw, dir, d.Blocks[x, y, z]);
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
