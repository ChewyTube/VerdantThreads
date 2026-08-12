using System;
using System.Collections.Generic;

// 方块更新中心（BlockUpdateCenter）：三类方块更新的统一分派中心（主线程，非 MonoBehaviour，World 持有）。
//   - 随机刻 Random Tick：OnGameTick（20Hz）每 chunk 抽 PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK 个随机位置
//     → DispatchRandomTick 按方块类型分派（当前豌豆；未来新方块加 switch 分支）
//   - 方块更新 Block Update：ChunkStore.SetBlock 写入成功后经 OnBlockWritten 通知（本位置 + 6 邻居）
//     → DispatchBlockUpdate 按方块类型分派联动（如两格高豌豆破坏顶/底联动）
//   - 计划刻 Scheduled Tick：ScheduleTick 注册 → 到期 DispatchScheduledTick 按方块类型分派（当前无消费方）
// 线程纪律：全部主线程；后台生成线程绝不触碰本类。mesh 重建（changed → 下一帧 rebuild）与逻辑更新解耦，不在此处理。
public class BlockUpdateCenter
{
    // 主线程随机源：MC 随机刻游玩随机（与生成确定性契约无关——地物/地形生成不用它，确定性保持）
    private readonly System.Random _random = new System.Random();

    private readonly ChunkStore store; // 注入的存储层（读块/跨 chunk 写入口）

    // 方块更新递归深度（MAX_BLOCK_UPDATE_DEPTH 上限防环：联动写入 → 再通知 → 再写入…）
    private int _updateDepth;

    // 计划刻：按 chunk 分组的待执行列表（chunk 卸载时经 OnChunkUnloaded 丢弃；不持久化）
    private readonly Dictionary<VCPosInWorld, List<ScheduledEntry>> _scheduled = new Dictionary<VCPosInWorld, List<ScheduledEntry>>();
    private long _tickCount; // 累计游戏 tick（ScheduleTick 的 delayTicks 基准）

    // 计划刻条目：世界坐标 + 触发 tick
    private readonly struct ScheduledEntry
    {
        public readonly BlockPosInWorld Pos;
        public readonly long TriggerTick;
        public ScheduledEntry(BlockPosInWorld pos, long triggerTick)
        {
            Pos = pos;
            TriggerTick = triggerTick;
        }
    }

    public BlockUpdateCenter(ChunkStore store)
    {
        this.store = store;
    }

    // 每个游戏 tick 调用一次（20Hz，由 World.Update 的 while 补 tick 驱动）。
    // 顺序：先执行到期计划刻，再执行随机刻（MC 语义：计划刻为定点行为、随机刻为密度采样）。
    public void OnGameTick()
    {
        _tickCount++;
        DispatchDueScheduledTicks();
        TickRandomTicks();
    }

    // ChunkStore 卸载钩子：丢弃该 chunk 的全部计划刻（订阅 store.OnChunkUnloaded）
    public void OnChunkUnloaded(VCPosInWorld vcPos)
    {
        _scheduled.Remove(vcPos);
    }

    // ---- Step C：计划刻（机制，暂不接方块）----

    // 注册一个计划刻：delayTicks 个游戏 tick 后触发（按 pos 所在 chunk 分组存储）
    public void ScheduleTick(BlockPosInWorld pos, int delayTicks)
    {
        VCPosInWorld vcPos = pos.GetCorrespondingVCPos();
        if (!_scheduled.TryGetValue(vcPos, out var list))
        {
            list = new List<ScheduledEntry>();
            _scheduled[vcPos] = list;
        }
        list.Add(new ScheduledEntry(pos, _tickCount + delayTicks));
    }

    // 执行到期计划刻：收集 TriggerTick ≤ 当前 tick 的条目（按触发 tick 升序），逐条按当前方块类型分派
    private void DispatchDueScheduledTicks()
    {
        if (_scheduled.Count == 0) return;

        var expired = new List<ScheduledEntry>();
        var emptyKeys = new List<VCPosInWorld>();
        foreach (var kv in _scheduled)
        {
            var list = kv.Value;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].TriggerTick <= _tickCount)
                {
                    expired.Add(list[i]);
                    list.RemoveAt(i);
                }
            }
            if (list.Count == 0) emptyKeys.Add(kv.Key);
        }
        foreach (var k in emptyKeys) _scheduled.Remove(k);

        expired.Sort((a, b) => a.TriggerTick.CompareTo(b.TriggerTick));
        foreach (var e in expired)
        {
            DispatchScheduledTick(e.Pos);
        }
    }

    // 计划刻分派：按当前方块类型 switch；当前无消费方（未来：沙子下落、水流等加分支）
    private void DispatchScheduledTick(BlockPosInWorld pos)
    {
        Block block = ReadBlockOrAir(pos);
        // Debug.Log($"[BlockUpdateCenter] 计划刻触发：{pos} {block.GetBlockType()}"); // 调试验证样例（默认注释关闭）
        switch (block.GetBlockType())
        {
            default:
                break;
        }
    }

    // ---- Step A：随机刻 ----

    // 每 tick 随机刻：遍历已加载 chunk，每 chunk 抽 PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK 个随机位置分派
    private void TickRandomTicks()
    {
        store.ForEachLoadedChunk((vcPos, blocks) =>
        {
            for (int i = 0; i < Constants.PEA_RANDOM_TICKS_PER_CHUNK_PER_TICK; i++)
            {
                int x = _random.Next(Constants.CHUNK_SIZE);
                int y = _random.Next(Constants.CHUNK_SIZE);
                int z = _random.Next(Constants.CHUNK_SIZE);
                DispatchRandomTick(x, y, z, vcPos, blocks);
            }
        });
    }

    // 随机刻分派：按方块类型 switch（当前豌豆两分支；未来新方块加分支）
    private void DispatchRandomTick(int x, int y, int z, VCPosInWorld vcPos, Block[,,] blocks)
    {
        Block b = blocks[x, y, z];
        switch (b.GetBlockType())
        {
            case BlockType.PeaPlantTop:
                break; // 顶部格不生长
            case BlockType.PeaStem:
                RandomTickPea(x, y, z, vcPos, blocks, b);
                break;
            default:
                break;
        }
    }

    // 豌豆随机刻（从原 ChunkStore.TickPeaRandomTicks 原样迁移，行为不变）：
    // 命中 PeaStem 且阶段 < 3 时以 PEA_GROWTH_ADVANCE_CHANCE 概率推进阶段（阶段只进不退）。
    // 阶段 1→2 为两格高植株：需先占上方格（PeaPlantTop），上方被占则卡住等空间（MC tall plant 式）。
    private void RandomTickPea(int x, int y, int z, VCPosInWorld vcPos, Block[,,] blocks, Block b)
    {
        int stage = (int)(b.GetBlockState() & BlockBits.StageMask);
        if (stage >= 3) return; // 已开花结果，不再生长

        if (_random.NextDouble() >= Constants.PEA_GROWTH_ADVANCE_CHANCE) return;

        int s = Constants.CHUNK_SIZE;
        BlockPosInWorld worldPos = new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + y, vcPos.Z * s + z);
        if (stage == 1)
        {
            // 阶段 1→2（两格高）：上方格必须为 Air 才能长高，先放顶部格（PeaPlantTop），成功后才推进底部阶段
            if (!TryEnsurePlantTop(vcPos, blocks, x, y, z)) return; // 上方被占 / 顶部目标 chunk 未加载 → 本次不推进，下次再试
            store.SetBlock(b.WithStage(2), worldPos);
        }
        else
        {
            store.SetBlock(b.WithStage((uint)(stage + 1)), worldPos);
        }
    }

    // 阶段 1→2 补顶：上方格必须为 Air（MC tall plant 式空间检查），随后写入 PeaPlantTop。
    // 同 chunk 读本块数组；y+1 越界（顶部在相邻 chunk）读邻居 chunk（未加载返回 false）。
    // 返回 false 表示本次不推进（上方被占或邻居未加载），下次随机刻再试。
    private bool TryEnsurePlantTop(VCPosInWorld vcPos, Block[,,] blocks, int x, int y, int z)
    {
        int s = Constants.CHUNK_SIZE;
        int topLocalY = y + 1;

        BlockType topType;
        if (topLocalY < s)
        {
            topType = blocks[x, topLocalY, z].GetBlockType(); // 同 chunk 上方格
        }
        else
        {
            // 顶部格在相邻 chunk（yc+1）：读邻居块数组，未加载 → 本次跳过
            Block[,,] up = store.GetChunkBlocks(new VCPosInWorld(vcPos.X, vcPos.Y + 1, vcPos.Z));
            if (up == null) return false;
            topType = up[x, 0, z].GetBlockType();
        }

        if (topType != BlockType.Air) return false; // 上方被占 → 不推进

        // 先放顶部格（跨 chunk 安全：未加载返回 false）；写入成功才由调用方推进底部阶段
        return store.SetBlock(BlockRegistry.PeaPlantTop, new BlockPosInWorld(vcPos.X * s + x, vcPos.Y * s + topLocalY, vcPos.Z * s + z));
    }

    // ---- Step B：方块更新通知与联动分派 ----

    // ChunkStore.SetBlock 写入成功后回调（订阅 store.OnBlockWritten）：本位置 + 6 邻居分派联动。
    // 本位置用「旧块」（写入后 pos 处已是新块，联动判定需要旧状态：谁被破坏、旧阶段是什么）；
    // 6 邻居用「当前块」。
    public void OnBlockWritten(BlockPosInWorld pos, Block oldBlock, Block newBlock)
    {
        if (_updateDepth >= Constants.MAX_BLOCK_UPDATE_DEPTH) return; // 递归深度上限，防联动链环

        _updateDepth++;
        try
        {
        BlockUpdateSource source = DetermineSource(oldBlock, newBlock);

        DispatchBlockUpdate(pos, oldBlock, source); // 本位置：用旧块分派（它是刚被替换的块，状态可判定联动）

        // 6 邻居：一律按 NeighborChanged 分派（邻居变化 ≠ 邻居自身被破坏/放置；
        // 若复用 source，破坏豌豆 A 会连带触发相邻豌豆 B 的 Break 联动 → 误清 B 顶部格）
        DispatchNeighbor(new BlockPosInWorld(pos.X + 1, pos.Y, pos.Z), BlockUpdateSource.NeighborChanged);
        DispatchNeighbor(new BlockPosInWorld(pos.X - 1, pos.Y, pos.Z), BlockUpdateSource.NeighborChanged);
        DispatchNeighbor(new BlockPosInWorld(pos.X, pos.Y + 1, pos.Z), BlockUpdateSource.NeighborChanged);
        DispatchNeighbor(new BlockPosInWorld(pos.X, pos.Y - 1, pos.Z), BlockUpdateSource.NeighborChanged);
        DispatchNeighbor(new BlockPosInWorld(pos.X, pos.Y, pos.Z + 1), BlockUpdateSource.NeighborChanged);
        DispatchNeighbor(new BlockPosInWorld(pos.X, pos.Y, pos.Z - 1), BlockUpdateSource.NeighborChanged);
        }
        finally
        {
            _updateDepth--;
        }
    }

    // 6 邻居分派：用当前块；未加载 chunk（GetChunkBlocks null）跳过
    private void DispatchNeighbor(BlockPosInWorld pos, BlockUpdateSource source)
    {
        if (!TryReadBlock(pos, out Block block)) return; // 未加载跳过
        DispatchBlockUpdate(pos, block, source);
    }

    // 联动分派：按方块类型 switch（source 决定动作；联动写入再走 store.SetBlock → 递归通知，深度上限兜底）
    private void DispatchBlockUpdate(BlockPosInWorld pos, Block block, BlockUpdateSource source)
    {
        switch (block.GetBlockType())
        {
            case BlockType.PeaStem:
            {
                // 两格高植株底部被破坏（阶段≥2）→ 上方格若为 PeaPlantTop 一并清除（跨 chunk 由 store.SetBlock 处理）
                if (source == BlockUpdateSource.Break &&
                    (int)(block.GetBlockState() & BlockBits.StageMask) >= 2)
                {
                    if (TryReadBlock(new BlockPosInWorld(pos.X, pos.Y + 1, pos.Z), out Block top) &&
                        top.GetBlockType() == BlockType.PeaPlantTop)
                    {
                        store.SetBlock(BlockRegistry.Air, new BlockPosInWorld(pos.X, pos.Y + 1, pos.Z));
                    }
                }
                // 支撑检查：邻居方块变化（下方支撑被挖掉）或刚放置于无支撑位置 → 植株掉落
                // （无掉落物实体系统：植株直接消失，tile 一并清理；阶段≥2 的顶部格经上方 Break 联动清除）
                else if ((source == BlockUpdateSource.NeighborChanged || source == BlockUpdateSource.Place) &&
                         TryReadBlock(new BlockPosInWorld(pos.X, pos.Y - 1, pos.Z), out Block below) &&
                         below.GetBlockType() == BlockType.Air)
                {
                    DropPeaPlant(pos);
                }
                break;
            }
            case BlockType.PeaPlantTop:
            {
                // 顶部格被破坏 → 下方若为 PeaStem 则退回阶段 0（不 RemoveTile——tile 基因保留，可继续生长）
                if (source == BlockUpdateSource.Break)
                {
                    if (TryReadBlock(new BlockPosInWorld(pos.X, pos.Y - 1, pos.Z), out Block bottom) &&
                        bottom.GetBlockType() == BlockType.PeaStem)
                    {
                        store.SetBlock(bottom.WithStage(0), new BlockPosInWorld(pos.X, pos.Y - 1, pos.Z));
                    }
                }
                break;
            }
            // Place / StateChange / NeighborChanged 当前无消费方（未来：树苗、沙子等加分支）
            default:
                break;
        }
    }

    // 豌豆掉落：植株失去支撑 → 移除方块 + tile（无掉落物实体系统，植株直接消失）。
    // 置 Air 触发 Break 通知 → 上方 PeaPlantTop 经联动清除（阶段≥2 时）；递归深度由上限兜底。
    private void DropPeaPlant(BlockPosInWorld pos)
    {
        store.RemoveTile(pos); // tile 与方块生命周期一致，随植株移除
        store.SetBlock(BlockRegistry.Air, pos);
    }

    // 事件源判别：Air→非 Air = Place；非 Air→Air = Break；其余 = StateChange
    private static BlockUpdateSource DetermineSource(Block oldBlock, Block newBlock)
    {
        BlockType oldT = oldBlock.GetBlockType();
        BlockType newT = newBlock.GetBlockType();
        bool oldEmpty = oldT == BlockType.Air || oldT == BlockType.Void;
        bool newEmpty = newT == BlockType.Air || newT == BlockType.Void;
        if (oldEmpty && !newEmpty) return BlockUpdateSource.Place;
        if (!oldEmpty && newEmpty) return BlockUpdateSource.Break;
        return BlockUpdateSource.StateChange;
    }

    // 读取世界坐标处的方块；chunk 未加载返回 false（out 无效）
    private bool TryReadBlock(BlockPosInWorld pos, out Block block)
    {
        Block[,,] blocks = store.GetChunkBlocks(pos.GetCorrespondingVCPos());
        if (blocks == null)
        {
            block = default;
            return false;
        }
        int m = Constants.CHUNK_SIZE - 1;
        block = blocks[pos.X & m, pos.Y & m, pos.Z & m];
        return true;
    }

    // 读取世界坐标处的方块；chunk 未加载返回 Air
    private Block ReadBlockOrAir(BlockPosInWorld pos)
    {
        return TryReadBlock(pos, out Block block) ? block : BlockRegistry.Air;
    }
}

// 方块更新事件源：Place 放置 / Break 破坏 / StateChange 状态变化 / NeighborChanged 邻居变化（未来预留）
public enum BlockUpdateSource
{
    Place,
    Break,
    StateChange,
    NeighborChanged,
}
