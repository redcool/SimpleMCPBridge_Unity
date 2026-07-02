using SimpleMCPBridge.Runtime;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Editor Window for controlling the SimpleMCPBridge connection.
    /// Provides Server IP/Port, Bridge GUID display, Connect/Disconnect toggle,
    /// and real-time status (including connection failure reasons).
    /// Also drains the bridge's main-thread queue in Edit Mode.
    ///
    /// Config is stored in Assets/SimpleMCPBridge/bridge-config.json.
    /// Open via: Tools > SimpleMCPBridge
    /// </summary>
    public class MCPBridgeWindow : EditorWindow
    {
        private string _serverIp = "127.0.0.1";
        private int _serverPort = 45678;
        private string _bridgeId;
        private MCPBridge _bridge;
        private bool _isConnecting;
        private string _lastError; // displayed when connection fails

        // ── Config path ──
        private static string ConfigPath => Path.Combine(Application.dataPath, "SimpleMCPBridge", "bridge-config.json");

        // ── Window registration ──

        [MenuItem("Tools/SimpleMCPBridge")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPBridgeWindow>("SimpleMCPBridge");
            window.minSize = new Vector2(380, 300);
            window.Show();
        }

        // ── Initialization ──

        private void OnEnable()
        {
            // Generate a new GUID each time the window opens
            _bridgeId = System.Guid.NewGuid().ToString("N");

            // Load config from json file
            LoadConfig();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonUtility.FromJson<ConfigData>(json);
                    if (cfg != null)
                    {
                        _serverIp = string.IsNullOrEmpty(cfg.serverIp) ? _serverIp : cfg.serverIp;
                        _serverPort = cfg.serverPort > 0 ? cfg.serverPort : _serverPort;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[MCPBridgeWindow] Failed to load bridge-config.json: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var cfg = new ConfigData { serverIp = _serverIp, serverPort = _serverPort };
                File.WriteAllText(ConfigPath, JsonUtility.ToJson(cfg, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[MCPBridgeWindow] Failed to save bridge-config.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Editor tick. Drains the bridge's main-thread queue in Edit Mode.
        /// </summary>
        private void OnEditorUpdate()
        {
            if (_bridge != null)
            {
                _bridge.DrainQueue();
            }
        }

        // ── GUI ──

        private void OnGUI()
        {
            // Header
            EditorGUILayout.LabelField("SimpleMCPBridge for Unity", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Connect to SimpleMcpServer via WebSocket", EditorStyles.miniLabel);
            EditorGUILayout.Space(8);

            // Server IP field
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Server IP", GUILayout.Width(100));
                _serverIp = EditorGUILayout.TextField(_serverIp);
            }

            // Server Port field
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Server Port", GUILayout.Width(100));
                _serverPort = EditorGUILayout.IntField(_serverPort);
            }

            EditorGUILayout.Space(4);

            // Bridge ID (readonly)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Bridge ID", GUILayout.Width(100));
                EditorGUILayout.SelectableLabel(_bridgeId, EditorStyles.textField, GUILayout.Height(18));
            }

            EditorGUILayout.Space(8);

            // Status panel
            var connected = _bridge != null && _bridge.IsConnected;
            DrawStatusPanel(connected);

            EditorGUILayout.Space(6);

            // Connect / Disconnect button
            if (connected)
            {
                GUI.color = new Color(1f, 0.5f, 0.5f);
                if (GUILayout.Button("■  Disconnect", GUILayout.Height(36)))
                {
                    DisconnectFromServer();
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.enabled = !_isConnecting;
                GUI.color = new Color(0.5f, 1f, 0.5f);
                if (GUILayout.Button("▶  Connect to Server", GUILayout.Height(36)))
                {
                    ConnectToServer();
                }
                GUI.color = Color.white;
                GUI.enabled = true;
            }

            // Connecting indicator
            if (_isConnecting)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Connecting...", EditorStyles.miniLabel);
            }

            // Connection error display
            if (!string.IsNullOrEmpty(_lastError) && !connected && !_isConnecting)
            {
                EditorGUILayout.Space(4);
                GUI.color = new Color(1f, 0.7f, 0.7f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Connection failed:", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(_lastError, EditorStyles.miniLabel, GUILayout.Height(36));
                EditorGUILayout.EndVertical();
                GUI.color = Color.white;
            }
        }

        private void DrawStatusPanel(bool connected)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (connected && _bridge != null)
            {
                EditorGUILayout.LabelField("●  Connected", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"ws://{_bridge.Host}:{_bridge.Port}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"ID: {_bridge.BridgeId}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("○  Disconnected", EditorStyles.boldLabel);
                if (_isConnecting)
                    EditorGUILayout.LabelField("Attempting to connect...", EditorStyles.miniLabel);
                else
                    EditorGUILayout.LabelField("Click 'Connect to Server'", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ── Bridge lifecycle ──

        private void ConnectToServer()
        {
            if (_bridge != null) return;

            _isConnecting = true;
            _lastError = null;
            SaveConfig();
            Repaint();

            var go = new GameObject("[SimpleMCPBridge]");
            go.hideFlags = HideFlags.HideAndDontSave;

            _bridge = go.AddComponent<MCPBridge>();
            _bridge.BridgeId = _bridgeId;

            // Wire up status callbacks
            _bridge.OnConnectionFailed += (reason) =>
            {
                _lastError = reason;
                _isConnecting = false;
                Repaint();
                Debug.Log($"[MCPBridgeWindow] Connection failed: {reason}");
            };

            _bridge.OnConnectedSuccess += () =>
            {
                _lastError = null;
                _isConnecting = false;
                Repaint();
            };

            _bridge.ConnectToServer(_serverIp, _serverPort);
            Debug.Log($"[MCPBridgeWindow] Connecting to ws://{_serverIp}:{_serverPort} (ID: {_bridgeId})");
        }

        private void DisconnectFromServer()
        {
            if (_bridge == null) return;

            _bridge.Disconnect();

            if (_bridge.gameObject != null)
                DestroyImmediate(_bridge.gameObject);

            _bridge = null;
            _isConnecting = false;
            _lastError = null;
            Repaint();
            Debug.Log("[MCPBridgeWindow] Disconnected");
        }

        // ── Config model ──

        [System.Serializable]
        private class ConfigData
        {
            public string serverIp = "127.0.0.1";
            public int serverPort = 45678;
        }
    }
}
