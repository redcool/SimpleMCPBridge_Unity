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
        [SerializeField] private string _serverIp = "127.0.0.1";
        [SerializeField] private int _serverPort = 45678;

        private const float ReconnectInterval = 3f;

        private BridgeClient _bridge;
        private double _lastAttemptTime;
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
            // 多实例防护:如果已有其他 MCPBridge 实例,禁用自己
            var others = FindObjectsByType<MCPBridge>(FindObjectsSortMode.None);
            foreach (var other in others)
            {
                if (other != this && other.enabled)
                {
                    Debug.Log($"[MCPBridge] Another MCPBridge exists on '{other.name}', disabling this one on '{gameObject.name}'.");
                    enabled = false;
                    return;
                }
            }

#if UNITY_EDITOR
            EditorApplication.update -= InstanceUpdate;
            EditorApplication.update += InstanceUpdate;
#endif
            ConnectIfNeeded();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= InstanceUpdate;
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// EditorApplication.update callback — drains queue and reconnects in Edit Mode.
        /// More reliable than MonoBehaviour.Update() during scene transitions and compilation.
        /// Registered in OnEnable, unregistered in OnDisable — tied to component lifecycle.
        /// </summary>
        private void InstanceUpdate()
        {
            if (_bridge == null) return;
            _bridge.IsAutoReconnect = isAutoReconnect;
            _bridge.DrainQueue();
#if UNITY_INPUT_SYSTEM
            MouseDeviceTools.TickDeferredClick();
#endif

            if (_bridge.IsAutoReconnect && !_bridge.IsConnected && EditorApplication.timeSinceStartup - _lastAttemptTime > ReconnectInterval)
            {
                _lastAttemptTime = EditorApplication.timeSinceStartup;
                _bridge.ConnectToServer(_serverIp, _serverPort);
            }
        }
#endif

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
#if UNITY_INPUT_SYSTEM
            MouseDeviceTools.TickDeferredClick();
#endif

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
