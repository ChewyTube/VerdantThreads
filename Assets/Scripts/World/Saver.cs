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
}

//public class AsyncSaver : IDisposable, IAsyncDisposable
//{
//    private readonly ConcurrentQueue<SaveTask> _queue = new();
//    private readonly SemaphoreSlim _signal = new(0);
//    private volatile bool _completed;
//    private Task _workerTask;
//    private readonly string _path;
//    private bool _disposed;

//    private const int MAX_QUEUE_SIZE = 1024;

//    public AsyncSaver(string path)
//    {
//        _path = path;
//        _workerTask = Task.Run(SaveLoop);
//    }
//    public async Task EnqueueSaveAsync(VCPosInWorld vcPos, Block[,,] chunkData)
//    {
//        if (_completed) throw new InvalidOperationException("Saver is already disposed.");

//        while (_queue.Count >= MAX_QUEUE_SIZE)
//        {
//            await Task.Yield();
//        }

//        uint[,,] rawCopy = new uint[16, 16, 16];
//        Buffer.BlockCopy(chunkData, 0, rawCopy, 0, 4096 * sizeof(uint));

//        var task = new SaveTask
//        {
//            RegionPos = new Vector3Int(vcPos.X >> 5, vcPos.Y >> 5, vcPos.Z >> 5),
//            LocalX = vcPos.X & 31,
//            LocalY = vcPos.Y & 31,
//            LocalZ = vcPos.Z & 31,
//            RawChunkData = rawCopy
//        };

//        _queue.Enqueue(task);
//        _signal.Release(); 
//    }

//    private async Task SaveLoop()
//    {
//        var readers = new Dictionary<Vector3Int, SimpleRegionWriter>();

//        while (true)
//        {
//            // 等待信号量，避免空转消耗 CPU
//            await _signal.WaitAsync();

//            if (_queue.TryDequeue(out var task))
//            {
//                try
//                {
//                    byte[] compressed = ZlibChunkCompressor.Compress(task.RawChunkData);

//                    if (!readers.TryGetValue(task.RegionPos, out var writer))
//                    {
//                        writer = new SimpleRegionWriter(_path, task.RegionPos);
//                        readers[task.RegionPos] = writer;
//                    }
//                    writer.WriteVoxelChunk(task.LocalX, task.LocalY, task.LocalZ, compressed);
//                    writer.Flush();
//                }
//                catch (Exception e) { Debug.LogException(e); }
//            }
//            else if (_completed)
//            {
//                break; 
//            }
//        }

//        foreach (var w in readers.Values) w.Dispose();
//    }

//    public async ValueTask DisposeAsync()
//    {
//        if (_disposed) return;
//        _disposed = true;
//        _completed = true;

//        _signal.Release(); // 唤醒可能正在 WaitAsync 的后台线程
//        if (_workerTask != null) await _workerTask;
//        _signal.Dispose();
//        GC.SuppressFinalize(this);
//    }

//    public void Dispose()
//    {
//        if (_disposed) return;
//        _disposed = true;
//        _completed = true;

//        _signal.Release();
//        try { _workerTask?.Wait(); } catch (AggregateException) { }
//        _signal.Dispose();
//        GC.SuppressFinalize(this);
//    }
//}
public class Saver
{
    private readonly ConcurrentQueue<SaveTask> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue); // 显式 maxCount，避免依赖单参构造器在不同 BCL 实现下的 maxCount 语义
    private volatile bool _completed;
    private Task _workerTask;
    private bool _disposed;
    private readonly string _path;
    private string _saveRoot; // 保存根目录（主线程初始化，Application.persistentDataPath 仅主线程可访问）

    public Saver(string path) {  _path = path; _workerTask = Task.Run(SaveLoop); }

    // 必须在主线程调用（Application.persistentDataPath 不能从后台线程读取）
    public void Initialize()
    {
        _saveRoot = Application.persistentDataPath;
    }

    public void SaveVoxelChunk(VCPosInWorld vcPos, uint[,,] chunkData)
    {
        if (_completed) throw new InvalidOperationException("Saver 已释放，不能继续入队保存任务。");

        uint[,,] rawCopy = new uint[16, 16, 16];
        Buffer.BlockCopy(chunkData, 0, rawCopy, 0, 4096 * sizeof(uint)); // 复制一份，防止调用方复用数组

        _queue.Enqueue(new SaveTask
        {
            RegionPos = new Vector3Int(vcPos.X >> 5, vcPos.Y >> 5, vcPos.Z >> 5),
            LocalX = vcPos.X & 31,
            LocalY = vcPos.Y & 31,
            LocalZ = vcPos.Z & 31,
            RawChunkData = rawCopy
        });
        _signal.Release();
    }

    public void SaveVoxelChunk(VCPosInWorld vcPos, Block[,,] chunkData)
    {
        if (_completed) throw new InvalidOperationException("Saver 已释放，不能继续入队保存任务。");

        uint[,,] rawCopy = new uint[16, 16, 16];
        // Buffer.BlockCopy 仅接受基元类型数组，Block 是 struct（即使单 uint 可 blittable）也会抛 ArgumentException，故手动转换
        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < 16; z++)
                    rawCopy[x, y, z] = (uint)chunkData[x, y, z];

        _queue.Enqueue(new SaveTask
        {
            RegionPos = new Vector3Int(vcPos.X >> 5, vcPos.Y >> 5, vcPos.Z >> 5),
            LocalX = vcPos.X & 31,
            LocalY = vcPos.Y & 31,
            LocalZ = vcPos.Z & 31,
            RawChunkData = rawCopy
        });
        _signal.Release();
    }

    private async Task SaveLoop()
    {
        var writers = new Dictionary<Vector3Int, SimpleRegionWriter>();

        while (true)
        {
            await _signal.WaitAsync();

            if (_queue.TryDequeue(out var task))
            {
                try
                {
                    byte[] compressed = ZlibChunkCompressor.Compress(task.RawChunkData, System.IO.Compression.CompressionLevel.Fastest);

                    if (!writers.TryGetValue(task.RegionPos, out var writer))
                    {
                        writer = new SimpleRegionWriter(_saveRoot, _path, task.RegionPos);
                        writers[task.RegionPos] = writer;
                    }

                    writer.WriteVoxelChunk(task.LocalX, task.LocalY, task.LocalZ, compressed);
                    writer.Flush();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            else if (_completed)
            {
                break; // 队列已空且已请求结束
            }
        }

        foreach (var w in writers.Values) w.Dispose();
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
    }
}

public class SimpleRegionWriter : IDisposable
{
    private const int SECTOR_SIZE = 4096;
    private const int HEADER_SIZE = 32 * 32 * 32 * 4;

    private readonly FileStream _stream;
    private readonly byte[] _header = new byte[HEADER_SIZE];

    private readonly Vector3Int _regionPos;

    private uint _nextFreeSector = 0;

    private bool _disposed;

    // saveRoot/subPath 须在主线程解析后传入（后台线程不能访问 Application.persistentDataPath）
    public SimpleRegionWriter(string saveRoot, string subPath, Vector3Int regionPos)
    {
        _regionPos = regionPos;

        string filePath = $"{saveRoot}/{subPath}/r.{regionPos.x}.{regionPos.y}.{regionPos.z}.vrf";

        string dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _stream.Write(_header, 0, HEADER_SIZE);
    }

    public void WriteVoxelChunk(int localX, int localY, int localZ, byte[] compressedData)
    {
        int totalBytes = 4 + compressedData.Length;
        byte sectorCount = (byte)Math.Ceiling(totalBytes / (double)SECTOR_SIZE);

        long writePos = HEADER_SIZE + (long)_nextFreeSector * SECTOR_SIZE;
        _stream.Seek(writePos, SeekOrigin.Begin);

        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, compressedData.Length);
        _stream.Write(lenBuf);
        _stream.Write(compressedData);

        // 补0对齐
        int padding = sectorCount * SECTOR_SIZE - totalBytes;
        if (padding > 0)
            _stream.Write(new byte[padding], 0, padding);

        int headerOffset = (localX + localY * 32 + localZ * 32 * 32) * 4;
        _header[headerOffset + 0] = (byte)((_nextFreeSector >> 16) & 0xFF);
        _header[headerOffset + 1] = (byte)((_nextFreeSector >> 8) & 0xFF);
        _header[headerOffset + 2] = (byte)(_nextFreeSector & 0xFF);
        _header[headerOffset + 3] = sectorCount;

        _nextFreeSector += sectorCount;
    }

    public void Flush()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(_header, 0, HEADER_SIZE);
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
    private const int RawDataSize = 16 * 16 * 16 * sizeof(uint);

    public static byte[] Compress(uint[,,] chunkData, System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Fastest)
    {
        if (chunkData.Length != 4096)
            throw new ArgumentException("Expected uint[16,16,16]");

        uint[] chunk = new uint[16 * 16 * 16]; // 4096 elements

        static int Index(int x, int y, int z) => (x << 8) | (y << 4) | z;

        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
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
    public static byte[] Compress(Block[,,] chunkData, System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Fastest)
    {
        if (chunkData.Length != 4096)
            throw new ArgumentException("Expected uint[16,16,16]");

        uint[] chunk = new uint[16 * 16 * 16]; // 4096 elements

        static int Index(int x, int y, int z) => (x << 8) | (y << 4) | z;

        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    chunk[Index(x, y, z)] = (uint)chunkData[x, y, z];
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

    public static uint[,,] Decompress(byte[] compressed)
    {
        uint[] rawResult = new uint[16 *  16 * 16];
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

        uint[,,] result = new uint[16, 16, 16];
        for (int i = 0; i < 4096; i++)
        {
            result[i >> 8, (i >> 4) & 0xF, i & 0xF] = rawResult[i];
        }

        return result;
    }
}