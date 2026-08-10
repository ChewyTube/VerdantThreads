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

    [SerializeField] private Vector3 cameraSpawnPos = new(0, 64, 0); // 相机出生点（可在 Inspector 覆盖，不再硬编码覆盖场景摆放）

    Camera cam;

    private void Awake()
    {
        saver.Initialize(); // 主线程解析保存根目录（Application.persistentDataPath 不能从后台线程读取）

        terrainGen = new TerrainGenerator(seed, saver);
        store = new ChunkStore(transform, saver, pos => streamer.RequestMeshRebuild(pos)); // 注入 mesh 重建回调（调用时 streamer 已就绪）
        streamer = new ChunkStreamer(terrainGen, store, lineOfSight, verticalLineOfSight);

        DontDestroyOnLoad(gameObject);
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
    }

    void OnDestroy()
    {
        saver.Dispose(); // 释放 Saver 持有的 FileStream，防止泄漏（内部会排空保存队列后退出）
    }

    void OnApplicationQuit()
    {
        SaveAllLoadedChunks();
    }

    // 退出兜底：卸载路径只保存被卸载的 chunk，仍在内存的 chunk 若不主动入队会丢修改
    private void SaveAllLoadedChunks()
    {
        saver.SetQueueLimit(int.MaxValue); // ⑤ 退出前放开背压：全量入队由 Dispose 排空落盘，不触发同步兜底

        // 先尽力应用一次跨 chunk 挂起写入（树冠等），避免退出时丢 pendingBlocks
        streamer.DrainPendingSetBlocks();

        // 全量入队（空 chunk 无对象不在 world 字典里，无需保存）；
        // OnApplicationQuit 先于 OnDestroy 触发，入队任务由随后 saver.Dispose() 排空落盘
        store.ForEachLoadedChunk((pos, blocks) => saver.SaveVoxelChunk(pos, blocks));
    }

    // ---- 公开 API（转发到专职类，签名保持不变，兼容既有调用方） ----

    // 跨 chunk 方块写入（玩家交互入口）
    public bool SetBlock(Block block, BlockPosInWorld pos) => store.SetBlock(block, pos);

    // 请求重建 chunk mesh（走帧预算队列）
    public void RequestMeshRebuild(VCPosInWorld vcPos) => streamer.RequestMeshRebuild(vcPos);

    // 读取 chunk 块数据（未加载返回 null；BlockInteraction / mesh 快照用）
    public Block[,,] GetChunkBlocks(VCPosInWorld vcPos) => store.GetChunkBlocks(vcPos);
}
