using UnityEngine;

// 常驻 IMGUI 热栏：屏幕底部居中 9 槽，图集图标 + 槽号 1-9 + 选中高亮。
// 选择状态读写 Backpack（唯一权威），本组件只做展示。
public class HotbarWindow : MonoBehaviour
{
    private Backpack backpack;

    // 装配注入：由 World 在 Awake 中创建并调用（保证先于首次 OnGUI）
    public void Init(Backpack backpack)
    {
        this.backpack = backpack;
    }

    // 图集惰性获取：WorldManager 单例可能未就绪 / 材质缺失，返回 null 时跳过图标绘制（不报错）
    private Texture2D GetAtlasTexture()
    {
        Material mat = WorldManager.Instance != null ? WorldManager.Instance.BlockMaterial : null;
        return mat != null ? mat.mainTexture as Texture2D : null;
    }

    private void OnGUI()
    {
        if (backpack == null) return; // 未注入不渲染

        const int slotSize = 48; // 槽边长（像素）
        const int spacing = 4;   // 槽间距（像素）

        float totalWidth = Constants.HOTBAR_SLOT_COUNT * slotSize + (Constants.HOTBAR_SLOT_COUNT - 1) * spacing;
        float startX = (Screen.width - totalWidth) * 0.5f;
        float y = Screen.height - slotSize - 12f; // 底部留白

        Texture2D atlas = GetAtlasTexture();

        for (int i = 0; i < Constants.HOTBAR_SLOT_COUNT; i++)
        {
            Rect slotRect = new Rect(startX + i * (slotSize + spacing), y, slotSize, slotSize);

            // 选中槽高亮：用背景色区分（暖黄 = 选中，半透明黑 = 空槽/未选中）
            GUI.backgroundColor = i == backpack.SelectedIndex
                ? new Color(1f, 0.85f, 0.25f, 0.9f)
                : new Color(0f, 0f, 0f, 0.35f);
            GUI.Box(slotRect, GUIContent.none);
            GUI.backgroundColor = Color.white;

            // 槽左上角数字 1-9
            GUI.Label(new Rect(slotRect.x + 2f, slotRect.y + 1f, 24f, 16f), (i + 1).ToString());

            // 物品图标（物品数不足时空槽只画边框；物品数超过槽位数时只显示前 N 个）
            if (atlas != null)
            {
                ItemInstance item = backpack[i];
                if (item != null)
                {
                    Rect iconRect = new Rect(slotRect.x + 6f, slotRect.y + 6f, slotRect.width - 12f, slotRect.height - 12f);
                    GUI.DrawTextureWithTexCoords(iconRect, atlas, CalcIconUVRect(item));
                }
            }

            // 堆叠数量：Count > 1 时在槽右下角显示 xN
            int stackCount = backpack.GetStackCount(i);
            if (stackCount > 1)
                GUI.Label(new Rect(slotRect.x + slotRect.width - 26f, slotRect.y + slotRect.height - 18f, 24f, 16f), $"x{stackCount}");

            // 槽位右键（解锁状态）：鼠标位置命中种子袋槽 → 打开背包窗并弹出种子袋内容子面板
            if (i < backpack.Count)
            {
                ItemInstance item = backpack[i];
                if (item != null && item.ItemType == ItemType.SeedBag &&
                    Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                    slotRect.Contains(Event.current.mousePosition))
                {
                    backpack.BackpackOpen = true;
                    backpack.IsSeedBagOpen = true;
                    backpack.OpenSeedBagSlotIndex = i;
                    Event.current.Use();
                }
            }
        }

        // 锁定状态右键：光标固定在屏幕中心、无法命中热栏槽位 → 按当前选中槽判断。
        // 选中种子袋时右键即打开（与背包窗内右键行为一致）；选中其他物品时右键仍走放置逻辑
        if (Cursor.lockState == CursorLockMode.Locked &&
            Event.current.type == EventType.MouseDown && Event.current.button == 1)
        {
            int sel = backpack.SelectedIndex;
            if (sel >= 0 && sel < backpack.Count)
            {
                ItemInstance item = backpack[sel];
                if (item != null && item.ItemType == ItemType.SeedBag)
                {
                    backpack.BackpackOpen = true;
                    backpack.IsSeedBagOpen = true;
                    backpack.OpenSeedBagSlotIndex = sel;
                    Event.current.Use();
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
