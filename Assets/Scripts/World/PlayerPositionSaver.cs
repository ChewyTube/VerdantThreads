using System.IO;
using UnityEngine;

// 玩家位置存档：退出时保存相机（眼睛）位置，启动时读回（避免每次进游戏都从出生点坠落）。
// 简单二进制格式：3 个 float（x, y, z）。文件在 persistentDataPath/player_pos.dat。
// 掉落物持久化按设计暂不做（5 分钟自动消失，退出即重置，见 VOXEL_COLLISION.md §8.6）。
public static class PlayerPositionSaver
{
    private static readonly string FilePath = Path.Combine(Application.persistentDataPath, "player_pos.dat");

    public static void Save(Vector3 pos)
    {
        try
        {
            using (BinaryWriter w = new BinaryWriter(File.Open(FilePath, FileMode.Create)))
            {
                w.Write(pos.x);
                w.Write(pos.y);
                w.Write(pos.z);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"保存玩家位置失败：{e.Message}");
        }
    }

    public static bool TryLoad(out Vector3 pos)
    {
        pos = default;
        if (!File.Exists(FilePath)) return false;
        try
        {
            using (BinaryReader r = new BinaryReader(File.Open(FilePath, FileMode.Open)))
            {
                pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"读取玩家位置失败：{e.Message}");
            return false;
        }
    }
}