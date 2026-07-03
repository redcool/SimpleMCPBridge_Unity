using SimpleMCPBridge.Runtime.Models;
using System;
using UnityEngine;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Receives raw JSON-RPC messages from WebSocket clients,
    /// routes to the appropriate handler via MCPToolRegistry, and returns a response.
    ///
    /// Handlers are auto-discovered at construction time by scanning
    /// all assemblies for [MCPTool]-annotated methods (uses TypeCache
    /// in Editor, assembly scan in Runtime).
    /// </summary>
    public class MessageRouter
    {
        private readonly MCPToolRegistry _registry;

        public MessageRouter()
        {
            _registry = new MCPToolRegistry();
            _registry.AutoRegisterAll();
        }

        /// <summary>
        /// Returns JSON array of registered tools for the `register_tools` message.
        /// </summary>
        public string GetToolsJson()
        {
            return _registry.ListToolsJson();
        }

        /// <summary>
        /// Handle a raw JSON message from a client.
        /// Returns the JSON response string, or null if no response needed (e.g. notification).
        /// </summary>
        public string HandleMessage(string rawMessage)
        {
            if (string.IsNullOrEmpty(rawMessage))
                return null;

            MCPRequest request = null;
            try
            {
                request = JsonUtility.FromJson<MCPRequest>(rawMessage);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SimpleMCPBridge] Failed to parse message: {ex.Message}");
                return BuildErrorResponse(null, "Failed to parse request JSON");
            }

            if (request == null || string.IsNullOrEmpty(request.method))
                return null;

            return Dispatch(request);
        }

        private string Dispatch(MCPRequest request)
        {
            // ── Special built-in: list all registered tools ──
            if (request.method == MCPMethodConst.LIST_TOOLS)
                return BuildSuccessResponse(request.id, _registry.ListToolsJson());

            // ── Route to registered tool handler ──
            if (!_registry.HasTool(request.method))
                return BuildErrorResponse(request.id, $"Unknown method: {request.method}");

            try
            {
                var result = _registry.Dispatch(request.method, request.paramsJson);
                return BuildSuccessResponse(request.id, result);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SimpleMCPBridge] Error handling '{request.method}': {ex.Message}");
                return BuildErrorResponse(request.id, ex.Message);
            }
        }

        private string BuildErrorResponse(string requestId, string errorMessage)
        {
            var response = new MCPResponse
            {
                id = requestId,
                result = null,
                error = errorMessage
            };
            return JsonUtility.ToJson(response);
        }

        private string BuildSuccessResponse(string requestId, string resultJson)
        {
            // Build manually so result is embedded as raw JSON, not an escaped string.
            return $@"{{""id"":{JsonHelper.EscapeString(requestId ?? "")},""result"":{resultJson ?? "null"},""error"":null}}";
        }
    }
}
// mcp-revision: 181632
