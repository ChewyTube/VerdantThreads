using System;
using System.Collections.Generic;
using UnityEngine;

// World：世界外观协调器（facade）。职责：生命周期、相机出生点、存档集成与公开 API 转发。
// 具体实现拆分为三个专职类（见 Awake），World 只做组装与转发：
//   ChunkStreamer    —— 流式调度（视距盒、后台生成/mesh 调度、帧预算队列、就近排序）
//   TerrainGenerator —— 地形/树木生成 + 存档读路径
//   ChunkStore       —— chunk 存储、对象池、跨 chunk 写入、卸载保存
// 非单例：场景中显式放置，其他组件通过序列化引用 / FindObjectOfType 获取（去单例化 #16）
public class World : MonoBehaviour
{
    int lineOfSight = 12; // 水平视距（可调）
    int verticalLineOfSight = 6; // 垂直视距，独立于水平视距，减少高空空气 chunk 加载
    int seed = 985211;

    private Saver saver = new Saver("world_saves");

    private TerrainGenerator terrainGen;
    private ChunkStore store;
    private ChunkStreamer streamer;
    private BlockUpdateCenter blockUpdateCenter; // 方块更新中心（随机刻 / 方块联动 / 计划刻统一分派）

    // 背包（选择状态唯一权威）：由 Awake 创建，注入给热栏 / 背包窗 / BlockInteraction
    public Backpack Backpack { get; private set; }

    [SerializeField] private Vector3 cameraSpawnPos = new(0, 64, 0); // 相机出生点（可在 Inspector 覆盖，不再硬编码覆盖场景摆放）

    Camera cam;

    // 游戏 tick 累加器（达到 PEA_GROWTH_TICK_INTERVAL=1/20s 时补一个 tick，驱动 BlockUpdateCenter.OnGameTick）
    private float _growthTickAccumulator;

    private void Awake()
    {
        saver.Initialize(); // 主线程解析保存根目录（Application.persistentDataPath 不能从后台线程读取）

        terrainGen = new TerrainGenerator(seed, saver);
        store = new ChunkStore(transform, saver, pos => streamer.RequestMeshRebuild(pos)); // 注入 mesh 重建回调（调用时 streamer 已就绪）
        // 方块更新中心装配：注入 store（随机刻/计划刻读块、联动写入走 store.SetBlock）；
        // 订阅写入通知与 chunk 卸载（循环引用用事件订阅解耦，避免构造期交叉引用）
        blockUpdateCenter = new BlockUpdateCenter(store);
        store.OnBlockWritten += blockUpdateCenter.OnBlockWritten;
        store.OnChunkUnloaded += blockUpdateCenter.OnChunkUnloaded;
        streamer = new ChunkStreamer(terrainGen, store, lineOfSight, verticalLineOfSight, seed);

        DontDestroyOnLoad(gameObject);

        // 物品系统装配：创建背包（选择状态唯一权威），挂 UI 组件并注入引用。
        // AddComponent 后立即 Init，保证注入先于首次 OnGUI（同一 Awake 帧内完成）
        // 有存档则读回背包（含堆叠/种子袋内容），无存档用默认物品
        Backpack = BackpackSaver.Load() ?? new Backpack();
        HotbarWindow hotbar = gameObject.AddComponent<HotbarWindow>();
        hotbar.Init(Backpack);
        BackpackWindow backpackWindow = gameObject.AddComponent<BackpackWindow>();
        backpackWindow.Init(Backpack);
    }

    void Start()
    {
        cam = Camera.main;
        cam.transform.position = cameraSpawnPos; // 出生点可配，不再强制 (0,64,0)

        Vector3Int camVCPos = new BlockPosInWorld((int)cameraSpawnPos.x, (int)cameraSpawnPos.y, (int)cameraSpawnPos.z).GetCorrespondingVCPos();
        streamer.InitializeCamera(camVCPos);
        streamer.GenerateInitial(camVCPos);
    }

    void Update()
    {
        Vector3 camPos = cam.transform.position;

        BlockPosInWorld camPosInt = new BlockPosInWorld((int)camPos.x, (int)camPos.y, (int)camPos.z);

        // 相机所在 VC 的坐标变化检测与各队列调度全部交给流式调度器
        streamer.Tick(camPosInt.GetCorrespondingVCPos());

        // 方块更新中心：20 tick/秒（PEA_GROWTH_TICK_INTERVAL=1/20s）。deltaTime 累加，while 补 tick
        // （低帧率一帧跨多个 tick 也全部补上，与 MC 固定 tick 节奏一致；超过单帧上限 MAX_GAME_TICKS_PER_FRAME
        // 的积压直接丢弃，防止"卡顿→追赶更多 tick→更卡"的正反馈）
        _growthTickAccumulator += Time.deltaTime;
        int ticksRun = 0;
        while (_growthTickAccumulator >= Constants.PEA_GROWTH_TICK_INTERVAL && ticksRun < Constants.MAX_GAME_TICKS_PER_FRAME)
        {
            _growthTickAccumulator -= Constants.PEA_GROWTH_TICK_INTERVAL;
            blockUpdateCenter.OnGameTick();
            ticksRun++;
        }
        if (_growthTickAccumulator >= Constants.PEA_GROWTH_TICK_INTERVAL)
            _growthTickAccumulator = 0f; // 超过单帧上限：丢弃剩余积压
    }

    void OnDestroy()
    {
        saver.Dispose(); // 释放 Saver 持有的 FileStream，防止泄漏（内部会排空保存队列后退出）
    }

    void OnApplicationQuit()
    {
        SaveAllLoadedChunks();
        // 背包存档（含堆叠数量、内部分基因型分布、种子袋内容）；失败仅日志警告不影响退出
        if (Backpack != null) BackpackSaver.Save(Backpack);
    }

    // 退出兜底：卸载路径只保存被卸载的 chunk，仍在内存的 chunk 若不主动入队会丢修改
    private void SaveAllLoadedChunks()
    {
        saver.SetQueueLimit(int.MaxValue); // ⑤ 退出前放开背压：全量入队由 Dispose 排空落盘，不触发同步兜底

        // 先尽力应用一次跨 chunk 挂起写入（树冠等），避免退出时丢 pendingBlocks
        streamer.DrainPendingSetBlocks();

        // 全量入队（空 chunk 无对象不在 world 字典里，无需保存）；
        // OnApplicationQuit 先于 OnDestroy 触发，入队任务由随后 saver.Dispose() 排空落盘。
        // 存档 v3：主线程内先快照 tile（Saver.SnapshotTiles），随块数据一起入队
        store.ForEachLoadedChunk((pos, blocks, tiles) => saver.SaveVoxelChunk(pos, blocks, Saver.SnapshotTiles(tiles)));
    }

    // ---- 公开 API（转发到专职类，签名保持不变，兼容既有调用方） ----

    // 跨 chunk 方块写入（玩家交互入口）
    public bool SetBlock(Block block, BlockPosInWorld pos) => store.SetBlock(block, pos);

    // 请求重建 chunk mesh（走帧预算队列）
    public void RequestMeshRebuild(VCPosInWorld vcPos) => streamer.RequestMeshRebuild(vcPos);

    // 读取 chunk 块数据（未加载返回 null；BlockInteraction / mesh 快照用）
    public Block[,,] GetChunkBlocks(VCPosInWorld vcPos) => store.GetChunkBlocks(vcPos);

    // 豌豆 tile 读写转发（仅主线程；种植/破坏/生长 tick 用）
    public bool SetTile(BlockPosInWorld pos, PeaTileData tile) => store.SetTile(pos, tile);
    public bool RemoveTile(BlockPosInWorld pos) => store.RemoveTile(pos);
    public PeaTileData GetTile(BlockPosInWorld pos) => store.GetTile(pos);
}
