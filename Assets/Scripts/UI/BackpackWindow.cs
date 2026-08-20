using UnityEngine;

// E 键开关的背包窗（IMGUI）：MC 风格 4 行 × 9 列固定网格（row 0 = 热栏，row 1-3 = 主背包）。
// 左键点击选中、拖拽交换（含空槽）；右键种子袋打开内容子面板、右键豌豆荚分解。
// 悬停非空槽显示物品名（按住 Tab 时豌豆粒追加表型）。
// 开关状态归属 Backpack（BackpackOpen），BlockInteraction 据此暂停破坏/放置。
public class BackpackWindow : MonoBehaviour
{
    private Backpack backpack;
    private World world; // 背包内 Q 扔出掉落物用（懒查找）

    // 拖拽交换状态：左键按下的源槽索引；-1 = 未拖拽
    private int dragFromIndex = -1;

    // 最近一次 OnGUI 计算的槽位矩形（背包内 Q 扔出悬停槽物品用；GUI 坐标，原点左上）
    private Rect[] slotRects;

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

        // 背包窗内 Q 键：扔出悬停槽物品（Shift+Q 扔整组）。BlockInteraction 在背包打开时早退，这里单独处理。
        if (backpack.BackpackOpen && Input.GetKeyDown(KeyCode.Q))
        {
            TryDropHoveredSlot();
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

    // 背包窗内 Q 键扔出：悬停槽非空 → 取出（Q 扔 1 个，Shift+Q 扔整组）→ 生成掉落物实体。
    // 位置/初速度与 BlockInteraction.TryDropSelected 一致（眼睛 + 前向 1.5 + 上 0.5；前向 3 + 上 2）。
    private void TryDropHoveredSlot()
    {
        if (slotRects == null) return;

        // Input.mousePosition 原点在左下，slotRects 是 GUI 坐标（原点左上）→ 翻转 Y
        Vector2 mousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        int hovered = -1;
        for (int i = 0; i < slotRects.Length; i++)
        {
            if (slotRects[i].Contains(mousePos)) { hovered = i; break; }
        }
        if (hovered < 0) return;

        ItemInstance item = backpack[hovered];
        if (item == null) return;

        bool dropAll = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int count = dropAll ? backpack.GetSlotCount(hovered) : 1;
        int taken = backpack.TakeFromSlot(hovered, count);
        if (taken <= 0) return;

        if (world == null) world = FindObjectOfType<World>();
        if (world == null) return;

        Camera cam = Camera.main;
        Vector3 pos = cam != null ? cam.transform.position + cam.transform.forward * 1.5f + Vector3.up * 0.5f
                                  : Vector3.up * 64f;
        Vector3 vel = cam != null ? cam.transform.forward * 3f + Vector3.up * 2f : Vector3.up * 2f;
        world.DropItem(item, taken, pos, vel);
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
        const float slotSize = 44f;   // 槽边长（像素）
        const float spacing = 4f;     // 槽间距（像素）
        const float margin = 8f;      // 窗口内边距
        const float gap = 8f;         // 主背包与热栏之间的间隔（MC 风格）

        float gridWidth = Constants.INVENTORY_COLUMNS * slotSize + (Constants.INVENTORY_COLUMNS - 1) * spacing;
        float windowWidth = gridWidth + margin * 2f;
        float gridHeight = Constants.INVENTORY_ROWS * slotSize + (Constants.INVENTORY_ROWS - 1) * spacing + gap;
        float windowHeight = titleHeight + gridHeight + margin * 2f;

        Rect winRect = new Rect(
            (Screen.width - windowWidth) * 0.5f,
            (Screen.height - windowHeight) * 0.5f,
            windowWidth, windowHeight);

        // 窗口背景与标题
        GUI.Box(winRect, GUIContent.none);
        GUI.Label(new Rect(winRect.x + margin, winRect.y + margin, windowWidth - margin * 2f, titleHeight), "背包");

        Texture2D atlas = GetAtlasTexture();

        // 布局：主背包 3 行（row 1-3）在上，热栏 1 行（row 0）在下
        float mainTop = winRect.y + margin + titleHeight;
        float hotbarTop = mainTop + (Constants.INVENTORY_ROWS - 1) * (slotSize + spacing) + gap;

        // 悬停提示文本（最后统一绘制，覆盖在最上层）
        string hoverText = null;

        for (int row = 0; row < Constants.INVENTORY_ROWS; row++)
        {
            float y = row == 0 ? hotbarTop : mainTop + (row - 1) * (slotSize + spacing);
            for (int col = 0; col < Constants.INVENTORY_COLUMNS; col++)
            {
                int index = Backpack.IndexAt(row, col);
                Rect slotRect = new Rect(winRect.x + margin + col * (slotSize + spacing), y, slotSize, slotSize);
                ItemInstance item = backpack[index];

                // 记录槽位矩形（背包内 Q 扔出悬停槽物品用）
                if (slotRects == null || slotRects.Length != Constants.INVENTORY_SLOT_COUNT)
                    slotRects = new Rect[Constants.INVENTORY_SLOT_COUNT];
                slotRects[index] = slotRect;

                // 右键种子袋行 → 打开种子袋内容子面板（在绘制前检测，避免事件被后续控件消费）
                if (item != null && item.ItemType == ItemType.SeedBag &&
                    Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                    slotRect.Contains(Event.current.mousePosition))
                {
                    backpack.IsSeedBagOpen = true;
                    backpack.OpenSeedBagSlotIndex = index;
                    Event.current.Use();
                }
                // 右键豌豆荚行 → 分解为豌豆粒（优先存入种子袋，见 Phase 3）
                else if (item != null && item.ItemType == ItemType.PeaPod &&
                    Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                    slotRect.Contains(Event.current.mousePosition))
                {
                    int seedCount = backpack.DecomposePeaPod(index);
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
                    break; // 槽内容已变化，本行剩余格下一帧重绘
                }

                // 左键拖拽交换：按下记录源槽并选中，松开落在另一格则交换（拖到空白处取消）
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    slotRect.Contains(Event.current.mousePosition))
                {
                    backpack.Select(index);
                    dragFromIndex = index;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp && Event.current.button == 0 &&
                    dragFromIndex >= 0 && slotRect.Contains(Event.current.mousePosition))
                {
                    if (dragFromIndex != index) backpack.SwapSlots(dragFromIndex, index);
                    dragFromIndex = -1;
                    Event.current.Use();
                }

                // 槽底色：选中暖黄 / 拖拽源蓝 / 空槽浅黑 / 有物品深黑
                if (index == backpack.SelectedIndex)
                    GUI.backgroundColor = new Color(1f, 0.85f, 0.25f, 0.85f);
                else if (index == dragFromIndex)
                    GUI.backgroundColor = new Color(0.5f, 0.75f, 1f, 0.8f);
                else
                    GUI.backgroundColor = item != null ? new Color(0f, 0f, 0f, 0.35f) : new Color(0f, 0f, 0f, 0.18f);
                GUI.Box(slotRect, GUIContent.none);
                GUI.backgroundColor = Color.white;

                // 热栏行（row 0）左上角数字 1-9
                if (row == 0)
                    GUI.Label(new Rect(slotRect.x + 2f, slotRect.y + 1f, 24f, 16f), (col + 1).ToString());

                // 物品图标（图集缺失时仅显示文字，不报错）
                if (item != null && atlas != null)
                {
                    Rect iconRect = new Rect(slotRect.x + 5f, slotRect.y + 5f, slotRect.width - 10f, slotRect.height - 10f);
                    GUI.DrawTextureWithTexCoords(iconRect, atlas, ItemIcon.GetUVRect(item));
                }

                // 堆叠数量（Count > 1 时在槽右下角显示 xN）
                int stackCount = backpack.GetStackCount(index);
                if (stackCount > 1)
                    GUI.Label(new Rect(slotRect.x + slotRect.width - 26f, slotRect.y + slotRect.height - 18f, 24f, 16f), $"x{stackCount}");

                // 悬停提示：非空槽显示物品名；按住 Tab 时豌豆粒追加表型（子叶颜色 + 种子形状）
                if (item != null && slotRect.Contains(Event.current.mousePosition))
                {
                    hoverText = stackCount > 1 ? $"{item.DisplayName} x{stackCount}" : item.DisplayName;
                    if (Input.GetKey(KeyCode.Tab) && item.ItemType == ItemType.PeaSeed && item.Genome.HasValue)
                        hoverText += $"（{string.Join(" ", PeaTraits.GetPhenotypeTags(item.Genome.Value, 1, 0))}）";
                }
            }
        }

        // 拖拽兜底：MouseUp 未落在任何格（空白/窗外）→ 取消拖拽，不交换
        if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && dragFromIndex >= 0)
        {
            dragFromIndex = -1;
            Event.current.Use();
        }

        // 悬停提示绘制（最上层，跟随鼠标）
        if (hoverText != null)
        {
            Vector2 mp = Event.current.mousePosition;
            GUIContent tipContent = new GUIContent(hoverText);
            Vector2 tipSize = GUI.skin.box.CalcSize(tipContent);
            Rect tipRect = new Rect(mp.x + 12f, mp.y + 12f, tipSize.x + 10f, tipSize.y + 6f);
            GUI.Box(tipRect, GUIContent.none);
            GUI.Label(new Rect(tipRect.x + 5f, tipRect.y + 3f, tipRect.width - 10f, tipRect.height - 6f), tipContent);
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
                const float subRowHeight = 36f;
                float subHeight = titleHeight + (peas.Count + 1) * subRowHeight + margin * 2f; // +1 行给关闭按钮
                Rect subRect = new Rect(winRect.x + winRect.width + margin, winRect.y, subWidth, subHeight);

                // 子面板背景与标题（总粒数 / 容量）
                GUI.Box(subRect, GUIContent.none);
                GUI.Label(
                    new Rect(subRect.x + margin, subRect.y + margin, subWidth - margin * 2f, titleHeight),
                    $"种子袋（{seedBagItem.SeedBag.TotalCount}/{Constants.SEED_BAG_CAPACITY}）");

                // 逐行显示基因型分布：默认显示"豌豆粒 x数量"；按住 Tab 时显示表型（子叶颜色 + 种子形状）+ 取出按钮
                int rowIdx = 0;
                foreach (var kv in peas)
                {
                    Rect peaRow = new Rect(subRect.x + margin, subRect.y + titleHeight + rowIdx * subRowHeight, subWidth - margin * 2f, subRowHeight - 4f);
                    // 按住 Tab 显示表型（位点 1 子叶颜色 黄/绿 + 位点 0 种子形状 圆/皱）；否则显示"豌豆粒"
                    string rowName = Input.GetKey(KeyCode.Tab)
                        ? string.Join(" ", PeaTraits.GetPhenotypeTags(kv.Key, 1, 0))
                        : "豌豆粒";
                    GUI.Label(new Rect(peaRow.x, peaRow.y + 6f, peaRow.width - 52f, 22f), $"{rowName} x{kv.Value}");

                    // 取出按钮：把该基因型全部豌豆从种子袋取出落入背包（可种植的豌豆粒）
                    if (GUI.Button(new Rect(peaRow.x + peaRow.width - 48f, peaRow.y + 4f, 44f, subRowHeight - 12f), "取出"))
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
                Rect closeRect = new Rect(subRect.x + margin, subRect.y + titleHeight + peas.Count * subRowHeight + margin, subWidth - margin * 2f, subRowHeight - 4f);
                if (GUI.Button(closeRect, "关闭"))
                {
                    backpack.IsSeedBagOpen = false;
                }
            }
        }
    }
}