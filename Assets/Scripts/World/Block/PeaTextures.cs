using UnityEngine;

// 豌豆贴图：4 阶段生长链 最小苗→苗→开花→结果。
// 阶段0/1 使用用户在图集绘制的 cell——PeaLittleSeedling(1,3)、PeaSeedling(1,2)，
// 运行时占位绘制绝不覆盖这两个用户 cell；阶段2/3 用运行时占位绘制（开花(2,1)、结果(2,2)）。
// 图集为 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），与 MeshData 的 UV 数学一致
public static class PeaTextures
{
    public static readonly Vector2Int[] CellByStage =
    {
        new(1, 3), // 阶段0 最小苗 PeaLittleSeedling（用户绘制）
        new(1, 2), // 阶段1 苗 PeaSeedling（用户绘制）
        new(2, 1), // 阶段2 开花（运行时占位）
        new(2, 2), // 阶段3 结果/结荚（运行时占位）
    };

    public static void PaintAtlasPlaceholders(Texture2D atlas)
    {
        // 只画运行时占位 cell（阶段2 开花 / 阶段3 结果）；用户已绘制的 (1,3)/(1,2) 绝不覆盖
        for (int stage = 2; stage < CellByStage.Length; stage++)
        {
            Vector2Int c = CellByStage[stage];
            // 图集为 768×768，32 cell × 24px（16px 贴图 + 两侧 4px padding）；与 MeshData 的 UV 数学一致
            int ox = c.x * 24 + 4, oy = c.y * 24 + 4; // 像素原点：cell 左下（Unity 像素数组 y 从底部起，与 UV 的 v 方向一致）
            Color[] px = new Color[16 * 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    float t = y / 15f; // 0=底 1=顶
                    Color col = stage switch
                    {
                        2 => new Color(0.3f + 0.5f * t, 0.6f + 0.3f * t, 0.22f),        // 开花：绿底
                        _ => new Color(0.28f + 0.5f * t, 0.58f + 0.32f * t, 0.2f),      // 结果/结荚：绿底
                    };
                    if (stage == 2 && y >= 11 && x >= 5 && x <= 10) col = new Color(0.62f, 0.3f, 0.75f); // 开花：顶部紫点
                    if (stage == 3 && y >= 5 && y <= 10 && x >= 5 && x <= 9) col = new Color(0.85f, 0.75f, 0.25f); // 结果：中部黄点
                    px[y * 16 + x] = col;
                }
            atlas.SetPixels(ox, oy, 16, 16, px);
        }
        atlas.Apply(false);
    }

    // 安装占位图：直接画进资产纹理的空闲 cell（isReadable 需生效，meta 已开）
    // 只写 (2,1)(2,2) 两个 16×16 格（开花/结果占位），方块贴图数据零改动；Apply(true) 重建 mip 链保证所有距离显示新内容
    public static void InstallToMaterial(Material mat)
    {
        Texture2D atlas = mat.mainTexture as Texture2D;
        if (atlas == null) return;
        try
        {
            PaintAtlasPlaceholders(atlas);
            atlas.Apply(true);
            Debug.Log("[PeaTextures] 豌豆占位贴图已安装");
        }
        catch (System.Exception e)
        {
            // isReadable 未生效或纹理格式不支持写入时抛异常
            Debug.LogError($"[PeaTextures] 豌豆占位贴图安装失败，请选中 Atlas.png 重新导入后重试：{e.Message}");
        }
    }
}
