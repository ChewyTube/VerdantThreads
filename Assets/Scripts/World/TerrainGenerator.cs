using System;
using System.Collections.Generic;
using UnityEngine;

// 地形生成器：噪声地形 + 地物系统（树/豌豆丛）+ 存档读路径（#14）。
// 纯生成逻辑，不碰任何调度队列与 Unity 主线程对象，可在后台线程调用；
// 确定性（固定 seed + 位置公式）保证同坐标永远生成同一结果，与存档数据可复现。
// 地物见 World/Feature/（Feature 基类 + TreeFeature + PeaClumpFeature）：列循环地形填充完成后
// 统一按锚点放置，跨界写入由 data.Setblock → pendingBlocks / AddPendingTileWrite 处理。
public class TerrainGenerator
{
    // 被多个后台生成 Task 并发只读调用（GetNoise），FastNoiseLite 只读线程安全；禁止在后台线程重新配置/写入 noise
    private readonly FastNoiseLite noise = new FastNoiseLite();
    private readonly int seed;
    private readonly Saver saver; // 读路径：生成前先查存档，命中则用已保存数据（含玩家修改）
    private readonly Feature[] features; // 地物列表（生成期装饰物：树/豌豆丛）

    public TerrainGenerator(int seed, Saver saver)
    {
        this.seed = seed;
        this.saver = saver;
        InitializeNoise();
        // 地物装配：顺序即放置优先级（树先、豌豆丛后；豌豆丛靠 Air 检查避让树干）
        // 豌豆丛注入列高度函数（与地形填充同公式，纯函数、后台线程安全）
        features = new Feature[]
        {
            new TreeFeature(),
            new PeaClumpFeature((bx, bz) => (int)((noise.GetNoise(bx, bz) + 1) * 0.5f * 64)),
        };
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
        // 存档 v2：同时读回 tile 快照（豌豆基因/世代/生长时间），随数据传给主线程回挂
        if (saver.TryLoadVoxelChunk(pos, out var loaded, out var tiles))
        {
            var loadedData = new VoxelChunkData(loaded, pos, new List<(BlockPosInWorld, Block)>(), fillAir: false);
            loadedData.SetLoadedTiles(tiles);
            return loadedData;
        }

        int CHUNK_SIZE = Constants.CHUNK_SIZE;

        int baseHeight = 0;

        VoxelChunkData data = new VoxelChunkData(blocks, pos, new List<(BlockPosInWorld, Block)>());

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

                // 地物锚点（地表上方一格；树干基部/豌豆落脚格），锚点必须落在本 chunk 的 Y 范围内
                // 跨界（上/邻 chunk）写入由 Setblock → pendingBlocks 处理
                int anchorY = baseHeight + 4;
                int anchorLocalY = anchorY - pos.Y * CHUNK_SIZE;
                if (anchorLocalY >= 0 && anchorLocalY < CHUNK_SIZE)
                {
                    foreach (var feature in features)
                    {
                        if (feature.CanPlace(data, blockX, anchorY, blockZ))
                            feature.Place(data, blockX, anchorY, blockZ);
                    }
                }
            }

        return data;
    }
}
