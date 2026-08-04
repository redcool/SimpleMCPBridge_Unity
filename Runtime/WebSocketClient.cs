using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Minimal WebSocket client (RFC 6455) built on raw TCP sockets.
    /// Connects to SimpleMcpServer, sends/receives WebSocket frames.
    /// Zero external dependencies — works with Unity's .NET Standard 2.1 profile.
    ///
    /// == TCP 粘包 / 拆包 handling ==
    /// All received data goes into a unified receive buffer (`_recvBuffer`) first.
    /// Frame parsing reads from this buffer. This naturally handles:
    ///   - 粘包: multiple WebSocket frames in one TCP segment
    ///   - 拆包: partial frame split across TCP segments
    ///   - 混合: HTTP response + first WS frame in one segment (the bug this fixes)
    ///
    /// == Status: LEGACY (research/reference only) ==
    /// This custom implementation is NOT used by the bridge at runtime — the
    /// active transport is <see cref="NetWebSocketClient"/> (wraps .NET's
    /// ClientWebSocket). This class is kept as a dependency-free reference
    /// implementation of RFC 6455 (handshake, frame masking, TCP 粘包 handling).
    /// Do not instantiate it for production use.
    /// </summary>
    [Obsolete("WebSocketClient is the legacy custom RFC 6455 implementation, kept for reference only. Use NetWebSocketClient (the active transport used by BridgeClient) instead.")]
    public class WebSocketClient : IWebSocketClient
    {
        // ── WebSocket protocol constants (RFC 6455) ──
        private const int FinBit = 0x80;
        private const int MaskBit = 0x80;
        private const int OpcodeMask = 0x0F;
        private const int PayloadLenMask = 0x7F;
        private const int TextOpcode = 0x1;
        private const int CloseOpcode = 0x8;
        private const int PingOpcode = 0x9;
        private const int PongOpcode = 0xA;
        private const int SmallPayloadMax = 125;
        private const int Extended16Marker = 126;
        private const int Extended64Marker = 127;
        private const int MaxUInt16Payload = 65535; // max value for 16-bit extended length
        private const int MaskKeySize = 4;
        private const int FrameHeaderMinSize = 2;
        private const int Extended16Size = 2;
        private const int Extended64Size = 8;

        // ── Timeouts / constants ──
        private const int DisconnectTimeoutMs = 5000;
        private const int ConnectTimeoutMs = 10000;
        private const int RecvBufferSize = 65536; // 64KB — fits any WebSocket frame below this size

        private TcpClient _tcpClient;
        private Stream _stream;
        private CancellationTokenSource _cts;
        private volatile bool _isConnected;
        private volatile bool _disconnecting;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // ── Unified receive buffer (solves TCP 粘包) ──
        // _recvBuffer[_recvStart .. _recvStart + _recvCount) = valid received bytes
        // CompactIfNeeded() slides data to front when offset grows large.
        private byte[] _recvBuffer;
        private int _recvStart;  // start of valid data in _recvBuffer
        private int _recvCount;  // number of valid bytes

        // ── Events ──
        /// <summary>Message received from server (JSON-RPC tool calls).</summary>
        public event Action<string> OnMessageReceived;
        /// <summary>Connected to SimpleMcpServer.</summary>
        public event Action OnConnected;
        /// <summary>Disconnected from SimpleMcpServer.</summary>
        public event Action OnDisconnected;
        /// <summary>An error occurred.</summary>
        public event Action<string> OnError;

        // ── Properties ──
        public bool IsConnected => _isConnected;
        /// <summary>True when a TcpClient exists but the WebSocket handshake hasn't completed yet (connecting or reconnecting).</summary>
        public bool IsConnecting => _tcpClient != null && !_isConnected;

        // ── Connection ──

        /// <summary>
        /// Connect to SimpleMcpServer at host:port and perform WebSocket upgrade.
        /// </summary>
        public async Task ConnectAsync(string host, int port)
        {
            Disconnect(); // start fresh

            _cts = new CancellationTokenSource();
            using var timeoutCts = new CancellationTokenSource(ConnectTimeoutMs);
            var ct = timeoutCts.Token;

            _tcpClient = new TcpClient();
            try
            {
                // Use WhenAny for timeout since TcpClient.ConnectAsync may not support CancellationToken
                var connectTask = _tcpClient.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
                var completed = await Task.WhenAny(connectTask, timeoutTask);
                ThrowIfDisposed();
                if (completed == timeoutTask)
                    throw new TimeoutException($"Connect timeout after {ConnectTimeoutMs}ms to {host}:{port}");
                await connectTask; // propagate any connection exception

                ThrowIfDisposed();
                _stream = _tcpClient.GetStream();

                // ── Receive buffer ──
                _recvBuffer = new byte[RecvBufferSize];
                _recvStart = 0;
                _recvCount = 0;

                // Perform HTTP WebSocket upgrade handshake (reads into _recvBuffer)
                await PerformHandshakeAsync(host, port);

                ThrowIfDisposed();

                _isConnected = true;
                _disconnecting = false; // Fix 3: reset for reconnect
                OnConnected?.Invoke();

                // Start reading frames on background thread
                // ReadLoopAsync uses _recvBuffer — any WS frame data that arrived
                // during the handshake is already in the buffer.
                _ = ReadLoopAsync(_cts.Token);
            }
            catch
            {
                CleanupAfterFailedConnect();
                throw;
            }
        }

        /// <summary>
        /// Clean up after a failed connect attempt so IsConnecting returns false.
        /// Called from catch blocks in ConnectAsync.
        /// </summary>
        private void CleanupAfterFailedConnect()
        {
            _tcpClient?.Close();
            _tcpClient = null;
            _stream = null;
            _isConnected = false;
        }

        private void ThrowIfDisposed()
        {
            if (_tcpClient == null)
                throw new InvalidOperationException("WebSocketClient was disconnected during async connect");
        }

        /// <summary>
        /// Disconnect and clean up.
        /// Sets _disconnecting first so in-flight SendAsync aborts early,
        /// then waits briefly for the send lock to let any in-progress write
        /// finish before closing the TCP socket. This prevents partial frames
        /// (header written, payload not yet) from reaching the server.
        /// </summary>
        public void Disconnect()
        {
            _disconnecting = true;
            _cts?.Cancel();
            // Wait briefly for in-flight send to finish (cooperative handover)
            if (_sendLock.Wait(DisconnectTimeoutMs))
            {
                try { _sendLock.Release(); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[WebSocket] send-lock release failed: {ex.Message}"); }
            }
            _cts?.Dispose();
            _cts = null;
            if (_tcpClient != null)
            {
                try { _tcpClient.Close(); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[WebSocket] tcp close failed: {ex.Message}"); }
                _tcpClient = null;
            }
            _stream = null;
            _recvBuffer = null;
            _recvStart = _recvCount = 0;
            _isConnected = false;
        }

        public void Dispose()
        {
            Disconnect();
            _sendLock?.Dispose();
        }

        // ── Send ──

        /// <summary>
        /// Send a text message to the server.
        /// Serialized via _sendLock to prevent concurrent TCP writes from
        /// interleaving WebSocket frames (causes "Invalid UTF-8 sequence" on server).
        /// </summary>
        public async Task SendAsync(string message)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_disconnecting || !_isConnected || _stream == null)
                    throw new InvalidOperationException("Not connected");
                var payload = Encoding.UTF8.GetBytes(message);
                await SendFrameAsync(TextOpcode, payload); // text frame, masked (client→server)
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ── WebSocket frame sending (client→server, MUST be masked) ──

        private async Task SendFrameAsync(int opcode, byte[] payload)
        {
            var maskKey = GenerateMaskKey();

            var header = new List<byte>();
            header.Add((byte)(FinBit | opcode)); // FIN + opcode

            if (payload.Length <= SmallPayloadMax)
            {
                header.Add((byte)(MaskBit | payload.Length)); // MASK + length
            }
            else if (payload.Length <= MaxUInt16Payload)
            {
                header.Add(MaskBit | Extended16Marker);
                header.Add((byte)((payload.Length >> 8) & 0xFF));
                header.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                header.Add(MaskBit | Extended64Marker);
                var lenBytes = BitConverter.GetBytes((ulong)payload.Length);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(lenBytes);
                header.AddRange(lenBytes);
            }

            header.AddRange(maskKey); // MaskKeySize-byte mask key

            var ct = _cts?.Token ?? CancellationToken.None;
            await _stream.WriteAsync(header.ToArray(), 0, header.Count, ct);

            // Mask payload
            var masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ maskKey[i % MaskKeySize]);

            await _stream.WriteAsync(masked, 0, masked.Length, ct);
            await _stream.FlushAsync(ct);
        }

        // ── Receive buffer management ──

        /// <summary>
        /// Compact the receive buffer: slide valid data to the front when
        /// _recvStart has grown large, or simply reset counters when empty.
        /// </summary>
        private void CompactBuffer()
        {
            if (_recvCount <= 0)
            {
                _recvStart = 0;
                return;
            }
            if (_recvStart > 0)
            {
                Array.Copy(_recvBuffer, _recvStart, _recvBuffer, 0, _recvCount);
                _recvStart = 0;
            }
        }

        /// <summary>
        /// Drain up to <c>count</c> bytes from the receive buffer into <c>dest</c>.
        /// Returns the number of bytes actually copied (may be less than <c>count</c>
        /// if buffer doesn't have enough data).
        /// Does NOT block — only reads from the in-memory buffer.
        /// </summary>
        private int DrainBuffer(byte[] dest, int offset, int count)
        {
            if (_recvCount <= 0) return 0;
            int toCopy = Math.Min(_recvCount, count);
            Array.Copy(_recvBuffer, _recvStart, dest, offset, toCopy);
            _recvStart += toCopy;
            _recvCount -= toCopy;
            return toCopy;
        }

        /// <summary>
        /// Fill the receive buffer from the network.
        /// Blocks until data arrives or the connection is closed.
        /// Returns the number of bytes read (0 = connection closed).
        /// </summary>
        private async Task<int> FillFromNetworkAsync(CancellationToken token)
        {
            // Compact to make room at the end of the buffer
            CompactBuffer();
            int space = _recvBuffer.Length - _recvStart - _recvCount;
            if (space <= 0)
                throw new InvalidOperationException("Receive buffer full");

            int read = await _stream.ReadAsync(_recvBuffer, _recvStart + _recvCount, space, token);
            if (read > 0)
                _recvCount += read;
            return read;
        }

        /// <summary>
        /// Read exactly <c>count</c> bytes from the network into <c>buffer[offset..]</c>.
        /// Uses the internal receive buffer first, then reads from the network.
        /// This is the central method that handles TCP 拆包 (partial reads) correctly:
        /// it loops until the requested number of bytes have been obtained.
        /// </summary>
        private async Task<int> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                // 1) Drain from internal buffer first (non-blocking)
                totalRead += DrainBuffer(buffer, offset + totalRead, count - totalRead);
                if (totalRead >= count) break;

                // 2) Fill buffer from network (blocks until data or close)
                int netRead = await FillFromNetworkAsync(token);
                if (netRead <= 0) break; // connection closed
            }
            return totalRead;
        }

        // ── WebSocket frame reading (server→client, NOT masked) ──

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var headerBuf = new byte[FrameHeaderMinSize];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Read 2-byte frame header
                    var read = await ReadExactAsync(headerBuf, 0, FrameHeaderMinSize, token);
                    if (read < FrameHeaderMinSize) break;

                    int opcode = headerBuf[0] & OpcodeMask;
                    bool masked = (headerBuf[1] & MaskBit) != 0;
                    long payloadLength = headerBuf[1] & PayloadLenMask;

                    // Extended payload length
                    if (payloadLength == Extended16Marker)
                    {
                        var ext = new byte[Extended16Size];
                        await ReadExactAsync(ext, 0, Extended16Size, token);
                        payloadLength = (ext[0] << 8) | ext[1];
                    }
                    else if (payloadLength == Extended64Marker)
                    {
                        var ext = new byte[Extended64Size];
                        await ReadExactAsync(ext, 0, Extended64Size, token);
                        payloadLength = 0;
                        for (int i = 0; i < Extended64Size; i++)
                            payloadLength = (payloadLength << 8) | ext[i];
                    }

                    // ── Fix 1: Payload size validation ──
                    if (payloadLength > 10 * 1024 * 1024)
                        throw new InvalidOperationException($"WebSocket frame payload too large: {payloadLength} bytes");

                    // Mask key (server frames shouldn't be masked, but handle per spec)
                    byte[] maskKey = null;
                    if (masked)
                    {
                        maskKey = new byte[MaskKeySize];
                        await ReadExactAsync(maskKey, 0, MaskKeySize, token);
                    }

                    // Payload (use ReadExactAsync for consistency with buffer)
                    var payload = new byte[payloadLength];
                    long totalRead = 0;
                    while (totalRead < payloadLength)
                    {
                        int chunkSize = Math.Min((int)(payloadLength - totalRead), 8192); // read in chunks
                        var chunk = await ReadExactAsync(payload, (int)totalRead, chunkSize, token);
                        if (chunk <= 0) break;
                        totalRead += chunk;
                    }

                    // Unmask if needed
                    if (masked && maskKey != null)
                    {
                        for (long i = 0; i < payloadLength; i++)
                            payload[i] ^= maskKey[i % MaskKeySize];
                    }

                    switch (opcode)
                    {
                        case CloseOpcode: // Close
                            return;
                        case PingOpcode: // Ping — respond with Pong
                            await SendFrameAsync(PongOpcode, payload);
                            break;
                        case TextOpcode: // Text
                            var message = Encoding.UTF8.GetString(payload);
                            OnMessageReceived?.Invoke(message);
                            break;
                        case 0x2: // Binary — ignore
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is SocketException || ex is IOException) { break; }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                    break;
                }
            }
            // Clean up so IsConnecting returns false (allows reconnection)
            _isConnected = false;
            _tcpClient?.Close();
            _tcpClient = null;
            _stream = null;
            OnDisconnected?.Invoke();
        }

        // ── HTTP WebSocket upgrade (client side) ──

        /// <summary>
        /// Send the HTTP WebSocket upgrade request, then read the response.
        ///
        /// == TCP 粘包 handling ==
        /// Reads ALL available data into <c>_recvBuffer</c> until the HTTP
        /// response headers are complete (detected by \r\n\r\n).
        /// Any data after the HTTP headers (e.g. the server's first
        /// WebSocket frame) stays in <c>_recvBuffer</c> and will be consumed
        /// by <c>ReadLoopAsync</c> via <c>ReadExactAsync</c>.
        ///
        /// This replaces the old byte-by-byte <c>ReadByte</c> approach with
        /// a proper buffering scheme that naturally handles:
        ///   - HTTP 101 + WS frame in one TCP segment (粘包)
        ///   - HTTP response split across TCP segments (拆包)
        ///   - HTTP response partially buffered, rest from network
        /// </summary>
        private async Task PerformHandshakeAsync(string host, int port)
        {
            var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            var request = $"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            var bytes = Encoding.UTF8.GetBytes(request);
            await _stream.WriteAsync(bytes, 0, bytes.Length);

            // Read into _recvBuffer until \r\n\r\n is found.
            // Any data after \r\n\r\n (i.e., the first WebSocket frame(s))
            // stays in _recvBuffer for ReadLoopAsync.
            int headerEndIndex = -1;

            while (true)
            {
                // Compact buffer to make room for more network data
                CompactBuffer();
                int space = _recvBuffer.Length - _recvStart - _recvCount;
                if (space <= 0)
                    throw new Exception("HTTP response headers too large (>64KB)");
                int read = await _stream.ReadAsync(_recvBuffer, _recvStart + _recvCount, space);
                if (read <= 0)
                    throw new EndOfStreamException("Server closed connection during HTTP handshake");
                _recvCount += read;

                // Search for \r\n\r\n in the buffered data
                for (int i = _recvStart; i <= _recvStart + _recvCount - 4; i++)
                {
                    if (_recvBuffer[i] == '\r' &&
                        _recvBuffer[i + 1] == '\n' &&
                        _recvBuffer[i + 2] == '\r' &&
                        _recvBuffer[i + 3] == '\n')
                    {
                        headerEndIndex = i + 4; // position AFTER \r\n\r\n
                        break;
                    }
                }

                if (headerEndIndex >= 0)
                    break;
            }

            // Extract and validate the HTTP response
            var response = Encoding.UTF8.GetString(_recvBuffer, _recvStart, headerEndIndex - _recvStart);

            // Trim the buffer: remove the HTTP response, keep only WebSocket frame data
            int httpHeaderLen = headerEndIndex - _recvStart;
            int surplus = _recvCount - httpHeaderLen;
            if (surplus > 0)
            {
                // Slide surplus data to front of buffer
                Array.Copy(_recvBuffer, headerEndIndex, _recvBuffer, 0, surplus);
            }
            _recvStart = 0;
            _recvCount = surplus;

            if (!response.Contains("101") || !response.Contains("Sec-WebSocket-Accept"))
            {
                var preview = response.Substring(0, Math.Min(response.Length, 100));
                throw new Exception($"WebSocket handshake failed: server returned {preview}");
            }
        }

        // ── Helpers ──

        private static byte[] GenerateMaskKey()
        {
            var key = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }
            return key;
        }
    }
}
