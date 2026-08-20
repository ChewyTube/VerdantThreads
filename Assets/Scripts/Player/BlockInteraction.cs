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

        // 鼠标右键：优先分解手中豌豆荚（手持即消费，符合"手持右键分解"设计）→
        // 再试豌豆采收（未手持豌豆荚时）→ 最后走放置
        if (Input.GetMouseButtonDown(1))
        {
            if (!TryDecomposeSelected() && !TryHarvestPea()) TryPlaceBlock();
        }

        // Q 键扔出选中物品（Shift+Q 扔整组）：生成掉落物实体（方案 C 体素物理）
        if (Input.GetKeyDown(KeyCode.Q))
        {
            TryDropSelected();
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

        // 放置成功（目标 chunk 存在/按需创建）才继续；PeaStem 放置时同步创建 tile（基因 + 世代 0）
        if (world.SetBlock(BlockRegistry.GetBlock(current.PlaceableBlockType.Value), placePos))
        {
            // 种植联动：豌豆种子放置 → 创建 tile 记录基因/世代/生长进度（生长 tick 据此推进阶段）
            if (current.ItemType == ItemType.PeaSeedBlock)
            {
                var tile = new PeaTileData(current.Genome ?? Genome.Random(), 0);
                tile.SetHarvestGenome(HarvestGenome.Random()); // 采收基因随机（玩家种植，非生成确定性契约）
                world.SetTile(placePos, tile);
            }
            // 豌豆粒（分解产物）：继承母本基因组 + 载荷采收基因；消耗 1 粒（有限资源）
            else if (current.ItemType == ItemType.PeaSeed)
            {
                var tile = new PeaTileData(current.Genome ?? Genome.Random(), 0);
                tile.SetHarvestGenome(current.GetHarvestGenome()); // 载荷继承（无 → 全隐性基线）
                world.SetTile(placePos, tile);
                world.Backpack.TakeFromSelected(1); // 消耗一粒（种子袋内种子暂不可直接种植）
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

    // 右键分解选中物品：当前选中为豌豆荚（PeaPod）时 → 消耗 1 个，产出 4~8 粒豌豆种子
    // （携带母本基因组 + 采收基因组的 HTT 载荷），优先存入种子袋。
    // 按住 Shift 右键 → 分解选中槽内全部豌豆荚（逐粒循环，槽清空后自动停止）。
    // 返回 true 表示已消费右键（即使分解失败——无选中/非豌豆荚也返回 false 走放置逻辑）。
    private bool TryDecomposeSelected()
    {
        if (world == null || world.Backpack == null)
        {
            Debug.Log("[分解] 跳过：world 或 Backpack 为空");
            return false;
        }

        ItemInstance selected = world.Backpack.CurrentSelected;
        if (selected == null)
        {
            Debug.Log($"[分解] 跳过：当前选中槽为空（SelectedIndex={world.Backpack.SelectedIndex}，占用槽数={world.Backpack.OccupiedCount}）");
            return false;
        }

        if (selected.ItemType != ItemType.PeaPod)
        {
            Debug.Log($"[分解] 跳过：选中物品不是豌豆荚，实际类型={selected.ItemType}，名称={selected.DisplayName}");
            return false;
        }

        // Shift + 右键：分解选中槽内全部豌豆荚；普通右键只分解 1 个
        bool decomposeAll = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        Debug.Log($"[分解] 开始分解豌豆荚（SelectedIndex={world.Backpack.SelectedIndex}，堆叠数={world.Backpack.GetSlotCount(world.Backpack.SelectedIndex)}，模式={(decomposeAll ? "全部" : "单个")}）");

        int totalSeeds = 0;
        int totalBagged = 0;
        int decomposed = 0;
        while (true)
        {
            // 槽被清空后 SelectedIndex 会顶位/下移，逐次校验当前选中仍是豌豆荚
            ItemInstance cur = world.Backpack.CurrentSelected;
            if (cur == null || cur.ItemType != ItemType.PeaPod) break;

            int seedCount = world.Backpack.DecomposePeaPod(world.Backpack.SelectedIndex);
            if (seedCount < 0)
            {
                Debug.Log("[分解] 失败：DecomposePeaPod 返回 -1（槽无效/非豌豆荚/扣除失败）");
                break;
            }
            totalSeeds += seedCount;
            totalBagged += world.Backpack.LastBaggedSeedCount;
            decomposed++;
            if (!decomposeAll) break;
        }

        if (decomposed == 0)
        {
            Debug.Log("[分解] 失败：DecomposePeaPod 返回 -1（槽无效/非豌豆荚/扣除失败）");
            return false;
        }

        // LastBaggedSeedCount = 本次分解存入种子袋的粒数；其余落入背包
        if (decomposed == 1)
        {
            if (totalBagged > 0)
                Debug.Log($"分解豌豆荚 -> {totalSeeds} 粒豌豆粒（种子袋 {totalBagged} 粒，背包 {totalSeeds - totalBagged} 粒）");
            else
                Debug.Log($"分解豌豆荚 -> {totalSeeds} 粒豌豆粒（已存入种子袋）");
        }
        else
        {
            Debug.Log($"Shift 分解豌豆荚 x{decomposed} -> 共 {totalSeeds} 粒豌豆粒（种子袋 {totalBagged} 粒，背包 {totalSeeds - totalBagged} 粒）");
        }
        return true;
    }

    // Q 键扔出：选中槽非空 → 从背包取出（Q 扔 1 个，Shift+Q 扔整组）→ 生成掉落物实体。
    // 位置 = 眼睛 + 前向 1.5 + 上 0.5；初速度 = 前向 3 + 上 2（MC"扔"手感）。
    private void TryDropSelected()
    {
        if (world == null || world.Backpack == null) return;

        int sel = world.Backpack.SelectedIndex;
        if (sel < 0 || sel >= world.Backpack.Count) return;
        ItemInstance current = world.Backpack.CurrentSelected;
        if (current == null) return;

        // Q 扔 1 个；Shift+Q 扔整组
        bool dropAll = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int count = dropAll ? world.Backpack.GetSlotCount(sel) : 1;

        int taken = world.Backpack.TakeFromSelected(count);
        if (taken <= 0) return;

        // 生成掉落物：位置 = 眼睛 + 前向 1.5 + 上 0.5；初速度 = 前向 3 + 上 2
        Vector3 pos = transform.position + transform.forward * 1.5f + Vector3.up * 0.5f;
        Vector3 vel = transform.forward * 3f + Vector3.up * 2f;
        world.DropItem(current, taken, pos, vel);
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
