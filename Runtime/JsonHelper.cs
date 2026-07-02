using System;
using System.Text;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Minimal JSON helpers to complement JsonUtility.
    /// JsonUtility cannot handle top-level arrays, so we provide
    /// wrappers and manual builders for common patterns.
    /// </summary>
    internal static class JsonHelper
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
            var sb = new StringBuilder("{");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append('"');
                sb.Append(fields[i].key);
                sb.Append("\":");
                sb.Append(fields[i].valueJson);
            }
            sb.Append("}");
            return sb.ToString();
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
            var sb = new StringBuilder("[");
            for (int i = 0; i < elementJsons.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(elementJsons[i]);
            }
            sb.Append("]");
            return sb.ToString();
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
            if (values == null || values.Length == 0)
                return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(values[i].ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Build a JSON string array from string values.
        /// </summary>
        public static string StringArrayJson(string[] values)
        {
            if (values == null || values.Length == 0)
                return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(EscapeString(values[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
// mcp-revision: 181632
