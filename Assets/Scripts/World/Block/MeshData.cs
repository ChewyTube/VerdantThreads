using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MeshData
{
    private List<Vector3> vertices;
    private List<int> triangles;
    private List<Vector2> uvs;
    private List<Vector3> normals;

    public long Seq;     // 构建代次（同一 chunk 实例内单调递增，用于丢弃乱序上传的旧 mesh）
    public long ChunkId; // 所属 chunk 实例 ID（用于丢弃已卸载/已重建 chunk 的过期上传）


    // 图集配置
    private int atlasSize = 512 / 16; 
    private int padding = 4;
    private int pixelPerTexture = 16;
    private int sizePerTexture;
    private int totalSize;

    public MeshData() : this(256)
    {
    }

    public MeshData(int initialCapacity)
    {
        sizePerTexture = pixelPerTexture + 2 * padding;
        totalSize = atlasSize * sizePerTexture;

        vertices = new List<Vector3>(initialCapacity);
        triangles = new List<int>(initialCapacity * 3 / 2);
        uvs = new List<Vector2>(initialCapacity);
        normals = new List<Vector3>(initialCapacity);
    }

    // 按 VoxelChunk.Direction 语义映射（North=+Z、South=-Z），与 FaceIndex 数值标签相反
    private static readonly Vector3[] FaceNormals =
    {
        /* East=0  */ Vector3.right,   // +X
        /* West=1  */ Vector3.left,    // -X
        /* Up=2    */ Vector3.up,      // +Y
        /* Down=3  */ Vector3.down,    // -Y
        /* South=4 */ Vector3.back,    // -Z
        /* North=5 */ Vector3.forward, // +Z
    };

    public void Clear()
    {
        vertices.Clear(); triangles.Clear(); uvs.Clear(); normals.Clear();
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

        // 4. 逐面写入法线（体素面为轴对齐平面，法线即面朝向，无需 RecalculateNormals）
        Vector3 normal = FaceNormals[(int)dir];
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
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
        Vector2Int uvIndex = BlockUVMap.GetUV(blockType, faceIndex);

        float u = 1f / totalSize * ((sizePerTexture * uvIndex.x) + padding);
        float v = 1f / totalSize * ((sizePerTexture * uvIndex.y) + padding);

        return new(u, v);
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

    // 原地写入既有 Mesh 实例，避免每次 new Mesh + ToArray 分配
    public void FillMesh(Mesh mesh)
    {
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uvs);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();
    }
}
