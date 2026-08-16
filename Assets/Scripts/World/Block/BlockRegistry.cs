public static class BlockRegistry
{
    public static readonly Block Air        = new Block(BlockType.Air);
    public static readonly Block Grass      = new Block(BlockType.Grass);
    public static readonly Block Dirt       = new Block(BlockType.Dirt);
    public static readonly Block Bedrock    = new Block(BlockType.Bedrock);
    public static readonly Block Void       = new Block(BlockType.Void);
    public static readonly Block Stone      = new Block(BlockType.Stone);
    public static readonly Block Log        = new Block(BlockType.Log);
    public static readonly Block Leaves     = new Block(BlockType.Leaves);
    public static readonly Block PeaSeed    = new Block((uint)BlockType.PeaStem); // 豌豆种子（生长阶段 0=最小苗）
    public static readonly Block PeaPlantTop = new Block(BlockType.PeaPlantTop);   // 豌豆两格高植株顶部格（无 tile 无阶段）
    public static readonly Block PeaPlantMiddle = new Block(BlockType.PeaPlantMiddle); // 高茎豌豆中部格（无 tile，状态位带阶段 + 高茎标志）
    public static readonly Block PeaWithered = new Block(BlockType.PeaWithered);   // 豌豆枯萎植株（采收次数耗尽；贴图列 8 用户已绘制）

    public static Block GetBlock(BlockType blockType)
    {
        return blockType switch
        {
            BlockType.Air => Air,
            BlockType.Grass => Grass,
            BlockType.Dirt => Dirt,
            BlockType.Bedrock => Bedrock,
            BlockType.Void => Void,
            BlockType.Stone => Stone,
            BlockType.Log => Log,
            BlockType.Leaves => Leaves,
            BlockType.PeaStem => PeaSeed,
            BlockType.PeaPlantTop => PeaPlantTop,
            BlockType.PeaPlantMiddle => PeaPlantMiddle,
            BlockType.PeaWithered => PeaWithered,

            _ => throw new System.ArgumentException($"未知方块类型: {blockType}"),
        };
    }
}