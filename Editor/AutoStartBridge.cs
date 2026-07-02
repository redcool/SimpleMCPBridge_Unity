using SimpleMCPBridge.Runtime;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Auto-connects to SimpleMcpServer after script compilation (domain reload).
    /// Reads server IP/port from bridge-config.json in the package folder.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoStartBridge
    {
        private static MCPBridge _bridge;
        private static string _serverIp = "127.0.0.1";
        private static int _serverPort = 45678;

        static AutoStartBridge()
        {
            var configPath = Path.Combine(Application.dataPath, "SimpleMCPBridge", "bridge-config.json");
            try
            {
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonUtility.FromJson<BridgeConfig>(json);
                    if (config != null)
                    {
                        _serverIp = string.IsNullOrEmpty(config.serverIp) ? _serverIp : config.serverIp;
                        _serverPort = config.serverPort > 0 ? config.serverPort : _serverPort;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AutoStartBridge] Failed to read bridge-config.json: {ex.Message}");
            }

            EditorApplication.delayCall += () => TryConnect();
        }

        private static void TryConnect()
        {
            if (_bridge != null) return;

            var go = new GameObject("[SimpleMCPBridge]");
            go.hideFlags = HideFlags.HideAndDontSave;
            _bridge = go.AddComponent<MCPBridge>();
            _bridge.ConnectToServer(_serverIp, _serverPort);
            EditorApplication.update += () => _bridge.DrainQueue();
        }

        // ── Config model ──
        [System.Serializable]
        private class BridgeConfig
        {
            public string serverIp = "127.0.0.1";
            public int serverPort = 45678;
        }
    }
}
