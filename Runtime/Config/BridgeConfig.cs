using SimpleMCPBridge.Runtime;
using System.IO;
using UnityEngine;

namespace SimpleMCPBridge
{
    /// <summary>
    /// bridge-config.json 加载策略（UPM 包友好）：
    ///   Editor → 项目 Assets/SimpleMCPBridge-config/bridge-config.json（可写，首次自动从 Resources 拷贝）
    ///   Player → 首次启动从 Resources 拷贝到 persistentDataPath，之后从 persistentDataPath 读取
    ///   都不存在时回退到 Resources 内嵌默认值。
    ///
    /// 用户修改配置后需重启 App 生效。
    /// </summary>
    public static class BridgeConfig
    {
        private const string CONFIG_FILE = "bridge-config.json";
        private const string RESOURCE_NAME = "bridge-config";
        private const string EDITOR_CONFIG_DIR = "SimpleMCPBridge-config";

        [System.Serializable]
        public class ConfigData
        {
            public string serverIp = "127.0.0.1";
            public int serverPort = 45678;
            public string encryptionKey = "";
        }

        /// <summary>Shared encryption key for AES-256-CBC payload encryption. Empty = no encryption.</summary>
        public static string EncryptionKey { get; private set; } = "";

        /// <summary>
        /// 启动时调用：确保目标位置存在可写的配置文件副本（不存在则从 Resources 拷贝默认值）。
        /// 已存在则**不覆盖**（用户修改过的配置保持原样）。
        /// </summary>
        public static void EnsureConfigOnDevice()
        {
            string path = GetTargetPath();

            var textAsset = Resources.Load<TextAsset>(RESOURCE_NAME);
            if (textAsset == null)
            {
                Debug.LogWarning("[BridgeConfig] Default config not found in Resources. Using hardcoded defaults.");
                return;
            }

            if (File.Exists(path))
                return;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, textAsset.text);
            DebugUtils.Log($"[BridgeConfig] Created default config at {path}");

#if UNITY_EDITOR
            // 让新建的文件/目录在 Project 窗口可见（Play Mode 下跳过，避免导入抖动）
            if (!Application.isPlaying)
                UnityEditor.AssetDatabase.Refresh();
#endif
        }

        /// <summary>
        /// 读取配置。
        ///   Editor → 项目 Assets/SimpleMCPBridge-config/bridge-config.json
        ///   Player → Application.persistentDataPath/bridge-config.json
        /// 不存在时回退到 Resources。
        /// </summary>
        public static bool LoadConfig(out string ip, out int port)
        {
            ip = "127.0.0.1";
            port = 45678;

            string json = null;

            string targetPath = GetTargetPath();
            if (File.Exists(targetPath))
                json = File.ReadAllText(targetPath);

            // 回退到 Resources 内嵌默认值
            if (string.IsNullOrEmpty(json))
            {
                var textAsset = Resources.Load<TextAsset>(RESOURCE_NAME);
                if (textAsset != null)
                    json = textAsset.text;
            }

            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                var config = JsonUtility.FromJson<ConfigData>(json);
                if (config == null) return false;
                ip = config.serverIp;
                port = config.serverPort;
                EncryptionKey = config.encryptionKey ?? "";
                return true;
            }
            catch (System.Exception ex)
            {
                DebugUtils.LogWarning($"[BridgeConfig] Failed to parse config: {ex.Message}");
                return false;
            }
        }

        /// <summary>目标配置文件路径：Editor 在项目 Assets 下，Player 在 persistentDataPath。</summary>
        private static string GetTargetPath()
        {
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, EDITOR_CONFIG_DIR, CONFIG_FILE);
#else
            return GetPersistentPath();
#endif
        }

        private static string GetPersistentPath()
        {
            return Path.Combine(Application.persistentDataPath, CONFIG_FILE);
        }
    }
}
