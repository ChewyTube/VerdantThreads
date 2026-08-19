using UnityEngine;

// E 键开关的背包窗（IMGUI）：逐行列出全部物品（图标 + 中文名），点击行选中并同步热栏。
// 右键种子袋行打开种子袋内容子面板（显示袋内按基因型分组的豌豆分布）。
// 开关状态归属 Backpack（BackpackOpen），BlockInteraction 据此暂停破坏/放置。
public class BackpackWindow : MonoBehaviour
{
    private Backpack backpack;

    // 拖拽交换状态：左键按下的源槽索引；-1 = 未拖拽
    private int dragFromIndex = -1;

    // 装配注入：由 World 在 Awake 中创建并调用（保证先于首次 OnGUI）
    public void Init(Backpack backpack)
    {
        this.backpack = backpack;
    }

    private void Update()
    {
        if (backpack == null) return;

        // 切换背包开关（状态写在 Backpack 上，与其他组件共享同一来源）
        if (Input.GetKeyDown(Constants.BACKPACK_TOGGLE_KEY))
        {
            backpack.BackpackOpen = !backpack.BackpackOpen;
            if (!backpack.BackpackOpen)
            {
                backpack.IsSeedBagOpen = false; // 关闭背包时同步关闭种子袋内容子面板
                dragFromIndex = -1;             // 关闭时重置拖拽状态
            }
        }

        // ESC 关闭背包（含种子袋内容子面板；种子袋仅在背包开启时可见，一并关闭）
        if (Input.GetKeyDown(KeyCode.Escape) && backpack.BackpackOpen)
        {
            backpack.BackpackOpen = false;
            backpack.IsSeedBagOpen = false;
            dragFromIndex = -1; // 关闭时重置拖拽状态
        }

        // 同步鼠标锁定状态：界面打开（背包窗 / 种子袋面板）时解锁并显示鼠标，否则锁定隐藏。
        // 每帧同步：覆盖 CameraMove 失焦后的遗留状态，也兼容热栏右键直接置 BackpackOpen 的路径
        if (backpack.BackpackOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // 图集惰性获取：WorldManager 可能未就绪 / 材质缺失，返回 null 时跳过图标绘制（不报错）
    private Texture2D GetAtlasTexture()
    {
        Material mat = WorldManager.Instance != null ? WorldManager.Instance.BlockMaterial : null;
        return mat != null ? mat.mainTexture as Texture2D : null;
    }

    private void OnGUI()
    {
        if (backpack == null || !backpack.BackpackOpen) return; // 未注入或未打开时不渲染

        const float titleHeight = 26f;
        const float rowHeight = 36f;
        const float margin = 8f;
        const float width = 240f;

        float height = titleHeight + backpack.Count * rowHeight + margin * 2f;
        Rect winRect = new Rect(
            (Screen.width - width) * 0.5f,
            (Screen.height - height) * 0.5f,
            width, height);

        // 窗口背景与标题
        GUI.Box(winRect, GUIContent.none);
        GUI.Label(new Rect(winRect.x + margin, winRect.y + margin, width - margin * 2f, titleHeight), "背包");

        Texture2D atlas = GetAtlasTexture();

        for (int i = 0; i < backpack.Count; i++)
        {
            ItemInstance item = backpack[i];
            if (item == null) continue;

            Rect rowRect = new Rect(winRect.x + margin, winRect.y + titleHeight + i * rowHeight, width - margin * 2f, rowHeight - 4f);

            // 右键种子袋行 → 打开种子袋内容子面板（在 GUI.Button 前检测，避免右键事件被按钮消费）
            if (item.ItemType == ItemType.SeedBag &&
                Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                rowRect.Contains(Event.current.mousePosition))
            {
                backpack.IsSeedBagOpen = true;
                backpack.OpenSeedBagSlotIndex = i;
                Event.current.Use();
            }
            // 右键豌豆荚行 → 分解为豌豆粒（优先存入种子袋，见 Phase 3）
            else if (item.ItemType == ItemType.PeaPod &&
                Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                rowRect.Contains(Event.current.mousePosition))
            {
                int seedCount = backpack.DecomposePeaPod(i);
                if (seedCount > 0)
                {
                    // LastBaggedSeedCount = 本次分解存入种子袋的粒数；其余落入背包
                    int bagged = backpack.LastBaggedSeedCount;
                    if (bagged > 0)
                        Debug.Log($"分解豌豆荚 -> {seedCount} 粒豌豆粒（种子袋 {bagged} 粒，背包 {seedCount - bagged} 粒）");
                    else
                        Debug.Log($"分解豌豆荚 -> {seedCount} 粒豌豆粒（已存入种子袋）");
                }
                Event.current.Use();
                break; // 槽可能已移除，行号已失效，下一帧重绘
            }

            // 左键拖拽交换：按下记录源槽并选中，松开落在另一行则交换（拖到空白处取消）。
            // 放在右键检测之后、GUI.Button 之前，避免按钮吞掉拖拽事件。
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                rowRect.Contains(Event.current.mousePosition))
            {
                backpack.Select(i);
                dragFromIndex = i;
                Event.current.Use();
            }
            else if (Event.current.type == EventType.MouseUp && Event.current.button == 0 &&
                dragFromIndex >= 0 && rowRect.Contains(Event.current.mousePosition))
            {
                if (dragFromIndex != i) backpack.SwapSlots(dragFromIndex, i);
                dragFromIndex = -1;
                Event.current.Use();
            }

            // 当前选中行高亮；拖拽源行蓝色高亮；整行可点击 → 选中该物品（热栏同步读同一 SelectedIndex，无需额外通知）
            if (i == backpack.SelectedIndex)
                GUI.backgroundColor = new Color(1f, 0.85f, 0.25f, 0.85f);
            else if (i == dragFromIndex)
                GUI.backgroundColor = new Color(0.5f, 0.75f, 1f, 0.8f);
            else
                GUI.backgroundColor = Color.white;
            if (GUI.Button(rowRect, GUIContent.none))
            {
                backpack.Select(i);
            }
            GUI.backgroundColor = Color.white;

            // 物品图标（图集缺失时仅显示文字，不报错）
            if (atlas != null)
            {
                GUI.DrawTextureWithTexCoords(
                    new Rect(rowRect.x + 4f, rowRect.y + 4f, rowRect.height - 8f, rowRect.height - 8f),
                    atlas, CalcIconUVRect(item));
            }

            // 中文显示名 + 堆叠数量（Count > 1 时显示 xN）
            int count = backpack.GetStackCount(i);
            string label = count > 1 ? $"{item.DisplayName} x{count}" : item.DisplayName;
            GUI.Label(
                new Rect(rowRect.x + rowRect.height + 6f, rowRect.y + 6f, rowRect.width - rowRect.height - 12f, 22f),
                label);
        }

        // 拖拽兜底：MouseUp 未落在任何行（空白/窗外）→ 取消拖拽，不交换
        if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && dragFromIndex >= 0)
        {
            dragFromIndex = -1;
            Event.current.Use();
        }

        // 种子袋内容子面板（覆盖在背包窗右侧）：数据源 = 种子袋物品的 SeedBag.Peas（按基因型分组计数）。
        // 注意：种子袋物品本身无 Genome，其 StackSlot 的基因型计数为空，必须从 item.SeedBag.Peas 读取
        if (backpack.IsSeedBagOpen)
        {
            ItemInstance seedBagItem = backpack[backpack.OpenSeedBagSlotIndex];
            if (seedBagItem != null && seedBagItem.SeedBag != null)
            {
                var peas = seedBagItem.SeedBag.Peas;

                const float subWidth = 260f;
                float subHeight = titleHeight + (peas.Count + 1) * rowHeight + margin * 2f; // +1 行给关闭按钮
                Rect subRect = new Rect(winRect.x + winRect.width + margin, winRect.y, subWidth, subHeight);

                // 子面板背景与标题（总粒数 / 容量）
                GUI.Box(subRect, GUIContent.none);
                GUI.Label(
                    new Rect(subRect.x + margin, subRect.y + margin, subWidth - margin * 2f, titleHeight),
                    $"种子袋（{seedBagItem.SeedBag.TotalCount}/{Constants.SEED_BAG_CAPACITY}）");

                // 逐行显示基因型分布：Genome.ToString() 为 14 字符等位串 + x数量 + 取出按钮
                int rowIdx = 0;
                foreach (var kv in peas)
                {
                    Rect peaRow = new Rect(subRect.x + margin, subRect.y + titleHeight + rowIdx * rowHeight, subWidth - margin * 2f, rowHeight - 4f);
                    GUI.Label(new Rect(peaRow.x, peaRow.y + 6f, peaRow.width - 52f, 22f), $"{kv.Key} x{kv.Value}");

                    // 取出按钮：把该基因型全部豌豆从种子袋取出落入背包（可种植的豌豆粒）
                    if (GUI.Button(new Rect(peaRow.x + peaRow.width - 48f, peaRow.y + 4f, 44f, rowHeight - 12f), "取出"))
                    {
                        int taken = seedBagItem.SeedBag.Take(kv.Key, kv.Value);
                        if (taken > 0)
                        {
                            var seedItem = new ItemInstance(ItemType.PeaSeed, "豌豆粒", kv.Key, BlockType.PeaStem);
                            seedItem.PhenotypeTags.Clear();
                            seedItem.PhenotypeTags.AddRange(PeaTraits.GetPhenotypeTags(kv.Key, 0, 1));
                            backpack.AddItem(seedItem, taken);
                            // 注意：种子袋只存 Genome 计数，不含采收基因载荷——取出的种子采收基因回落到默认基线
                            Event.current.Use();
                            break; // 列表已变化，重新绘制
                        }
                    }
                    rowIdx++;
                }

                // 关闭按钮
                Rect closeRect = new Rect(subRect.x + margin, subRect.y + titleHeight + peas.Count * rowHeight + margin, subWidth - margin * 2f, rowHeight - 4f);
                if (GUI.Button(closeRect, "关闭"))
                {
                    backpack.IsSeedBagOpen = false;
                }
            }
        }
    }

    // 物品图标 UV：图集 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），row 从图集底部起算。
    // 豌豆种子特判（PeaStem 无 BlockUVMap 条目，走 Fallback 会显示错误图标）；其余可放置方块取 Up 面 cell；
    // 非方块物品（豆荚/种子袋等）按类型+基因选图集 cell。
    private static Rect CalcIconUVRect(ItemInstance item)
    {
        Vector2Int cell;
        if (item.ItemType == ItemType.PeaSeedBlock)
        {
            // 豌豆种子 → 最小苗图标（不随可放置方块走 BlockUVMap）
            cell = PeaTextures.CellByStage[0];
        }
        else if (item.PlaceableBlockType.HasValue)
        {
            // 可放置方块 → 从 BlockUVMap 取 Up 面 cell
            cell = BlockUVMap.GetUV(item.PlaceableBlockType.Value, Direction.Up);
        }
        else
        {
            // 非方块物品 → 按类型选图集 cell（豌豆荚/豌豆粒按基因选表型图标；青嫩豆荚暂用占位）
            cell = item.ItemType switch
            {
                ItemType.PeaSeed when item.Genome.HasValue => PeaTextures.GetItemSeedCell(item.Genome.Value),
                ItemType.PeaSeed => PeaTextures.CellByStage[0], // 无基因兜底
                ItemType.PeaPod when item.Genome.HasValue => PeaTextures.GetItemPodCell(item.Genome.Value),
                ItemType.PeaPod => new Vector2Int(0, 0),        // 无基因兜底（占位）
                ItemType.SeedBag => PeaTextures.ItemSeedBagCell,
                ItemType.GreenBeanPod => PeaTextures.ItemGreenBeanPodCell,
                _ => new Vector2Int(0, 0),
            };
        }
        // Rect 不支持 / 运算符，逐分量除以 768 得到归一化 UV
        Rect uv = new Rect(cell.x * 24 + 4, cell.y * 24 + 4, 16, 16);
        return new Rect(uv.x / 768f, uv.y / 768f, uv.width / 768f, uv.height / 768f);
    }
}
