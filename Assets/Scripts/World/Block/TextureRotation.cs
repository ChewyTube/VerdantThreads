// 贴图随机旋转：由「世界坐标 + 固定 seed」确定性决定朝向（纯整数运算，无共享状态，
// 后台 mesh 构建线程可安全调用）。设计见 docs/design/TEXTURE_ROTATION.md 2.2。
// 旋转只影响 UV 角点循环位移（渲染层），不涉及块值/存档。
public static class TextureRotation
{
    // 2.2 哈希公式：golden-ratio 混合（参考 PeaClumpFeature 的 2654435761u 风格），返回 0~3 → 0°/90°/180°/270°。
    // 必须用世界坐标 (wx, wy, wz)，保证跨 chunk 边界、chunk 卸载重载后朝向不变。
    public static int GetRotation(int seed, int wx, int wy, int wz)
    {
        // 注意：C# 中 int * uint 会提升为 long，必须写成 (uint)wx * 常量 的 uint 环绕乘法形式
        uint h = (uint)seed;
        h = (h ^ ((uint)wx * 0x9E3779B9u)) ^ ((uint)wy * 0x85EBCA77u) ^ ((uint)wz * 0xC2B2AE3Du);
        h ^= h >> 15; h *= 0x85EBCA77u; h ^= h >> 13;
        return (int)(h & 3);
    }

    // 旋转白名单（哪些方块/面旋转）：各向同性纹理（石头/泥土/基岩等）旋转无视觉差异，默认不转。
    // 本期只开：草方块顶面（Grass + Up，用户点名）。草侧面/底面不转，Leaves/Log 后续可选，一律 false。
    public static bool ShouldRotateFace(BlockType bt, Direction dir)
    {
        if (bt == BlockType.Grass && dir == Direction.Up) return true;
        return false;
    }
}
