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

        /// <summary>
        /// Singleton accessor for the most recently constructed router.
        /// Used by game.batch to dispatch sub-calls without a tool→router reference.
        /// Set in the constructor; safe because BridgeClient owns exactly one router.
        /// </summary>
        public static MessageRouter Instance { get; private set; }

        public MessageRouter()
        {
            Instance = this;
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
        /// Public dispatch for sub-tool invocation (used by game.batch).
        /// Returns the tool's result JSON string (the raw result, not a JSON-RPC envelope).
        /// On unknown tool or exception, returns an error JSON object instead of throwing.
        /// </summary>
        public string DispatchTool(string method, string paramsJson)
        {
            if (string.IsNullOrEmpty(method))
                return @"{""success"":false,""error"":""Empty tool name""}";
            if (!_registry.HasTool(method))
                return $@"{{""success"":false,""error"":""Unknown tool: {JsonHelper.EscapeString(method)}""}}";
            try
            {
                return _registry.Dispatch(method, paramsJson);
            }
            catch (Exception ex)
            {
                DebugUtils.LogError($"[Batch dispatch] Error calling '{method}': {ex.Message}");
                return $@"{{""success"":false,""error"":""{JsonHelper.EscapeString(ex.Message)}""}}";
            }
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
                DebugUtils.LogError($"[SimpleMCPBridge] Failed to parse message: {ex.Message}");
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
                DebugUtils.LogError($"[SimpleMCPBridge] Error handling '{request.method}': {ex.Message}");
                return BuildErrorResponse(request.id, ex.Message);
            }
        }

        private string BuildErrorResponse(string requestId, string errorMessage)
        {
            // JSON-RPC 2.0: error response MUST NOT include "result" field.
            // Error must be an object with "code" (int) and "message" (string) per spec.
            // id must be null (not empty string) when requestId is null (Fix P2#1)
            string idJson = requestId != null ? JsonHelper.EscapeString(requestId) : "null";
            return $@"{{""id"":{idJson},""error"":{{""code"":-32603,""message"":{JsonHelper.EscapeString(errorMessage)}}}}}";
        }

        private string BuildSuccessResponse(string requestId, string resultJson)
        {
            // Build manually so result is embedded as raw JSON, not an escaped string.
            // JSON-RPC 2.0: success response MUST NOT include "error" field.
            // id must be null (not empty string) when requestId is null (Fix P2#1)
            string idJson = requestId != null ? JsonHelper.EscapeString(requestId) : "null";
            return $@"{{""id"":{idJson},""result"":{resultJson ?? "null"}}}";
        }
    }
}
// mcp-revision: 181632
