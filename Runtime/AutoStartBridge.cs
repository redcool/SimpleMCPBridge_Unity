using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
#endif

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Scene MonoBehaviour that creates and manages an MCPBridge instance.
    /// Handles automatic reconnection and monitors Unity compilation/play mode events.
    ///
    /// Design:
    ///   - [ExecuteAlways] → works in both Editor (edit/play) and Runtime builds
    ///   - Update() drains queue and checks connection every frame; reconnects if dropped
    ///   - Reads server IP/port from bridge-config.json in Assets/SimpleMCPBridge/
    ///
    /// Usage:
    ///   1. Create an empty GameObject in your scene (e.g., "[AutoStartBridge]")
    ///   2. Add this component (AutoStartBridgeInitializer does this automatically)
    ///   3. Ensure SimpleMcpServer is running before entering play mode
    ///
    /// On domain reload, this component is recreated by Unity (scene persistence).
    /// The MCPBridge instance is a plain C# object, recreated in Awake.
    /// </summary>
    [ExecuteAlways]
    public class AutoStartBridge : MonoBehaviour
    {
        [SerializeField] private string _serverIp = "127.0.0.1";
        [SerializeField] private int _serverPort = 45678;

        private const float ReconnectInterval = 0.5f;

        private MCPBridge _bridge;
        private float _lastAttemptTime;

        // ── Unity lifecycle ──

        private void Awake()
        {
            LoadConfig();
            // Reuse or create the shared default bridge
            _bridge = MCPBridge.Default ??= new MCPBridge();
            Runtime.AIRequest.Register(_bridge);

#if UNITY_EDITOR
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnEnable()
        {
            ConnectIfNeeded();
        }

        private void Update()
        {
            if (_bridge == null) return;

            // Drain the bridge's main-thread queue every frame
            _bridge.DrainQueue();

            // Every frame: if disconnected and user wants auto-reconnect, retry (throttled)
            if (_bridge.IsAutoReconnect && !_bridge.IsConnected && Time.unscaledTime - _lastAttemptTime > ReconnectInterval)
            {
                _lastAttemptTime = Time.unscaledTime;
                _bridge.ConnectToServer(_serverIp, _serverPort);
            }
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
            if (_bridge != null)
            {
                _bridge.Disconnect();
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
        }
#endif

        // ── Config ──

        private void LoadConfig()
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
                        _serverPort = config.serverPort > 0 ? _serverPort : _serverPort;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AutoStartBridge] Failed to read bridge-config.json: {ex.Message}");
            }
        }

        [System.Serializable]
        private class BridgeConfig
        {
            public string serverIp = "127.0.0.1";
            public int serverPort = 45678;
        }
    }
}
