using UnityEngine;

// 掉落物实体：物品图标 billboard + 代码物理（复用 VoxelCollision，方案 C）。
// AABB 0.25³；落地小幅反弹（×0.3）+ 水平摩擦衰减；速度趋零后停止模拟（只渲染 + 拾取检测）。
// 生命周期（5 分钟消失 / 64 上限 / 拾取）由 DroppedItemManager 统一管理。
public class DroppedItem : MonoBehaviour
{
    public ItemInstance Item; // 物品模板（含基因等数据）
    public int Count;         // 数量（Q 扔 1，Shift+Q 扔整组）

    private Vector3 velocity; // 速度（blocks/s）
    private float spawnTime;  // 出生时间（Time.time，5 分钟消失）
    private bool settled;     // 速度趋零后停止物理模拟

    private System.Func<Vector3Int, bool> isSolid; // 体素碰撞查询（World 注入）
    private Transform quad;   // billboard quad 子物体

    private static readonly Vector3 HalfExtents = Vector3.one * Constants.DROPPED_ITEM_HALF_SIZE;

    // 初始化（由 DroppedItemManager.Spawn 调用）
    public void Init(ItemInstance item, int count, Vector3 position, Vector3 velocity, System.Func<Vector3Int, bool> isSolid)
    {
        Item = item;
        Count = count;
        transform.position = position;
        this.velocity = velocity;
        this.isSolid = isSolid;
        spawnTime = Time.time;
        settled = false;
        BuildQuad();
    }

    // 出生时长（秒），供管理器判断 5 分钟消失
    public float Age => Time.time - spawnTime;

    // 每帧物理（由 DroppedItemManager.Tick 驱动）
    public void TickPhysics()
    {
        if (isSolid == null || settled) return;

        // 重力
        velocity.y -= Constants.DROPPED_ITEM_GRAVITY * Time.deltaTime;

        // 空气阻力（水平自然减速）
        velocity.x *= 0.99f;
        velocity.z *= 0.99f;

        Vector3 center = transform.position;
        bool wasFalling = velocity.y < 0f;

        // X 轴
        if (VoxelCollision.MoveAxis(center, HalfExtents, 0, velocity.x * Time.deltaTime, out float mx, isSolid)) velocity.x = 0f;
        center.x += mx;

        // Y 轴：落地小幅反弹（×0.3，仅真实落地）+ 水平摩擦；撞顶/贴地直接停
        if (VoxelCollision.MoveAxis(center, HalfExtents, 1, velocity.y * Time.deltaTime, out float my, isSolid))
        {
            if (wasFalling && velocity.y < -0.5f) velocity.y = -velocity.y * 0.3f; // 落地反弹（下落速度足够大才弹）
            else velocity.y = 0f;                                                  // 撞顶停 / 贴地停
            velocity.x *= 0.6f;
            velocity.z *= 0.6f;
        }
        center.y += my;

        // Z 轴
        if (VoxelCollision.MoveAxis(center, HalfExtents, 2, velocity.z * Time.deltaTime, out float mz, isSolid)) velocity.z = 0f;
        center.z += mz;

        transform.position = center;

        // 速度趋零 → 静止（只渲染 + 拾取检测）
        if (velocity.sqrMagnitude < 0.01f) settled = true;
    }

    // billboard：quad 每帧面向相机（由 DroppedItemManager.Tick 驱动）
    public void TickBillboard()
    {
        if (quad == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 toCam = cam.transform.position - quad.position;
        if (toCam.sqrMagnitude < 0.0001f) return; // 相机与 quad 重合时跳过（LookRotation 零向量会报错）
        quad.rotation = Quaternion.LookRotation(toCam);
    }

    // 构建 billboard quad（材质 = 图集材质；UV = 物品图标 cell）
    private void BuildQuad()
    {
        Material mat = WorldManager.Instance != null ? WorldManager.Instance.BlockMaterial : null;
        if (mat == null) return; // 材质未就绪：仅保留实体（图标缺失可接受）

        GameObject quadGO = new GameObject("ItemQuad");
        quadGO.transform.SetParent(transform, false);
        quad = quadGO.transform;

        MeshFilter mf = quadGO.AddComponent<MeshFilter>();
        MeshRenderer mr = quadGO.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        // 单位 quad 网格（XY 平面，法线 +Z），UV 直接设为物品图标 cell（图集 768 归一化）
        Rect uv = ItemIcon.GetUVRect(Item);
        Mesh mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
        };
        mesh.uv = new[]
        {
            new Vector2(uv.x, uv.y),
            new Vector2(uv.x + uv.width, uv.y),
            new Vector2(uv.x + uv.width, uv.y + uv.height),
            new Vector2(uv.x, uv.y + uv.height),
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        mf.sharedMesh = mesh;

        // 视觉尺寸略大于 AABB（0.25³ → 0.4 见方）
        quad.localScale = Vector3.one * 0.4f;
    }
}