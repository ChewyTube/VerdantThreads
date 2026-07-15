using System.Collections.Generic;
using UnityEngine;

public class DataBuffer : MonoBehaviour 
{
    public static DataBuffer Instance { get; private set; }

    public Dictionary<(BlockType, int), Vector2> Block2uvs = new Dictionary<(BlockType, int), Vector2>();

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
    }
}