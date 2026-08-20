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

    private void Update()
    {
        if (backpack == null) return;

        // 背包窗打开时不响应滚轮（鼠标已解锁，避免与背包内操作冲突）
        if (backpack.BackpackOpen) return;

        // 鼠标滚轮切换热栏选中（MC 行为：上滚向左、下滚向右，在 0-8 内循环）。
        // 选中在主背包（row>0）时先回到热栏再滚动。
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0f)
        {
            int hotbarSel = Mathf.Clamp(backpack.SelectedIndex, 0, Constants.HOTBAR_SLOT_COUNT - 1);
            hotbarSel = (hotbarSel + (scroll > 0 ? -1 : 1) + Constants.HOTBAR_SLOT_COUNT) % Constants.HOTBAR_SLOT_COUNT;
            backpack.Select(hotbarSel);
        }
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
                    GUI.DrawTextureWithTexCoords(iconRect, atlas, ItemIcon.GetUVRect(item));
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

    // 物品图标 UV 提取已抽到 ItemIcon.GetUVRect（BackpackWindow / HotbarWindow / DroppedItem 共用）
}
