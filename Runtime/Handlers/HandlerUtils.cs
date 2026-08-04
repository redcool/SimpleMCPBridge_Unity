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
                // If the array contains objects or strings, return raw JSON string
                // so callers can parse it with ParseJsonArrayOfObjects.
                if (s.Contains('{') || s.Contains('"'))
                    return s;

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

        public static bool? GetOptionalBool(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return null;
            if (v is bool b) return b;
            var s = v?.ToString();
            if (bool.TryParse(s, out var result)) return result;
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

        /// <summary>
        /// Convert an unknown object (from JSON parsing) to float[].
        /// Handles float[], int[], double[], IList (e.g. List{object}), etc.
        /// Returns null if the value is not a numeric collection.
        /// </summary>
        public static float[] ToFloatArray(object v)
        {
            if (v == null) return null;
            if (v is float[] arr) return arr;
            if (v is int[] intArr) return Array.ConvertAll(intArr, i => (float)i);
            if (v is double[] dblArr) return Array.ConvertAll(dblArr, d => (float)d);
            if (v is System.Collections.IList list)
            {
                var result = new List<float>(list.Count);
                foreach (var item in list)
                    result.Add(Convert.ToSingle(item, CultureInfo.InvariantCulture));
                return result.ToArray();
            }
            return null;
        }

        public static float[] GetOptionalFloatArray(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v) || v == null)
                return null;
            return ToFloatArray(v);
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

        // ── GameObject filters (shared by scene.get_objects_by_* tools) ──

        /// <summary>
        /// Apply common filters (nameContains, layer, layerName) to a candidate GameObject.
        /// Returns true if the object passes all filters (i.e. should be included).
        /// </summary>
        public static bool FilterGameObject(GameObject go, Dictionary<string, object> args)
        {
            // nameContains
            if (args.TryGetValue("nameContains", out var nameObj) && nameObj is string nameFilter && !string.IsNullOrEmpty(nameFilter))
            {
                if (!go.name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // layer (int, 0-31) — higher priority than layerName
            if (args.TryGetValue("layer", out var layerObj) && layerObj != null)
            {
                int layerInt;
                if (layerObj is int i) layerInt = i;
                else if (layerObj is long l) layerInt = (int)l;
                else if (layerObj is double d) layerInt = (int)d;
                else if (layerObj is string s && int.TryParse(s, out var pi)) layerInt = pi;
                else return false; // invalid layer value

                if (go.layer != layerInt) return false;
            }

            // layerName — skip if layer already filtered
            if (args.TryGetValue("layerName", out var layerNameObj) && layerNameObj is string layerNameFilter && !string.IsNullOrEmpty(layerNameFilter))
            {
                if (!LayerMask.LayerToName(go.layer).Equals(layerNameFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Build the transform path from scene root to this GameObject.
        /// e.g. "Canvas/Panel/Button"
        /// </summary>
        public static string GetObjectPath(GameObject go)
        {
            var segments = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
