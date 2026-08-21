using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SimpleMCPBridge;
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
    /// Queue draining must be called externally (e.g. from MCPBridge.Update() or
    /// an EditorApplication.update handler) via DrainQueue().
    ///
    /// Lifecycle is managed by MCPBridge.
    /// </summary>
    public class BridgeClient
    {
        // ── Constants ──
        // Max queued main-thread actions drained per frame. A burst of queued
        // messages (e.g. a flood of tool responses) must not freeze the Unity
        // main thread — the remainder stay in the queue and drain on later frames.
        private const int MaxActionsPerFrame = 12;

        
        private IWebSocketClient _client;
        private MessageRouter _router;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private int _tickCount;
        private bool _serverEncryptionEnabled = false;

        // ── Public properties ──
        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 45678;
        public bool IsConnected => _client != null && _client.IsConnected;
        /// <summary>Unique identifier for this bridge instance: <engine>-<project>-<guid>.
        /// Server routes toolToBridge on this id, so it must stay stable per connection.
        /// Changing it externally would break server-side routing, so the setter is private.</summary>
        public string BridgeId { get; private set; } = BuildBridgeId();

        /// <summary>构造三段式桥 id：engine 固定 "unity"，project 段取 config.projectName
        /// （回退 Application.productName）slug 化，guid 段保证唯一。</summary>
        private static string BuildBridgeId()
        {
            var project = SimpleMCPBridge.BridgeConfig.ProjectName;
            if (string.IsNullOrEmpty(project))
                project = Application.productName;
            return "unity-" + SimpleMCPBridge.BridgeConfig.Slugify(project) + "-" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Shared default bridge instance.
        /// Set by MCPBridge (or the first owner that creates a bridge).
        /// Thread-safe via lock (Fix P2#5).
        /// </summary>
        private static readonly object _defaultLock = new();
        private static BridgeClient _default;
        public static BridgeClient Default
        {
            get
            {
                if (_default != null) return _default;
                lock (_defaultLock)
                {
                    return _default ??= new BridgeClient();
                }
            }
            set { lock (_defaultLock) { _default = value; } }
        }

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
        /// <summary>Invoked when the bridge disconnects from the server.</summary>
        public event Action OnDisconnected;

        // ── Constructor ──

        public BridgeClient()
        {
            _router = new MessageRouter();
        }

        // ── Public API ──

        /// <summary>
        /// Connect to SimpleMcpServer at the specified host:port.
        /// On connect, registers all [MCPTool] tools automatically.
        /// </summary>
        public void ConnectToServer(string host, int port)
        {
            if (IsConnected) return;

            Host = host;
            Port = port;

            Log($"ConnectToServer({host}:{port})");

            // Force-clean any previous client (even stuck-connecting ones)
            if (_client != null)
            {
                try { _client.Disconnect(); } catch (Exception ex) { Log($"Disconnect cleanup error: {ex.Message}"); }
                // Unsubscribe from old client's events before discarding (Fix C3)
                _client.OnMessageReceived -= OnServerMessage;
                _client.OnConnected -= OnServerConnected;
                _client.OnDisconnected -= OnServerDisconnected;
                _client.OnError -= OnServerError;
                _client = null;
            }
            _client = new NetWebSocketClient();

            _client.OnMessageReceived += OnServerMessage;
            _client.OnConnected += OnServerConnected;
            _client.OnDisconnected += OnServerDisconnected;
            _client.OnError += OnServerError;

            _ = ConnectAsync(host, port);
        }

        /// <summary>
        /// Disconnect from the server and clean up.
        /// </summary>
        public void Disconnect()
        {
            Log("Disconnect called");
            _client?.Dispose();
            _client = null;
            // _router is created once in constructor — do NOT null it

            while (_mainThreadQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Re-create the MessageRouter (re-runs AutoRegisterAll with the current
        /// Application.isPlaying state) and push the updated tool list to the server.
        /// Called on Play Mode transitions so RequirePlayMode tools appear/disappear.
        /// </summary>
        public void ReRegisterTools()
        {
            _router = new MessageRouter();
            var toolsJson = _router.GetToolsJson();
            var registerMsg = $"{{\"type\":\"register_tools\",\"tools\":{toolsJson},\"bridgeId\":\"{BridgeId}\"}}";
            SendIfConnected(registerMsg);
            Log($"ReRegisterTools: pushed {toolsJson.Length} chars");
        }

        /// <summary>
        /// Apply payload encryption when the server has flagged encryption enabled.
        /// Warns if the key is missing (server requires encryption but bridge has
        /// no key configured). Returns the original message when encryption is off,
        /// or the #ENC#-prefixed ciphertext when on. Shared by SendAsync/SendSafeAsync.
        /// </summary>
        private string EncryptIfNeeded(string message)
        {
            if (!_serverEncryptionEnabled) return message;
            if (string.IsNullOrEmpty(SimpleMCPBridge.BridgeConfig.EncryptionKey))
                LogWarning("Server requires encryption but no key configured in bridge-config.json");
            return SimpleMCPBridge.EncryptionHelper.Encrypt(message, SimpleMCPBridge.BridgeConfig.EncryptionKey);
        }

        /// <summary>
        /// Send a raw message to the server asynchronously.
        /// Used by AIRequest to send ai_request messages.
        /// Encrypts payload if encryption is configured.
        /// </summary>
        public async Task SendAsync(string message)
        {
            if (_client != null && _client.IsConnected)
                await _client.SendAsync(EncryptIfNeeded(message));
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
        /// Capped at MaxActionsPerFrame per call so a burst of queued messages
        /// can't freeze the main thread; unprocessed actions stay in the queue
        /// and are handled on subsequent frames (nothing is dropped).
        /// </summary>
        public void DrainQueue()
        {
            _tickCount++;
            var count = 0;
            while (count < MaxActionsPerFrame && _mainThreadQueue.TryDequeue(out var action))
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
                LogWarning($"Cannot reach SimpleMcpServer at {Host}:{Port} — {ex.Message}");
                OnConnectionFailed?.Invoke(reason);
            }
        }

        /// <summary>
        /// Fire-and-forget send with error logging.
        /// Takes the client reference explicitly to avoid race with external client swap.
        /// Encrypts payload if encryption is configured (via EncryptIfNeeded).
        /// </summary>
        private async Task SendSafeAsync(IWebSocketClient client, string message)
        {
            try
            {
                if (client != null && client.IsConnected)
                    await client.SendAsync(EncryptIfNeeded(message));
            }
            catch (Exception ex)
            {
                Log($"SendSafeAsync error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HandleMessage(string rawMessage)
        {
            // Redacted trace: type/method + payload size only — never log the body
            // (tool-call params like editor.eval code, args, secrets).
            Log("HANDLE MESSAGE: " + DescribeMessage(rawMessage));

            // ── Extract message type once for routing (Fix I1) ──
            var msgType = ExtractJsonString(rawMessage, "type");

            // ── Server info notification (encryption flag, etc.) ──
            if (msgType == "server_info")
            {
                var encStr = ExtractJsonString(rawMessage, "encryption");
                _serverEncryptionEnabled = encStr == "true";
                Log($"Server info: encryption={_serverEncryptionEnabled}");
                // Don't send a response — this is a notification
                return;
            }

            // ── Protocol: server requests tool list → we respond ──
            if (msgType == "request_tools")
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
            if (msgType == "ai_response")
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
                // Log size only — the response body can be huge and may contain
                // game data; the request trace (method + size) is already logged.
                Log($"  Response ({DescribeMessage(rawMessage)}): {response.Length} chars");
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

        // ── Named event handlers (Fix C3: enables proper unsubscribe on reconnect) ──

        private void OnServerMessage(string message)
        {
            // Decrypt only when server says encryption is enabled
            string processed;
            if (_serverEncryptionEnabled)
            {
                if (string.IsNullOrEmpty(SimpleMCPBridge.BridgeConfig.EncryptionKey))
                {
                    LogWarning("Server requires encryption but no key configured in bridge-config.json");
                    return;
                }
                processed = SimpleMCPBridge.EncryptionHelper.Decrypt(message, SimpleMCPBridge.BridgeConfig.EncryptionKey);
                if (processed == null)
                {
                    LogWarning("Failed to decrypt server message — key mismatch?");
                    // Send error as plaintext (peer clearly can't decrypt encrypted frames)
                    var errMsg = "{\"type\":\"error\",\"code\":\"decrypt_failed\",\"message\":\"Payload decryption failed — check encryptionKey\"}";
                    var sendClient = _client;
                    if (sendClient != null && sendClient.IsConnected)
                        _ = SendSafeAsync(sendClient, errMsg);
                    return;
                }
            }
            else
            {
                processed = message;
            }
            Log("MSG QUEUED: " + DescribeMessage(processed));
            _mainThreadQueue.Enqueue(() => HandleMessage(processed));
#if UNITY_EDITOR
            // Wake up Unity's main loop when a message is queued.
            // Without this, when the Editor window is unfocused, EditorApplication.update
            // may not fire frequently enough (or at all), causing the queue to never drain
            // and tool calls to time out.
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        private void OnServerConnected()
        {
            _mainThreadQueue.Enqueue(() =>
            {
                var encLabel = string.IsNullOrEmpty(SimpleMCPBridge.BridgeConfig.EncryptionKey) ? "" : " (encrypted)";
                Log($"Connected to server — ws://{Host}:{Port}{encLabel}");
                OnConnectedSuccess?.Invoke();
            });
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        private void OnServerDisconnected()
        {
            _mainThreadQueue.Enqueue(() =>
            {
                Log("Disconnected from server");
                LogWarning("Disconnected from SimpleMcpServer");
                OnDisconnected?.Invoke();
            });
        }

        private void OnServerError(string err)
        {
            _mainThreadQueue.Enqueue(() =>
            {
                Log($"Client error: {err}");
            });
        }

        private void Log(string msg)
        {
            DebugUtils.Log(msg);
        }

        private void LogWarning(string msg)
        {
            DebugUtils.LogWarning(msg);
        }

        /// <summary>
        /// Redacted trace description for a message: type/method + payload character
        /// length only. Never logs the body — tool-call params (editor.eval code,
        /// args) and other payload contents may contain secrets and can be huge.
        /// Error paths may keep full detail separately.
        /// </summary>
        private static string DescribeMessage(string rawMessage)
        {
            var msgType = ExtractJsonString(rawMessage, "type") ?? "?";
            var method = ExtractJsonString(rawMessage, "method");
            return string.IsNullOrEmpty(method)
                ? $"{msgType} ({rawMessage.Length} chars)"
                : $"{msgType}/{method} ({rawMessage.Length} chars)";
        }

        /// <summary>
        /// Minimal JSON string extraction for known keys.
        /// Returns the unquoted string value or null if not found.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            while (idx >= 0)
            {
                // 向前跳过空白,检查前一个字符是否为 { 或 , (确保是 JSON key 而非 value 内的子串, Fix P2#2)
                int i = idx - 1;
                while (i >= 0 && char.IsWhiteSpace(json[i])) i--;
                if (i < 0 || json[i] == '{' || json[i] == ',')
                    break;
                idx = json.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
            }
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
 
