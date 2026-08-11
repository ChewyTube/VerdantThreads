using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

public struct SaveTask
{
    public Vector3Int RegionPos;
    public int LocalX, LocalY, LocalZ;
    public uint[,,] RawChunkData;
    public TileSaveRecord[] Tiles; // 豌豆 tile 快照（纯值数组，worker 只读；null 视为无 tile）
    public int RetryCount; // 失败重试次数（重入队时递增）
}

// 豌豆 tile 存档记录（存档 v3）：纯值字段（ushort key + uint 基因 + int 世代），
// 由主线程快照生成后交给 worker 只读，跨线程安全。GrowthTime 已退役（生长阶段存方块状态位）。
public struct TileSaveRecord
{
    public ushort Key;       // 块内线性 key：(x<<8)|(y<<4)|z
    public uint GenomeValue; // PeaTileData.Genome.Value
    public int Generation;   // 世代（种植时 0）
}

public class Saver
{
    private readonly ConcurrentQueue<SaveTask> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue); // 显式 maxCount，避免依赖单参构造器在不同 BCL 实现下的 maxCount 语义
    private volatile bool _completed;
    private Task _workerTask;
    private bool _disposed;
    private readonly string _path;
    private string _saveRoot; // 保存根目录（主线程初始化，Application.persistentDataPath 仅主线程可访问）
    private const int MAX_RETRY = 3; // 单个 chunk 保存失败最多重试次数
    private int _failedCount;        // 最终失败的 chunk 数（Interlocked，供 Dispose 汇总）

    // ⑤ 存档背压：队列上限（满则主线程同步兜底，防内存无界增长）与批量 flush（减少 fsync 次数）
    private int _maxQueueSize = 1024;   // 队列上限：1024 × 16KB ≈ 16MB（可被 SetQueueLimit 覆盖）
    private const int BATCH_FLUSH_CHUNKS = 32; // worker 每写满 N 个 chunk 批量 flush 一次全部活跃 region
    private readonly Dictionary<Vector3Int, SimpleRegionWriter> _writers = new(); // region writer 缓存（worker 与同步兜底共享，_writeLock 保护）
    private readonly object _writeLock = new();

    public Saver(string path) {  _path = path; _workerTask = Task.Run(SaveLoop); }

    // 必须在主线程调用（Application.persistentDataPath 不能从后台线程读取）
    public void Initialize()
    {
        _saveRoot = Application.persistentDataPath;
    }

    // ⑤ 背压：运行时默认上限 1024；退出全量保存前放开上限（int.MaxValue），
    // 避免几千个 chunk 全走主线程同步兜底卡死退出流程。须主线程调用。
    public void SetQueueLimit(int maxQueueSize)
    {
        _maxQueueSize = maxQueueSize;
    }

    public void SaveVoxelChunk(VCPosInWorld vcPos, uint[,,] chunkData)
    {
        if (_completed) throw new InvalidOperationException("Saver 已释放，不能继续入队保存任务。");

        uint[,,] rawCopy = new uint[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
        Buffer.BlockCopy(chunkData, 0, rawCopy, 0, Constants.CHUNK_VOLUME * sizeof(uint)); // 复制一份，防止调用方复用数组

        EnqueueSave(new SaveTask
        {
            RegionPos = new Vector3Int(vcPos.X >> Constants.REGION_SIZE_LOG2, vcPos.Y >> Constants.REGION_SIZE_LOG2, vcPos.Z >> Constants.REGION_SIZE_LOG2),
            LocalX = vcPos.X & (Constants.REGION_SIZE - 1),
            LocalY = vcPos.Y & (Constants.REGION_SIZE - 1),
            LocalZ = vcPos.Z & (Constants.REGION_SIZE - 1),
            RawChunkData = rawCopy
        });
    }

    public void SaveVoxelChunk(VCPosInWorld vcPos, Block[,,] chunkData)
    {
        SaveVoxelChunk(vcPos, chunkData, Array.Empty<TileSaveRecord>()); // 兼容入口：无 tile → 委托带 tile 重载（仍写 v3 载荷，tileCount=0）
    }

    // 带 tile 的保存（存档 v3）：主线程调用。tiles 须由调用方预先快照（Saver.SnapshotTiles，
    // 快照须发生在主线程），本方法仅传递数组引用，worker 只读 TileSaveRecord[]（纯值，跨线程安全）。
    public void SaveVoxelChunk(VCPosInWorld vcPos, Block[,,] chunkData, TileSaveRecord[] tiles)
    {
        if (_completed) throw new InvalidOperationException("Saver 已释放，不能继续入队保存任务。");

        uint[,,] rawCopy = new uint[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
        // Buffer.BlockCopy 仅接受基元类型数组，Block 是 struct（即使单 uint 可 blittable）也会抛 ArgumentException，故手动转换
        for (int x = 0; x < Constants.CHUNK_SIZE; x++)
            for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                for (int z = 0; z < Constants.CHUNK_SIZE; z++)
                    rawCopy[x, y, z] = (uint)chunkData[x, y, z];

        EnqueueSave(new SaveTask
        {
            RegionPos = new Vector3Int(vcPos.X >> Constants.REGION_SIZE_LOG2, vcPos.Y >> Constants.REGION_SIZE_LOG2, vcPos.Z >> Constants.REGION_SIZE_LOG2),
            LocalX = vcPos.X & (Constants.REGION_SIZE - 1),
            LocalY = vcPos.Y & (Constants.REGION_SIZE - 1),
            LocalZ = vcPos.Z & (Constants.REGION_SIZE - 1),
            RawChunkData = rawCopy,
            Tiles = tiles ?? Array.Empty<TileSaveRecord>()
        });
    }

    // ⑤ 背压：队列未满正常入队；满则主线程同步兜底直接写入，防止连续移动时内存无界增长
    private void EnqueueSave(SaveTask task)
    {
        if (_queue.Count >= _maxQueueSize)
        {
            SaveSync(task);
            return;
        }

        _queue.Enqueue(task);
        _signal.Release();
    }

    // 主线程同步兜底（仅队列满时触发；与 worker 通过 _writeLock 互斥，_saveRoot 已由 Initialize 解析）
    private void SaveSync(SaveTask task)
    {
        try
        {
            byte[] compressed = ZlibChunkCompressor.Compress(task.RawChunkData, task.Tiles, System.IO.Compression.CompressionLevel.Fastest);

            lock (_writeLock)
            {
                if (!_writers.TryGetValue(task.RegionPos, out var writer))
                {
                    writer = new SimpleRegionWriter(_saveRoot, _path, task.RegionPos);
                    _writers[task.RegionPos] = writer;
                }
                writer.WriteVoxelChunk(task.LocalX, task.LocalY, task.LocalZ, compressed);
                writer.Flush();
            }
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _failedCount);
            Debug.LogError($"[Saver] 同步兜底保存失败：region {task.RegionPos}，" +
                           $"local ({task.LocalX},{task.LocalY},{task.LocalZ})，异常：{e.Message}");
        }
    }

    private async Task SaveLoop()
    {
        int sinceFlush = 0; // 距上次批量 flush 已写的 chunk 数

        while (true)
        {
            await _signal.WaitAsync();

            if (_queue.TryDequeue(out var task))
            {
                try
                {
                    byte[] compressed = ZlibChunkCompressor.Compress(task.RawChunkData, task.Tiles, System.IO.Compression.CompressionLevel.Fastest);

                    lock (_writeLock) // 与主线程同步兜底（SaveSync）互斥
                    {
                        if (!_writers.TryGetValue(task.RegionPos, out var writer))
                        {
                            writer = new SimpleRegionWriter(_saveRoot, _path, task.RegionPos);
                            _writers[task.RegionPos] = writer;
                        }

                        writer.WriteVoxelChunk(task.LocalX, task.LocalY, task.LocalZ, compressed);
                    }

                    // ⑤ 批量 flush：每写满 BATCH_FLUSH_CHUNKS 个 chunk 才落盘一次，减少 fsync 次数
                    if (++sinceFlush >= BATCH_FLUSH_CHUNKS)
                    {
                        sinceFlush = 0;
                        FlushAllWriters();
                    }
                }
                catch (Exception e)
                {
                    if (task.RetryCount < MAX_RETRY)
                    {
                        task.RetryCount++;
                        _queue.Enqueue(task); // 重新入队重试（写入失败多为瞬时 IO 问题）
                        _signal.Release();    // 必须补信号，否则 worker 会挂在 WaitAsync 上不去消费重试任务
                    }
                    else
                    {
                        Interlocked.Increment(ref _failedCount);
                        Debug.LogError($"[Saver] chunk 保存失败（已重试 {MAX_RETRY} 次后放弃）：region {task.RegionPos}，" +
                                       $"local ({task.LocalX},{task.LocalY},{task.LocalZ})，异常：{e.Message}");
                    }
                }
            }
            else if (_completed)
            {
                FlushAllWriters(); // 退出前把积压的写入落盘
                break; // 队列已空且已请求结束
            }
        }

        lock (_writeLock)
        {
            foreach (var w in _writers.Values) w.Dispose();
            _writers.Clear();
        }
    }

    // 落盘全部活跃 region writer（批量 flush，减少 fsync 次数）
    private void FlushAllWriters()
    {
        lock (_writeLock)
        {
            foreach (var w in _writers.Values) w.Flush();
        }
    }

    // 读路径：从 .vrf region 文件加载单个 chunk（存档 v3：块数据 + tile 快照；v1/v2 旧档自动兼容）。
    // 缺失/损坏/未保存 → 返回 false 且 blocks/tiles = null，由调用方重新生成。
    // 后台线程可安全调用（独立打开文件流，与写路径的 _writers 互不干扰；FileShare.ReadWrite 允许并发）。
    public bool TryLoadVoxelChunk(VCPosInWorld vcPos, out Block[,,] blocks, out TileSaveRecord[] tiles)
    {
        blocks = null;
        tiles = null;

        // 存档根目录未初始化（Initialize 前）→ 视为无存档
        if (string.IsNullOrEmpty(_saveRoot)) return false;

        string filePath = $"{_saveRoot}/{_path}/r.{vcPos.X >> Constants.REGION_SIZE_LOG2}.{vcPos.Y >> Constants.REGION_SIZE_LOG2}.{vcPos.Z >> Constants.REGION_SIZE_LOG2}.vrf";
        if (!File.Exists(filePath)) return false; // 无该 region 文件：避免走异常路径（启动时每 chunk 查一次）

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 1. 校验版本头（magic "VRF1" + version=1）
            if (fs.Length < SimpleRegionWriter.FORMAT_MAGIC_SIZE + SimpleRegionWriter.INDEX_SIZE)
                return false;

            Span<byte> magic = stackalloc byte[8];
            int read = fs.Read(magic);
            if (read < 8 ||
                magic[0] != (byte)'V' || magic[1] != (byte)'R' || magic[2] != (byte)'F' || magic[3] != (byte)'1' ||
                BinaryPrimitives.ReadInt32BigEndian(magic[4..]) != 1)
                return false; // 旧格式或损坏 → 回退重新生成

            // 2. 读索引区定位 chunk
            int localX = vcPos.X & (Constants.REGION_SIZE - 1);
            int localY = vcPos.Y & (Constants.REGION_SIZE - 1);
            int localZ = vcPos.Z & (Constants.REGION_SIZE - 1);
            int headerOffset = (localX + localY * Constants.REGION_SIZE + localZ * Constants.REGION_SIZE * Constants.REGION_SIZE) * 4;

            fs.Seek(SimpleRegionWriter.FORMAT_MAGIC_SIZE + headerOffset, SeekOrigin.Begin);
            Span<byte> entry = stackalloc byte[4];
            if (fs.Read(entry) < 4) return false;

            uint sectorOffset = ((uint)entry[0] << 16) | ((uint)entry[1] << 8) | entry[2];
            int sectorCount = entry[3];
            if (sectorCount == 0) return false; // 该 chunk 从未保存

            // 3. 读数据扇区：4B 压缩长度 + 压缩数据
            fs.Seek(SimpleRegionWriter.FORMAT_MAGIC_SIZE + SimpleRegionWriter.INDEX_SIZE + sectorOffset * Constants.SECTOR_SIZE, SeekOrigin.Begin);
            Span<byte> lenBuf = stackalloc byte[4];
            if (fs.Read(lenBuf) < 4) return false;
            int compressedLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            if (compressedLen <= 0 || compressedLen > sectorCount * Constants.SECTOR_SIZE - 4) return false; // 越界防护

            byte[] compressed = new byte[compressedLen];
            if (fs.Read(compressed, 0, compressedLen) < compressedLen) return false;

            // 4. 解压 → 载荷自动判别（v1 纯块数据 / v2 带 tile 段）→ uint[,,] → Block[,,]
            ZlibChunkCompressor.ChunkPayload payload = ZlibChunkCompressor.DecompressPayload(compressed);
            Block[,,] blockArr = new Block[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
            for (int x = 0; x < Constants.CHUNK_SIZE; x++)
                for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                    for (int z = 0; z < Constants.CHUNK_SIZE; z++)
                        blockArr[x, y, z] = payload.Blocks[x, y, z]; // uint → Block 隐式转换

            blocks = blockArr;
            tiles = payload.Tiles;
            return true;
        }
        catch (Exception)
        {
            return false; // 文件缺失/IO 错误 → 回退重新生成（存档是优化，非正确性依赖）
        }
    }

    // 主线程调用：把 chunk 的 tile 字典拍成纯值快照数组（worker 线程只读该数组，绝不碰 tile 字典）。
    // tile 字典仅主线程访问，故快照须在主线程发生（调用方传入时即已完成）；本方法只做纯复制。
    public static TileSaveRecord[] SnapshotTiles(Dictionary<ushort, PeaTileData> tiles)
    {
        if (tiles == null || tiles.Count == 0) return Array.Empty<TileSaveRecord>();

        TileSaveRecord[] records = new TileSaveRecord[tiles.Count];
        int i = 0;
        foreach (var kv in tiles)
        {
            PeaTileData tile = kv.Value;
            records[i++] = new TileSaveRecord
            {
                Key = kv.Key,
                GenomeValue = tile.Genome.Value,
                Generation = tile.Generation
            };
        }
        return records;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completed = true;

        _signal.Release(); // 唤醒可能正在等待的工作线程
        try { _workerTask?.Wait(); } catch (AggregateException) { }
        _signal.Dispose();
        GC.SuppressFinalize(this);

        if (_failedCount > 0)
            Debug.LogError($"[Saver] 本次会话共 {_failedCount} 个 chunk 保存失败，存档可能不完整。");
    }
}

public class SimpleRegionWriter : IDisposable
{
    // .vrf 文件布局：8B 版本头（magic "VRF1" + int32 version=1）→ 索引区（32³ × 4B）→ 数据扇区
    internal const int FORMAT_MAGIC_SIZE = 8; // "VRF1" 4B + version 4B
    internal const int INDEX_SIZE = Constants.REGION_SIZE * Constants.REGION_SIZE * Constants.REGION_SIZE * 4;
    private const int HEADER_SIZE = FORMAT_MAGIC_SIZE + INDEX_SIZE; // 文件头总长（版本头 + 索引区）

    private readonly FileStream _stream;
    private readonly byte[] _header = new byte[INDEX_SIZE];

    private readonly Vector3Int _regionPos;

    private uint _nextFreeSector = 0;

    private bool _disposed;

    // 读取索引条目中的扇区偏移（3B，大端），与 WriteVoxelChunk/Flush 的写入格式对称
    private static uint ReadSectorOffset(byte[] header, int headerOffset) =>
        ((uint)header[headerOffset] << 16) | ((uint)header[headerOffset + 1] << 8) | header[headerOffset + 2];

    // saveRoot/subPath 须在主线程解析后传入（后台线程不能访问 Application.persistentDataPath）
    public SimpleRegionWriter(string saveRoot, string subPath, Vector3Int regionPos)
    {
        _regionPos = regionPos;

        string filePath = $"{saveRoot}/{subPath}/r.{regionPos.x}.{regionPos.y}.{regionPos.z}.vrf";

        string dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Step 0：不再用 FileMode.Create 整文件重建（会丢弃本会话未加载 chunk 的旧数据）。
        // 文件已存在且版本头合法 → 读旧索引续写，旧 chunk 数据保留；否则才全新创建。
        bool resume = false;
        if (File.Exists(filePath))
        {
            try
            {
                using (var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (probe.Length >= FORMAT_MAGIC_SIZE + INDEX_SIZE)
                    {
                        Span<byte> magic = stackalloc byte[FORMAT_MAGIC_SIZE];
                        if (probe.Read(magic) == FORMAT_MAGIC_SIZE &&
                            magic[0] == (byte)'V' && magic[1] == (byte)'R' && magic[2] == (byte)'F' && magic[3] == (byte)'1' &&
                            BinaryPrimitives.ReadInt32BigEndian(magic[4..]) == 1)
                        {
                            probe.Seek(FORMAT_MAGIC_SIZE, SeekOrigin.Begin);
                            if (probe.Read(_header, 0, INDEX_SIZE) == INDEX_SIZE)
                            {
                                // 尾部扇区向上取整对齐：新数据写到旧数据之后，不覆盖已有 chunk
                                long dataBytes = probe.Length - HEADER_SIZE;
                                _nextFreeSector = (uint)((dataBytes + Constants.SECTOR_SIZE - 1) / Constants.SECTOR_SIZE);
                                resume = true;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 旧文件不可读 → 按损坏处理回退重建；存档是优化，非正确性依赖
            }
        }

        // FileShare.ReadWrite：读路径（TryLoadVoxelChunk）需与写并发打开同一文件
        _stream = new FileStream(filePath, resume ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

        if (!resume)
        {
            // 全新文件：写版本头（magic "VRF1" + version = 1，big-endian）+ 空索引区
            Span<byte> magic = stackalloc byte[FORMAT_MAGIC_SIZE];
            magic[0] = (byte)'V'; magic[1] = (byte)'R'; magic[2] = (byte)'F'; magic[3] = (byte)'1';
            BinaryPrimitives.WriteInt32BigEndian(magic[4..], 1);
            _stream.Write(magic);
            _stream.Write(_header, 0, INDEX_SIZE);
        }
    }

    public void WriteVoxelChunk(int localX, int localY, int localZ, byte[] compressedData)
    {
        int totalBytes = 4 + compressedData.Length;
        byte sectorCount = (byte)Math.Ceiling(totalBytes / (double)Constants.SECTOR_SIZE);

        int headerOffset = (localX + localY * Constants.REGION_SIZE + localZ * Constants.REGION_SIZE * Constants.REGION_SIZE) * 4;

        // 续写策略：旧条目容量足够 → 复用旧扇区（防同一 chunk 反复保存使文件无限膨胀）；
        // 否则追加到文件尾（保留旧 chunk 数据，索引更新为新位置，旧扇区成为孤儿但无害）。
        uint sectorOffset = ReadSectorOffset(_header, headerOffset);
        bool reuse = _header[headerOffset + 3] >= sectorCount;
        if (!reuse)
        {
            sectorOffset = _nextFreeSector;
            _nextFreeSector += sectorCount;
        }

        long writePos = HEADER_SIZE + (long)sectorOffset * Constants.SECTOR_SIZE;
        _stream.Seek(writePos, SeekOrigin.Begin);

        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, compressedData.Length);
        _stream.Write(lenBuf);
        _stream.Write(compressedData);

        // 补0对齐
        int padding = sectorCount * Constants.SECTOR_SIZE - totalBytes;
        if (padding > 0)
            _stream.Write(new byte[padding], 0, padding);

        _header[headerOffset + 0] = (byte)((sectorOffset >> 16) & 0xFF);
        _header[headerOffset + 1] = (byte)((sectorOffset >> 8) & 0xFF);
        _header[headerOffset + 2] = (byte)(sectorOffset & 0xFF);
        _header[headerOffset + 3] = sectorCount;
    }

    public void Flush()
    {
        _stream.Seek(FORMAT_MAGIC_SIZE, SeekOrigin.Begin); // 版本头在构造时已写，此处只重写索引区
        _stream.Write(_header, 0, INDEX_SIZE);
        _stream.Flush(true); // fsync确保落盘
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream?.Flush();
            _stream?.Dispose();
            _disposed = true;
        }
    }
}

public static class ZlibChunkCompressor
{
    private const int RawDataSize = Constants.CHUNK_VOLUME * sizeof(uint);

    // v2/v3 载荷：解压后的字节布局（块数据段仍为 v1 的 native 端序线性布局）：
    //   [0..1]    magic 'V' '2' / 'V' '3'
    //   [2..5]    uint32 tileCount（大端）
    //   [6..]     tileCount × TileRecord（全部大端）：
    //               v2：ushort key | uint genome | int generation | int growthTime（SingleToInt32Bits）→ 14B
    //               v3：ushort key | uint genome | int generation（GrowthTime 退役）→ 10B
    //   [..]      uint[CHUNK_VOLUME] 块数据（16384 字节）
    private const int V2_HEADER_SIZE = 6;              // magic(2) + tileCount(4)（v2/v3 共用）
    private const int V2_TILE_RECORD_SIZE = 14;        // 2 + 4 + 4 + 4（v2 含 GrowthTime）
    private const int V3_HEADER_SIZE = 6;              // magic(2) + tileCount(4)
    private const int V3_TILE_RECORD_SIZE = 10;        // 2 + 4 + 4（v3 移除 GrowthTime）
    private const int V2_MAGIC0 = (byte)'V';
    private const int V2_MAGIC1 = (byte)'2';
    private const int V3_MAGIC0 = (byte)'V';
    private const int V3_MAGIC1 = (byte)'3';

    public static byte[] Compress(uint[,,] chunkData, System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Fastest)
    {
        if (chunkData.Length != Constants.CHUNK_VOLUME)
            throw new ArgumentException($"Expected uint[{Constants.CHUNK_SIZE},{Constants.CHUNK_SIZE},{Constants.CHUNK_SIZE}]");

        uint[] chunk = new uint[Constants.CHUNK_VOLUME]; // CHUNK_VOLUME elements

        // 块内线性索引：x 高 8 位、y 中 4 位、z 低 4 位（与 .vrf 扇区布局一致）
        static int Index(int x, int y, int z) => (x << (Constants.CHUNK_SIZE_LOG2 * 2)) | (y << Constants.CHUNK_SIZE_LOG2) | z;

        for (int z = 0; z < Constants.CHUNK_SIZE; z++)
            for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                for (int x = 0; x < Constants.CHUNK_SIZE; x++)
                {
                    chunk[Index(x, y, z)] = chunkData[x, y, z];
                }

        ReadOnlySpan<byte> rawBytes = MemoryMarshal.AsBytes(chunk.AsSpan());

        // 2. 使用 MemoryStream + DeflateStream 进行压缩
        using var output = new MemoryStream(RawDataSize); // 预分配原始大小作为初始容量
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(rawBytes);
        }

        return output.ToArray();
    }

    // v3 压缩：始终产出 v3 载荷（0 tile 也写 magic + tileCount=0），全档统一 v3 格式
    public static byte[] Compress(uint[,,] chunkData, TileSaveRecord[] tiles, System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Fastest)
    {
        if (chunkData.Length != Constants.CHUNK_VOLUME)
            throw new ArgumentException($"Expected uint[{Constants.CHUNK_SIZE},{Constants.CHUNK_SIZE},{Constants.CHUNK_SIZE}]");

        if (tiles == null) tiles = Array.Empty<TileSaveRecord>();

        // 1. 组装 v3 载荷：magic + tileCount + 记录（大端）+ 块数据（native 端序，与 v1 一致）
        int payloadSize = V3_HEADER_SIZE + tiles.Length * V3_TILE_RECORD_SIZE + RawDataSize;
        byte[] payload = new byte[payloadSize];
        payload[0] = (byte)V3_MAGIC0;
        payload[1] = (byte)V3_MAGIC1;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2), tiles.Length);

        int offset = V3_HEADER_SIZE;
        foreach (var t in tiles)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), t.Key);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset + 2), t.GenomeValue);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset + 6), t.Generation);
            offset += V3_TILE_RECORD_SIZE;
        }

        // 块数据：与 v1 相同的线性索引布局
        uint[] chunk = new uint[Constants.CHUNK_VOLUME];
        static int Index(int x, int y, int z) => (x << (Constants.CHUNK_SIZE_LOG2 * 2)) | (y << Constants.CHUNK_SIZE_LOG2) | z;
        for (int z = 0; z < Constants.CHUNK_SIZE; z++)
            for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                for (int x = 0; x < Constants.CHUNK_SIZE; x++)
                    chunk[Index(x, y, z)] = chunkData[x, y, z];

        MemoryMarshal.AsBytes(chunk.AsSpan()).CopyTo(payload.AsSpan(offset));

        // 2. deflate 压缩（raw deflate，与 v1 一致）
        using var output = new MemoryStream(payloadSize / 2 + 64);
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return output.ToArray();
    }

    // 解压载荷的解析结果：块数据 + tile 快照（v1 旧档 Tiles 为空数组）
    public struct ChunkPayload
    {
        public uint[,,] Blocks;
        public TileSaveRecord[] Tiles;
    }

    // 解压并自动判别 v1/v2/v3 载荷：长度 == RawDataSize → v1 纯块数据（Tiles 空）；
    // magic "V2" → v2（记录 14B，含 GrowthTime，已退役读出后丢弃）；magic "V3" → v3（记录 10B）；
    // 长度校验按各自记录尺寸；其余视为损坏抛 InvalidDataException。
    public static ChunkPayload DecompressPayload(byte[] compressed)
    {
        byte[] raw;
        using (var input = new MemoryStream(compressed))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output); // 解压全部字节（v2/v3 载荷比 RawDataSize 长）
            raw = output.ToArray();
        }

        // v1 旧档：解压后即纯块数据（16384 字节），无 tile 段
        if (raw.Length == RawDataSize)
        {
            return new ChunkPayload
            {
                Blocks = ToBlocks(raw, 0, RawDataSize),
                Tiles = Array.Empty<TileSaveRecord>()
            };
        }

        // v2 旧档：magic 'V''2' + tileCount + tileCount×14B 记录 + 块数据（GrowthTime 读出后丢弃）
        if (raw.Length > RawDataSize && raw[0] == V2_MAGIC0 && raw[1] == V2_MAGIC1)
        {
            int tileCount = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(2));
            if (tileCount < 0)
                throw new InvalidDataException($"v2 tileCount 非法：{tileCount}");
            int expected = V2_HEADER_SIZE + tileCount * V2_TILE_RECORD_SIZE + RawDataSize;
            if (raw.Length != expected)
                throw new InvalidDataException($"v2 载荷长度不符：got {raw.Length}, expected {expected}");

            TileSaveRecord[] tiles = new TileSaveRecord[tileCount];
            int offset = V2_HEADER_SIZE;
            for (int i = 0; i < tileCount; i++)
            {
                tiles[i] = new TileSaveRecord
                {
                    Key = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset)),
                    GenomeValue = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 2)),
                    Generation = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(offset + 6))
                    // v2 第 4 段（offset+10）为 GrowthTime（SingleToInt32Bits），已退役，读出后直接丢弃
                };
                offset += V2_TILE_RECORD_SIZE;
            }

            return new ChunkPayload
            {
                Blocks = ToBlocks(raw, offset, RawDataSize),
                Tiles = tiles
            };
        }

        // v3：magic 'V''3' + tileCount + tileCount×10B 记录 + 块数据
        if (raw.Length > RawDataSize && raw[0] == V3_MAGIC0 && raw[1] == V3_MAGIC1)
        {
            int tileCount = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(2));
            if (tileCount < 0)
                throw new InvalidDataException($"v3 tileCount 非法：{tileCount}");
            int expected = V3_HEADER_SIZE + tileCount * V3_TILE_RECORD_SIZE + RawDataSize;
            if (raw.Length != expected)
                throw new InvalidDataException($"v3 载荷长度不符：got {raw.Length}, expected {expected}");

            TileSaveRecord[] tiles = new TileSaveRecord[tileCount];
            int offset = V3_HEADER_SIZE;
            for (int i = 0; i < tileCount; i++)
            {
                tiles[i] = new TileSaveRecord
                {
                    Key = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset)),
                    GenomeValue = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 2)),
                    Generation = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(offset + 6))
                };
                offset += V3_TILE_RECORD_SIZE;
            }

            return new ChunkPayload
            {
                Blocks = ToBlocks(raw, offset, RawDataSize),
                Tiles = tiles
            };
        }

        throw new InvalidDataException($"未知存档载荷：解压后长度 {raw.Length}");
    }

    // 解压：与 Compress 对称（raw deflate，非 zlib 封装）
    public static uint[,,] Decompress(byte[] compressed)
    {
        uint[] rawResult = new uint[Constants.CHUNK_VOLUME];
        Span<byte> rawBytes = MemoryMarshal.AsBytes(rawResult.AsSpan());

        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        int totalRead = 0;
        while (totalRead < RawDataSize)
        {
            int read = deflate.Read(rawBytes.Slice(totalRead));
            if (read == 0) break; // 流提前结束
            totalRead += read;
        }

        if (totalRead != RawDataSize)
            throw new InvalidDataException($"Decompressed size mismatch: got {totalRead}, expected {RawDataSize}");

        uint[,,] result = new uint[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
        for (int i = 0; i < Constants.CHUNK_VOLUME; i++)
        {
            result[i >> (Constants.CHUNK_SIZE_LOG2 * 2), (i >> Constants.CHUNK_SIZE_LOG2) & (Constants.CHUNK_SIZE - 1), i & (Constants.CHUNK_SIZE - 1)] = rawResult[i];
        }

        return result;
    }

    // 从载荷字节段还原 uint[,,]（native 端序，与写路径 MemoryMarshal.AsBytes 对称）
    private static uint[,,] ToBlocks(byte[] raw, int offset, int byteCount)
    {
        uint[] rawResult = new uint[byteCount / sizeof(uint)];
        raw.AsSpan(offset, byteCount).CopyTo(MemoryMarshal.AsBytes(rawResult.AsSpan()));

        uint[,,] result = new uint[Constants.CHUNK_SIZE, Constants.CHUNK_SIZE, Constants.CHUNK_SIZE];
        for (int i = 0; i < Constants.CHUNK_VOLUME; i++)
        {
            result[i >> (Constants.CHUNK_SIZE_LOG2 * 2), (i >> Constants.CHUNK_SIZE_LOG2) & (Constants.CHUNK_SIZE - 1), i & (Constants.CHUNK_SIZE - 1)] = rawResult[i];
        }
        return result;
    }
}
