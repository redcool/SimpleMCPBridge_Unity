using System;
using System.Collections.Generic;
using System.Globalization;
using SimpleMCPBridge.Runtime.Models;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Static utility class for MCP tool parameter parsing and response building.
    /// Use via: using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
    /// </summary>
    public static class HandlerUtils
    {
        // ── JSON param parsing (minimal, no external JSON library needed) ──

        public static Dictionary<string, object> ParseJsonObject(string json)
        {
            var dict = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(json) || json.Trim() == "{}")
                return dict;

            try
            {
                var trimmed = json.Trim().TrimStart('{').TrimEnd('}');
                var parts = SplitJsonTopLevel(trimmed);

                foreach (var part in parts)
                {
                    var colonIdx = part.IndexOf(':');
                    if (colonIdx < 0) continue;

                    var key = part.Substring(0, colonIdx).Trim().Trim('"');
                    var valueStr = part.Substring(colonIdx + 1).Trim();

                    dict[key] = ParseJsonValue(valueStr);
                }
            }
            catch
            {
                // Silently return partial parse on malformed input
            }

            return dict;
        }

        public static List<string> SplitJsonTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            bool inString = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\'))
                    inString = !inString;

                if (!inString)
                {
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        parts.Add(s.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }

            if (start < s.Length)
                parts.Add(s.Substring(start));

            return parts;
        }

        public static object ParseJsonValue(string s)
        {
            s = s.Trim();
            if (s == "null") return null;
            if (s == "true") return true;
            if (s == "false") return false;

            if (s.StartsWith("\"") && s.EndsWith("\""))
                return s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n");

            if (s.StartsWith("["))
            {
                var inner = s.Trim('[', ']').Trim();
                if (string.IsNullOrEmpty(inner)) return new float[0];

                var items = inner.Split(',');
                var floats = new List<float>();
                foreach (var item in items)
                {
                    var trimmed = item.Trim();
                    if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        floats.Add(f);
                }
                return floats.ToArray();
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                if (num == Math.Truncate(num) && num >= int.MinValue && num <= int.MaxValue)
                    return (int)num;
                return num;
            }

            return s;
        }

        // ── Typed accessors ──

        public static string GetString(Dictionary<string, object> dict, string key, string defaultValue = "")
        {
            return dict.TryGetValue(key, out var v) ? v?.ToString() ?? defaultValue : defaultValue;
        }

        public static string GetRequiredString(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return v?.ToString() ?? throw new ArgumentException($"Parameter '{key}' cannot be null");
        }

        public static int GetRequiredInt(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        public static int? GetOptionalInt(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var v) && v != null)
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static bool GetRequiredBool(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            if (v is bool b) return b;
            var s = v?.ToString();
            if (bool.TryParse(s, out var result)) return result;
            throw new ArgumentException($"Parameter '{key}' must be a boolean, got '{s}'");
        }

        public static float[] GetOptionalFloatArray(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var v) && v is float[] arr)
                return arr;
            return null;
        }

        public static object GetRawValue(Dictionary<string, object> dict, string key)
        {
            dict.TryGetValue(key, out var v);
            return v;
        }

        // ── Response builders ──

        public static string BuildObjectRefArrayJson(List<UnityObjectRef> refs)
        {
            var elementJsons = new List<string>();
            foreach (var r in refs)
            {
                elementJsons.Add(JsonUtility.ToJson(r));
            }
            return JsonHelper.BuildJsonArray(elementJsons.ToArray());
        }

        public static string ErrorJson(string message)
        {
            return JsonHelper.BuildJsonObject(
                ("success", "false"),
                ("error", JsonHelper.EscapeString(message))
            );
        }
    }
}
