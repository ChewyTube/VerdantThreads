using UnityEngine;

public static class Constants
{
    public const int CHUNK_SIZE = 16;
    public const int CHUNK_SIZE_LOG2 = 4;

    // 存档格式常量（.vrf）
    public const int REGION_SIZE = 32;       // region 每边 chunk 数（32³ chunk/region）
    public const int REGION_SIZE_LOG2 = 5;   // log2(REGION_SIZE)
    public const int SECTOR_SIZE = 4096;     // region 文件扇区字节数
    public const int CHUNK_VOLUME = CHUNK_SIZE * CHUNK_SIZE * CHUNK_SIZE; // 16³ = 4096 方块

    // ---- 物品栏（热栏）与背包 ----
    public const int HOTBAR_SLOT_COUNT = 9;               // 热栏固定槽位数
    public const KeyCode BACKPACK_TOGGLE_KEY = KeyCode.E; // 背包窗开关按键

    // ---- 豌豆生长（4 阶段：最小苗→苗→开花→结果）----
    public const float PEA_GROWTH_TICK_INTERVAL = 1f;   // 生长 tick 扫描间隔（秒）
    public const float PEA_STAGE_1_SECONDS = 20f;       // 最小苗→苗所需生长时间（秒，可调）
    public const float PEA_STAGE_2_SECONDS = 40f;       // 苗→开花所需生长时间（秒，可调）
    public const float PEA_STAGE_3_SECONDS = 60f;       // 开花→结果所需生长时间（秒，可调）

    // ---- 地物系统 ----
    public const int PEA_CLUMP_DENSITY = 256;           // 豌豆丛中心频率（哈希取模分母，越小越密；约 1/256 列一丛，每丛 14-18 株）
    public const int PEA_CLUMP_MIN_PLANTS = 14;         // 每丛最少株数
    public const int PEA_CLUMP_MAX_PLANTS = 18;         // 每丛最多株数（均值 16 左右）
    public const int PEA_CLUMP_RADIUS = 3;              // 丛内株距中心的最大水平偏移（格）
}