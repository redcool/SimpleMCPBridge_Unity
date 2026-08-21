using SimpleMCPBridge.Runtime;
using System.Collections.Generic;
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
            /// <summary>桥 id 友好段（<engine>-<project>-<guid>）。留空回退 Application.productName（slug 化）。</summary>
            public string projectName = "";
            /// <summary>追加的 scene.call_component_method 拦截项：可含 "MethodName" 或 "TypeName.MethodName"。空数组 = 无追加。</summary>
            public string[] methodBlocklist = new string[0];
            /// <summary>scene.call_component_method 白名单：非空 = 白名单模式（同上两种格式）。空数组 = 关闭。</summary>
            public string[] methodAllowlist = new string[0];
        }

        /// <summary>Shared encryption key for AES-256-CBC payload encryption. Empty = no encryption.</summary>
        public static string EncryptionKey { get; private set; } = "";

        /// <summary>桥 id 友好段（projectName 配置 → 回退 Application.productName，Slugify 后使用）。</summary>
        public static string ProjectName { get; private set; } = "";

        /// <summary>追加拦截项（来自 config.methodBlocklist，已清洗）。代码默认黑名单始终生效、不在此处。</summary>
        public static string[] MethodBlocklistExtra { get; private set; } = new string[0];

        /// <summary>白名单项（来自 config.methodAllowlist，已清洗）。空数组 = 白名单模式关闭。</summary>
        public static string[] MethodAllowlist { get; private set; } = new string[0];

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
                ProjectName = config.projectName ?? "";
                MethodBlocklistExtra = SanitizeList(config.methodBlocklist);
                MethodAllowlist = SanitizeList(config.methodAllowlist);
                return true;
            }
            catch (System.Exception ex)
            {
                DebugUtils.LogWarning($"[BridgeConfig] Failed to parse config: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清洗配置数组：去首尾空白、丢弃空/null 项、保留原大小写（下游比较一律大小写不敏感）。
        /// JsonUtility 对缺失的 JSON key 保留字段默认值 → 旧配置（无新 key）安全回退为空数组。
        /// </summary>
        private static string[] SanitizeList(string[] raw)
        {
            if (raw == null || raw.Length == 0) return new string[0];
            var list = new List<string>(raw.Length);
            foreach (var item in raw)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                list.Add(item.Trim());
            }
            return list.ToArray();
        }

        /// <summary>
        /// 桥 id 友好段规范化（与 Godot 桥 MCPBridge.gd._slugify 规则一致）：
        /// 仅保留 ASCII a-z / 0-9，大写转小写，其余 → '-'，压缩连续 '-'，去首尾 '-'；结果为空 → "unknown"。
        /// </summary>
        public static string Slugify(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9')
                    sb.Append(c);
                else if (c >= 'A' && c <= 'Z')
                    sb.Append(char.ToLowerInvariant(c));
                else
                    sb.Append('-');
            }
            string slug = sb.ToString();
            while (slug.Contains("--"))
                slug = slug.Replace("--", "-");
            slug = slug.Trim('-');
            return slug.Length == 0 ? "unknown" : slug;
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
