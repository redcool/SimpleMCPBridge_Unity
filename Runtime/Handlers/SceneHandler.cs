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
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles scene-related MCP operations: query, create, delete, transform, component properties.
    /// Uses only UnityEngine APIs (no UnityEditor dependency) so it works in both Editor and Runtime.
    ///
    /// Object resolution priority:
    ///   1. instanceId — fast O(1) lookup via scan, survives renames/moves
    ///   2. path — transform path ("Canvas/Panel/Button"), survives domain reload
    ///   All public tools accept both "instanceId" and "path" parameters.
    /// </summary>
    public class SceneHandler
    {
        [MCPTool(MCPMethodConst.GET_HIERARCHY, "Get the full scene hierarchy as a tree of objects with position, components, children, and transform path")]
        public static string GetHierarchy(string paramsJson)
        {
            var rootObjects = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var entries = new List<string>();
            foreach (var root in rootObjects)
            {
                entries.Add(BuildTreeEntry(root, root.name));
            }
            return JsonHelper.BuildJsonArray(entries.ToArray());
        }

        private static string BuildTreeEntry(GameObject go, string path)
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
                childJsons.Add(BuildTreeEntry(child.gameObject, path + "/" + child.name));
            }

            var pos = go.transform.position;
            return JsonHelper.BuildJsonObject(
                ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("path", JsonHelper.EscapeString(path)),
                ("name", JsonHelper.EscapeString(go.name)),
                ("active", JsonHelper.BoolJson(go.activeSelf)),
                ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                ("components", JsonHelper.StringArrayJson(compNames.ToArray())),
                ("children", JsonHelper.BuildJsonArray(childJsons.ToArray()))
            );
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS, "Find GameObjects in the scene by optional name filter — returns instanceId + path for each")]
        public static string GetObjects(string paramsJson)
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

        [MCPTool(MCPMethodConst.CREATE_OBJECT, "Create a new GameObject with optional name, position, rotation, scale, and parent (by instanceId or path)")]
        public static string CreateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var name = GetString(args, "name", "New GameObject");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var go = new GameObject(name);
            UndoRegisterCreated(go, $"Create {name}");

            var parent = ResolveParentTarget(args);
            if (parent != null)
                go.transform.SetParent(parent.transform);

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            var ref_ = UnityObjectRef.FromGameObject(go);
            return JsonUtility.ToJson(ref_);
        }

        [MCPTool(MCPMethodConst.DELETE_OBJECT, "Destroy a GameObject by instanceId or path")]
        public static string DeleteObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            UndoDestroyObject(go);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_TRANSFORM, "Set position, rotation, and/or scale of a GameObject by instanceId or path")]
        public static string SetTransform(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            UndoRecord(go.transform, "Set Transform");
            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_COMPONENT_PROPERTY, "Set a serializable property value on a component of a GameObject (by instanceId or path)")]
        public static string SetComponentProperty(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");
            var propertyName = GetRequiredString(args, "propertyName");
            var rawValue = GetRawValue(args, "value");

            // Try GetComponent by type name (the MCP client sends a type name like "Transform")
            var component = FindComponentByTypeName(go, componentType);
            if (component == null)
                return ErrorJson($"Component '{componentType}' not found on object '{go.name}'");

            // Try field first (most serialized Unity properties are fields)
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var field = component.GetType().GetField(propertyName, flags);
            if (field != null)
            {
                UndoRecord(component, $"Set {propertyName}");
                var typedValue = ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try property
            var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                UndoRecord(component, $"Set {propertyName}");
                var typedValue = ConvertValue(rawValue, prop.PropertyType);
                prop.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try finding a serialized property by name convention
            // (Unity often serializes with 'm_' prefix or different casing)
            var altName = $"m_{propertyName}";
            field = component.GetType().GetField(altName, flags);
            if (field != null)
            {
                UndoRecord(component, $"Set {propertyName}");
                var typedValue = ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            return ErrorJson($"Property/field '{propertyName}' not found on {componentType}");
        }

        [MCPTool(MCPMethodConst.GET_COMPONENTS, "Get all components on a GameObject by instanceId or path — returns type names and assembly info")]
        public static string GetComponents(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var components = go.GetComponents<Component>();
            var componentJsons = new List<string>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var type = comp.GetType();
                componentJsons.Add(JsonHelper.BuildJsonObject(
                    ("type", JsonHelper.EscapeString(type.Name)),
                    ("fullType", JsonHelper.EscapeString(type.FullName)),
                    ("enabled", JsonHelper.BoolJson(comp is Behaviour b ? b.enabled : true))
                ));
            }

            return JsonHelper.BuildJsonObject(
                ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("objectName", JsonHelper.EscapeString(go.name)),
                ("componentCount", componentJsons.Count.ToString(CultureInfo.InvariantCulture)),
                ("components", JsonHelper.BuildJsonArray(componentJsons.ToArray()))
            );
        }

        [MCPTool(MCPMethodConst.GET_COMPONENT_PROPERTIES, "Get all serializable properties and current values of a component on a GameObject (by instanceId or path)")]
        public static string GetComponentProperties(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");

            var component = FindComponentByTypeName(go, componentType);
            if (component == null) return ErrorJson($"Component '{componentType}' not found on '{go.name}'");

            var flags = BindingFlags.Public | BindingFlags.Instance;
            var props = new List<string>();

            foreach (var field in component.GetType().GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                string valStr;
                try
                {
                    var val = field.GetValue(component);
                    valStr = val?.ToString() ?? "null";
                }
                catch
                {
                    valStr = "<error reading>";
                }
                props.Add(JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(field.Name)),
                    ("type", JsonHelper.EscapeString(field.FieldType.Name)),
                    ("value", JsonHelper.EscapeString(valStr))
                ));
            }

            foreach (var prop in component.GetType().GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                string valStr;
                try
                {
                    var val = prop.GetValue(component);
                    valStr = val?.ToString() ?? "null";
                }
                catch
                {
                    valStr = "<error reading>";
                }
                props.Add(JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(prop.Name)),
                    ("type", JsonHelper.EscapeString(prop.PropertyType.Name)),
                    ("value", JsonHelper.EscapeString(valStr))
                ));
            }

            return JsonHelper.BuildJsonObject(
                ("componentType", JsonHelper.EscapeString(componentType)),
                ("propertyCount", props.Count.ToString(CultureInfo.InvariantCulture)),
                ("properties", JsonHelper.BuildJsonArray(props.ToArray()))
            );
        }

        [MCPTool(MCPMethodConst.SET_ACTIVE, "Enable or disable a GameObject by instanceId or path")]
        public static string SetActive(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var active = GetRequiredBool(args, "active");

            UndoRecord(go, "Set Active");
            go.SetActive(active);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.DUPLICATE_OBJECT, "Duplicate a GameObject by instanceId or path")]
        public static string DuplicateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var clone = Object.Instantiate(go);
            UndoRegisterCreated(clone, $"Duplicate {go.name}");
            clone.name = go.name + " (Copy)";
            var ref_ = UnityObjectRef.FromGameObject(clone);
            return JsonUtility.ToJson(ref_);
        }

        [MCPTool(MCPMethodConst.RENAME, "Rename a GameObject by instanceId or path")]
        public static string Rename(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var name = GetRequiredString(args, "name");

            UndoRecord(go, "Rename");
            go.name = name;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_PARENT, "Set parent of a GameObject by instanceId/path and optional parentId/parentPath. Leave parent empty to unparent to root.")]
        public static string SetParent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            UndoRecord(go.transform, "Set Parent");

            var parent = ResolveParentTarget(args);
            if (parent != null)
                go.transform.SetParent(parent.transform);
            else
                go.transform.SetParent(null);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.ADD_COMPONENT, "Add a component to a GameObject by instanceId or path and type name (e.g. Rigidbody, BoxCollider)")]
        public static string AddComponent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name == componentType && type.IsSubclassOf(typeof(Component)) && !type.IsAbstract)
                    {
                        UndoAddComponent(go, type);
                        return JsonHelper.BuildJsonObject(("success", "true"));
                    }
                }
            }

            return ErrorJson($"Component type '{componentType}' not found in any assembly");
        }

#if UNITY_EDITOR
        [MCPTool(MCPMethodConst.ENTER_PLAY_MODE, "Enter Play Mode in the Unity Editor")]
        public static string EnterPlayMode(string paramsJson)
        {
            UnityEditor.EditorApplication.isPlaying = true;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.EXIT_PLAY_MODE, "Exit Play Mode in the Unity Editor")]
        public static string ExitPlayMode(string paramsJson)
        {
            UnityEditor.EditorApplication.isPlaying = false;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.PAUSE_PLAY_MODE, "Pause or resume Play Mode in the Unity Editor — set paused=true to pause, false to resume")]
        public static string PausePlayMode(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var paused = GetRequiredBool(args, "paused");
            UnityEditor.EditorApplication.isPaused = paused;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.REQUEST_COMPILE, "Request Unity to recompile all scripts (useful after editing C# files externally via filesystem)")]
        public static string RequestCompile(string paramsJson)
        {
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.OPEN_WINDOW, "Open a Unity Editor window by menu path — use the exact path as shown in Unity's menu bar (e.g. 'Tools/SimpleMCPBridge', 'Window/General/Console'). Returns an error if the menu item is not found.")]
        public static string OpenWindow(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var menuPath = GetRequiredString(args, "menuPath");
            var success = UnityEditor.EditorApplication.ExecuteMenuItem(menuPath);
            return JsonHelper.BuildJsonObject(("success", success ? "true" : "false"));
        }

        [MCPTool(MCPMethodConst.GET_PLAY_MODE, "Get current Unity Editor play mode state — returns playing/paused/edit mode status")]
        public static string GetPlayMode(string paramsJson)
        {
            var mode = "edit";
            if (UnityEditor.EditorApplication.isPlaying)
            {
                mode = UnityEditor.EditorApplication.isPaused ? "paused" : "playing";
            }
            return JsonHelper.BuildJsonObject(
                ("isPlaying", JsonHelper.BoolJson(UnityEditor.EditorApplication.isPlaying)),
                ("isPaused", JsonHelper.BoolJson(UnityEditor.EditorApplication.isPaused)),
                ("mode", JsonHelper.EscapeString(mode))
            );
        }
#endif

        // ──────────────────────────────────────────────
        //  Object resolution
        // ──────────────────────────────────────────────

        /// <summary>
        /// Resolve a GameObject from tool arguments.
        /// Priority: instanceId (precise) → path (domain-reload safe).
        /// </summary>
        private static GameObject ResolveTarget(Dictionary<string, object> args)
        {
            // Try instanceId first (fast scan, survives renames/moves)
            var instanceId = GetOptionalInt(args, "instanceId");
            if (instanceId.HasValue)
            {
                var go = FindObjectById(instanceId.Value);
                if (go != null) return go;
            }

            // Fallback to path (survives domain reload, human-readable)
            var path = GetString(args, "path");
            if (!string.IsNullOrEmpty(path))
            {
                return FindObjectByPath(path);
            }

            return null;
        }

        /// <summary>
        /// Resolve a parent GameObject from tool arguments.
        /// Priority: parentId → parentPath.
        /// Returns null if no parent specified (meaning "unparent to root").
        /// </summary>
        private static GameObject ResolveParentTarget(Dictionary<string, object> args)
        {
            var parentId = GetOptionalInt(args, "parentId");
            if (parentId.HasValue && parentId.Value != 0)
            {
                var parent = FindObjectById(parentId.Value);
                if (parent != null) return parent;
            }

            var parentPath = GetString(args, "parentPath");
            if (!string.IsNullOrEmpty(parentPath))
            {
                return FindObjectByPath(parentPath);
            }

            return null;
        }

        private static GameObject FindObjectById(int instanceId)
        {
            // We can't do a direct lookup by instanceId, so we scan.
            // For large scenes this is slow, but acceptable for Phase 1.
            // Must include inactive objects (e.g. after SetActive(false)).
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var go in allObjects)
            {
                if (go.GetInstanceID() == instanceId)
                    return go;
            }
            return null;
        }

        /// <summary>
        /// Find a GameObject by its transform path (e.g. "Canvas/Panel/Button").
        /// Walks root objects + Transform.Find for the remaining segments.
        /// Inactive objects ARE included (root objects include all).
        /// </summary>
        private static GameObject FindObjectByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            // Match root name first
            var roots = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root.name == parts[0])
                {
                    if (parts.Length == 1) return root;
                    // Navigate remaining path via Transform.Find
                    var remainingPath = string.Join("/", parts, 1, parts.Length - 1);
                    var t = root.transform.Find(remainingPath);
                    if (t != null) return t.gameObject;
                }
            }
            return null;
        }

        private static Component FindComponentByTypeName(GameObject go, string typeName)
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

        private static object ConvertValue(object rawValue, Type targetType)
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

        // ──────────────────────────────────────────────
        //  Undo helpers (no-op outside Editor)
        // ──────────────────────────────────────────────

        private static void UndoRecord(Object target, string label)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(target, $"[MCP] {label}");
#endif
        }

        private static void UndoRegisterCreated(GameObject go, string label)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, $"[MCP] {label}");
#endif
        }

        private static void UndoDestroyObject(GameObject go)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.DestroyObjectImmediate(go);
#else
            Object.DestroyImmediate(go);
#endif
        }

        private static Component UndoAddComponent(GameObject go, Type type)
        {
#if UNITY_EDITOR
            return UnityEditor.Undo.AddComponent(go, type);
#else
            return go.AddComponent(type);
#endif
        }

#if UNITY_EDITOR
        // ── Asset tools (Editor only) ──

        [MCPTool(MCPMethodConst.INSTANTIATE_PREFAB, "Instantiate a prefab from project Assets into the scene by asset path")]
        public static string InstantiatePrefab(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var assetPath = GetRequiredString(args, "assetPath");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                return ErrorJson($"Prefab not found at path: {assetPath}");

            var go = Object.Instantiate(prefab);
            UndoRegisterCreated(go, $"Instantiate {prefab.name}");

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            var parent = ResolveParentTarget(args);
            if (parent != null) go.transform.SetParent(parent.transform);

            return JsonUtility.ToJson(UnityObjectRef.FromGameObject(go));
        }

        [MCPTool(MCPMethodConst.SET_MATERIAL, "⚠ Set material color and/or main texture on a Renderer (by instanceId or path). For asset-level material edits, prefer editing .meta GUIDs via filesystem — faster.")]
        public static string SetMaterial(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var materialIndex = (int)GetOptionalInt(args, "materialIndex").GetValueOrDefault(0);
            var colorArr = GetOptionalFloatArray(args, "color");
            var texturePath = GetString(args, "texturePath");

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return ErrorJson("No Renderer found on object");

            if (materialIndex < 0 || materialIndex >= renderer.sharedMaterials.Length)
                return ErrorJson($"Material index {materialIndex} out of range (0-{renderer.sharedMaterials.Length - 1})");

            var mat = renderer.sharedMaterials[materialIndex];
            UndoRecord(mat, "Set Material");

            if (colorArr != null && colorArr.Length >= 3)
            {
                var c = colorArr.Length >= 4
                    ? new Color(colorArr[0], colorArr[1], colorArr[2], colorArr[3])
                    : new Color(colorArr[0], colorArr[1], colorArr[2], 1f);
                mat.color = c;
            }

            if (!string.IsNullOrEmpty(texturePath))
            {
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (tex == null) return ErrorJson($"Texture not found at path: {texturePath}");
                mat.mainTexture = tex;
            }

            return JsonHelper.BuildJsonObject(("success", "true"));
        }
#endif
    }
}
// mcp-revision: 181633
