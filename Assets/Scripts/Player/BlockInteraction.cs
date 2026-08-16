using UnityEngine;

// 玩家方块交互：鼠标左键破坏、右键放置、数字键 1-9 切换选中物品（选中状态在 Backpack）
public class BlockInteraction : MonoBehaviour
{
    [SerializeField] private World world;              // 场景显式引用（可在 Inspector 拖 World；未拖时 Awake 自动查找）
    [SerializeField] private float reachDistance = 8f;      // 射线距离

    private void Awake()
    {
        // 场景引用兜底：未在 Inspector 拖 World 时自动查找（去单例化 #16）
        if (world == null)
        {
            world = FindObjectOfType<World>();
        }
    }

    private void Update()
    {
        // 世界/背包未就绪时不处理
        if (world == null || world.Backpack == null) return;

        // 背包窗打开时暂停放置/破坏（E 键开关由 BackpackWindow 处理）
        if (world.Backpack.BackpackOpen) return;

        // 数字键 1-9 切换选中物品（最多热栏槽位个数；物品数不足时只支持到物品数）
        int maxSlot = Mathf.Min(Constants.HOTBAR_SLOT_COUNT, world.Backpack.Count);
        for (int i = 1; i <= maxSlot; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + i))
            {
                world.Backpack.Select(i - 1);
                break;
            }
        }

        // 鼠标左键：破坏射线命中的方块
        if (Input.GetMouseButtonDown(0))
        {
            TryBreakBlock();
        }

        // 鼠标右键：优先拦截豌豆采收；未命中豌豆再走放置
        if (Input.GetMouseButtonDown(1))
        {
            if (!TryHarvestPea()) TryPlaceBlock();
        }
    }

    // 破坏逻辑：命中且非世界最底层 Bedrock 则置为 Air，并重建相关 chunk mesh。
    // 豌豆两格高植株的顶/底破坏联动已迁入 BlockUpdateCenter（DispatchBlockUpdate）——本方法只管写入 + 重建
    private void TryBreakBlock()
    {
        if (!RaycastVoxel(out BlockPosInWorld hit, out _)) return;

        // 破坏前记录方块信息：Bedrock 守卫与 tile 移除需要类型/块值
        Block hitBlock = GetBlockAt(hit);
        BlockType hitType = hitBlock.GetBlockType();

        // 射线只命中非 Air 方块；世界最底层（Y=0）Bedrock 不可破坏，防止挖穿世界底
        if (hit.Y == 0 && hitType == BlockType.Bedrock) return;

        // 置 Air（触发更新中心：破坏 PeaStem 阶段≥2 → 上方 PeaPlantTop 清除；破坏 PeaPlantTop → 下方退回阶段 0）
        world.SetBlock(BlockRegistry.Air, hit);
        // 豌豆底部格持有 tile，破坏即移除（tile 与方块生命周期一致；顶部格无 tile）
        if (hitType == BlockType.PeaStem)
        {
            world.RemoveTile(hit);
        }
        RequestMeshRebuildAround(hit);
    }

    // 放置逻辑：命中面外侧放置选中物品对应方块，并重建相关 chunk mesh
    private void TryPlaceBlock()
    {
        if (!RaycastVoxel(out BlockPosInWorld hit, out Vector3Int faceNormal)) return;

        // 无选中物品则不放置
        ItemInstance current = world.Backpack.CurrentSelected;
        if (current == null) return;

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

        // 非可放置物品（豆荚/种子袋等）→ 不放置（未来可做其他右键交互）
        if (current.PlaceableBlockType == null) return;

        // 放置成功（目标 chunk 存在/按需创建）才继续；PeaStem 放置时同步创建 tile（随机基因 + 世代 0）
        if (world.SetBlock(BlockRegistry.GetBlock(current.PlaceableBlockType.Value), placePos))
        {
            // 种植联动：豌豆种子放置 → 创建 tile 记录基因/世代/生长进度（生长 tick 据此推进阶段）
            if (current.ItemType == ItemType.PeaSeedBlock)
            {
                var tile = new PeaTileData(current.Genome ?? Genome.Random(), 0);
                tile.SetHarvestGenome(HarvestGenome.Random()); // 采收基因随机（玩家种植，非生成确定性契约）
                world.SetTile(placePos, tile);
            }
        }
        RequestMeshRebuildAround(placePos);
    }

    // 右键采收：命中豌豆（底部/中部/顶部格）→ 按阶段产出豆荚入背包并扣减采摘次数。
    // 返回 true 表示本次右键已被采收消费（不再走放置逻辑）。
    // 流程：向下找底部格 → 阶段 <3 提示未成熟 → 阶段 3/4 按公式产出（青嫩豆荚无基因 /
    // 豌豆荚携带母本基因组 + 采收基因 HTT 载荷）→ 次数 -1（未初始化先按公式初始化）→
    // 归 0 整株枯萎（WitherPeaPlant），否则回退阶段 2（RevertToStage2）+ mesh 重建
    private bool TryHarvestPea()
    {
        if (!RaycastVoxel(out BlockPosInWorld hit, out _)) return false;

        Block hitBlock = GetBlockAt(hit);
        BlockType hitType = hitBlock.GetBlockType();
        if (hitType != BlockType.PeaStem && hitType != BlockType.PeaPlantMiddle && hitType != BlockType.PeaPlantTop)
            return false;

        // 向下找底部格：穿过 PeaPlantTop/PeaPlantMiddle 直到 PeaStem
        BlockPosInWorld bottomPos = hit;
        while (true)
        {
            Block below = GetBlockAt(new BlockPosInWorld(bottomPos.X, bottomPos.Y - 1, bottomPos.Z));
            BlockType belowType = below.GetBlockType();
            if (belowType != BlockType.PeaStem && belowType != BlockType.PeaPlantMiddle) break;
            bottomPos = new BlockPosInWorld(bottomPos.X, bottomPos.Y - 1, bottomPos.Z);
        }
        Block bottomBlock = GetBlockAt(bottomPos);
        if (bottomBlock.GetBlockType() != BlockType.PeaStem) return true; // 结构异常（防御），已消费右键

        int stage = (int)(bottomBlock.GetBlockState() & BlockBits.StageMask);
        if (stage < 3)
        {
            Debug.Log("豌豆尚未成熟，无法采收"); // 未成熟提示（项目无 UI 消息系统，走日志）
            return true;
        }

        // tile 必须存在（阶段≥3 植株必有；缺失防御：不采收，避免空引用）
        PeaTileData tile = world.GetTile(bottomPos);
        if (tile == null) return true;

        Genome genome = tile.Genome;
        HarvestGenome harvestGenome = tile.GetHarvestGenome(); // 无载荷 → 默认（全隐性，k=0 基线）

        // 产出：阶段 4 豌豆荚（携带母本基因组 + 采收基因 HTT 载荷）；阶段 3 青嫩豆荚
        // （无基因，表型标签取花色+花位置位点 {2,5}，见 HARVEST_SYSTEM.md §2.2）
        int yield = PeaHarvestCalculator.GetYield(harvestGenome, stage);
        ItemInstance item;
        if (stage >= 4)
        {
            item = new ItemInstance(ItemType.PeaPod, "豌豆荚", genome);
            item.PhenotypeTags.Clear();
            item.PhenotypeTags.AddRange(PeaTraits.GetPhenotypeTags(genome, 3, 4));
            item.Payload = new HTTCompound();
            item.Payload.SetInt("harvestGenome", (int)harvestGenome.Value);
        }
        else
        {
            item = new ItemInstance(ItemType.GreenBeanPod, "青嫩豆荚", PeaTraits.GetPhenotypeTags(genome, 2, 5));
        }
        world.Backpack.AddItem(item, yield);

        // 扣减采摘次数：未初始化（0）→ 按公式初始化上限；归 0 → 整株枯萎，否则回退阶段 2
        int harvests = bottomBlock.GetHarvests();
        if (harvests <= 0) harvests = PeaHarvestCalculator.GetHarvestLimit(harvestGenome);
        harvests--;
        if (harvests <= 0)
        {
            world.BlockUpdateCenter.WitherPeaPlant(bottomPos);
        }
        else
        {
            world.SetBlock(bottomBlock.WithHarvests(harvests), bottomPos);
            world.BlockUpdateCenter.RevertToStage2(bottomPos);
        }
        RequestMeshRebuildAround(bottomPos);
        return true;
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

    // 获取世界坐标处的方块完整值（含阶段状态位；未加载 chunk 返回 Air）
    private Block GetBlockAt(BlockPosInWorld pos)
    {
        Block[,,] blocks = world.GetChunkBlocks(pos.GetCorrespondingVCPos());
        if (blocks == null) return BlockRegistry.Air;

        int mask = Constants.CHUNK_SIZE - 1;
        return blocks[pos.X & mask, pos.Y & mask, pos.Z & mask];
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

    // OnGUI：屏幕中心准星（2x2 像素）与左下角当前选中物品名
    private void OnGUI()
    {
        // 屏幕中心准星
        float crossX = Screen.width * 0.5f - 1f;
        float crossY = Screen.height * 0.5f - 1f;
        GUI.Box(new Rect(crossX, crossY, 2f, 2f), GUIContent.none);

        // 左下角显示当前选中物品名（读 Backpack 当前选中，与热栏/背包窗同一来源）
        ItemInstance current = world != null && world.Backpack != null ? world.Backpack.CurrentSelected : null;
        GUI.Label(new Rect(8f, Screen.height - 24f, 200f, 20f),
            $"当前物品：{(current != null ? current.DisplayName : "无")}");
    }
}
