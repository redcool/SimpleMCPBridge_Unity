#if UNITY_EDITOR
using SimpleMCPBridge.Runtime;
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Editor Window for controlling the SimpleMCPBridge connection.
    /// Provides Server IP/Port, Bridge GUID display, Connect/Disconnect toggle,
    /// and real-time status (including connection failure reasons).
    ///
    /// Bridge lifecycle (create Default, DrainQueue, auto-reconnect) runs in a
    /// static EditorApplication.update handler. The handler is subscribed once
    /// when the user first opens the window (ShowWindow) and cleaned up on
    /// EditorApplication.quitting — NOT tied to OnEnable/OnDisable.
    /// After domain reload, the static constructor re-subscribes automatically.
    ///
    /// This ensures the bridge keeps working even when the window is closed.
    ///
    /// Config is stored in Assets/SimpleMCPBridge/bridge-config.json.
    /// Open via: PowerUtilities > SimpleMCPBridge
    /// </summary>
    [InitializeOnLoad]
    public class MCPBridgeWindow : EditorWindow
    {
        // ── Static lifecycle (independent of window open/close) ──

        private const string k_ActivatedKey = "SimpleMCPBridge_Activated";

        private static string s_serverIp = "127.0.0.1";
        private static int s_serverPort = 45678;

        /// <summary>
        /// Persisted flag: true after user first clicks Connect.
        /// Survives domain reload. StaticUpdate no-ops until this is true.
        /// </summary>
        private static bool s_activated
        {
            get => EditorPrefs.GetBool(k_ActivatedKey, false);
            set => EditorPrefs.SetBool(k_ActivatedKey, value);
        }

        /// <summary>
        /// Static constructor: fires on every domain reload.
        /// Subscribes EditorApplication.update unconditionally (safe via -= before +=).
        /// StaticUpdate itself gates on s_activated, so it no-ops until user connects.
        /// </summary>
        static MCPBridgeWindow()
        {
            LoadStaticConfig();
            EditorApplication.update -= StaticUpdate;
            EditorApplication.update += StaticUpdate;
            EditorApplication.quitting -= OnEditorQuit;
            EditorApplication.quitting += OnEditorQuit;

            AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// Disconnect BEFORE domain reload so the old WebSocket/ReadLoopAsync
        /// is properly closed (not relying on DomainUnload which fires when
        /// the AppDomain is already halfway torn down and socket.Close() may
        /// silently fail).
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode ||
                state == PlayModeStateChange.ExitingEditMode)
            {
                MCPBridge.Default?.Disconnect();
            }
        }

        private static void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            MCPBridge.Default?.Disconnect();
        }


        private static void OnEditorQuit()
        {
            EditorApplication.update -= StaticUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AppDomain.CurrentDomain.DomainUnload -= CurrentDomain_DomainUnload;
        }

        private static void StaticUpdate()
        {
            // Until user opens window + clicks Connect, do nothing
            if (!s_activated) return;

            // Ensure Default bridge exists
            if (MCPBridge.Default == null)
            {
                var bridge = new MCPBridge();
                MCPBridge.Default = bridge;
                Runtime.AIRequest.Register(bridge);
            }

            var b = MCPBridge.Default;
            b.DrainQueue();

            // Auto-reconnect: if disconnected and user wants it
            if (!b.IsConnected && b.IsAutoReconnect)
            {
                b.ConnectToServer(s_serverIp, s_serverPort);
            }
        }

        private static void LoadStaticConfig()
        {
            var configPath = Path.Combine(Application.dataPath, "SimpleMCPBridge", "bridge-config.json");
            try
            {
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var cfg = JsonUtility.FromJson<ConfigData>(json);
                    if (cfg != null)
                    {
                        s_serverIp = string.IsNullOrEmpty(cfg.serverIp) ? s_serverIp : cfg.serverIp;
                        s_serverPort = cfg.serverPort > 0 ? s_serverPort : s_serverPort;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[MCPBridgeWindow] Failed to load bridge-config.json: {ex.Message}");
            }
        }

        // ── Instance fields (per open window) ──

        private string _bridgeId;
        private MCPBridge _bridge;
        private bool _isConnecting;
        private string _lastError;

        private static string ConfigPath => Path.Combine(Application.dataPath, "SimpleMCPBridge", "bridge-config.json");

        // ── Window registration ──

        [MenuItem("PowerUtilities/SimpleMCPBridge/MCPBridgeWindow")]
        public static void ShowWindow()
        {
            // Subscription is handled by static constructor, not here.
            var window = GetWindow<MCPBridgeWindow>("SimpleMCPBridge");
            window.minSize = new Vector2(380, 300);
            window.Show();
        }

        // ── Initialization ──

        private void OnEnable()
        {
            _bridgeId = System.Guid.NewGuid().ToString("N");
            // Adopt the Default bridge created by StaticUpdate
            _bridge = MCPBridge.Default;
            if (_bridge != null)
                WireBridgeEvents(_bridge);
        }

        private void OnDisable()
        {
            // Detach only — StaticUpdate keeps draining and reconnecting
            _bridge = null;
            _isConnecting = false;
        }

        // ── GUI ──

        private void OnGUI()
        {
            // Re-adopt Default if window was closed and re-opened
            if (_bridge == null && MCPBridge.Default != null)
            {
                _bridge = MCPBridge.Default;
                WireBridgeEvents(_bridge);
            }

            // Header
            EditorGUILayout.LabelField("SimpleMCPBridge for Unity", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Connect to SimpleMcpServer via WebSocket", EditorStyles.miniLabel);
            EditorGUILayout.Space(8);

            // Server IP field
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Server IP", GUILayout.Width(100));
                s_serverIp = EditorGUILayout.TextField(s_serverIp);
            }

            // Server Port field
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Server Port", GUILayout.Width(100));
                s_serverPort = EditorGUILayout.IntField(s_serverPort);
            }

            EditorGUILayout.Space(4);

            // Bridge ID (readonly)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Bridge ID", GUILayout.Width(100));
                var displayId = _bridge != null ? _bridge.BridgeId : _bridgeId;
                EditorGUILayout.SelectableLabel(displayId, EditorStyles.textField, GUILayout.Height(18));
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
                    DisconnectFromServer();
                GUI.color = Color.white;
            }
            else
            {
                GUI.enabled = !_isConnecting;
                GUI.color = new Color(0.5f, 1f, 0.5f);
                if (GUILayout.Button("▶  Connect to Server", GUILayout.Height(36)))
                    ConnectToServer();
                GUI.color = Color.white;
                GUI.enabled = true;
            }

            if (_isConnecting)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Connecting...", EditorStyles.miniLabel);
            }

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
            if (_bridge != null && _bridge.IsConnected)
                return;

            if (_bridge == null)
            {
                _bridge = MCPBridge.Default ??= new MCPBridge();
                Runtime.AIRequest.Register(_bridge);
                WireBridgeEvents(_bridge);
            }

            _isConnecting = true;
            _lastError = null;
            SaveConfig();
            Repaint();

            s_activated = true;
            _bridge.IsAutoReconnect = true;
            _bridge.ConnectToServer(s_serverIp, s_serverPort);
            Debug.Log($"[MCPBridgeWindow] Connecting to ws://{s_serverIp}:{s_serverPort} (ID: {_bridge?.BridgeId ?? "null"})");
        }

        private void WireBridgeEvents(MCPBridge bridge)
        {
            bridge.OnConnectionFailed -= OnBridgeConnectionFailed;
            bridge.OnConnectedSuccess -= OnBridgeConnectedSuccess;
            bridge.OnConnectionFailed += OnBridgeConnectionFailed;
            bridge.OnConnectedSuccess += OnBridgeConnectedSuccess;
        }

        private void OnBridgeConnectionFailed(string reason)
        {
            _lastError = reason;
            _isConnecting = false;
            Repaint();
            Debug.Log($"[MCPBridgeWindow] Connection failed: {reason}");
        }

        private void OnBridgeConnectedSuccess()
        {
            _lastError = null;
            _isConnecting = false;
            Repaint();
        }

        private void DisconnectFromServer()
        {
            if (_bridge == null) return;
            _bridge.IsAutoReconnect = false;
            _bridge.Disconnect();
            _isConnecting = false;
            _lastError = null;
            Repaint();
            Debug.Log("[MCPBridgeWindow] Disconnected");
        }

        private void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var cfg = new ConfigData { serverIp = s_serverIp, serverPort = s_serverPort };
                File.WriteAllText(ConfigPath, JsonUtility.ToJson(cfg, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[MCPBridgeWindow] Failed to save bridge-config.json: {ex.Message}");
            }
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
#endif
