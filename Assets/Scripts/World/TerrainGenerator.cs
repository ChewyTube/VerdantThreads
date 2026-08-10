using System;
using System.Collections.Generic;
using UnityEngine;

// 地形生成器：噪声地形 + 树木生成 + 存档读路径（#14）。
// 纯生成逻辑，不碰任何调度队列与 Unity 主线程对象，可在后台线程调用；
// 确定性（固定 seed + 位置公式）保证同坐标永远生成同一结果，与存档数据可复现。
public class TerrainGenerator
{
    private readonly FastNoiseLite noise = new FastNoiseLite();
    private readonly int seed;
    private readonly Saver saver; // 读路径：生成前先查存档，命中则用已保存数据（含玩家修改）

    public TerrainGenerator(int seed, Saver saver)
    {
        this.seed = seed;
        this.saver = saver;
        InitializeNoise();
    }

    private void InitializeNoise()
    {
        noise.SetSeed(seed);                          // 固定种子保证结果可复现
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFrequency(0.002f);                   // 基础频率，控制地形尺度
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(6);                   // 叠加层数，越多细节越丰富
        noise.SetFractalLacunarity(2.0f);             // 每层频率倍增系数
        noise.SetFractalGain(0.5f);                   // 每层振幅衰减系数
    }

    // 生成单个 chunk 数据：先查存档，命中则直接使用已保存数据（跳过重新生成，保留玩家修改）
    public VoxelChunkData GenerateVoxelChunkData(VCPosInWorld pos, Block[,,] blocks)
    {
        // 读路径：#14 先查存档，命中则直接用已保存数据（含玩家修改），跳过重新生成
        Block[,,] loaded = saver.TryLoadVoxelChunk(pos);
        if (loaded != null)
        {
            return new VoxelChunkData(loaded, pos, new List<(BlockPosInWorld, Block)>(), fillAir: false);
        }

        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        int baseHeight = 0;

        VoxelChunkData data = new VoxelChunkData(blocks, pos, new List<(BlockPosInWorld, Block)>());

        int maxY = (pos.Y + 1) * CHUNK_SIZE;

        for (int x = 0; x < CHUNK_SIZE; x++)
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                int blockX = pos.X * CHUNK_SIZE + x;
                int blockZ = pos.Z * CHUNK_SIZE + z;

                baseHeight = (int)((noise.GetNoise(blockX, blockZ) + 1) * 0.5f * 64);

                for (int y = 0; y < CHUNK_SIZE; y++)
                {
                    int blockY = pos.Y * CHUNK_SIZE + y;

                    if (blockY == 0)
                    {
                        data.Setblock(BlockRegistry.Bedrock, x, y, z);
                    }
                    else if (blockY > 0 && blockY <= baseHeight)
                    {
                        data.Setblock(BlockRegistry.Stone, x, y, z);
                    }
                    else if (blockY <= baseHeight + 2)
                    {
                        data.Setblock(BlockRegistry.Dirt, x, y, z);
                    }
                    else if (blockY == baseHeight + 3)
                    {
                        data.Setblock(BlockRegistry.Grass, x, y, z);
                    }
                }

                if (HasTree(blockX, baseHeight + 4, blockZ) && (baseHeight + 4 < maxY) && (baseHeight + 4) >= (maxY - CHUNK_SIZE))
                {
                    // 香樟风球冠树：树干下部裸露、上部穿入球状树冠（确定性伪随机，保持固定 seed 的确定性）
                    int realY = (baseHeight + 4) % 16;

                    int trunkHeight = (x * 31 + z * 17) % 3 + 4;  // 树干 4-6 格
                    int crownRadius = (x * 7 + z * 11) % 2 + 3;   // 树冠水平半径 3-4 格
                    int trunkTop = realY + trunkHeight;           // 树干顶
                    int crownCenterY = trunkTop + crownRadius - 2; // 球心：树干顶深入球内 2 格
                    int crownBottom = crownCenterY - crownRadius;  // 树冠底（树干下部裸露 2 格后展开）
                    int crownTop = crownCenterY + crownRadius;

                    // 树干（下部裸露，上部被树冠包裹）
                    for (int i = realY; i < trunkTop; i++)
                    {
                        data.Setblock(BlockRegistry.Log, x, i, z);
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
                                    !(d2 == layerRadius * layerRadius && (x * 13 + z * 7 + layerY * 3) % 4 == 0))
                                {
                                    data.Setblock(BlockRegistry.Leaves, x, layerY, z, j, 0, k);
                                }
                            }
                        }
                    }
                }
            }

        return data;
    }

    private bool HasTree(int x, int y, int z)
    {
        return (x * x * 13 + y * 17 + z * z * 19) % 128 == 37;
    }
}
