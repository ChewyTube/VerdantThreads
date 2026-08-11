using System;

// 香樟风球冠树地物：从 TerrainGenerator 原内嵌树代码 1:1 搬迁，外观行为完全不变。
// 锚点（groundY）为地表上方一格（baseHeight+4）；树干/树冠跨出本 chunk 的写入
// 由 data.Setblock → pendingBlocks 处理，主线程在目标 chunk 加载后重放。
public class TreeFeature : Feature
{
    // 与原 HasTree 一致：确定性伪随机决定本列是否长树（固定 seed 可复现）
    public override bool CanPlace(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        return (blockX * blockX * 13 + groundY * 17 + blockZ * blockZ * 19) % 128 == 37;
    }

    public override void Place(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        // 块内坐标：CHUNK_SIZE=16 为 2 的幂，blockX/blockZ 的 &15 即原列循环局部 x/z（等价）
        int lx = blockX & (Constants.CHUNK_SIZE - 1);
        int lz = blockZ & (Constants.CHUNK_SIZE - 1);
        int realY = groundY % Constants.CHUNK_SIZE; // 原 (baseHeight+4)%16

        // 香樟风球冠树：树干下部裸露、上部穿入球状树冠（确定性伪随机，保持固定 seed 的确定性）
        int trunkHeight = (lx * 31 + lz * 17) % 3 + 4;  // 树干 4-6 格
        int crownRadius = (lx * 7 + lz * 11) % 2 + 3;   // 树冠水平半径 3-4 格
        int trunkTop = realY + trunkHeight;             // 树干顶
        int crownCenterY = trunkTop + crownRadius - 2;  // 球心：树干顶深入球内 2 格
        int crownBottom = crownCenterY - crownRadius;   // 树冠底（树干下部裸露 2 格后展开）
        int crownTop = crownCenterY + crownRadius;

        // 树干（下部裸露，上部被树冠包裹）
        for (int i = realY; i < trunkTop; i++)
        {
            data.Setblock(BlockRegistry.Log, lx, i, lz);
        }

        // 球状树冠：逐层按球方程取水平半径（向上取整保证饱满），树冠内的树干格保留不盖
        for (int layerY = crownBottom; layerY <= crownTop; layerY++)
        {
            float dy = layerY - crownCenterY;
            int layerRadius = (int)Math.Ceiling(Math.Sqrt(crownRadius * crownRadius - dy * dy));

            for (int j = -layerRadius; j <= layerRadius; j++)
            {
                for (int k = -layerRadius; k <= layerRadius; k++)
                {
                    int d2 = j * j + k * k;
                    // 球内填充；树冠内的树干格跳过；边缘格按确定性扰动少量缺角增加自然感
                    if (d2 <= layerRadius * layerRadius &&
                        !(j == 0 && k == 0 && layerY < trunkTop) &&
                        !(d2 == layerRadius * layerRadius && (lx * 13 + lz * 7 + layerY * 3) % 4 == 0))
                    {
                        data.Setblock(BlockRegistry.Leaves, lx, layerY, lz, j, 0, k);
                    }
                }
            }
        }
    }
}
