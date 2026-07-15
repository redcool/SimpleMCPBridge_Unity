using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
#endif

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Scene MonoBehaviour that creates and manages a BridgeClient instance.
    /// Works in both Editor (edit/play) and Runtime builds (device).
    /// This is the sole bridge manager — simply add to any scene GameObject.
    ///
    /// Design:
    ///   - [ExecuteAlways] → works in Edit Mode, Play Mode, and device builds
    ///   - Update() drains queue and checks connection every frame; reconnects if dropped
    ///   - Reads server IP/port from bridge-config.json
    ///   - Custom Editor (MCPBridgeEditor) shows connection status inline
    ///
    /// Usage:
    ///   1. Add this component to any GameObject in your scene
    ///   2. Set Server IP/Port to match SimpleMcpServer
    ///   3. Enable Auto Reconnect to auto-connect on scene load / domain reload
    /// </summary>
    [ExecuteAlways]
    public class MCPBridge : MonoBehaviour
    {
#if UNITY_EDITOR
        // ── Static bootstrap: auto-recover after domain reload ──

        [InitializeOnLoadMethod]
        private static void AutoCreateOnLoad()
        {
            // Subscribe to EditorApplication.update as a fallback drain loop.
            // This survives domain reload and runs even if MonoBehaviour.Update()
            // stops being called (e.g. during scene transitions, play mode enter/exit).
            EditorApplication.update -= StaticUpdate;
            EditorApplication.update += StaticUpdate;

            // delayCall: wait for scene to be fully deserialized
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling) return; // not ready yet
                var existing = FindFirstObjectByType<MCPBridge>();
                if (existing != null)
                {
                    Debug.Log($"[MCPBridge] Found existing component on '{existing.name}', skipping auto-create.");
                    return;
                }

                var go = new GameObject("MCPBridge_Auto", typeof(MCPBridge));
                go.hideFlags = HideFlags.DontSaveInBuild;
                Debug.Log("[MCPBridge] Auto-created MCPBridge GameObject (domain-reload recovery).");
            };
        }

        /// <summary>
        /// Static fallback drain loop.
        /// EditorApplication.update fires even when MonoBehaviour.Update() is not called
        /// (e.g. after Enter Play Mode while scene objects are being rebuilt).
        /// Duplicate-drain-safe: DrainQueue is a no-op if the queue is empty.
        /// </summary>
        private static void StaticUpdate()
        {
            var bridge = BridgeClient.Default;
            if (bridge == null) return;
            bridge.DrainQueue();
            if (bridge.IsAutoReconnect && !bridge.IsConnected && EditorApplication.timeSinceStartup - s_lastAttempt > 0.5)
            {
                s_lastAttempt = EditorApplication.timeSinceStartup;
                // Call ConnectToServer via an MCPBridge instance that knows the IP
                var inst = FindFirstObjectByType<MCPBridge>();
                if (inst != null && inst._bridge != null && inst._bridge == bridge)
                    inst.Connect();
            }
        }
        private static double s_lastAttempt;
#endif
        [SerializeField] private string _serverIp = "127.0.0.1";
        [SerializeField] private int _serverPort = 45678;

        private const float ReconnectInterval = 0.5f;

        private BridgeClient _bridge;
        private float _lastAttemptTime;
        private string _lastError;

        // ── Inspector config ──
        public bool dontDestroyOnLoad = true;
        public bool isAutoReconnect = true;

        public Texture mcpLogo;

        // ── Public read-only state (for Custom Editor) ──
        public bool IsConnected => _bridge != null && _bridge.IsConnected;
        public string BridgeId => _bridge?.BridgeId ?? "(none)";
        public string ServerIp => _serverIp;
        public int ServerPort => _serverPort;
        public string LastError => _lastError;

        private void Awake()
        {
            if (Application.isPlaying)
            {
                if (dontDestroyOnLoad)
                    DontDestroyOnLoad(gameObject);
            }

            SimpleMCPBridge.BridgeConfig.EnsureConfigOnDevice();
            SimpleMCPBridge.BridgeConfig.LoadConfig(out _serverIp, out _serverPort);
            Debug.Log($"MCP Server {_serverIp}:{_serverPort}");
            // Reuse or create the shared default bridge
            _bridge = BridgeClient.Default ??= new BridgeClient();
            _bridge.IsAutoReconnect = isAutoReconnect;

            Runtime.AIRequest.Register(_bridge);

#if UNITY_EDITOR
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        public void OnGUI()
        {
            if (_bridge != null && _bridge.IsConnected && mcpLogo != null)
            {
                var x = Screen.width * 0.6f;
                GUI.DrawTexture(new Rect(x, 10, 64, 64), mcpLogo);
            }
        }

        private void OnEnable()
        {
            ConnectIfNeeded();
        }

        private void Update()
        {
            if (_bridge == null)
            {
                if (isAutoReconnect)
                    Awake();
                return;
            }

            _bridge.IsAutoReconnect = isAutoReconnect;
            _bridge.DrainQueue();

            if (_bridge.IsAutoReconnect && !_bridge.IsConnected && Time.unscaledTime - _lastAttemptTime > ReconnectInterval)
            {
                _lastAttemptTime = Time.unscaledTime;
                _bridge.ConnectToServer(_serverIp, _serverPort);
            }
        }

        // ── Public connect/disconnect (used by Custom Editor and runtime) ──

        /// <summary>Connect to the MCP server with current IP/port settings.</summary>
        public void Connect()
        {
            if (_bridge == null)
            {
                _bridge = BridgeClient.Default ??= new BridgeClient();
                Runtime.AIRequest.Register(_bridge);
            }
            _lastError = null;
            _bridge.IsAutoReconnect = isAutoReconnect;
            _bridge.ConnectToServer(_serverIp, _serverPort);
        }

        /// <summary>Disconnect from the MCP server and disable auto-reconnect.</summary>
        public void Disconnect()
        {
            _lastError = null;
            if (_bridge != null)
            {
                _bridge.IsAutoReconnect = false;
                _bridge.Disconnect();
            }
        }

        /// <summary>Connect to a specific server (overrides config).</summary>
        public void ConnectTo(string ip, int port)
        {
            _serverIp = ip;
            _serverPort = port;
            Connect();
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
            // Don't disconnect BridgeClient.Default — it may be shared by other
            // MCPBridge instances (e.g. after domain-reload bootstrap creates a
            // new one before the old inactive one is destroyed).
            if (_bridge != null)
            {
                _bridge.IsAutoReconnect = false; // stop retry loop
                _bridge = null;
            }
        }



        // ── Internal ──

        private void ConnectIfNeeded()
        {
            if (_bridge != null && !_bridge.IsConnected && _bridge.IsAutoReconnect)
                _bridge.ConnectToServer(_serverIp, _serverPort);
        }

#if UNITY_EDITOR
        // ── Compilation / Play mode monitoring ──

        private void OnCompilationStarted(object obj)
        {
            _bridge?.SendIfConnected("{\"type\":\"compilation\",\"status\":\"started\"}");
        }

        private void OnCompilationFinished(object obj)
        {
            _bridge?.SendIfConnected("{\"type\":\"compilation\",\"status\":\"finished\"}");
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            string status = state switch
            {
                PlayModeStateChange.EnteredPlayMode => "entered",
                PlayModeStateChange.ExitingPlayMode => "exiting",
                PlayModeStateChange.EnteredEditMode => "entered_edit",
                PlayModeStateChange.ExitingEditMode => "exiting_edit",
                _ => null
            };
            if (status == null) return;
            _bridge?.SendIfConnected($"{{\"type\":\"playmode\",\"status\":\"{status}\"}}");

            // Bridge disconnect is NOT done here — it would close the WebSocket
            // before the ExitPlayMode JSON-RPC response can be sent.
            // DomainUnload (during domain reload) handles cleanup.
        }
#endif

    }
}
