using UnityEngine;

// 豌豆贴图：4 阶段生长链 最小苗→苗→两格高植株→开花结果。
// 阶段0/1 使用用户在图集绘制的 cell——PeaLittleSeedling(2,3)、PeaSeedling(2,2)，
// 运行时占位绘制绝不覆盖用户 cell；阶段2/3 为两格高植株，用用户绘制的
// PlantTopCell(2,4)（pos_PeaPlantShort_top）+ PlantBottomCell(2,5)（pos_PeaPlantShort_bottom），
// 只引用坐标、绝不运行时覆盖。
// 图集为 768×768 = 32×32 个 24px cell（16px 贴图 + 两侧 4px padding），与 MeshData 的 UV 数学一致
public static class PeaTextures
{
    // 阶段 0/1 单格十字贴图（阶段 2/3 不再走本数组，见 PlantBottomCell/PlantTopCell）
    public static readonly Vector2Int[] CellByStage =
    {
        new(2, 3), // 阶段0 最小苗 PeaLittleSeedling（用户绘制）
        new(2, 2), // 阶段1 苗 PeaSeedling（用户绘制）
        new(2, 1), // （退役）原阶段2 开花运行时占位——两格高后不再使用，保留占位数组位
        new(2, 0), // （退役）原阶段3 结果/结荚运行时占位——两格高后不再使用，保留占位数组位
    };

    // 两格高植株贴图（用户已绘制，绝不运行时覆盖）：
    //   底部格（PeaStem 阶段 2/3）用 PlantBottomCell，顶部格（PeaPlantTop）用 PlantTopCell
    public static readonly Vector2Int PlantBottomCell = new(2, 5); // pos_PeaPlantShort_bottom
    public static readonly Vector2Int PlantTopCell = new(2, 4);    // pos_PeaPlantShort_top

    // 占位绘制已停用：阶段 2/3 改用用户绘制的 (2,4)/(2,5) 两格贴图，(2,1)/(2,0) 运行时占位退役。
    // 函数体保留为空（InstallToMaterial 调用点与 WorldManager 不变），绝不写任何像素，特别是 (2,4)/(2,5)。
    public static void PaintAtlasPlaceholders(Texture2D atlas)
    {
    }

    // 安装占位图：占位绘制已停用（阶段 2/3 用用户绘制的两格贴图），仅保留 Apply 重建 mip 链
    public static void InstallToMaterial(Material mat)
    {
        Texture2D atlas = mat.mainTexture as Texture2D;
        if (atlas == null) return;
        try
        {
            PaintAtlasPlaceholders(atlas);
            atlas.Apply(true);
            Debug.Log("[PeaTextures] 豌豆贴图安装完成（两格高贴图已停用运行时占位绘制）");
        }
        catch (System.Exception e)
        {
            // isReadable 未生效或纹理格式不支持写入时抛异常
            Debug.LogError($"[PeaTextures] 豌豆贴图安装失败，请选中 Atlas.png 重新导入后重试：{e.Message}");
        }
    }
}
