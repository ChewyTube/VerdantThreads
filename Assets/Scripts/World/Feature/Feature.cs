// 地物（Feature）抽象基类：生成期在已填充地形上放置装饰物的纯生成逻辑单元。
// 契约：
//   - 纯生成逻辑：只读输入参数 + 写 VoxelChunkData，不依赖任何 Unity 主线程对象
//     （GameObject/Time/Transform 等一律不碰），可在后台生成线程安全调用；
//   - 确定性：同坐标 + 固定 seed 必须产生同结果（禁止 System.Random/DateTime 等
//     非确定源），保证世界可复现；
//   - 跨界写入：一律通过 data.Setblock 写方块（块外坐标自动进 pendingBlocks，
//     由主线程在目标 chunk 加载后重放），地物自身不得直接访问 ChunkStore/world。
public abstract class Feature
{
    // 判定是否在本列放置（确定性哈希 + 目标格可用性检查；仅主生成线程调用，无共享可变状态）
    public abstract bool CanPlace(VoxelChunkData data, int blockX, int groundY, int blockZ);

    // 执行放置：通过 data.Setblock 写方块（跨界自动进 pendingBlocks）；需要 tile 时记入 data
    public abstract void Place(VoxelChunkData data, int blockX, int groundY, int blockZ);
}
