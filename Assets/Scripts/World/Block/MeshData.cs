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


    // UV 数学假设虚拟网格 32 cells × 24px = 768px，实际图集为 512×512 Atlas.png；勿改这些常量，否则贴图错位
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

    // Direction 是唯一方向类型，其数值即 UV 槽位索引（BlockUVSet 槽位与 Direction 语义一致：South=-Z、North=+Z）
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
        Vector2 uvOffset = GetUVOffset(blockType, dir);
        Vector2[] faceUVs = GetFaceUVs(uvOffset, dir);
        uvs.AddRange(faceUVs);

        // 4. 逐面写入法线（体素面为轴对齐平面，法线即面朝向，无需 RecalculateNormals）
        Vector3 normal = FaceNormals[(int)dir];
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
    }

    // 豌豆十字面片：两个交叉四边形（XZ 对角），固定满格高度；双面绘制，法线朝上。
    // cell 由调用方指定（阶段 0/1 单格贴图 / 两格高植株底部与顶部贴图）。
    public void AddPeaQuadCell(int x, int y, int z, Vector2Int cell)
    {
        float h = 1.0f;
        float u0 = 1f / totalSize * (sizePerTexture * cell.x + padding);
        float v0 = 1f / totalSize * (sizePerTexture * cell.y + padding);
        float s = 1f / totalSize * pixelPerTexture;

        Vector3[] a = { new(x, y, z), new(x + 1, y, z + 1), new(x + 1, y + h, z + 1), new(x, y + h, z) };
        Vector3[] b = { new(x + 1, y, z), new(x, y, z + 1), new(x, y + h, z + 1), new(x + 1, y + h, z) };
        Vector2[] uv = { new(u0, v0), new(u0 + s, v0), new(u0 + s, v0 + s), new(u0, v0 + s) };

        AddQuad(vertices, triangles, uvs, normals, a, uv);
        AddQuad(vertices, triangles, uvs, normals, b, uv);
    }

    // 豌豆十字面片（按生长阶段选贴图）：阶段 0/1 单格用 CellByStage；
    // 阶段 2/3 为两格高植株——本方法画底部格用 PlantBottomCell（顶部格由 PeaPlantTop 方块单独画 PlantTopCell）
    public void AddPeaQuad(int x, int y, int z, int stage)
    {
        Vector2Int cell = stage >= 2
            ? PeaTextures.PlantBottomCell
            : PeaTextures.CellByStage[Mathf.Clamp(stage, 0, PeaTextures.CellByStage.Length - 1)];
        AddPeaQuadCell(x, y, z, cell);
    }

    // 把 4 顶点 + UV 写成 2 三角形；双面共用顶点时法线二选一背面光照会错，
    // 因此写 8 顶点双份：正面 4 个（法线朝上）+ 背面 4 个（三角反序，法线同样朝上）。
    // 注意：背面绝不能用朝下法线——十字面片两片交叉时相机必站在其中一片的背面侧，
    // 若背面法线朝下会导致「一片亮一片暗」的不对称光照。统一上法线即可光照均匀。
    private static void AddQuad(List<Vector3> vs, List<int> ts, List<Vector2> uvs, List<Vector3> ns,
        Vector3[] quad, Vector2[] uv)
    {
        int start = vs.Count;

        // 正面 4 顶点（法线朝上）
        vs.AddRange(quad);
        uvs.AddRange(uv);
        ts.Add(start + 0); ts.Add(start + 1); ts.Add(start + 2);
        ts.Add(start + 0); ts.Add(start + 2); ts.Add(start + 3);
        Vector3 n = new(0, 1, 0);
        ns.Add(n); ns.Add(n); ns.Add(n); ns.Add(n);

        // 背面 4 顶点（三角反序，法线同样朝上，保证两片交叉面片光照一致）
        int b0 = start + 4;
        vs.AddRange(quad);
        uvs.AddRange(uv);
        ts.Add(b0 + 0); ts.Add(b0 + 2); ts.Add(b0 + 1);
        ts.Add(b0 + 0); ts.Add(b0 + 3); ts.Add(b0 + 2);
        ns.Add(n); ns.Add(n); ns.Add(n); ns.Add(n);
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

    private Vector2 GetUVOffset(BlockType blockType, Direction face = Direction.Up)
    {
        Vector2Int uvIndex = BlockUVMap.GetUV(blockType, face);

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
