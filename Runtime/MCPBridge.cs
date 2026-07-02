using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Main bridge MonoBehaviour.
    /// Connects to SimpleMcpServer as a WebSocket client and dispatches
    /// received tool-call messages from the receive thread to the Unity main thread.
    ///
    /// On connect, automatically registers all [MCPTool]-annotated methods
    /// with the server via a `register_tools` message.
    ///
    /// If connection fails, logs a warning and retries every 5 seconds.
    ///
    /// Queue draining:
    ///   Play Mode → MonoBehaviour.Update()
    ///   Edit Mode → MCPBridgeWindow calls DrainQueue() via EditorApplication.update
    ///
    /// Usage (via Editor Window):
    ///   var go = new GameObject("[SimpleMCPBridge]");
    ///   go.hideFlags = HideFlags.HideAndDontSave;
    ///   var bridge = go.AddComponent<MCPBridge>();
    ///   bridge.ConnectToServer("127.0.0.1", 45678);
    /// </summary>
    public class MCPBridge : MonoBehaviour
    {
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 45678;
        private string _logPath;

        private WebSocketClient _client;
        private MessageRouter _router;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private CancellationTokenSource _retryCts;
        private int _tickCount;

        // ── Public properties ──
        public string Host => _host;
        public int Port => _port;
        public bool IsConnected => _client != null && _client.IsConnected;
        /// <summary>Unique identifier for this Bridge instance.</summary>
        public string BridgeId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Invoked when connection fails (reason string).</summary>
        public event Action<string> OnConnectionFailed;
        /// <summary>Invoked when connection succeeds.</summary>
        public event Action OnConnectedSuccess;

        // ── Public API ──

        /// <summary>
        /// Connect to SimpleMcpServer at the specified host:port.
        /// On connect, registers all [MCPTool] tools automatically.
        /// Retries in background if connection fails.
        /// </summary>
        public void ConnectToServer(string host, int port)
        {
            if (IsConnected) return;

            _host = host;
            _port = port;

            var projectDir = Path.GetDirectoryName(Application.dataPath) ?? ".";
            var logDir = Path.Combine(projectDir, "Logs");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "mcp_bridge_debug.log");
            Log($"ConnectToServer({host}:{port})");

            _router = new MessageRouter();
            _client = new WebSocketClient();

            _client.OnMessageReceived += (message) =>
            {
                Log($"MSG QUEUED: {message.Trim().Substring(0, Math.Min(message.Length, 80))}");
                _mainThreadQueue.Enqueue(() => HandleMessage(message));
            };

            _client.OnConnected += () =>
            {
                Log("Connected to server — registering tools...");
                Debug.Log($"[SimpleMCPBridge] Connected to SimpleMcpServer at ws://{_host}:{_port}");
                var toolsJson = _router.GetToolsJson();
                var registerMsg = $"{{\"type\":\"register_tools\",\"tools\":{toolsJson},\"bridgeId\":\"{BridgeId}\"}}";
                _ = _client.SendAsync(registerMsg);
                Log("Tools registered with server");
                Debug.Log($"[SimpleMCPBridge] Registered tools with SimpleMcpServer");
                OnConnectedSuccess?.Invoke();
            };

            _client.OnDisconnected += () =>
            {
                Log("Disconnected from server");
                Debug.LogWarning($"[SimpleMCPBridge] Disconnected from SimpleMcpServer");
                // Start retry loop
                StartRetryLoop();
            };

            _client.OnError += (err) =>
            {
                Log($"Client error: {err}");
            };

            _ = ConnectAsync(host, port);
        }

        private async System.Threading.Tasks.Task ConnectAsync(string host, int port)
        {
            try
            {
                await _client.ConnectAsync(host, port);
                Log($"Connected to ws://{host}:{port}");

#if UNITY_EDITOR
                EditorApplication.update += DrainQueue;
                Log("Registered EditorApplication.update for queue draining");
#endif

                // Connection succeeded — cancel any retry loop
                _retryCts?.Cancel();
            }
            catch (Exception ex)
            {
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                Log($"Connection failed: {reason}");
                Debug.LogWarning($"[SimpleMCPBridge] Cannot reach SimpleMcpServer at {_host}:{_port} — {ex.Message}");
                Debug.LogWarning("[SimpleMCPBridge] Make sure SimpleMcpServer is running. Retrying in 5s...");
                // Notify UI
                OnConnectionFailed?.Invoke(reason);
                // Start retry loop
                StartRetryLoop();
            }
        }

        private void StartRetryLoop()
        {
            // Cancel any existing retry loop
            _retryCts?.Cancel();
            _retryCts = new CancellationTokenSource();
            var token = _retryCts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await System.Threading.Tasks.Task.Delay(5000, token);
                    if (token.IsCancellationRequested) break;

                    Log("Retrying connection...");
                    try
                    {
                        var newClient = new WebSocketClient();
                        newClient.OnMessageReceived += (message) =>
                        {
                            Log($"MSG QUEUED: {message.Trim().Substring(0, Math.Min(message.Length, 80))}");
                            _mainThreadQueue.Enqueue(() => HandleMessage(message));
                        };

                        await newClient.ConnectAsync(_host, _port);

                        // Success — wire up and register tools
                        _mainThreadQueue.Enqueue(() =>
                        {
                            // Clean up old client
                            _client?.Disconnect();
                            _client = newClient;

                            Log("Reconnected via retry loop");
                            Debug.Log($"[SimpleMCPBridge] Reconnected to SimpleMcpServer at ws://{_host}:{_port}");

                            var toolsJson = _router.GetToolsJson();
                            var registerMsg = $"{{\"type\":\"register_tools\",\"tools\":{toolsJson}}}";
                            _ = _client.SendAsync(registerMsg);
                            Debug.Log($"[SimpleMCPBridge] Registered tools with SimpleMcpServer");
                        });

                        var disconnectToken = token; // capture for disconnect handler
                        newClient.OnDisconnected += () =>
                        {
                            Log("Reconnect client disconnected");
                            // Use the same token to check cancellation — the token
                            // gets cancelled when Disconnect() is called, so we
                            // don't restart in that case.
                            if (!disconnectToken.IsCancellationRequested)
                                StartRetryLoop();
                        };

                        // Success: exit retry loop WITHOUT cancelling the token,
                        // so the OnDisconnected handler above can still detect
                        // whether a manual Disconnect() happened.
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"Retry failed: {ex.Message}");
                    }
                }
            }, token);
        }

        /// <summary>
        /// Disconnect from the server and clean up.
        /// </summary>
        public void Disconnect()
        {
            Log("Disconnect called");
            _retryCts?.Cancel();
#if UNITY_EDITOR
            EditorApplication.update -= DrainQueue;
#endif
            _client?.Disconnect();
            _client = null;
            _router = null;

            while (_mainThreadQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Drain queued main-thread actions.
        /// Called from MonoBehaviour.Update() (Play Mode) or Editor Window tick (Edit Mode).
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

        // ── Unity lifecycle ──
        private void Update() { DrainQueue(); }
        private void OnDestroy() { Log("OnDestroy"); Disconnect(); }
        private void OnApplicationQuit() { Disconnect(); }

        // ── Internal ──

        private void HandleMessage(string rawMessage)
        {
            Log("HANDLE MESSAGE");
            if (_router == null)
            {
                Log("  _router is NULL — aborting");
                return;
            }

            var response = _router.HandleMessage(rawMessage);
            if (response != null)
            {
                Log($"  Response: {response.Substring(0, Math.Min(response.Length, 100))}...");
                if (_client != null && _client.IsConnected)
                    _ = _client.SendAsync(response);
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
    }
}

