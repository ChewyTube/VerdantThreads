// 豌豆自然生成地物：草地锚点格随机长出阶段 0 豌豆苗，并登记 tile。
// 密度与基因均为确定性哈希（同坐标重启后结果一致），严禁使用 Genome.Random()
// （时间种子非确定 + 共享静态 Random 后台多线程不安全）。
public class PeaFeature : Feature
{
    // 密度哈希 + 目标格 Air 检查（避让树干：树先放置、豌豆后放置，靠 Air 检查绕开树干）
    public override bool CanPlace(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        if ((blockX * 7 + blockZ * 13 + groundY * 29) % Constants.PEA_FEATURE_DENSITY != 0)
            return false;

        int lx = blockX & (Constants.CHUNK_SIZE - 1);
        int lz = blockZ & (Constants.CHUNK_SIZE - 1);
        int anchorLocalY = groundY % Constants.CHUNK_SIZE;

        return data.GetBlocksData()[lx, anchorLocalY, lz].GetBlockType() == BlockType.Air;
    }

    public override void Place(VoxelChunkData data, int blockX, int groundY, int blockZ)
    {
        int lx = blockX & (Constants.CHUNK_SIZE - 1);
        int lz = blockZ & (Constants.CHUNK_SIZE - 1);
        int anchorLocalY = groundY % Constants.CHUNK_SIZE;

        // 确定性基因：由列坐标哈希派生（28 bit 装得下），保证同坐标重启后基因一致
        uint genomeValue = (uint)(blockX * 73856093 ^ groundY * 19349663 ^ blockZ * 83492791) & 0x0FFFFFFFu;
        Genome genome = new Genome(genomeValue);

        data.Setblock(BlockRegistry.GetBlock(BlockType.PeaStem), lx, anchorLocalY, lz); // 默认状态 = 阶段 0

        // 登记 tile（key 公式与 ChunkStore.TileKey 一致，世代 0、生长时间 0），主线程 CreateChunk 后统一回挂
        data.AddPendingTile(
            (ushort)((lx << (Constants.CHUNK_SIZE_LOG2 * 2)) | (anchorLocalY << Constants.CHUNK_SIZE_LOG2) | lz),
            genome);
    }
}
