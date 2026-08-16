// 豌豆 tile 数据：挂在 VoxelChunk 的 tile 字典上，主线程独享。
// 每个豌豆方块（PeaStem）对应一份 PeaTileData，保存基因与世代。
// 生长阶段存方块状态位（StageMask bit16-18），由 MC 式随机刻推进；GrowthTime 已退役（存档 v3）。
// HTT 载荷（Payload）：主线程独享的层级标签树，存 8 位点采收基因（"harvestGenome"）等扩展数据；
// 序列化/反序列化均在主线程进行（见 HTTSerializer / docs/design/HTT.md），Genome/Generation 字段不动。
public class PeaTileData
{
    public Genome Genome;      // 基因
    public int Generation;     // 世代（种植时 0）
    public HTTCompound Payload; // HTT 载荷（可空，主线程独享；空为基线株）

    public PeaTileData(Genome genome, int generation)
    {
        Genome = genome;
        Generation = generation;
    }

    // 读取采收基因组：无载荷/缺键 → default（0 = 全隐性）
    public HarvestGenome GetHarvestGenome()
    {
        return Payload != null ? new HarvestGenome((uint)Payload.GetInt("harvestGenome")) : default;
    }

    // 写入采收基因组：惰性建树（无载荷才新建 Compound）
    public void SetHarvestGenome(HarvestGenome genome)
    {
        Payload ??= new HTTCompound();
        Payload.SetInt("harvestGenome", (int)genome.Value);
    }
}
