using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MeshData
{
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<Vector2> uvs = new List<Vector2>();


    // 图集配置
    private int atlasSize = 512 / 16; 
    private int padding = 4;
    private int pixelPerTexture = 16;
    private int sizePerTexture;
    private int totalSize;

    public MeshData()
    {
        sizePerTexture = pixelPerTexture + 2 * padding;
        totalSize = atlasSize * sizePerTexture;
    }

    public void Clear()
    {
        vertices.Clear(); triangles.Clear(); uvs.Clear();
    }

    public void AddFace(int x, int y, int z, Direction dir, Block block)
    {
        BlockType blockType = block.GetBlockType();

        int vertexStart = vertices.Count;

        // 1. 添加4个顶点（构成一个面）
        Vector3[] faceVertices = GetFaceVertices(x, y, z, dir);
        vertices.AddRange(faceVertices);

        // 2. 添加2个三角形（索引）
        triangles.Add(vertexStart + 0);
        triangles.Add(vertexStart + 1);
        triangles.Add(vertexStart + 2);
        triangles.Add(vertexStart + 0);
        triangles.Add(vertexStart + 2);
        triangles.Add(vertexStart + 3);

        // 3. 计算 UV
        Vector2 uvOffset = GetUVOffset(blockType, (int)dir);
        Vector2[] faceUVs = GetFaceUVs(uvOffset, dir);
        uvs.AddRange(faceUVs);
    }

    private Vector3[] GetFaceVertices(int x, int y, int z, Direction dir)
    {
        return dir switch
        {
            Direction.Up    => new Vector3[] { new(x + 1, y + 1, z), new(x, y + 1, z), new(x, y + 1, z + 1), new(x + 1, y + 1, z + 1) },
            Direction.Down  => new Vector3[] { new(x, y, z), new(x + 1, y, z), new(x + 1, y, z + 1), new(x, y, z + 1) },
            Direction.North => new Vector3[] { new(x + 1, y + 1, z + 1), new(x, y + 1, z + 1), new(x, y, z + 1), new(x + 1, y, z + 1) },
            Direction.South => new Vector3[] { new(x, y + 1, z), new(x + 1, y + 1, z), new(x + 1, y, z), new(x, y, z) },
            Direction.East  => new Vector3[] { new(x + 1, y, z), new(x + 1, y + 1, z), new(x + 1, y + 1, z + 1), new(x + 1, y, z + 1)},
            Direction.West  => new Vector3[] { new(x, y + 1, z), new(x, y, z), new(x, y, z + 1), new(x, y + 1, z + 1) },
            _ => new Vector3[4]
        };
    }

    private Vector2 GetUVOffset(BlockType blockType, int faceIndex=2)
    {
        // Vector2Int uvIndex = BlockUVMap.GetUVIndex(blockType);
        Vector2Int uvIndex = BlockUVMap.GetUV(blockType, faceIndex);

        if (uvIndex == null)
        {
            uvIndex = BlockUVMap.GetUV(BlockType.ERROR, faceIndex);
        }

        Vector2 uv = new(0, 0);
        if(!DataBuffer.Instance.Block2uvs.TryGetValue((blockType, faceIndex), out uv))
        {
            float u = 1f / totalSize * ((sizePerTexture * uvIndex.x) + padding);
            float v = 1f / totalSize * ((sizePerTexture * uvIndex.y) + padding);

            uv = new(u, v);

            DataBuffer.Instance.Block2uvs[(blockType, faceIndex)] = uv;

            Debug.Log($"uv not found:{blockType} -> uv={uv}; uvIndex={uvIndex}");
        }
        else
        {
            // Debug.Log($"uv found:{blockType} -> {uv}");
        }

        return uv;
    }

    private Vector2[] GetFaceUVs(Vector2 offset, Direction dir)
    {
        float s = (float)(pixelPerTexture) / totalSize;
        // Debug.Log($"s = {s}");
        return dir switch
        {
            Direction.Up or Direction.Down or Direction.North or Direction.South =>
                new Vector2[] { new(offset.x, offset.y + s), new(offset.x + s, offset.y + s), new(offset.x + s, offset.y), new(offset.x, offset.y) },
            Direction.East =>
                new Vector2[] { new(offset.x + s, offset.y), new(offset.x + s, offset.y + s), new(offset.x, offset.y + s), new(offset.x, offset.y) },
            Direction.West =>
                new Vector2[] { new(offset.x, offset.y + s), new(offset.x, offset.y), new(offset.x + s, offset.y), new(offset.x + s, offset.y + s) },
            _ => new Vector2[4]
        };
    }

    public Mesh CreateMesh()
    {
        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();

        // Debug.Log($"Mesh UV count: {uvs.Count}, First UV: {uvs[0]}, TileSize: {pixelSize}");

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
