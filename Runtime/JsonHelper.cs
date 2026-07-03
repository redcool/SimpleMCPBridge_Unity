using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Minimal JSON helpers to complement JsonUtility.
    /// JsonUtility cannot handle top-level arrays, so we provide
    /// wrappers and manual builders for common patterns.
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Wrap an array in a serializable container for JsonUtility.ToJson.
        /// </summary>
        [Serializable]
        private class ArrayWrapper<T>
        {
            public T[] items;
        }

        /// <summary>
        /// Serialize an array to JSON using a wrapper.
        /// Produces: { "items": [...] }
        /// </summary>
        public static string ToArrayJson<T>(T[] array)
        {
            var wrapper = new ArrayWrapper<T> { items = array };
            return UnityEngine.JsonUtility.ToJson(wrapper);
        }

        /// <summary>
        /// Build a JSON object string from key-value pairs.
        /// Values are automatically quoted as JSON strings.
        /// </summary>
        public static string BuildJsonObject(params (string key, string valueJson)[] fields)
        {
            if (fields == null || fields.Length == 0) return "{}";
            return $"{{{string.Join(",", fields.Select(f => $"\"{f.key}\":{f.valueJson}"))}}}";
        }

        /// <summary>
        /// Escape a string for use inside a JSON string value.
        /// </summary>
        public static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "\"\"";
            
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Build a JSON array string from already-encoded JSON value strings.
        /// </summary>
        public static string BuildJsonArray(params string[] elementJsons)
        {
            if (elementJsons == null || elementJsons.Length == 0) return "[]";
            return $"[{string.Join(",", elementJsons)}]";
        }

        /// <summary>
        /// Quick bool value JSON.
        /// </summary>
        public static string BoolJson(bool value) => value ? "true" : "false";

        /// <summary>
        /// Quick number JSON from float array (e.g. position).
        /// </summary>
        public static string FloatArrayJson(float[] values)
        {
            if (values == null || values.Length == 0) return "[]";
            return $"[{string.Join(",", values.Select(v => v.ToString("G", CultureInfo.InvariantCulture)))}]";
        }

        /// <summary>
        /// Build a JSON string array from string values.
        /// </summary>
        public static string StringArrayJson(string[] values)
        {
            if (values == null || values.Length == 0) return "[]";
            return $"[{string.Join(",", values.Select(EscapeString))}]";
        }
    }
}
// mcp-revision: 181632
