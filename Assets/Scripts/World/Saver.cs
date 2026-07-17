using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

using UnityEngine;

public class Saver
{
    private Dictionary<Vector3Int, SimpleRegionWriter> _regionWriters = new Dictionary<Vector3Int, SimpleRegionWriter>();
    private string _path;

    public Saver(string path) {  _path = path; }

    public void SaveVoxelChunk(VCPosInWorld vcPos, uint[,,] chunkData)
    {
        byte[] data = ZlibChunkCompressor.Compress(chunkData, System.IO.Compression.CompressionLevel.Fastest);

        _regionWriters.TryGetValue(vcPos, out var writer);

        if (writer == null)
        {
            Vector3Int regionPos = new Vector3Int(vcPos.X >> 5, vcPos.Y >> 5, vcPos.Z >> 5);

            writer = new SimpleRegionWriter(_path, regionPos);
            _regionWriters[regionPos] = writer;
        }

        writer.WriteVoxelChunk(vcPos.X & 31, vcPos.Y & 31, vcPos.Z & 31, data);
        writer.Flush();
    }
    public void SaveVoxelChunk(VCPosInWorld vcPos, Block[,,] chunkData)
    {
        byte[] data = ZlibChunkCompressor.Compress(chunkData, System.IO.Compression.CompressionLevel.Fastest);

        Vector3Int regionPos = new Vector3Int(vcPos.X >> 5, vcPos.Y >> 5, vcPos.Z >> 5);

        _regionWriters.TryGetValue(regionPos, out var writer);

        if (writer == null)
        {
            writer = new SimpleRegionWriter(_path, regionPos);
            _regionWriters[regionPos] = writer;
        }

        writer.WriteVoxelChunk(vcPos.X & 31, vcPos.Y & 31, vcPos.Z & 31, data);
        writer.Flush();
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

    public SimpleRegionWriter(string path, Vector3Int regionPos)
    {
        _regionPos = regionPos;

        path = $"{Application.persistentDataPath}/{path}/r.{regionPos.x}.{regionPos.y}.{regionPos.z}.vrf";

        // Debug.Log(path);

        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
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