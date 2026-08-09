using System;
using System.Collections.Generic;
using UnityEngine;

public readonly struct VCPosInWorld : IEquatable<VCPosInWorld>
{
    public readonly int X, Y, Z;
    public VCPosInWorld(int x, int y, int z) => (X, Y, Z) = (x, y, z);

    public static implicit operator Vector3Int(VCPosInWorld pos) => new(pos.X, pos.Y, pos.Z);

    public static bool operator ==(VCPosInWorld left, VCPosInWorld right) => left.Equals(right);
    public static bool operator !=(VCPosInWorld left, VCPosInWorld right) => !left.Equals(right);

    public bool Equals(VCPosInWorld other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object obj) => obj is VCPosInWorld other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = X * 73856093;
            hash ^= Y * 19349663;
            hash ^= Z * 83492791;
            return hash;
        }
    }

    public override string ToString() => $"VC({X}, {Y}, {Z})";
}

public readonly struct BlockPosInVoxelChunk : IEquatable<BlockPosInVoxelChunk>
{
    public readonly int X, Y, Z;
    private BlockPosInVoxelChunk(int x, int y, int z) => (X, Y, Z) = (x, y, z);

    public static implicit operator Vector3Int(BlockPosInVoxelChunk pos) => new(pos.X, pos.Y, pos.Z);

    public static bool TryCreate(int x, int y, int z, out BlockPosInVoxelChunk result)
    {
        if ((uint)x < Constants.CHUNK_SIZE &&
            (uint)y < Constants.CHUNK_SIZE &&
            (uint)z < Constants.CHUNK_SIZE)
        {
            result = new BlockPosInVoxelChunk(x, y, z);
            return true;
        }
        result = default;
        return false;
    }

    public static bool operator ==(BlockPosInVoxelChunk left, BlockPosInVoxelChunk right) => left.Equals(right);
    public static bool operator !=(BlockPosInVoxelChunk left, BlockPosInVoxelChunk right) => !left.Equals(right);

    public bool Equals(BlockPosInVoxelChunk other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object obj) => obj is BlockPosInVoxelChunk other && Equals(other);

    public override int GetHashCode()
    {
        // 块内坐标 → 单 int 哈希（x 高 8 位、y 中 4 位、z 低 4 位，与 CHUNK_SIZE_LOG2 一致）
        return (X << (Constants.CHUNK_SIZE_LOG2 * 2)) | (Y << Constants.CHUNK_SIZE_LOG2) | Z;
    }

    public override string ToString() => $"Local({X}, {Y}, {Z})";
}

public readonly struct BlockPosInWorld : IEquatable<BlockPosInWorld>
{
    public readonly int X, Y, Z;
    public BlockPosInWorld(int x, int y, int z) => (X, Y, Z) = (x, y, z);

    public static implicit operator Vector3Int(BlockPosInWorld pos) => new(pos.X, pos.Y, pos.Z);

    public VCPosInWorld GetCorrespondingVCPos()
        => new(X >> Constants.CHUNK_SIZE_LOG2, Y >> Constants.CHUNK_SIZE_LOG2, Z >> Constants.CHUNK_SIZE_LOG2);

    public BlockPosInVoxelChunk GetCorrespondingPosInVC()
    {
        const int mask = Constants.CHUNK_SIZE - 1;
        if (BlockPosInVoxelChunk.TryCreate(X & mask, Y & mask, Z & mask, out var localPos))
            return localPos;

        throw new Exception($"Invalid chunk-local pos: ({X},{Y},{Z})");
    }

    public static bool operator ==(BlockPosInWorld left, BlockPosInWorld right) => left.Equals(right);
    public static bool operator !=(BlockPosInWorld left, BlockPosInWorld right) => !left.Equals(right);

    public bool Equals(BlockPosInWorld other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object obj) => obj is BlockPosInWorld other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = X * 73856093;
            hash ^= Y * 19349663;
            hash ^= Z * 83492791;
            return hash;
        }
    }

    public override string ToString() => $"World({X}, {Y}, {Z})";
}