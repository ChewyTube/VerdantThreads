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
                    GUI.DrawTextureWithTexCoords(iconRect, atlas, CalcIconUVRect(item.ItemType));
                }
            }
        }
    }

    // 物品图标 UV：图集 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），
    // row 从图集底部起算。cell 取自 BlockUVMap 的 Up 面，豌豆用 PeaTextures.CellByStage[0]（苗期）。
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
