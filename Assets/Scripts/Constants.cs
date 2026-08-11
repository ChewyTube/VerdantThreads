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

    // ---- 豌豆生长（MC 随机刻制：20 tick/秒，每 tick 每 chunk 抽随机位置，命中豌豆按概率推进阶段）----
    public const float PEA_GROWTH_TICK_INTERVAL = 0.05f; // 随机刻 tick 间隔（秒）：1/20s = 每秒 20 tick（MC 同款）
    public const int PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK = 3;  // 每 chunk 每 tick 随机刻次数（MC 每 section 每 tick 3 次，1:1 对齐；chunk 体积 = MC section）
    public const float PEA_GROWTH_ADVANCE_CHANCE = 1f / 3f;      // 随机刻命中时的阶段推进概率（MC 小麦同款）
    // 期望节奏：单阶段 ≈ 4096 / (3 × 20) / (1/3) ≈ 205s；三阶段全熟 ≈ 10 分钟（可调，嫌快/慢改上面两个常量）

    // ---- 方块更新机制 ----
    public const int MAX_BLOCK_UPDATE_DEPTH = 256;      // 方块更新递归通知深度上限（防环：破坏联动等递归写入链）

    // ---- 地物系统 ----
    public const int PEA_CLUMP_DENSITY = 256;           // 豌豆丛中心频率（哈希取模分母，越小越密；约 1/256 列一丛，每丛 14-18 株）
    public const int PEA_CLUMP_MIN_PLANTS = 14;         // 每丛最少株数
    public const int PEA_CLUMP_MAX_PLANTS = 18;         // 每丛最多株数（均值 16 左右）
    public const int PEA_CLUMP_RADIUS = 3;              // 丛内株距中心的最大水平偏移（格）
}