using SimpleMCPBridge.Runtime;
using System.IO;
using UnityEngine;

namespace SimpleMCPBridge
{
    /// <summary>
    /// bridge-config.json 加载策略：
    ///   Editor → 从项目文件 Assets/SimpleMCPBridge/bridge-config.json 读取
    ///   Player → 首次启动从 Resources 拷贝到 persistentDataPath，之后从 persistentDataPath 读取
    ///   如果目标文件不存在，回退到 Resources 内嵌默认值。
    ///
    /// 用户修改配置后需重启 App 生效。
    /// </summary>
    public static class BridgeConfig
    {
        private const string CONFIG_FILE = "bridge-config.json";
        private const string RESOURCE_NAME = "bridge-config";

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
        /// 启动时调用：确保 Player 设备上有可写的配置文件副本。
        /// Editor 下不做任何操作（直接读项目文件）。
        /// </summary>
        public static void EnsureConfigOnDevice()
        {
#if !UNITY_EDITOR
            string path = GetPersistentPath();

            var textAsset = Resources.Load<TextAsset>(RESOURCE_NAME);
            if (textAsset == null)
            {
                Debug.LogWarning("[BridgeConfig] Default config not found in Resources. Using hardcoded defaults.");
                return;
            }

            Directory.CreateDirectory(Application.persistentDataPath);

            // Overwrite if content changed (e.g. useTls was added in a new build)
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (existing.Trim() == textAsset.text.Trim())
                    return;
                File.WriteAllText(path, textAsset.text);
                DebugUtils.Log("[BridgeConfig] Updated config (content changed)");
            }
            else
            {
                File.WriteAllText(path, textAsset.text);
                DebugUtils.Log($"[BridgeConfig] Copied default config to {path}");
            }
#endif
        }

        /// <summary>
        /// 读取配置。
        ///   Editor → 从项目文件 Assets/SimpleMCPBridge/bridge-config.json 读取
        ///   Player → Application.persistentDataPath/bridge-config.json
        /// 都不存在时回退到 Resources。
        /// </summary>
        public static bool LoadConfig(out string ip, out int port)
        {
            ip = "127.0.0.1";
            port = 45678;

            string json = null;

#if UNITY_EDITOR
            // Editor: 读取项目 Assets/SimpleMCPBridge/bridge-config.json
            string editorPath = Path.Combine(Application.dataPath, "SimpleMCPBridge", CONFIG_FILE);
            if (File.Exists(editorPath))
                json = File.ReadAllText(editorPath);
#else
            // Player: 从 persistentDataPath 读取（EnsureConfigOnDevice 已保证存在）
            string persistentPath = GetPersistentPath();
            if (File.Exists(persistentPath))
                json = File.ReadAllText(persistentPath);
#endif

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


        private static string GetPersistentPath()
        {
            return Path.Combine(Application.persistentDataPath, CONFIG_FILE);
        }
    }
}
