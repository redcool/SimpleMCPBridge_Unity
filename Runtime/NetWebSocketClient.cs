using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// WebSocket client built on System.Net.WebSockets.ClientWebSocket.
    /// Drop-in alternative to WebSocketClient with identical event API.
    /// Payload encryption (if configured) is handled by BridgeClient, not at this layer.
    /// </summary>
    public class NetWebSocketClient : IWebSocketClient
    {
        // ── Constants ──
        private const int ConnectTimeoutMs = 10000;
        private const int DisconnectTimeoutMs = 5000;
        private const int ReceiveBufferSize = 8192; // per ReadAsync call; fragments accumulate via StringBuilder
        private const int MaxMessageBytes = 4 * 1024 * 1024;

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private volatile bool _isConnected;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // ── Events (same signature as WebSocketClient) ──
        /// <summary>Message received from server (JSON-RPC tool calls).</summary>
        public event Action<string> OnMessageReceived;
        /// <summary>Connected to SimpleMcpServer.</summary>
        public event Action OnConnected;
        /// <summary>Disconnected from SimpleMcpServer.</summary>
        public event Action OnDisconnected;
        /// <summary>An error occurred.</summary>
        public event Action<string> OnError;

        // ── Properties ──
        /// <summary>True when the WebSocket is in the Open state.</summary>
        public bool IsConnected => _isConnected;
        /// <summary>True when a ClientWebSocket exists but hasn't finished connecting.</summary>
        public bool IsConnecting => _ws != null && !_isConnected;

        // ── Connection ──

        /// <summary>
        /// Connect to SimpleMcpServer via WebSocket at host:port.
        /// </summary>
        public async Task ConnectAsync(string host, int port)
        {
            Disconnect(); // start fresh

            _cts = new CancellationTokenSource();
            var uri = new Uri($"ws://{host}:{port}");
            _ws = new ClientWebSocket();

            using var timeoutCts = new CancellationTokenSource(ConnectTimeoutMs);
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
                await _ws.ConnectAsync(uri, linkedCts.Token);

                _isConnected = true;
                OnConnected?.Invoke();

                // Start receiving frames on background thread
                _ = ReceiveLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                CleanupAfterFailedConnect();
                throw new TimeoutException($"Connect timeout after {ConnectTimeoutMs}ms to {host}:{port}");
            }
            catch (Exception ex)
            {
                CleanupAfterFailedConnect();
                var inner = ex.InnerException?.Message ?? ex.Message;
                DebugUtils.LogWarning($"[NetWebSocketClient] Connect failed to {host}:{port}: {inner}");
                throw;
            }
        }

        /// <summary>
        /// Send a text message to the server.
        /// </summary>
        public async Task SendAsync(string message)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_ws == null || _ws.State != WebSocketState.Open)
                    throw new InvalidOperationException("Not connected");

                var bytes = Encoding.UTF8.GetBytes(message);
                if (bytes.Length > MaxMessageBytes) throw new InvalidOperationException("Outbound message exceeds 4MB limit");
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ── Disconnect / Cleanup ──

        /// <summary>
        /// Disconnect from the server immediately (non-blocking).
        /// Disposes the WebSocket directly — no blocking close handshake.
        /// The server will detect the dropped connection on its side.
        /// </summary>
        public void Disconnect()
        {
            _cts?.Cancel();

            // Non-blocking send-lock drain
            try { if (_sendLock.Wait(0)) _sendLock.Release(); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[NetWebSocket] send-lock release on disconnect failed: {ex.Message}"); }

            _cts?.Dispose();
            _cts = null;

            if (_ws != null)
            {
                // Dispose sends TCP RST — instant non-blocking close.
                // A clean close handshake via CloseAsync would block the
                // calling thread for up to DisconnectTimeoutMs (5s), which
                // would freeze the Editor if called from the main thread.
                _ws.Dispose();
                _ws = null;
            }

            _isConnected = false;
        }

        public void Dispose()
        {
            Disconnect();
            _sendLock?.Dispose();
        }

        // ── Receive loop ──

        /// <summary>
        /// Continuous receive loop. Runs on a background thread.
        /// Accumulates fragmented messages and fires OnMessageReceived for complete text messages.
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[ReceiveBufferSize];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var totalBytes = result.Count;
                        if (totalBytes > MaxMessageBytes) throw new WebSocketException("Inbound message exceeds 4MB limit");
                        var sb = new StringBuilder(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        while (!result.EndOfMessage)
                        {
                            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            totalBytes += result.Count;
                            if (totalBytes > MaxMessageBytes) throw new WebSocketException("Inbound message exceeds 4MB limit");
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        OnMessageReceived?.Invoke(sb.ToString());
                    }
                    // Binary messages are ignored
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                    break;
                }
            }

            // Connection lost — notify
            _isConnected = false;
            OnDisconnected?.Invoke();
        }

        // ── Helpers ──

        private void CleanupAfterFailedConnect()
        {
            if (_ws != null)
            {
                _ws.Dispose();
                _ws = null;
            }
            _isConnected = false;
        }
    }
}
