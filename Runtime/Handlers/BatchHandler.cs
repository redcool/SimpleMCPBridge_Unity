using System.Collections.Generic;
using SimpleMCPBridge;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Batch dispatch: execute multiple tool calls in one Unity frame,
    /// collapsing N+1 network round trips into 1.
    /// </summary>
    [MCPToolClass]
    public class BatchHandler
    {
        private const int MaxBatchCalls = 50;

        [MCPTool(MCPMethodConst.GAME_BATCH,
            "Execute multiple tool calls in one Unity frame. Pass an array of {name, arguments} objects; each call's result is embedded in the response array. Reduces N+1 round trips to 1. Max 50 calls.")]
        public static string Batch(string paramsJson)
        {
            var args = HandlerUtils.ParseJsonObject(paramsJson);
            var callsRaw = HandlerUtils.GetRawValue(args, "calls") as string;

            if (string.IsNullOrEmpty(callsRaw))
                return HandlerUtils.ErrorJson("Missing 'calls' array parameter (expected: {\"calls\":[{\"name\":\"...\",\"arguments\":{...}}]})");

            var trimmed = callsRaw.Trim();
            // Strip outer [ ]
            if (trimmed.StartsWith("[")) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("]")) trimmed = trimmed.Substring(0, trimmed.Length - 1);
            trimmed = trimmed.Trim();

            if (string.IsNullOrEmpty(trimmed))
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", "0"),
                    ("results", "[]")
                );

            var callParts = HandlerUtils.SplitJsonTopLevel(trimmed);
            var results = new List<string>();
            int executed = 0;

            foreach (var callPart in callParts)
            {
                if (executed >= MaxBatchCalls)
                {
                    results.Add($@"{{""name"":"""",""success"":false,""error"":""Batch limit ({MaxBatchCalls}) reached — remaining calls skipped""}}");
                    break;
                }

                var call = HandlerUtils.ParseJsonObject(callPart);
                var name = HandlerUtils.GetString(call, "name", "");
                var argumentsRaw = HandlerUtils.GetRawValue(call, "arguments") as string;

                if (string.IsNullOrEmpty(name))
                {
                    results.Add($@"{{""name"":"""",""success"":false,""error"":""Missing 'name' in call""}}");
                    continue;
                }

                // Guard against recursive batch (infinite loop)
                if (name == MCPMethodConst.GAME_BATCH)
                {
                    results.Add($@"{{""name"":{JsonHelper.EscapeString(name)},""success"":false,""error"":""Recursive game.batch not allowed""}}");
                    continue;
                }

                var argumentsJson = argumentsRaw ?? "{}";
                var subResult = MessageRouter.Instance?.DispatchTool(name, argumentsJson);
                var resultJson = string.IsNullOrEmpty(subResult) ? "null" : subResult;

                results.Add($@"{{""name"":{JsonHelper.EscapeString(name)},""result"":{resultJson}}}");
                executed++;
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("count", executed.ToString()),
                ("results", JsonHelper.BuildJsonArray(results.ToArray()))
            );
        }
    }
}
