using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldManager : MonoBehaviour
{
    public static WorldManager Instance { get; private set; }

    [SerializeField] private Material blockMaterial;

    public Material BlockMaterial => blockMaterial;

    void Awake()
    {
        // 单例初始化
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject); // 跨场景不销毁

        // 豌豆占位图：运行时拷贝图集到可写副本再画（失败会打 LogError 提示）
        if (blockMaterial != null) PeaTextures.InstallToMaterial(blockMaterial);
    }
}