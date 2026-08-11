// 豌豆 tile 数据：挂在 VoxelChunk 的 tile 字典上，主线程独享。
// 每个豌豆方块（PeaStem）对应一份 PeaTileData，保存基因与世代。
// 生长阶段存方块状态位（StageMask bit16-17），由 MC 式随机刻推进；GrowthTime 已退役（存档 v3）。
public class PeaTileData
{
    public Genome Genome;      // 基因
    public int Generation;     // 世代（种植时 0）

    public PeaTileData(Genome genome, int generation)
    {
        Genome = genome;
        Generation = generation;
    }
}
