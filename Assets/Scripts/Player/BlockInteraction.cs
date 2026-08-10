using UnityEngine;

// 玩家方块交互：鼠标左键破坏、右键放置、数字键 1-9 切换选中方块
public class BlockInteraction : MonoBehaviour
{
    [SerializeField] private World world;              // 场景显式引用（可在 Inspector 拖 World；未拖时 Awake 自动查找）
    [SerializeField] private float reachDistance = 8f;      // 射线距离
    [SerializeField] private Block[] placeableBlocks;       // 可放置方块列表
    [SerializeField] private int defaultSelectedIndex = 2;  // 默认选中索引（对应 Stone）

    private int selectedIndex; // 当前选中方块索引

    private void Awake()
    {
        // 场景引用兜底：未在 Inspector 拖 World 时自动查找（去单例化 #16）
        if (world == null)
        {
            world = FindObjectOfType<World>();
        }

        // placeableBlocks 为空时填充默认放置列表
        if (placeableBlocks == null || placeableBlocks.Length == 0)
        {
            placeableBlocks = new Block[]
            {
                BlockRegistry.Grass,
                BlockRegistry.Dirt,
                BlockRegistry.Stone,
                BlockRegistry.Log,
                BlockRegistry.Leaves,
                BlockRegistry.Bedrock,
            };
        }

        // 修正默认选中索引到有效范围
        selectedIndex = Mathf.Clamp(defaultSelectedIndex, 0, placeableBlocks.Length - 1);
    }

    private void Update()
    {
        // 世界引用未就绪时不处理
        if (world == null) return;

        // 数字键 1-9 切换选中方块（最多 9 个，超出列表长度则只支持到列表长度）
        int maxSlot = Mathf.Min(9, placeableBlocks.Length);
        for (int i = 1; i <= maxSlot; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + i))
            {
                selectedIndex = i - 1;
                break;
            }
        }

        // 鼠标左键：破坏射线命中的方块
        if (Input.GetMouseButtonDown(0))
        {
            TryBreakBlock();
        }

        // 鼠标右键：在命中面外侧放置选中方块
        if (Input.GetMouseButtonDown(1))
        {
            TryPlaceBlock();
        }
    }

    // 破坏逻辑：命中且非世界最底层 Bedrock 则置为 Air，并重建相关 chunk mesh
    private void TryBreakBlock()
    {
        if (!RaycastVoxel(out BlockPosInWorld hit, out _)) return;

        // 射线只命中非 Air 方块；世界最底层（Y=0）Bedrock 不可破坏，防止挖穿世界底
        if (hit.Y == 0 && GetBlockTypeAt(hit) == BlockType.Bedrock) return;

        world.SetBlock(BlockRegistry.Air, hit);
        RequestMeshRebuildAround(hit);
    }

    // 放置逻辑：命中面外侧放置选中方块，并重建相关 chunk mesh
    private void TryPlaceBlock()
    {
        if (!RaycastVoxel(out BlockPosInWorld hit, out Vector3Int faceNormal)) return;

        // 放置位置 = 命中方块 + 进入面法线（相邻格）
        BlockPosInWorld placePos = new BlockPosInWorld(hit.X + faceNormal.x, hit.Y + faceNormal.y, hit.Z + faceNormal.z);

        // 防止把自己封进方块：放置格与相机所在格相同则忽略
        Vector3 camPos = transform.position;
        if (placePos.X == Mathf.FloorToInt(camPos.x) &&
            placePos.Y == Mathf.FloorToInt(camPos.y) &&
            placePos.Z == Mathf.FloorToInt(camPos.z))
        {
            return;
        }

        // 世界高度范围外（0 ≤ Y < 16 chunk × 16）忽略
        if (placePos.Y < 0 || placePos.Y >= Constants.CHUNK_SIZE * 16) return;

        // 目标格已是固体 → 忽略
        if (IsSolid(placePos.X, placePos.Y, placePos.Z)) return;

        world.SetBlock(placeableBlocks[selectedIndex], placePos);
        RequestMeshRebuildAround(placePos);
    }

    // 体素射线检测（Amanatides-Woo DDA 网格步进）：返回命中块世界坐标与进入面法线
    private bool RaycastVoxel(out BlockPosInWorld hit, out Vector3Int faceNormal)
    {
        Vector3 origin = transform.position;
        Vector3 dir = transform.forward;

        // 起始格（世界坐标向下取整）
        int bx = Mathf.FloorToInt(origin.x);
        int by = Mathf.FloorToInt(origin.y);
        int bz = Mathf.FloorToInt(origin.z);

        // 每轴步进方向：dir >= 0 时沿正方向推进
        int stepX = dir.x >= 0 ? 1 : -1;
        int stepY = dir.y >= 0 ? 1 : -1;
        int stepZ = dir.z >= 0 ? 1 : -1;

        // 沿某轴跨越一格所需的 t 增量（垂直轴为无穷，永不推进该轴）
        float tDeltaX = dir.x != 0 ? Mathf.Abs(1f / dir.x) : float.PositiveInfinity;
        float tDeltaY = dir.y != 0 ? Mathf.Abs(1f / dir.y) : float.PositiveInfinity;
        float tDeltaZ = dir.z != 0 ? Mathf.Abs(1f / dir.z) : float.PositiveInfinity;

        // 到达首个格边界的 t（dir < 0 时分子取 (格坐标 - 原点)，与负分母同号，结果恒为正）
        float tMaxX = dir.x != 0 ? (dir.x > 0 ? (bx + 1 - origin.x) : (bx - origin.x)) / dir.x : float.PositiveInfinity;
        float tMaxY = dir.y != 0 ? (dir.y > 0 ? (by + 1 - origin.y) : (by - origin.y)) / dir.y : float.PositiveInfinity;
        float tMaxZ = dir.z != 0 ? (dir.z > 0 ? (bz + 1 - origin.z) : (bz - origin.z)) / dir.z : float.PositiveInfinity;

        // 进入面法线：起始格即固体时保持 (0,0,0)
        Vector3Int normal = Vector3Int.zero;

        while (true)
        {
            // 当前格为固体 → 命中
            if (IsSolid(bx, by, bz))
            {
                hit = new BlockPosInWorld(bx, by, bz);
                faceNormal = normal;
                return true;
            }

            // 推进 t 最小的轴，进入面法线 = 刚推进轴的反方向
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                bx += stepX;
                normal = new Vector3Int(-stepX, 0, 0);
                tMaxX += tDeltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                by += stepY;
                normal = new Vector3Int(0, -stepY, 0);
                tMaxY += tDeltaY;
            }
            else
            {
                bz += stepZ;
                normal = new Vector3Int(0, 0, -stepZ);
                tMaxZ += tDeltaZ;
            }

            // 已越过射线距离仍未命中 → 未命中
            if (Mathf.Min(tMaxX, tMaxY, tMaxZ) > reachDistance)
            {
                hit = default;
                faceNormal = default;
                return false;
            }
        }
    }

    // 世界坐标 (x,y,z) 处是否为固体（未加载 chunk 或越界视为非固体）
    private bool IsSolid(int x, int y, int z)
    {
        VCPosInWorld vcPos = new VCPosInWorld(x >> Constants.CHUNK_SIZE_LOG2, y >> Constants.CHUNK_SIZE_LOG2, z >> Constants.CHUNK_SIZE_LOG2);
        Block[,,] blocks = world.GetChunkBlocks(vcPos);
        if (blocks == null) return false;

        int mask = Constants.CHUNK_SIZE - 1;
        return blocks[x & mask, y & mask, z & mask].GetBlockType() != BlockType.Air;
    }

    // 获取世界坐标处的方块类型（未加载 chunk 返回 Air）
    private BlockType GetBlockTypeAt(BlockPosInWorld pos)
    {
        Block[,,] blocks = world.GetChunkBlocks(pos.GetCorrespondingVCPos());
        if (blocks == null) return BlockType.Air;

        int mask = Constants.CHUNK_SIZE - 1;
        return blocks[pos.X & mask, pos.Y & mask, pos.Z & mask].GetBlockType();
    }

    // 重建目标 chunk 及其 6 个相邻 chunk 的 mesh（内部去重，未加载 chunk 安全跳过）
    private void RequestMeshRebuildAround(BlockPosInWorld pos)
    {
        VCPosInWorld vc = pos.GetCorrespondingVCPos();
        world.RequestMeshRebuild(vc);
        world.RequestMeshRebuild(new VCPosInWorld(vc.X + 1, vc.Y, vc.Z));
        world.RequestMeshRebuild(new VCPosInWorld(vc.X - 1, vc.Y, vc.Z));
        world.RequestMeshRebuild(new VCPosInWorld(vc.X, vc.Y + 1, vc.Z));
        world.RequestMeshRebuild(new VCPosInWorld(vc.X, vc.Y - 1, vc.Z));
        world.RequestMeshRebuild(new VCPosInWorld(vc.X, vc.Y, vc.Z + 1));
        world.RequestMeshRebuild(new VCPosInWorld(vc.X, vc.Y, vc.Z - 1));
    }

    // OnGUI：屏幕中心准星（2x2 像素）与左下角当前选中方块名
    private void OnGUI()
    {
        // 屏幕中心准星
        float crossX = Screen.width * 0.5f - 1f;
        float crossY = Screen.height * 0.5f - 1f;
        GUI.Box(new Rect(crossX, crossY, 2f, 2f), GUIContent.none);

        // 左下角显示当前选中方块名
        GUI.Label(new Rect(8f, Screen.height - 24f, 200f, 20f),
            $"当前方块：{placeableBlocks[selectedIndex].GetBlockType()}");
    }
}
