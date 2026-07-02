using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Minimal WebSocket client (RFC 6455) built on raw TCP sockets.
    /// Connects to SimpleMcpServer, sends/receives WebSocket frames.
    /// Zero external dependencies — works with Unity's .NET Standard 2.1 profile.
    /// </summary>
    public class WebSocketClient : IDisposable
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private volatile bool _isConnected;

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

        // ── Connection ──

        /// <summary>
        /// Connect to SimpleMcpServer at host:port and perform WebSocket upgrade.
        /// </summary>
        public async Task ConnectAsync(string host, int port)
        {
            Disconnect(); // start fresh

            _cts = new CancellationTokenSource();
            _tcpClient = new TcpClient();

            await _tcpClient.ConnectAsync(host, port);
            _stream = _tcpClient.GetStream();

            // Perform HTTP WebSocket upgrade handshake
            await PerformHandshakeAsync(host, port);

            _isConnected = true;
            OnConnected?.Invoke();

            // Start reading frames on background thread
            _ = ReadLoopAsync(_cts.Token);
        }

        /// <summary>
        /// Disconnect and clean up.
        /// </summary>
        public void Disconnect()
        {
            _cts?.Cancel();
            if (_tcpClient != null)
            {
                try { _tcpClient.Close(); } catch { }
                _tcpClient = null;
            }
            _stream = null;
            _isConnected = false;
        }

        public void Dispose() => Disconnect();

        // ── Send ──

        /// <summary>
        /// Send a text message to the server.
        /// </summary>
        public async Task SendAsync(string message)
        {
            if (!_isConnected || _stream == null)
                throw new InvalidOperationException("Not connected");
            var payload = Encoding.UTF8.GetBytes(message);
            await SendFrameAsync(0x1, payload); // text frame, masked (client→server)
        }

        // ── WebSocket frame sending (client→server, MUST be masked) ──

        private async Task SendFrameAsync(int opcode, byte[] payload)
        {
            var maskKey = GenerateMaskKey();

            var header = new List<byte>();
            header.Add((byte)(0x80 | opcode)); // FIN + opcode

            if (payload.Length < 126)
            {
                header.Add((byte)(0x80 | payload.Length)); // MASK + length
            }
            else if (payload.Length < 65536)
            {
                header.Add(0x80 | 126);
                header.Add((byte)((payload.Length >> 8) & 0xFF));
                header.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                header.Add(0x80 | 127);
                var lenBytes = BitConverter.GetBytes((ulong)payload.Length);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(lenBytes);
                header.AddRange(lenBytes);
            }

            header.AddRange(maskKey); // 4-byte mask key

            await _stream.WriteAsync(header.ToArray(), 0, header.Count);

            // Mask payload
            var masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ maskKey[i % 4]);

            await _stream.WriteAsync(masked, 0, masked.Length);
            await _stream.FlushAsync();
        }

        // ── WebSocket frame reading (server→client, NOT masked) ──

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var headerBuf = new byte[2];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var read = await ReadExactAsync(headerBuf, 0, 2, token);
                    if (read < 2) break;

                    int opcode = headerBuf[0] & 0x0F;
                    bool masked = (headerBuf[1] & 0x80) != 0;
                    long payloadLength = headerBuf[1] & 0x7F;

                    // Extended payload length
                    if (payloadLength == 126)
                    {
                        var ext = new byte[2];
                        await ReadExactAsync(ext, 0, 2, token);
                        payloadLength = (ext[0] << 8) | ext[1];
                    }
                    else if (payloadLength == 127)
                    {
                        var ext = new byte[8];
                        await ReadExactAsync(ext, 0, 8, token);
                        payloadLength = 0;
                        for (int i = 0; i < 8; i++)
                            payloadLength = (payloadLength << 8) | ext[i];
                    }

                    // Mask key (server frames shouldn't be masked, but handle per spec)
                    byte[] maskKey = null;
                    if (masked)
                    {
                        maskKey = new byte[4];
                        await ReadExactAsync(maskKey, 0, 4, token);
                    }

                    // Payload
                    var payload = new byte[payloadLength];
                    long totalRead = 0;
                    while (totalRead < payloadLength)
                    {
                        var chunk = await _stream.ReadAsync(payload, (int)totalRead,
                            (int)(payloadLength - totalRead), token);
                        if (chunk <= 0) break;
                        totalRead += chunk;
                    }

                    // Unmask if needed
                    if (masked && maskKey != null)
                    {
                        for (long i = 0; i < payloadLength; i++)
                            payload[i] ^= maskKey[i % 4];
                    }

                    switch (opcode)
                    {
                        case 0x8: // Close
                            return;
                        case 0x9: // Ping — respond with Pong
                            await SendFrameAsync(0xA, payload);
                            break;
                        case 0x1: // Text
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

            _isConnected = false;
            OnDisconnected?.Invoke();
        }

        // ── HTTP WebSocket upgrade (client side) ──

        private async Task PerformHandshakeAsync(string host, int port)
        {
            var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            var request = $"GET / HTTP/1.1\r\n"
                        + $"Host: {host}:{port}\r\n"
                        + $"Upgrade: websocket\r\n"
                        + $"Connection: Upgrade\r\n"
                        + $"Sec-WebSocket-Key: {key}\r\n"
                        + $"Sec-WebSocket-Version: 13\r\n"
                        + $"\r\n";

            var bytes = Encoding.UTF8.GetBytes(request);
            await _stream.WriteAsync(bytes, 0, bytes.Length);

            // Read server response
            var buffer = new byte[4096];
            var read = await _stream.ReadAsync(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, read);

            if (!response.Contains("101") || !response.Contains("Sec-WebSocket-Accept"))
            {
                throw new Exception($"WebSocket handshake failed: server returned {response.Substring(0, Math.Min(response.Length, 100))}");
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

        private async Task<int> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                var read = await _stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (read <= 0) break;
                totalRead += read;
            }
            return totalRead;
        }
    }
}
