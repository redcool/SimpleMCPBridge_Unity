using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Core bridge logic — plain C# class (not MonoBehaviour).
    /// Connects to SimpleMcpServer as a WebSocket client and dispatches
    /// received tool-call messages from the receive thread to the Unity main thread.
    ///
    /// On connect, automatically registers all [MCPTool]-annotated methods
    /// with the server via a `register_tools` message.
    ///
    /// Queue draining must be called externally (e.g. from AutoStartBridge.Update() or
    /// an EditorApplication.update handler) via DrainQueue().
    ///
    /// Lifecycle is managed by the owner (AutoStartBridge or MCPBridgeWindow).
    /// </summary>
    public class MCPBridge
    {
        // ── Constants ──
        private const int LogPreviewLength = 80;
        private const int ResponseLogLength = 100;

        private string _logPath;
        private IWebSocketClient _client;
        private MessageRouter _router;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private int _tickCount;

        // ── Public properties ──
        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 45678;
        public bool IsConnected => _client != null && _client.IsConnected;
        /// <summary>Unique identifier for this Bridge instance.</summary>
        public string BridgeId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Shared default bridge instance.
        /// Set by AutoStartBridge (or the first owner that creates a bridge).
        /// MCPBridgeWindow adopts this instance to share the same WebSocketClient.
        /// </summary>
        public static MCPBridge Default { get; set; }

        /// <summary>
        /// Whether the bridge should automatically reconnect when disconnected.
        /// Set true on user Connect, false on user Disconnect.
        /// Persisted via EditorPrefs (#if UNITY_EDITOR) so it survives domain reload.
        /// Default true so domain reload / play-mode transitions auto-reconnect.
        /// In Runtime builds always true (no window to toggle it).
        /// </summary>
        public bool IsAutoReconnect
        {
            get
            {
#if UNITY_EDITOR
                return EditorPrefs.GetBool(k_AutoReconnectKey, true);
#else
                return true;
#endif
            }
            set
            {
#if UNITY_EDITOR
                EditorPrefs.SetBool(k_AutoReconnectKey, value);
#endif
            }
        }

        private const string k_AutoReconnectKey = "SimpleMCPBridge_AutoReconnect";

        // ── Events ──
        /// <summary>Invoked when connection fails (reason string).</summary>
        public event Action<string> OnConnectionFailed;
        /// <summary>Invoked when connection succeeds.</summary>
        public event Action OnConnectedSuccess;
        /// <summary>Invoked when an AI response arrives from the server (type: ai_response).</summary>
        public event Action<string, string> OnAIResponse; // (requestId, text)

        // ── Constructor ──

        public MCPBridge()
        {
            var projectDir = Path.GetDirectoryName(Application.dataPath) ?? ".";
            var logDir = Path.Combine(projectDir, "Logs");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "mcp_bridge_debug.log");
        }

        // ── Public API ──

        /// <summary>
        /// Connect to SimpleMcpServer at the specified host:port.
        /// On connect, registers all [MCPTool] tools automatically.
        /// </summary>
        public void ConnectToServer(string host, int port)
        {
            if (IsConnected) return;
            if (_client != null && _client.IsConnecting) return;

            Host = host;
            Port = port;

            Log($"ConnectToServer({host}:{port})");

            _router = new MessageRouter();
            // Disconnect any previous client to avoid leaking connections
            _client?.Disconnect();
            _client = new NetWebSocketClient();

            _client.OnMessageReceived += (message) =>
            {
                Log($"MSG QUEUED: {message.Trim().Substring(0, Math.Min(message.Length, LogPreviewLength))}");
                _mainThreadQueue.Enqueue(() => HandleMessage(message));
#if UNITY_EDITOR
                // Wake up Unity's main loop when a message is queued.
                // Without this, when the Editor window is unfocused, EditorApplication.update
                // may not fire frequently enough (or at all), causing the queue to never drain
                // and tool calls to time out.
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
            };

            _client.OnConnected += () =>
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    Log("Connected to server");
                    Debug.Log($"[SimpleMCPBridge] Connected to SimpleMcpServer at ws://{Host}:{Port}");
                    OnConnectedSuccess?.Invoke();
                });
#if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
            };

            _client.OnDisconnected += () =>
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    Log("Disconnected from server");
                    Debug.LogWarning($"[SimpleMCPBridge] Disconnected from SimpleMcpServer");
                });
            };

            _client.OnError += (err) =>
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    Log($"Client error: {err}");
                });
            };

            _ = ConnectAsync(host, port);
        }

        /// <summary>
        /// Disconnect from the server and clean up.
        /// </summary>
        public void Disconnect()
        {
            Log("Disconnect called");
            _client?.Disconnect();
            _client = null;
            _router = null;

            while (_mainThreadQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Send a raw message to the server asynchronously.
        /// Used by AIRequest to send ai_request messages.
        /// </summary>
        public async Task SendAsync(string message)
        {
            if (_client != null && _client.IsConnected)
                await _client.SendAsync(message);
            else
                throw new InvalidOperationException("Client not connected");
        }

        /// <summary>
        /// Send a message if connected — no exception on failure.
        /// Safe for fire-and-forget calls from Unity lifecycle events.
        /// </summary>
        public void SendIfConnected(string message)
        {
            var client = _client;
            if (client != null && client.IsConnected)
                _ = SendSafeAsync(client, message);
            else
                Log("  SendIfConnected: client not connected");
        }

        /// <summary>
        /// Drain queued main-thread actions.
        /// Must be called from the Unity main thread every frame.
        /// </summary>
        public void DrainQueue()
        {
            _tickCount++;
            var count = 0;
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                count++;
                try { action(); }
                catch (Exception e)
                {
                    Log($"DISPATCH ERROR: {e.Message}\n{e.StackTrace}");
                }
            }
            if (count > 0)
                Log($"DrainQueue: processed {count} items (tick #{_tickCount})");
        }

        // ── Internal ──

        private async Task ConnectAsync(string host, int port)
        {
            // Capture _client locally — Disconnect can null _client while we await
            var client = _client;
            if (client == null) return;

            try
            {
                await client.ConnectAsync(host, port);
            }
            catch (Exception ex)
            {
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                Log($"Connection failed: {reason}");
                Debug.LogWarning($"[SimpleMCPBridge] Cannot reach SimpleMcpServer at {Host}:{Port} — {ex.Message}");
                OnConnectionFailed?.Invoke(reason);
            }
        }

        /// <summary>
        /// Fire-and-forget send with error logging.
        /// Takes the client reference explicitly to avoid race with external client swap.
        /// </summary>
        private async Task SendSafeAsync(IWebSocketClient client, string message)
        {
            try
            {
                if (client != null && client.IsConnected)
                    await client.SendAsync(message);
            }
            catch (Exception ex)
            {
                Log($"SendSafeAsync error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HandleMessage(string rawMessage)
        {
            Log("HANDLE MESSAGE");

            // ── Protocol: server requests tool list → we respond ──
            if (rawMessage.Contains("\"request_tools\"") || rawMessage.Contains("'request_tools'"))
            {
                Log("  Server requested tool list — sending register_tools");
                if (_router == null)
                {
                    Log("  _router is NULL — cannot respond");
                    return;
                }
                var toolsJson = _router.GetToolsJson();
                var registerMsg = $"{{\"type\":\"register_tools\",\"tools\":{toolsJson},\"bridgeId\":\"{BridgeId}\"}}";
                var client = _client;
                if (client != null && client.IsConnected)
                {
                    _ = SendSafeAsync(client, registerMsg);
                    Log($"  Register_tools sent ({toolsJson.Length} chars)");
                }
                else
                {
                    Log("  _client not connected — cannot send register_tools");
                }
                return;
            }

            // ── AI response from server ──
            if (rawMessage.Contains("\"type\":\"ai_response\"") || rawMessage.Contains("\"type\":\"ai_response\""))
            {
                Log("  AI response received");
                try
                {
                    var requestId = ExtractJsonString(rawMessage, "requestId");
                    var text = ExtractJsonString(rawMessage, "text");
                    if (!string.IsNullOrEmpty(requestId))
                        OnAIResponse?.Invoke(requestId, text ?? "");
                }
                catch (Exception ex)
                {
                    Log($"  Failed to parse ai_response: {ex.Message}");
                }
                return;
            }

            // ── Regular JSON-RPC tool calls ──
            if (_router == null)
            {
                Log("  _router is NULL — aborting");
                return;
            }

            var response = _router.HandleMessage(rawMessage);
            if (response != null)
            {
                Debug.Log($"  Response: {response.Substring(0, Math.Min(response.Length, ResponseLogLength))}...");
                Log($"  Response: {response.Substring(0, Math.Min(response.Length, ResponseLogLength))}...");
                var client = _client;
                if (client != null && client.IsConnected)
                    _ = SendSafeAsync(client, response);
                else
                    Log("  _client not connected — cannot send");
            }
            else
            {
                Log("  No response (null)");
            }
        }

        private void Log(string msg)
        {
            try
            {
                using (var writer = new StreamWriter(_logPath, append: true, encoding: System.Text.Encoding.UTF8))
                {
                    writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
                }
            }
            catch { }
        }

        /// <summary>
        /// Minimal JSON string extraction for known keys.
        /// Returns the unquoted string value or null if not found.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\n' || json[start] == '\r')) start++;
            if (start >= json.Length) return null;
            if (json[start] != '"') return null;
            start++;
            var sb = new System.Text.StringBuilder();
            while (start < json.Length)
            {
                var c = json[start];
                if (c == '"') break;
                if (c == '\\' && start + 1 < json.Length)
                {
                    start++;
                    switch (json[start])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(json[start]); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                start++;
            }
            return sb.ToString();
        }
    }
}
