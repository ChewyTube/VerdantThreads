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

    // 高茎基础三格贴图（列 5，用户绘制）：底部=PeaStem 底部格、中部=PeaPlantMiddle、顶部=PeaPlantTop
    public static readonly Vector2Int PlantTallBottomCell = new(5, 2); // 高茎底部格
    public static readonly Vector2Int PlantTallMiddleCell = new(5, 1); // 高茎中部格
    public static readonly Vector2Int PlantTallTopCell = new(5, 0);    // 高茎顶部格

    // 阶段 3（开花）花贴图：花色（紫/白，位点2 显性=紫）× 花位置（位点5 显性=腋生）4 种表型。
    // 矮茎列 3（bottom/top 两张）：row = (花位?0:4) + (花色?0:2)
    //   腋紫 (3,0)/(3,1)  腋白 (3,2)/(3,3)  顶紫 (3,4)/(3,5)  顶白 (3,6)/(3,7)
    public static void GetFlowerColorCells(Genome genome, out Vector2Int bottomCell, out Vector2Int topCell)
    {
        bool purple = genome.IsDominant(2);   // 花色：显性 → 紫
        bool axillary = genome.IsDominant(5); // 花位置：显性 → 腋生
        int row = (axillary ? 0 : 4) + (purple ? 0 : 2);
        bottomCell = new Vector2Int(3, row);
        topCell = new Vector2Int(3, row + 1);
    }

    // 高茎阶段 3 花贴图（列 6，stride 3）：row = (花位?0:6) + (花色?0:3)，bottom/middle/top = row / row+1 / row+2
    //   腋紫 (6,0)/(6,1)/(6,2)  腋白 (6,3)/(6,4)/(6,5)  顶紫 (6,6)/(6,7)/(6,8)  顶白 (6,9)/(6,10)/(6,11)
    public static void GetTallFlowerColorCells(Genome genome, out Vector2Int bottomCell, out Vector2Int middleCell, out Vector2Int topCell)
    {
        bool purple = genome.IsDominant(2);
        bool axillary = genome.IsDominant(5);
        int row = (axillary ? 0 : 6) + (purple ? 0 : 3);
        bottomCell = new Vector2Int(6, row);
        middleCell = new Vector2Int(6, row + 1);
        topCell = new Vector2Int(6, row + 2);
    }

    // 阶段 4（结果）荚贴图：豆荚色（绿/黄，位点4 显性=绿）× 花位置（位点5 显性=腋生）4 种表型。
    // 矮茎列 4（bottom/top）：row = (花位?0:4) + (荚色?0:2)
    //   腋绿 (4,0)/(4,1)  腋黄 (4,2)/(4,3)  顶绿 (4,4)/(4,5)  顶黄 (4,6)/(4,7)
    public static void GetPodCells(Genome genome, out Vector2Int bottomCell, out Vector2Int topCell)
    {
        bool green = genome.IsDominant(4);    // 豆荚色：显性 → 绿
        bool axillary = genome.IsDominant(5); // 花位置：显性 → 腋生
        int row = (axillary ? 0 : 4) + (green ? 0 : 2);
        bottomCell = new Vector2Int(4, row);
        topCell = new Vector2Int(4, row + 1);
    }

    // 高茎阶段 4 荚贴图（列 7，stride 3）：row = (花位?0:6) + (荚色?0:3)，bottom/middle/top = row / row+1 / row+2
    //   腋绿 (7,0)/(7,1)/(7,2)  腋黄 (7,3)/(7,4)/(7,5)  顶绿 (7,6)/(7,7)/(7,8)  顶黄 (7,9)/(7,10)/(7,11)
    public static void GetTallPodCells(Genome genome, out Vector2Int bottomCell, out Vector2Int middleCell, out Vector2Int topCell)
    {
        bool green = genome.IsDominant(4);
        bool axillary = genome.IsDominant(5);
        int row = (axillary ? 0 : 6) + (green ? 0 : 3);
        bottomCell = new Vector2Int(7, row);
        middleCell = new Vector2Int(7, row + 1);
        topCell = new Vector2Int(7, row + 2);
    }

    // 茎高度：位点 6 显性 = 高茎（三格高）；隐性 = 矮茎（两格高）
    public static bool IsTall(Genome genome) => genome.IsDominant(6);

    // 枯萎植株贴图（列 8，用户绘制）：矮茎 2 格 + 高茎 3 格。
    // 矮茎 (8,0)/(8,1) bottom/top；高茎 (8,2)/(8,3)/(8,4) bottom/middle/top
    public static readonly Vector2Int WitheredShortBottomCell = new(8, 0);
    public static readonly Vector2Int WitheredShortTopCell = new(8, 1);
    public static readonly Vector2Int WitheredTallBottomCell = new(8, 2);
    public static readonly Vector2Int WitheredTallMiddleCell = new(8, 3);
    public static readonly Vector2Int WitheredTallTopCell = new(8, 4);

    // 物品图标（列 9-10，用户绘制）：
    // 豌豆荚图标：豆荚色（绿/黄，位点4 显性=绿）× 豆荚形状（饱满/皱缩，位点3 显性=饱满）
    //   (9,0) 绿饱满 (9,1) 绿皱缩 (9,2) 黄饱满 (9,3) 黄皱缩
    public static Vector2Int GetItemPodCell(Genome genome)
    {
        bool green = genome.IsDominant(4);
        bool full = genome.IsDominant(3);
        return new Vector2Int(9, (green ? 0 : 2) + (full ? 0 : 1));
    }

    // 豌豆粒图标：子叶色（黄/绿，位点1 显性=黄）× 种子形状（圆粒/皱粒，位点0 显性=圆粒）
    //   (10,0) 黄圆 (10,1) 黄皱 (10,2) 绿圆 (10,3) 绿皱
    public static Vector2Int GetItemSeedCell(Genome genome)
    {
        bool yellow = genome.IsDominant(1);
        bool round = genome.IsDominant(0);
        return new Vector2Int(10, (yellow ? 0 : 2) + (round ? 0 : 1));
    }

    // 种子袋图标 (10,4)
    public static readonly Vector2Int ItemSeedBagCell = new(10, 4);

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
