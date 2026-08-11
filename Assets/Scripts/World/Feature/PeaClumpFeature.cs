using System;
using System.Collections.Generic;

// 豌豆丛生地物：一丛豌豆（14-18 株）在中心周围半径内聚集，模拟真实丛生/分蘖形态。
// 整丛共享一个母本基因（丛中心世界坐标哈希派生，28 bit），每株再叠加株坐标哈希驱动的
// 确定性微变异（1-2 个等位基因位 0↔1 翻转），丛内高度相似但不完全相同。
// 契约：全部确定性哈希（严禁 System.Random/Genome.Random()，时间种子非确定 + 共享静态源
// 后台多线程不安全）；每株独立 Air 检查（树先放、丛后放，靠 Air 检查避让树干/树冠）；
// 跨 chunk 株：块走 data.Setblock → pendingBlocks、tile 走 AddPendingTileWrite →
// pendingTileWrites 世界坐标通道，两条路在目标 chunk 汇合。
public class PeaClumpFeature : Feature
{
    // 地形高度函数：heightAt(bx, bz) 返回该列地形高度 baseHeight（纯函数、后台线程安全）
    private readonly Func<int, int, int> heightAt;

    public PeaClumpFeature(Func<int, int, int> heightAt)
    {
        this.heightAt = heightAt;
    }

    // 只在丛中心列返回 true：密度哈希 + 中心格 Air 检查（避让树干）
    public override bool CanPlace(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        if ((blockX * 7 + blockZ * 13 + groundY * 29) % Constants.PEA_CLUMP_DENSITY != 0)
            return false;

        int lx = blockX & (Constants.CHUNK_SIZE - 1);
        int lz = blockZ & (Constants.CHUNK_SIZE - 1);
        int anchorLocalY = groundY % Constants.CHUNK_SIZE;

        return data.GetBlocksData()[lx, anchorLocalY, lz].GetBlockType() == BlockType.Air;
    }

    public override void Place(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        VCPosInWorld pos = data.GetPos();

        // 母本基因：丛中心世界坐标哈希（28 bit）
        uint centerHash = (uint)(blockX * 73856093 ^ groundY * 19349663 ^ blockZ * 83492791);
        Genome mother = new Genome(centerHash & 0x0FFFFFFFu);

        // 株数：14-18，由 centerHash 高位移位派生
        int plantCount = Constants.PEA_CLUMP_MIN_PLANTS
            + (int)((centerHash >> 24) % (Constants.PEA_CLUMP_MAX_PLANTS - Constants.PEA_CLUMP_MIN_PLANTS + 1));

        // 中心株：CanPlace 已保证锚点格 Air（锚点必在本 chunk 的 Y 范围内），直接放置
        PlacePlant(data, pos, blockX, groundY, blockZ, mother);

        // 卫星株：半径内确定性 jitter，去重偏移（株数可能略少于 plantCount，确定性不变）
        var used = new HashSet<(int, int)> { (0, 0) }; // 中心已占用
        for (int i = 0; i < plantCount - 1; i++)
        {
            // 每株偏移：centerHash 混入 i（黄金分割常数搅动），半径内确定性 jitter
            uint seed = centerHash ^ (uint)(i * 2654435761u);
            int dx = (int)(seed & 0x7Fu) % (Constants.PEA_CLUMP_RADIUS * 2 + 1) - Constants.PEA_CLUMP_RADIUS;
            int dz = (int)((seed >> 8) & 0x7Fu) % (Constants.PEA_CLUMP_RADIUS * 2 + 1) - Constants.PEA_CLUMP_RADIUS;
            if (!used.Add((dx, dz))) continue; // 同偏移跳过

            int plantBX = blockX + dx;
            int plantBZ = blockZ + dz;
            int plantY = heightAt(plantBX, plantBZ) + 4; // 株在自己列的地表上方一格

            PlacePlant(data, pos, plantBX, plantY, plantBZ, mother);
        }
    }

    // 放置单株：块写当前/跨界（pendingBlocks），tile 走世界坐标通道；基因 = 母本 ± 微变异
    private void PlacePlant(VoxelChunkData data, VCPosInWorld pos, int plantBX, int plantY, int plantBZ, Genome mother)
    {
        // 每株基因：母本 + 株坐标哈希驱动的确定性微变异（翻转 1-2 个等位基因位，0↔1）
        uint plantHash = (uint)(plantBX * 73856093 ^ plantY * 19349663 ^ plantBZ * 83492791);
        int flipCount = 1 + (int)((plantHash >> 16) & 1u); // 1-2 次
        Genome genome = mother;
        for (int f = 0; f < flipCount; f++)
        {
            int locus = (int)((plantHash >> (f * 6)) % Genome.LocusCount);
            int alleleIdx = (int)((plantHash >> (f * 6 + 3)) % Genome.AllelesPerLocus);
            int oldVal = genome.GetAllele(locus, alleleIdx);
            genome = genome.WithAllele(locus, alleleIdx, 1 - oldVal); // 0↔1 翻转，确定性
        }

        // 株在本 chunk 内 → 读本 chunk 块做 Air 检查（避让树干/树冠）；跨 chunk 无法预检，best-effort 写入
        int relX = plantBX - pos.X * Constants.CHUNK_SIZE;
        int relY = plantY - pos.Y * Constants.CHUNK_SIZE;
        int relZ = plantBZ - pos.Z * Constants.CHUNK_SIZE;
        bool inChunk = (uint)relX < Constants.CHUNK_SIZE && (uint)relY < Constants.CHUNK_SIZE && (uint)relZ < Constants.CHUNK_SIZE;
        if (inChunk && data.GetBlocksData()[relX, relY, relZ].GetBlockType() != BlockType.Air)
            return; // 本 chunk 内格已被占（树干/树冠/其他）→ 不放

        data.Setblock(BlockRegistry.GetBlock(BlockType.PeaStem), relX, relY, relZ); // 跨 chunk 自动进 pendingBlocks
        data.AddPendingTileWrite(new BlockPosInWorld(plantBX, plantY, plantBZ), genome);
    }
}
