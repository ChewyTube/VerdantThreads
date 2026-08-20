using System;
using UnityEngine;

// 体素碰撞核心：AABB vs 体素世界，逐轴分解移动（MC 同款，防卡角）。
// 不依赖 Unity 物理引擎；isSolid 由调用方注入（World 提供），碰撞系统与具体世界实现解耦。
// 世界边界守卫：Y < 0 或 Y >= 世界高度视为固体（防玩家/掉落物掉出世界）。
// 见 docs/design/VOXEL_COLLISION.md
public static class VoxelCollision
{
    // 吸附到方块面时的浮点安全间隙（避免 AABB 外沿恰好落在整数边界导致采样到固体方块）
    private const float Epsilon = 0.001f;

    // 单轴移动 + 碰撞解析：把 AABB 沿 axis（0=X, 1=Y, 2=Z）移动 delta。
    // 无碰撞：moved = delta，返回 false；有碰撞：吸附到方块面，moved = 实际位移，返回 true。
    public static bool MoveAxis(Vector3 center, Vector3 halfExtents, int axis, float delta,
                                out float moved, Func<Vector3Int, bool> isSolid)
    {
        if (delta == 0f) { moved = 0f; return false; }

        Vector3 newCenter = center;
        newCenter[axis] += delta;

        if (OverlapsSolid(newCenter, halfExtents, isSolid))
        {
            // 吸附到碰撞方块面：沿移动方向取 AABB 外沿所在方块边界，再退一个安全间隙
            float sign = Mathf.Sign(delta);
            float edge = newCenter[axis] + sign * halfExtents[axis];
            float blockBoundary = sign > 0 ? Mathf.Floor(edge) : Mathf.Ceil(edge);
            float clampedCenter = blockBoundary - sign * (Epsilon + halfExtents[axis]);
            moved = clampedCenter - center[axis];
            return true;
        }

        moved = delta;
        return false;
    }

    // 完整移动（X→Y→Z 逐轴），velocity 对应轴在碰撞时归零。
    public static void Move(ref Vector3 center, Vector3 halfExtents, ref Vector3 velocity, float dt,
                            Func<Vector3Int, bool> isSolid)
    {
        if (MoveAxis(center, halfExtents, 0, velocity.x * dt, out float mx, isSolid)) velocity.x = 0f;
        center.x += mx;

        if (MoveAxis(center, halfExtents, 1, velocity.y * dt, out float my, isSolid)) velocity.y = 0f;
        center.y += my;

        if (MoveAxis(center, halfExtents, 2, velocity.z * dt, out float mz, isSolid)) velocity.z = 0f;
        center.z += mz;
    }

    // AABB 是否与任何固体方块重叠（含世界边界守卫：Y 越界视为固体）
    public static bool OverlapsSolid(Vector3 center, Vector3 halfExtents, Func<Vector3Int, bool> isSolid)
    {
        int minX = Mathf.FloorToInt(center.x - halfExtents.x);
        int maxX = Mathf.FloorToInt(center.x + halfExtents.x);
        int minY = Mathf.FloorToInt(center.y - halfExtents.y);
        int maxY = Mathf.FloorToInt(center.y + halfExtents.y);
        int minZ = Mathf.FloorToInt(center.z - halfExtents.z);
        int maxZ = Mathf.FloorToInt(center.z + halfExtents.z);

        for (int y = minY; y <= maxY; y++)
        {
            if (y < 0 || y >= Constants.CHUNK_SIZE * 16) return true; // 世界边界守卫
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (isSolid(new Vector3Int(x, y, z))) return true;
                }
            }
        }
        return false;
    }
}