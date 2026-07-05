using System;
using System.Threading.Tasks;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Common interface for WebSocket client implementations.
    /// Both WebSocketClient (custom RFC 6455) and NetWebSocketClient (ClientWebSocket) implement this.
    /// </summary>
    public interface IWebSocketClient : IDisposable
    {
        event Action<string> OnMessageReceived;
        event Action OnConnected;
        event Action OnDisconnected;
        event Action<string> OnError;

        bool IsConnected { get; }
        bool IsConnecting { get; }

        Task ConnectAsync(string host, int port);
        Task SendAsync(string message);
        void Disconnect();
    }
}
