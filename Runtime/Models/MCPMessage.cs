using System;

namespace SimpleMCPBridge.Runtime.Models
{
    /// <summary>
    /// JSON-RPC style message from client (MCP Server) to Unity Bridge.
    /// </summary>
    [Serializable]
    public class MCPRequest
    {
        public string id;
        public string method;
        public string paramsJson;
    }

    /// <summary>
    /// JSON-RPC style response from Unity Bridge to client.
    /// </summary>
    [Serializable]
    public class MCPResponse
    {
        public string id;
        /// <summary>
        /// Raw JSON value of the result (object/array/value).
        /// </summary>
        public string result;
        /// <summary>
        /// Error string, null/empty if success.
        /// </summary>
        public string error;
    }

    /// <summary>
    /// Event pushed from Unity Bridge to client (no id, correlation via event type).
    /// </summary>
    [Serializable]
    public class MCPEvent
    {
        public string eventType;
        public string dataJson;
    }
}
// mcp-revision: 181632
