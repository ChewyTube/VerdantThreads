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

    public static readonly Block ERROR      = new Block(BlockType.ERROR); // ERROR Block

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

            BlockType.ERROR => ERROR, // ERROR Block\
            _ => ERROR,
        };
    }
}