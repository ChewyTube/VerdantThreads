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
    public const int HOTBAR_SLOT_COUNT = 9;               // 热栏固定槽位数（= 背包每行格数）
    public const int INVENTORY_ROWS = 4;                  // 背包总行数（含热栏行）
    public const int INVENTORY_COLUMNS = 9;               // 每行格数（= HOTBAR_SLOT_COUNT；index = row * COLUMNS + col）
    public const int INVENTORY_SLOT_COUNT = INVENTORY_ROWS * INVENTORY_COLUMNS; // 总格数 = 36（row 0 = 热栏）
    public const KeyCode BACKPACK_TOGGLE_KEY = KeyCode.E; // 背包窗开关按键
    public const int STACK_LIMIT = 64;                 // 物品单格堆叠上限
    public const int SEED_BAG_CAPACITY = 1024;         // 种子袋容量上限（豌豆总数）
    public const string BACKPACK_SAVE_FILE = "backpack.dat"; // 背包存档文件名（world_saves 目录下）

    // ---- 豌豆生长（MC 随机刻制：20 tick/秒，每 tick 每 chunk 抽随机位置，命中豌豆按概率推进阶段）----
    public const float PEA_GROWTH_TICK_INTERVAL = 0.05f; // 随机刻 tick 间隔（秒）：1/20s = 每秒 20 tick（MC 同款）
    public const int PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK = 3;  // 每 chunk 每 tick 随机刻次数（MC 每 section 每 tick 3 次，1:1 对齐；chunk 体积 = MC section）
    public const float PEA_GROWTH_ADVANCE_CHANCE = 1f / 3f;      // 随机刻命中时的阶段推进概率（MC 小麦同款）
    // 期望节奏：单阶段 ≈ 4096 / (3 × 20) / (1/3) ≈ 205s；三阶段全熟 ≈ 10 分钟（可调，嫌快/慢改上面两个常量）

    // ---- 豌豆采收（Phase 2：8 新基因 HarvestGenome 多基因数量性状，见 HARVEST_SYSTEM.md §5.2）----
    public const int HARVEST_LIMIT_BASE_EXPONENT = 1; // 采摘次数上限 = min(2^(1+k), CAP)，k = 纯合显性位点数
    public const int HARVEST_LIMIT_CAP = 64;          // 采摘次数上限封顶
    public const int YIELD_BASE_STAGE4 = 12;          // 阶段 4 豌豆荚基础产量
    public const int YIELD_PER_DOMINANT_STAGE4 = 2;   // 阶段 4 每纯合显性位点产量加成
    public const int YIELD_BASE_STAGE3 = 3;           // 阶段 3 青嫩豆荚基础产量
    public const int YIELD_PER_DOMINANT_STAGE3 = 1;   // 阶段 3 每纯合显性位点产量加成

    // ---- 方块更新机制 ----
    public const int MAX_BLOCK_UPDATE_DEPTH = 256;      // 方块更新递归通知深度上限（防环：破坏联动等递归写入链）
    public const int MAX_GAME_TICKS_PER_FRAME = 5;     // 单帧游戏 tick 追赶上限（=250ms；超限丢弃积压，防止长帧连锁放大卡顿）

    // ---- 地物系统 ----
    public const int PEA_CLUMP_DENSITY = 256;           // 豌豆丛中心频率（哈希取模分母，越小越密；约 1/256 列一丛，每丛 14-18 株）
    public const int PEA_CLUMP_MIN_PLANTS = 14;         // 每丛最少株数
    public const int PEA_CLUMP_MAX_PLANTS = 18;         // 每丛最多株数（均值 16 左右）
    public const int PEA_CLUMP_RADIUS = 3;              // 丛内株距中心的最大水平偏移（格）

    // ---- 体素碰撞与玩家物理（方案 C，见 docs/design/VOXEL_COLLISION.md）----
    public const float PLAYER_GRAVITY = 28f;            // 玩家重力加速度（blocks/s²）
    public const float PLAYER_JUMP_SPEED = 8.5f;        // 跳跃初速（跳高 ≈ 1.25 格，MC 同款）
    public const float PLAYER_WALK_SPEED = 4.3f;        // 步行速度（blocks/s，MC 同款）
    public const float PLAYER_EYE_HEIGHT = 1.62f;       // 眼睛高度（相机位置 = 身体位置 + 此值）
    public const float PLAYER_HALF_WIDTH = 0.3f;        // 身体半宽（AABB 0.6 宽）
    public const float PLAYER_HALF_HEIGHT = 0.9f;       // 身体半高（AABB 1.8 高）
    public const float PLAYER_STEP_HEIGHT = 0.5f;       // 自动上台阶高度（水平被挡时尝试）
    public const float DROPPED_ITEM_GRAVITY = 20f;      // 掉落物重力加速度（blocks/s²）
    public const float DROPPED_ITEM_HALF_SIZE = 0.125f; // 掉落物 AABB 半边长（0.25³）
    public const float DROPPED_ITEM_LIFETIME = 300f;    // 掉落物消失时间（秒，MC 5 分钟）
    public const int DROPPED_ITEM_CAP = 64;             // 全场景掉落物数量上限（超出丢弃最老的）
    public const float DROPPED_ITEM_PICKUP_RADIUS = 1.5f; // 拾取半径（格）
}