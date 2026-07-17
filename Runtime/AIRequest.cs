using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Allows Unity game code to send AI inference requests through the MCP Bridge.
    ///
    /// Usage:
    /// <code>
    /// // Wire up to the bridge (call once when bridge connects)
    /// AIRequest.Register(mcpBridgeInstance);
    ///
    /// // Send an AI request (async, awaitable)
    /// var response = await AIRequest.AskAsync(
    ///     prompt: "Decide what the NPC should do next.",
    ///     context: new Dictionary&lt;string, object&gt; {
    ///         { "npc_state", "idle" },
    ///         { "nearby_entities", 3 }
    ///     }
    /// );
    /// Debug.Log($"AI decision: {response}");
    /// </code>
    ///
    /// The Server must have LLM configured in config.json (apiKey, baseUrl, model).
    /// </summary>
    public static class AIRequest
    {
        /// <summary>
        /// Called when an AI response arrives from the server.
        /// Parameters: (requestId, text).
        /// requestId lets callers match responses to their requests when multiple are in-flight.
        /// </summary>
        public static event Action<string, string> OnResponseReceived;

        private static BridgeClient _bridge;
        private static readonly Dictionary<string, TaskCompletionSource<string>> _pending = new();
        private static int _requestCounter;

        /// <summary>
        /// Register the MCPBridge instance. Call this when the bridge connects
        /// (e.g. from OnConnectedSuccess callback or when the bridge is created).
        /// </summary>
        public static void Register(BridgeClient bridge)
        {
            if (bridge == null) return;

            // Unsubscribe from previous bridge if any
            if (_bridge != null)
            {
                _bridge.OnAIResponse -= HandleAIResponse;
                _bridge.OnConnectedSuccess -= OnBridgeConnected;
            }

            _bridge = bridge;
            _bridge.OnAIResponse += HandleAIResponse;
            _bridge.OnConnectedSuccess += OnBridgeConnected;
        }

        /// <summary>
        /// Unregister and clean up. Call when the bridge disconnects.
        /// </summary>
        public static void Unregister()
        {
            if (_bridge == null) return;
            _bridge.OnAIResponse -= HandleAIResponse;
            _bridge.OnConnectedSuccess -= OnBridgeConnected;
            _bridge = null;
        }

        private static void OnBridgeConnected()
        {
            // Bridge reconnected — re-register
            // (OnConnectedSuccess is already wired to this handler via Register)
        }

        /// <summary>
        /// Send an AI inference request and wait for the response.
        /// </summary>
        /// <param name="prompt">The user prompt / question for the AI.</param>
        /// <param name="context">Optional context data to send alongside the prompt.</param>
        /// <param name="system">Optional system prompt to guide the AI's behavior.</param>
        /// <param name="messages">Optional conversation history (list of {"role","content"} dicts). Role: "user"|"assistant"|"system".</param>
        /// <param name="cancellationToken">Cancellation token (optional).</param>
        /// <returns>The AI's text response.</returns>
        /// <exception cref="InvalidOperationException">Bridge is not connected.</exception>
        /// <exception cref="TimeoutException">Request timed out (90s).</exception>
        public static async Task<string> AskAsync(
            string prompt,
            Dictionary<string, object> context = null,
            string system = null,
            System.Collections.IList messages = null,
            CancellationToken cancellationToken = default)
        {
            if (_bridge == null || !_bridge.IsConnected)
            {
                throw new InvalidOperationException("MCP Bridge is not connected. Call Register(bridge) first or ensure the bridge is connected.");
            }

            var requestId = $"ai_{Interlocked.Increment(ref _requestCounter)}_{DateTime.Now:HHmmssfff}";
            var tcs = new TaskCompletionSource<string>();

            using (var registration = cancellationToken.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
            {
                _pending[requestId] = tcs;

                // Build the JSON message
                var contextObjJson = BuildContextJson(context);
                var messagesJson = BuildMessagesJson(messages);

                string message;
                if (!string.IsNullOrEmpty(system))
                {
                    message = $"{{\"type\":\"ai_request\",\"requestId\":\"{requestId}\",\"prompt\":{JsonHelper.EscapeString(prompt)},\"context\":{contextObjJson},\"system\":{JsonHelper.EscapeString(system)},\"messages\":{messagesJson}}}";
                }
                else if (messages != null && messages.Count > 0)
                {
                    message = $"{{\"type\":\"ai_request\",\"requestId\":\"{requestId}\",\"prompt\":{JsonHelper.EscapeString(prompt)},\"context\":{contextObjJson},\"messages\":{messagesJson}}}";
                }
                else
                {
                    message = $"{{\"type\":\"ai_request\",\"requestId\":\"{requestId}\",\"prompt\":{JsonHelper.EscapeString(prompt)},\"context\":{contextObjJson}}}";
                }

                // Queue the send on the main thread if needed, or send directly
                try
                {
                    await _bridge.SendAsync(message);
                }
                catch (Exception ex)
                {
                    _pending.Remove(requestId);
                    throw new InvalidOperationException($"Failed to send AI request: {ex.Message}");
                }

                // Wait for response with timeout (90s)
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                {
                    try
                    {
                        return await tcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new Exception("AI request cancelled by caller");
                        throw new TimeoutException($"AI request timed out after 90 seconds (requestId: {requestId}). Make sure the server has LLM configured and is responding.");
                    }
                    finally
                    {
                        _pending.Remove(requestId);
                    }
                }
            }
        }

        /// <summary>
        /// Fire-and-forget AI request. Response is delivered via OnResponseReceived.
        /// </summary>
        public static async void Ask(
            string prompt,
            Dictionary<string, object> context = null,
            string system = null)
        {
            try
            {
                await AskAsync(prompt, context, system);
            }
            catch (Exception ex)
            {
                DebugUtils.LogWarning($"[AIRequest] Ask failed: {ex.Message}");
                OnResponseReceived?.Invoke(null, null);
            }
        }

        private static void HandleAIResponse(string requestId, string text)
        {
            if (_pending.TryGetValue(requestId, out var tcs))
            {
                _pending.Remove(requestId); // Fix 4: prevent leak for fire-and-forget calls
                if (text != null)
                    tcs.TrySetResult(text);
                else
                    tcs.TrySetResult(null); // null text means error already embedded
                OnResponseReceived?.Invoke(requestId, text);
            }
        }

        /// <summary>
        /// Build a JSON object string from a dictionary.
        /// Values are converted to JSON strings/arrays as appropriate.
        /// </summary>
        private static string BuildContextJson(Dictionary<string, object> dict)
        {
            if (dict == null || dict.Count == 0) return "{}";

            var parts = new List<string>();
            foreach (var kv in dict)
            {
                var key = JsonHelper.EscapeString(kv.Key);
                var valueJson = ObjectToJson(kv.Value);
                parts.Add($"{key}:{valueJson}");
            }
            return $"{{{string.Join(",", parts)}}}";
        }

        /// <summary>
        /// Serialize a messages array to JSON.
        /// Format: [{"role":"user","content":"..."}, {"role":"assistant","content":"..."}]
        /// </summary>
        private static string BuildMessagesJson(System.Collections.IList messages)
        {
            if (messages == null || messages.Count == 0) return "[]";
            return ObjectToJson(messages);
        }

        private static string ObjectToJson(object value)
        {
            if (value == null) return "null";
            if (value is string s) return JsonHelper.EscapeString(s);
            if (value is bool b) return b ? "true" : "false";
            if (value is int or long or float or double)
            {
                return Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value is System.Collections.IList list)
            {
                var items = new List<string>();
                foreach (var item in list)
                    items.Add(ObjectToJson(item));
                return $"[{string.Join(",", items)}]";
            }
            if (value is Dictionary<string, object> nestedDict)
                return BuildContextJson(nestedDict);
            // Fallback: try string conversion
            return JsonHelper.EscapeString(value.ToString());
        }
    }
}
