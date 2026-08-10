using UnityEngine;

// 豌豆占位贴图：运行时画进图集空闲 cell（不动 PNG 资产，重启自动重画）
// 图集为 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），与 MeshData 的 UV 数学一致
public static class PeaTextures
{
    public static readonly Vector2Int[] CellByStage =
    {
        new(2, 0), // 苗（绿渐变）
        new(2, 1), // 开花（绿 + 顶部紫点）
        new(2, 2), // 结荚（绿 + 中部黄点）
    };

    public static void PaintAtlasPlaceholders(Texture2D atlas)
    {
        for (int stage = 0; stage < CellByStage.Length; stage++)
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
                        0 => new Color(0.25f + 0.55f * t, 0.55f + 0.35f * t, 0.2f),     // 苗：下深上浅绿
                        1 => new Color(0.3f + 0.5f * t, 0.6f + 0.3f * t, 0.22f),        // 开花：绿底
                        _ => new Color(0.28f + 0.5f * t, 0.58f + 0.32f * t, 0.2f),      // 结荚：绿底
                    };
                    if (stage == 1 && y >= 11 && x >= 5 && x <= 10) col = new Color(0.62f, 0.3f, 0.75f); // 顶部紫点
                    if (stage == 2 && y >= 5 && y <= 10 && x >= 5 && x <= 9) col = new Color(0.85f, 0.75f, 0.25f); // 中部黄点
                    px[y * 16 + x] = col;
                }
            atlas.SetPixels(ox, oy, 16, 16, px);
        }
        atlas.Apply(false);
    }

    // 安装占位图：直接画进资产纹理的 3 个空闲 cell（isReadable 需生效，meta 已开）
    // 只写 (2,0)(2,1)(2,2) 三个 16×16 格，方块贴图数据零改动；Apply(true) 重建 mip 链保证所有距离显示新内容
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
