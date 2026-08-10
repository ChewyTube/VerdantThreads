using System.Collections.Generic;
using UnityEngine;

public readonly struct BlockUVSet
{
    private readonly Vector2Int[] _uvs; // 长度固定为6

    public BlockUVSet(Vector2Int all) : this(all, all, all, all, all, all) { }
    public BlockUVSet(Vector2Int top, Vector2Int buttom, Vector2Int side) : this(side, side, top, buttom, side, side){ }

    public BlockUVSet(
        Vector2Int east,    Vector2Int west,
        Vector2Int up,      Vector2Int down,
        Vector2Int south,   Vector2Int north)
    {
        _uvs = new Vector2Int[(int)Direction.Count] // 顺序与 Direction 数值一致（south=-Z、north=+Z）
        {
            east, west, up, down, south, north
        };
    }

    /// <summary>按 Direction 获取指定面的 UV（无分配）</summary>
    public Vector2Int GetUV(Direction face) => _uvs[(int)face];
}

public static class BlockUVMap
{
    private static readonly Dictionary<BlockType, BlockUVSet> uvTable = new()
    {
        // 单面纹理：所有面相同
        [BlockType.Void] = new(new(1, 1)),
        [BlockType.Air] = new(new(1, 1)),
        [BlockType.Dirt] = new(new(0, 3)),
        [BlockType.Bedrock] = new(new(0, 2)),
        [BlockType.Stone] = new(new(0, 4)),
        [BlockType.Leaves] = new(new(0, 6)),

        [BlockType.Grass] = new(
            new(0, 1),
            new(0, 3),
            new(0, 0)
        ),

        [BlockType.Log] = new(
            new(1, 5),
            new(1, 5),
            new(0, 5)
        ),
    };


    private static readonly BlockUVSet FallbackUV = new(new(1, 0));

    public static Vector2Int GetUV(BlockType blockType, Direction face)
    {
        return uvTable.TryGetValue(blockType, out var uvSet)
            ? uvSet.GetUV(face)
            : FallbackUV.GetUV(face);
    }

    // 向后兼容
    public static Vector2Int GetUVIndex(BlockType blockType)
    {
        return GetUV(blockType, Direction.Up);
    }
}