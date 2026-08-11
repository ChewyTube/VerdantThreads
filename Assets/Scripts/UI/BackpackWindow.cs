using UnityEngine;

// E 键开关的背包窗（IMGUI）：逐行列出全部物品（图标 + 中文名），点击行选中并同步热栏。
// 开关状态归属 Backpack（BackpackOpen），BlockInteraction 据此暂停破坏/放置。
public class BackpackWindow : MonoBehaviour
{
    private Backpack backpack;

    // 装配注入：由 World 在 Awake 中创建并调用（保证先于首次 OnGUI）
    public void Init(Backpack backpack)
    {
        this.backpack = backpack;
    }

    private void Update()
    {
        // 切换背包开关（状态写在 Backpack 上，与其他组件共享同一来源）
        if (backpack != null && Input.GetKeyDown(Constants.BACKPACK_TOGGLE_KEY))
        {
            backpack.BackpackOpen = !backpack.BackpackOpen;
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

            // 当前选中行高亮；整行可点击 → 选中该物品（热栏同步读同一 SelectedIndex，无需额外通知）
            GUI.backgroundColor = i == backpack.SelectedIndex
                ? new Color(1f, 0.85f, 0.25f, 0.85f)
                : Color.white;
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
                    atlas, CalcIconUVRect(item.ItemType));
            }

            // 中文显示名
            GUI.Label(
                new Rect(rowRect.x + rowRect.height + 6f, rowRect.y + 6f, rowRect.width - rowRect.height - 12f, 22f),
                item.DisplayName);
        }
    }

    // 物品图标 UV：与热栏同一套 24px cell 换算（16px 贴图 + 两侧 4px padding），row 从图集底部起算。
    // 豌豆用 PeaTextures.CellByStage[0]（苗期），其余用 BlockUVMap 的 Up 面 cell。
    private static Rect CalcIconUVRect(BlockType blockType)
    {
        Vector2Int cell = blockType == BlockType.PeaStem
            ? PeaTextures.CellByStage[0]
            : BlockUVMap.GetUV(blockType, Direction.Up);
        // Rect 不支持 / 运算符，逐分量除以 768 得到归一化 UV
        Rect uv = new Rect(cell.x * 24 + 4, cell.y * 24 + 4, 16, 16);
        return new Rect(uv.x / 768f, uv.y / 768f, uv.width / 768f, uv.height / 768f);
    }
}
