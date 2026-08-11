// 豌豆 tile 数据：挂在 VoxelChunk 的 tile 字典上，主线程独享。
// 每个豌豆方块（PeaStem）对应一份 PeaTileData，保存基因、世代与生长进度。
public class PeaTileData
{
    public Genome Genome;      // 基因
    public int Generation;     // 世代（种植时 0）
    public float GrowthTime;   // 已生长秒数（生长 tick 累加）

    public PeaTileData(Genome genome, int generation)
    {
        Genome = genome;
        Generation = generation;
        GrowthTime = 0f;
    }
}
