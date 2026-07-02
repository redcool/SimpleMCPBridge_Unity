using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;
using SceneManagement = UnityEngine.SceneManagement;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles scene-related MCP operations: query, create, delete, transform, component properties.
    /// Uses only UnityEngine APIs (no UnityEditor dependency) so it works in both Editor and Runtime.
    /// </summary>
    public class SceneHandler
    {
        [MCPTool("scene.get_hierarchy", "Get the full scene hierarchy as a tree of objects with position, components, and children")]
        public string GetHierarchy(string paramsJson)
        {
            var rootObjects = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var entries = new List<string>();
            foreach (var root in rootObjects)
            {
                entries.Add(BuildTreeEntry(root));
            }
            return JsonHelper.BuildJsonArray(entries.ToArray());
        }

        private string BuildTreeEntry(GameObject go)
        {
            // Collect component names
            var components = go.GetComponents<Component>();
            var compNames = new List<string>();
            foreach (var c in components)
            {
                if (c != null)
                    compNames.Add(c.GetType().Name);
            }

            // Collect children
            var childJsons = new List<string>();
            foreach (Transform child in go.transform)
            {
                childJsons.Add(BuildTreeEntry(child.gameObject));
            }

            var pos = go.transform.position;
            return JsonHelper.BuildJsonObject(
                ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("name", JsonHelper.EscapeString(go.name)),
                ("active", JsonHelper.BoolJson(go.activeSelf)),
                ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                ("components", JsonHelper.StringArrayJson(compNames.ToArray())),
                ("children", JsonHelper.BuildJsonArray(childJsons.ToArray()))
            );
        }

        [MCPTool("scene.get_objects", "Find GameObjects in the scene by optional name filter")]
        public string GetObjects(string paramsJson)
        {
            var filter = ParseJsonObject(paramsJson);
            filter.TryGetValue("nameContains", out var nameFilterObj);
            var nameFilter = nameFilterObj as string;

            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            var refs = new List<UnityObjectRef>();

            foreach (var go in allObjects)
            {
                if (!string.IsNullOrEmpty(nameFilter) &&
                    !go.name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                refs.Add(UnityObjectRef.FromGameObject(go));
            }

            // Build result JSON manually since JsonUtility can't do top-level arrays
            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool("scene.create_object", "Create a new GameObject with optional name, position, rotation, scale, and parent")]
        public string CreateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var name = GetString(args, "name", "New GameObject");
            var parentId = GetOptionalInt(args, "parentId");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var go = new GameObject(name);

            if (parentId.HasValue)
            {
                var parent = FindObjectById(parentId.Value);
                if (parent != null)
                    go.transform.SetParent(parent.transform);
            }

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            var ref_ = UnityObjectRef.FromGameObject(go);
            return JsonUtility.ToJson(ref_);
        }

        [MCPTool("scene.delete_object", "Destroy a GameObject by its instanceId")]
        public string DeleteObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var instanceId = GetRequiredInt(args, "instanceId");

            var go = FindObjectById(instanceId);
            if (go == null)
                return JsonHelper.BuildJsonObject(
                    ("success", "false"),
                    ("error", JsonHelper.EscapeString("Object not found"))
                );

            Object.DestroyImmediate(go);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool("scene.set_transform", "Set position, rotation, and/or scale of a GameObject by instanceId")]
        public string SetTransform(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var instanceId = GetRequiredInt(args, "instanceId");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var go = FindObjectById(instanceId);
            if (go == null)
                return JsonHelper.BuildJsonObject(
                    ("success", "false"),
                    ("error", JsonHelper.EscapeString("Object not found"))
                );

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool("scene.set_component_property", "Set a serializable property value on a component of a GameObject")]
        public string SetComponentProperty(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var instanceId = GetRequiredInt(args, "instanceId");
            var componentType = GetRequiredString(args, "componentType");
            var propertyName = GetRequiredString(args, "propertyName");
            var rawValue = GetRawValue(args, "value");

            var go = FindObjectById(instanceId);
            if (go == null)
                return ErrorJson("Object not found");

            // Try GetComponent by type name (the MCP client sends a type name like "Transform")
            var component = FindComponentByTypeName(go, componentType);
            if (component == null)
                return ErrorJson($"Component '{componentType}' not found on object '{go.name}'");

            // Try field first (most serialized Unity properties are fields)
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var field = component.GetType().GetField(propertyName, flags);
            if (field != null)
            {
                var typedValue = ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try property
            var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                var typedValue = ConvertValue(rawValue, prop.PropertyType);
                prop.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try finding a serialized property by name convention
            // (Unity often serializes with 'm_' prefix or different casing)
            var altName = "m_" + propertyName;
            field = component.GetType().GetField(altName, flags);
            if (field != null)
            {
                var typedValue = ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            return ErrorJson($"Property/field '{propertyName}' not found on {componentType}");
        }

        // ──────────────────────────────────────────────
        //  Internal helpers
        // ──────────────────────────────────────────────

        private GameObject FindObjectById(int instanceId)
        {
            // We can't do a direct lookup by instanceId, so we scan.
            // For large scenes this is slow, but acceptable for Phase 1.
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in allObjects)
            {
                if (go.GetInstanceID() == instanceId)
                    return go;
            }
            return null;
        }

        private Component FindComponentByTypeName(GameObject go, string typeName)
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp != null &&
                    string.Equals(comp.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
                    return comp;
            }
            return null;
        }

        private object ConvertValue(object rawValue, Type targetType)
        {
            if (rawValue == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            var rawStr = rawValue.ToString();

            if (targetType == typeof(float)) return Convert.ToSingle(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return Convert.ToInt64(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return Convert.ToBoolean(rawValue);
            if (targetType == typeof(string)) return rawValue.ToString();
            if (targetType == typeof(Vector2))
            {
                if (rawValue is float[] a2 && a2.Length >= 2)
                    return new Vector2(a2[0], a2[1]);
                return Vector2.zero;
            }
            if (targetType == typeof(Vector3))
            {
                if (rawValue is float[] a3 && a3.Length >= 3)
                    return new Vector3(a3[0], a3[1], a3[2]);
                return Vector3.zero;
            }
            if (targetType == typeof(Vector4))
            {
                if (rawValue is float[] a4v && a4v.Length >= 4)
                    return new Vector4(a4v[0], a4v[1], a4v[2], a4v[3]);
                return Vector4.zero;
            }
            if (targetType == typeof(Quaternion))
            {
                if (rawValue is float[] aq && aq.Length >= 4)
                    return new Quaternion(aq[0], aq[1], aq[2], aq[3]);
                return Quaternion.identity;
            }
            if (targetType == typeof(Color))
            {
                if (rawValue is float[] ac && ac.Length >= 4)
                    return new Color(ac[0], ac[1], ac[2], ac[3]);
                if (rawValue is float[] ac3 && ac3.Length >= 3)
                    return new Color(ac3[0], ac3[1], ac3[2], 1f);
                return Color.white;
            }
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, rawStr, ignoreCase: true);
            }

            // Fallback: try string conversion
            return Convert.ChangeType(rawValue, targetType, CultureInfo.InvariantCulture);
        }

        // ── JSON param parsing (minimal, no external JSON library needed) ──

        private Dictionary<string, object> ParseJsonObject(string json)
        {
            var dict = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(json) || json.Trim() == "{}")
                return dict;

            // Minimal parser: uses JsonUtility to get fields, then manual for values.
            // For Phase 1 we handle flat objects with primitive + array values.
            // This is intentionally simple — upgrade to Newtonsoft.Json when needed.

            // Try to parse with JsonUtility first for known structures
            try
            {
                // We use a simple approach: extract key:value pairs
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

        private List<string> SplitJsonTopLevel(string s)
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

        private object ParseJsonValue(string s)
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
                // Return int if whole number within range
                if (num == Math.Truncate(num) && num >= int.MinValue && num <= int.MaxValue)
                    return (int)num;
                return num;
            }

            return s;
        }

        private string GetString(Dictionary<string, object> dict, string key, string defaultValue = "")
        {
            return dict.TryGetValue(key, out var v) ? v?.ToString() ?? defaultValue : defaultValue;
        }

        private string GetRequiredString(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return v?.ToString() ?? throw new ArgumentException($"Parameter '{key}' cannot be null");
        }

        private int GetRequiredInt(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        private int? GetOptionalInt(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var v) && v != null)
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return null;
        }

        private float[] GetOptionalFloatArray(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var v) && v is float[] arr)
                return arr;
            return null;
        }

        private object GetRawValue(Dictionary<string, object> dict, string key)
        {
            dict.TryGetValue(key, out var v);
            return v;
        }

        private string BuildObjectRefArrayJson(List<UnityObjectRef> refs)
        {
            var elementJsons = new List<string>();
            foreach (var r in refs)
            {
                elementJsons.Add(JsonUtility.ToJson(r));
            }
            return JsonHelper.BuildJsonArray(elementJsons.ToArray());
        }

        private string ErrorJson(string message)
        {
            return JsonHelper.BuildJsonObject(
                ("success", "false"),
                ("error", JsonHelper.EscapeString(message))
            );
        }
    }
}
// mcp-revision: 181632
