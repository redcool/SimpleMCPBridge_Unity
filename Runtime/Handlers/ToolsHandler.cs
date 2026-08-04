using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Dynamic tool registration control: enable/disable tools by category and
    /// list available categories. Category filters persist across Play Mode
    /// transitions because MCPToolRegistry stores them statically.
    /// </summary>
    [MCPToolClass]
    public class ToolsHandler
    {
        [MCPTool(MCPMethodConst.TOOLS_LIST_CATEGORIES,
            "List all tool categories with tool counts and enabled state. " +
            "Params: none. Returns an array of {category, count, enabled}. " +
            "Use with tools.enable/tools.disable to prune the tool list per task.")]
        public static string ListCategories(string paramsJson)
        {
            try
            {
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("categories", MCPToolRegistry.Instance.ListCategoriesJson())
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"tools.list_categories failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.TOOLS_ENABLE,
            "Enable tool categories for registration. " +
            "Params: 'categories' (string[] req — e.g. [\"Scene\",\"Input\",\"Game\"]), " +
            "or 'all' (bool opt, default false — enable every category). " +
            "Enabled tools are re-registered and the new tool list is pushed to the server. " +
            "Takes effect immediately.")]
        public static string Enable(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var enableAll = GetOptionalBool(args, "all").GetValueOrDefault(false);

                if (enableAll)
                {
                    MCPToolRegistry.SetEnabledCategories(null);
                    return FinishChange("all categories enabled");
                }

                var cats = ParseStringArrayArg(args, "categories");
                if (cats == null || cats.Count == 0)
                    return ErrorJson("tools.enable requires 'categories' (string[]) or 'all'=true");

                MCPToolRegistry.AddEnabledCategories(cats);
                return FinishChange($"{cats.Count} categories enabled: {string.Join(", ", cats)}");
            }
            catch (Exception ex)
            {
                return ErrorJson($"tools.enable failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.TOOLS_DISABLE,
            "Disable tool categories so they are no longer registered. " +
            "Params: 'categories' (string[] req — e.g. [\"AssetBundle\",\"Recording\"]), " +
            "or 'all' (bool opt, default false — disable every category except the tools.* control tools). " +
            "Disabled tools are removed from the registered list and the new tool list is pushed to the server. " +
            "Use tools.reset to re-enable everything.")]
        public static string Disable(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var disableAll = GetOptionalBool(args, "all").GetValueOrDefault(false);

                if (disableAll)
                {
                    MCPToolRegistry.SetEnabledCategories(new[] { "Tools" });
                    return FinishChange("all categories except 'Tools' disabled");
                }

                var cats = ParseStringArrayArg(args, "categories");
                if (cats == null || cats.Count == 0)
                    return ErrorJson("tools.disable requires 'categories' (string[]) or 'all'=true");

                MCPToolRegistry.RemoveEnabledCategories(cats);
                return FinishChange($"{cats.Count} categories disabled: {string.Join(", ", cats)}");
            }
            catch (Exception ex)
            {
                return ErrorJson($"tools.disable failed: {ex.Message}");
            }
        }

        [MCPTool(MCPMethodConst.TOOLS_RESET,
            "Reset tool registration to defaults — re-enable every category. " +
            "Params: none. The full tool list is pushed to the server again.")]
        public static string Reset(string paramsJson)
        {
            try
            {
                MCPToolRegistry.SetEnabledCategories(null);
                return FinishChange("all categories re-enabled");
            }
            catch (Exception ex)
            {
                return ErrorJson($"tools.reset failed: {ex.Message}");
            }
        }

        private static string FinishChange(string summary)
        {
            var bridge = BridgeClient.Default;
            if (bridge != null)
                bridge.ReRegisterTools();
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("summary", JsonHelper.EscapeString(summary)),
                ("categories", MCPToolRegistry.Instance.ListCategoriesJson())
            );
        }

        private static List<string> ParseStringArrayArg(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var raw) || raw == null) return null;
            if (raw is System.Collections.IList list)
            {
                var result = new List<string>();
                foreach (var item in list)
                    result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                return result;
            }
            // ParseJsonValue returns arrays containing strings as raw JSON string
            // (e.g. "[\"Nav\",\"Audio\"]") — parse it manually.
            var rawStr = raw.ToString();
            if (string.IsNullOrWhiteSpace(rawStr) || !rawStr.TrimStart().StartsWith("["))
                return null;

            var trimmed = rawStr.Trim().TrimStart('[').TrimEnd(']').Trim();
            if (string.IsNullOrEmpty(trimmed))
                return new List<string>();

            var result2 = new List<string>();
            bool inStr = false;
            int start = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                    inStr = !inStr;
                if (!inStr && c == ',')
                {
                    var item = trimmed.Substring(start, i - start).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(item))
                        result2.Add(item);
                    start = i + 1;
                }
            }
            if (start < trimmed.Length)
            {
                var item = trimmed.Substring(start).Trim().Trim('"');
                if (!string.IsNullOrEmpty(item))
                    result2.Add(item);
            }
            return result2;
        }
    }
}
