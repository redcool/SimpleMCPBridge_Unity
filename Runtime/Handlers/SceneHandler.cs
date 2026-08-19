using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    [MCPToolClass]
    public class SceneHandler
    {
        /// <summary>
        /// Cache for instanceId → GameObject lookups.
        /// Bounded (512): attacker-controlled instanceIds feed it via full-scene rebuilds;
        /// on overflow the cache is cleared (next miss rebuilds) to bound steady-state memory.
        /// </summary>
        private static readonly Dictionary<int, GameObject> s_instanceIdCache = new();
        /// <summary>
        /// Cache for component type name → Type lookups (avoids repetitive assembly iteration).
        /// Bounded (256): attacker-controlled type names feed it via full AppDomain scans;
        /// on overflow the cache is cleared to bound steady-state memory.
        /// </summary>
        private static readonly Dictionary<string, Type> _typeCache = new();

        /// <summary>
        /// Cache for component runtime Type → (method name → resolved MethodInfo).
        /// Nested dict avoids ValueTuple keys (runtime support varies across Unity
        /// 2022.3 IL2CPP targets). No locking needed — the bridge drains messages on
        /// the main thread only. Failed lookups are NOT cached (each call re-attempts
        /// reflection so newly added methods can be found).
        /// </summary>
        private static readonly Dictionary<Type, Dictionary<string, MethodInfo>> _methodCache = new();

        /// <summary>
        /// Code-default method names blocked from scene.call_component_method (destructive or
        /// bridge-internal). Baseline — NEVER removable via config. Checked case-insensitively
        /// BEFORE any reflection lookup. Config may APPEND extra blocked names (methodBlocklist)
        /// or enable whitelist mode (methodAllowlist), but can never lift these defaults.
        /// </summary>
        private static readonly string[] s_codeBlockedMethodNames =
        {
            "destroy", "destroyimmediate", "destroyobject",
            "quit", "quitimmediate", "disconnect",
        };

        /// <summary>
        /// Effective blocklist = code defaults ∪ config extras (BridgeConfig.MethodBlocklistExtra).
        /// Built ONCE lazily — config is 重启生效 (loaded at startup, static until next load),
        /// so it is never rebuilt per call.
        /// </summary>
        private static string[] s_effectiveBlocklist;

        /// <summary>
        /// Allowlist entries (BridgeConfig.MethodAllowlist). Non-empty = whitelist mode.
        /// Built ONCE lazily alongside the effective blocklist.
        /// </summary>
        private static string[] s_effectiveAllowlist;

        /// <summary>
        /// Lazily build the effective blocklist (code defaults ∪ config extras, deduped
        /// case-insensitively) and snapshot the allowlist. Null-check init: built on the
        /// first tool call and reused for the lifetime of the loaded config.
        /// </summary>
        private static void EnsureMethodAccessCache()
        {
            if (s_effectiveBlocklist != null)
                return;

            var extra = BridgeConfig.MethodBlocklistExtra ?? new string[0];
            var combined = new List<string>(s_codeBlockedMethodNames.Length + extra.Length);
            combined.AddRange(s_codeBlockedMethodNames);
            foreach (var name in extra)
            {
                if (!combined.Contains(name, StringComparer.OrdinalIgnoreCase))
                    combined.Add(name);
            }
            s_effectiveBlocklist = combined.ToArray();
            s_effectiveAllowlist = BridgeConfig.MethodAllowlist ?? new string[0];
        }

        /// <summary>
        /// Match a blocklist/allowlist entry against a component type + method name.
        /// Entry formats: "MethodName" (any component) or "TypeName.MethodName" (specific component).
        /// Comparison is case-insensitive. Malformed entries (empty parts) never match.
        /// </summary>
        private static bool MatchesBlockEntry(string entry, string typeName, string methodName)
        {
            if (string.IsNullOrEmpty(entry)) return false;
            var dot = entry.IndexOf('.');
            if (dot < 0)
                return string.Equals(entry, methodName, StringComparison.OrdinalIgnoreCase);
            // "TypeName.MethodName" — type part matched against the runtime component type name.
            var entryType = entry.Substring(0, dot).Trim();
            var entryMethod = entry.Substring(dot + 1).Trim();
            return string.Equals(entryType, typeName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(entryMethod, methodName, StringComparison.OrdinalIgnoreCase);
        }
        [MCPTool(MCPMethodConst.GET_HIERARCHY, "Get the full scene hierarchy as a tree of objects with position, components, children, and transform path")]
        public static string GetHierarchy(string paramsJson)
        {
            var rootObjects = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var entries = new List<string>();
            foreach (var root in rootObjects)
            {
                entries.Add(SceneObjectTools.BuildTreeEntry(root, root.name));
            }
            return JsonHelper.BuildJsonArray(entries.ToArray());
        }



        [MCPTool(MCPMethodConst.GET_OBJECTS, "Find GameObjects in the scene by optional name filter — returns instanceId + path for each. " +
            "If nameContains contains '/', it is treated as a transform path (e.g. 'Canvas/Button').")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter, or transform path with '/'")]
        public static string GetObjects(string paramsJson)
        {
            var filter = ParseJsonObject(paramsJson);
            filter.TryGetValue("nameContains", out var nameFilterObj);
            var nameFilter = nameFilterObj as string;

            // If nameContains contains '/', treat it as a transform path
            if (!string.IsNullOrEmpty(nameFilter) && nameFilter.Contains("/"))
            {
                var go = FindObjectByPath(nameFilter);
                var pathRefs = new List<UnityObjectRef>();
                if (go != null)
                    pathRefs.Add(UnityObjectRef.FromGameObject(go));
                return BuildObjectRefArrayJson(pathRefs);
            }

            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            var refs = new List<UnityObjectRef>();

            foreach (var go in allObjects)
            {
                if (!string.IsNullOrEmpty(nameFilter) &&
                    !go.name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_TYPE, "Get all GameObjects in the scene that have a specific component type. " +
            "Supports optional filters: nameContains, layer (int), layerName (string), isIncludeInvisible (bool, default true). " +
            "Parameter 'typeName' accepts short type name (e.g. 'Button', 'Image', 'Renderer', 'Collider', 'Selectable').")]
        [MCPParam("typeName", Type = "string", Required = true, Description = "Component type short name, e.g. 'Button'")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        [MCPParam("isIncludeInvisible", Type = "boolean", Description = "Include inactive objects (default true)")]
        public static string GetObjectsByType(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("typeName", out var typeNameObj) || typeNameObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'typeName'")));

            var typeName = typeNameObj as string ?? "";
            var targetType = ResolveComponentType(typeName);
            if (targetType == null)
                return JsonHelper.BuildJsonObject(
                    ("error", JsonHelper.EscapeString($"Unknown component type: '{typeName}'")),
                    ("hint", JsonHelper.EscapeString("Try 'Button', 'Image', 'Renderer', 'Collider', 'Selectable', 'Rigidbody', etc.")));

            var includeInvisible = true;
            if (args.TryGetValue("isIncludeInvisible", out var includeObj) && includeObj is bool b)
                includeInvisible = b;

            // Use SceneObjectTools.FindObjectsByType for efficient root-based scan
            var scene = SceneManagement.SceneManager.GetActiveScene();
            var goList = scene.FindObjectsByType(targetType, includeInvisible, null);
            var refs = new List<UnityObjectRef>(goList.Count);

            foreach (var go in goList)
            {
                if (FilterGameObject(go, args))
                    refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_TAG, "Get all GameObjects in the scene with a specific tag. " +
            "Supports optional filters: nameContains, layer (int), layerName (string).")]
        [MCPParam("tag", Type = "string", Required = true, Description = "Unity tag to filter by")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        public static string GetObjectsByTag(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("tag", out var tagObj) || tagObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'tag'")));

            var tag = tagObj as string ?? "";
            var allObjects = GameObject.FindGameObjectsWithTag(tag);
            var refs = new List<UnityObjectRef>();

            foreach (var go in allObjects)
            {
                if (FilterGameObject(go, args))
                    refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_PATH, "Get a GameObject by its Transform path (e.g. 'Canvas/Panel/Button'). " +
            "Supports optional filters: nameContains, layer (int), layerName (string).")]
        [MCPParam("path", Type = "string", Required = true, Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        public static string GetObjectsByPath(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'path'")));

            var path = pathObj as string ?? "";
            var go = FindObjectByPath(path);
            if (go == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString($"No GameObject found at path: '{path}'")));

            if (!FilterGameObject(go, args))
                return BuildObjectRefArrayJson(new List<UnityObjectRef>());

            var refs = new List<UnityObjectRef> { UnityObjectRef.FromGameObject(go) };
            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.CREATE_OBJECT, "Create a new GameObject with optional name, position, rotation, scale, and parent (by instanceId or path)")]
        [MCPParam("name", Type = "string", Description = "New object name (default 'New GameObject')")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("parentId", Type = "integer", Description = "InstanceId of parent object")]
        [MCPParam("parentPath", Type = "string", Description = "Transform path of parent object")]
        public static string CreateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var name = GetString(args, "name", "New GameObject");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var go = new GameObject(name);
            SceneObjectTools.UndoRegisterCreated(go, $"Create {name}");

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
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        public static string DeleteObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            SceneObjectTools.UndoDestroyObject(go);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_TRANSFORM, "Set position, rotation, and/or scale of a GameObject by instanceId or path. " +
            "Optional 'space' parameter: 'world' (default, transform.position) or 'local' (transform.localPosition).")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("space", Type = "string", Description = "Coordinate space: world or local", EnumValues = new[] { "world", "local" })]
        public static string SetTransform(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");
            var space = GetString(args, "space", "world").ToLowerInvariant();
            var isLocal = space == "local";

            SceneObjectTools.UndoRecord(go.transform, "Set Transform");
            if (posArr != null && posArr.Length >= 3)
            {
                var pos = new Vector3(posArr[0], posArr[1], posArr[2]);
                if (isLocal) go.transform.localPosition = pos;
                else go.transform.position = pos;
            }
            if (rotArr != null && rotArr.Length >= 3)
            {
                var rot = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
                if (isLocal) go.transform.localRotation = rot;
                else go.transform.rotation = rot;
            }
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_COMPONENT_PROPERTY, "Set a serializable property value on a component of a GameObject (by instanceId or path)")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("componentType", Type = "string", Required = true, Description = "Component type name, e.g. 'Rigidbody'")]
        [MCPParam("propertyName", Type = "string", Required = true, Description = "Component field/property name")]
        [MCPParam("value", Type = "string", Required = true, Description = "New value (parsed to target type)")]
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
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var field = component.GetType().GetField(propertyName, flags);
            if (field != null)
            {
                SceneObjectTools.UndoRecord(component, $"Set {propertyName}");
                var typedValue = SceneObjectTools.ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try property
            var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                SceneObjectTools.UndoRecord(component, $"Set {propertyName}");
                var typedValue = SceneObjectTools.ConvertValue(rawValue, prop.PropertyType);
                prop.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // il2cpp fallback: GetProperty(name) can return null even when the property
            // is present (reflection trimming in il2cpp builds). Enumerate all public
            // instance properties and match case-insensitively.
            var propFallback = component.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p =>
                    string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    p.CanWrite &&
                    p.GetIndexParameters().Length == 0);
            if (propFallback != null)
            {
                SceneObjectTools.UndoRecord(component, $"Set {propertyName}");
                var typedValue = SceneObjectTools.ConvertValue(rawValue, propFallback.PropertyType);
                propFallback.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            // Try finding a serialized property by name convention
            // (Unity often serializes with 'm_' prefix or different casing)
            var altName = $"m_{propertyName}";
            field = component.GetType().GetField(altName, flags);
            if (field != null)
            {
                SceneObjectTools.UndoRecord(component, $"Set {propertyName}");
                var typedValue = SceneObjectTools.ConvertValue(rawValue, field.FieldType);
                field.SetValue(component, typedValue);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            return ErrorJson($"Property/field '{propertyName}' not found on {componentType}");
        }

        [MCPTool(MCPMethodConst.CALL_COMPONENT_METHOD, "Call a public instance method on a component of a GameObject (by instanceId or path) and return its serialized result. " +
            "componentType = component type name (e.g. 'Transform'); methodName = method name (e.g. 'Rotate'); " +
            "args = named map {parameterName: value} (e.g. {\"angle\": 30}) — omitted optional parameters use their defaults. " +
            "Returns: success + result (primitives as JSON, UnityEngine.Object as {instanceId,name,path}, collections as arrays capped at 100 items; void methods return success only).")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("componentType", Type = "string", Required = true, Description = "Component type name, e.g. 'Transform'")]
        [MCPParam("methodName", Type = "string", Required = true, Description = "Public instance method name, e.g. 'Rotate'")]
        [MCPParam("args", Type = "object", Description = "Named argument map: {parameterName: value}")]
        public static string CallComponentMethod(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");
            var methodName = GetRequiredString(args, "methodName");

            var component = FindComponentByTypeName(go, componentType);
            if (component == null)
                return ErrorJson($"Component '{componentType}' not found on object '{go.name}'");

            var componentTypeActual = component.GetType();

            // Triple gate — order matters (blocklists first; allowlist can NEVER override them).
            EnsureMethodAccessCache();

            // 1. Code-default blocklist (baseline, not configurable) — plain method names.
            if (s_codeBlockedMethodNames.Any(n => string.Equals(n, methodName, StringComparison.OrdinalIgnoreCase)))
                return ErrorJson($"Method '{methodName}' is blocked (destructive/bridge-internal)");

            // 2. Config-extra blocklist (methodBlocklist appends; "MethodName" or "TypeName.MethodName").
            //    s_effectiveBlocklist also carries the code defaults (redundant with step 1 — harmless).
            if (s_effectiveBlocklist.Any(e => MatchesBlockEntry(e, componentTypeActual.Name, methodName)))
                return ErrorJson($"Method '{methodName}' is blocked (destructive/bridge-internal)");

            // 3. Namespace guard (never configurable) — never reflect into the bridge's own assemblies.
            if (componentTypeActual.Namespace != null &&
                componentTypeActual.Namespace.StartsWith("SimpleMCPBridge", StringComparison.Ordinal))
                return ErrorJson($"Method '{methodName}' is blocked (destructive/bridge-internal)");

            // 4. Whitelist mode — non-empty allowlist: method must ALSO match an entry.
            if (s_effectiveAllowlist.Length > 0 &&
                !s_effectiveAllowlist.Any(e => MatchesBlockEntry(e, componentTypeActual.Name, methodName)))
                return ErrorJson($"Method '{methodName}' is not in allowlist");

            // The 'args' param is a nested JSON object → parsed to a raw JSON string by ParseJsonValue,
            // so it needs a second pass. Unknown/extra keys are simply ignored below.
            var methodArgs = new Dictionary<string, object>();
            if (args.TryGetValue("args", out var argsObj) && argsObj is string argsJson &&
                !string.IsNullOrWhiteSpace(argsJson))
                methodArgs = ParseJsonObject(argsJson);

            var method = ResolveMethod(componentTypeActual, methodName, methodArgs, out var resolveError);
            if (method == null)
                return ErrorJson(resolveError ?? $"Method '{methodName}' not found on {componentTypeActual.Name}");

            // Build the argument array in declaration order.
            var parameters = method.GetParameters();
            var argValues = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (methodArgs.TryGetValue(p.Name, out var rawArg) && rawArg != null)
                {
                    argValues[i] = SceneObjectTools.ConvertValue(rawArg, p.ParameterType);
                }
                else if (p.IsOptional)
                {
                    argValues[i] = Type.Missing;
                }
                else
                {
                    var signature = string.Join(", ", parameters.Select(pp => pp.ParameterType.Name));
                    return ErrorJson($"Missing required argument '{p.Name}' for {methodName}({signature})");
                }
            }

            object result;
            try
            {
                result = method.Invoke(component, argValues);
            }
            catch (TargetInvocationException tie)
            {
                // The method itself threw — surface the real message.
                return ErrorJson($"Method '{methodName}' threw: {tie.InnerException?.Message ?? tie.Message}");
            }
            catch (ArgumentException)
            {
                return ErrorJson($"Method '{methodName}' failed: argument mismatch");
            }
            catch (TargetParameterCountException)
            {
                return ErrorJson($"Method '{methodName}' failed: argument mismatch");
            }
            catch (Exception ex)
            {
                return ErrorJson($"Method '{methodName}' failed: {ex.Message}");
            }

            // void methods return null — success without a result key.
            if (method.ReturnType == typeof(void))
                return JsonHelper.BuildJsonObject(("success", "true"));

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("result", SerializeMethodResult(result))
            );
        }

        /// <summary>
        /// Resolve (and cache) the best MethodInfo for a public instance method by name.
        /// Overloads are scored by (1) declared parameter count == supplied arg count
        /// (strong preference) and (2) per-parameter ConvertValue-compatible type (tiebreaker).
        /// Failed lookups are NOT cached so newly added methods can be found on retry.
        /// Returns null when nothing matches; ambiguity (or no clear winner) is reported
        /// with the candidate list via <paramref name="error"/>.
        /// </summary>
        private static MethodInfo ResolveMethod(Type componentType, string methodName,
            Dictionary<string, object> methodArgs, out string error)
        {
            error = null;

            // Cache hit — resolved once, reused across calls.
            if (_methodCache.TryGetValue(componentType, out var nameMap) &&
                nameMap.TryGetValue(methodName, out var cached))
                return cached;

            var candidates = componentType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                return null; // not found

            MethodInfo best;
            if (candidates.Count == 1)
            {
                best = candidates[0];
            }
            else
            {
                // Multiple overloads — score by arg-count match first, then type compatibility.
                best = null;
                var bestScore = -1;
                var tied = false;
                foreach (var candidate in candidates)
                {
                    var score = ScoreOverload(candidate, methodArgs);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                        tied = false;
                    }
                    else if (score == bestScore)
                    {
                        tied = true;
                    }
                }

                if (best == null || tied || bestScore <= 0)
                {
                    var signatures = string.Join(", ",
                        candidates.Select(c => $"{c.Name}({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))})"));
                    error = $"Ambiguous method '{methodName}' on {componentType.Name}: {signatures}";
                    return null;
                }
            }

            _methodCache[componentType] = new Dictionary<string, MethodInfo> { [methodName] = best };
            return best;
        }

        /// <summary>
        /// Score how well an overload fits the supplied named arguments.
        /// (1) +10 when the declared parameter count equals the supplied arg count;
        /// (2) +1 per parameter whose type ConvertValue can feed (or that is 'object').
        /// </summary>
        private static int ScoreOverload(MethodInfo method, Dictionary<string, object> methodArgs)
        {
            var parameters = method.GetParameters();
            var score = 0;

            if (parameters.Length == methodArgs.Count)
                score += 10;

            foreach (var p in parameters)
            {
                if (!methodArgs.TryGetValue(p.Name, out var raw) || raw == null) continue;
                var pt = p.ParameterType;
                if (pt == typeof(object) || IsConvertValueCompatible(pt))
                    score += 1;
            }

            return score;
        }

        /// <summary>
        /// Types that SceneObjectTools.ConvertValue can convert from raw JSON values.
        /// </summary>
        private static bool IsConvertValueCompatible(Type t)
        {
            if (t.IsEnum) return true;
            return t == typeof(float) || t == typeof(double) || t == typeof(int) ||
                   t == typeof(long) || t == typeof(bool) || t == typeof(string) ||
                   t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) ||
                   t == typeof(Quaternion) || t == typeof(Color);
        }

        /// <summary>
        /// Serialize a method return value to a JSON value string (ready for BuildJsonObject).
        /// null → "null"; primitives/string/bool/enum → typed JSON; UnityEngine.Object →
        /// {instanceId,name,path}; IEnumerable (excluding string) → recursive array capped at
        /// 100 items with a truncation marker; anything else → escaped ToString().
        /// </summary>
        private static string SerializeMethodResult(object value)
        {
            if (value == null) return "null";

            if (value is string str) return JsonHelper.EscapeString(str);
            if (value is bool b) return JsonHelper.BoolJson(b);
            if (value is float f) return f.ToString("G", CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("G", CultureInfo.InvariantCulture);
            if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (value is short sh) return sh.ToString(CultureInfo.InvariantCulture);
            if (value is byte by) return by.ToString(CultureInfo.InvariantCulture);
            if (value is uint ui) return ui.ToString(CultureInfo.InvariantCulture);
            if (value is ulong ul) return ul.ToString(CultureInfo.InvariantCulture);
            if (value.GetType().IsEnum) return JsonHelper.EscapeString(value.ToString());

            // UnityEngine.Object (GameObject, Component, Material, ...) → object ref JSON.
            if (value is Object unityObject)
            {
                var go = value as GameObject;
                if (go == null && value is Component comp) go = comp.gameObject;
                var path = go != null ? GetObjectPath(go) : "";
                return JsonHelper.BuildJsonObject(
                    ("instanceId", unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("name", JsonHelper.EscapeString(unityObject.name)),
                    ("path", JsonHelper.EscapeString(path))
                );
            }

            // Collections → recursive array, capped at 100 items.
            if (value is IEnumerable enumerable)
            {
                var items = new List<string>();
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 100)
                    {
                        items.Add(JsonHelper.EscapeString("... (truncated, max 100 items)"));
                        break;
                    }
                    items.Add(SerializeMethodResult(item));
                    count++;
                }
                return JsonHelper.BuildJsonArray(items.ToArray());
            }

            return JsonHelper.EscapeString(value.ToString());
        }

        [MCPTool(MCPMethodConst.GET_COMPONENTS, "Get all components on a GameObject by instanceId or path — returns type names and assembly info")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
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
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("componentType", Type = "string", Required = true, Description = "Component type name, e.g. 'Transform'")]
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

                // Skip properties whose getter clones objects (side-effects leak memory in edit mode).
                // Specifically: Renderer.material clones the sharedMaterial, creating leaked instances.
                if (typeof(Renderer).IsAssignableFrom(component.GetType()) &&
                    prop.Name == "material" && prop.PropertyType == typeof(Material))
                    continue;

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
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("active", Type = "boolean", Required = true, Description = "True to enable, false to disable")]
        public static string SetActive(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var active = GetRequiredBool(args, "active");

            SceneObjectTools.UndoRecord(go, "Set Active");
            go.SetActive(active);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.DUPLICATE_OBJECT, "Duplicate a GameObject by instanceId or path")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        public static string DuplicateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var clone = Object.Instantiate(go);
            SceneObjectTools.UndoRegisterCreated(clone, $"Duplicate {go.name}");
            clone.name = go.name + " (Copy)";
            var ref_ = UnityObjectRef.FromGameObject(clone);
            return JsonUtility.ToJson(ref_);
        }

        [MCPTool(MCPMethodConst.RENAME, "Rename a GameObject by instanceId or path")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("name", Type = "string", Required = true, Description = "New name for the object")]
        public static string Rename(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var name = GetRequiredString(args, "name");

            SceneObjectTools.UndoRecord(go, "Rename");
            go.name = name;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_PARENT, "Set parent of a GameObject by instanceId/path and optional parentId/parentPath. Leave parent empty to unparent to root.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("parentId", Type = "integer", Description = "InstanceId of new parent (omit to unparent)")]
        [MCPParam("parentPath", Type = "string", Description = "Transform path of new parent")]
        public static string SetParent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            SceneObjectTools.UndoRecord(go.transform, "Set Parent");

            var parent = ResolveParentTarget(args);
            if (parent != null)
                go.transform.SetParent(parent.transform);
            else
                go.transform.SetParent(null);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.ADD_COMPONENT, "Add a component to a GameObject by instanceId or path and type name (e.g. Rigidbody, BoxCollider)")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("componentType", Type = "string", Required = true, Description = "Component type name, e.g. 'Rigidbody'")]
        public static string AddComponent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");

            var type = FindTypeCached(componentType);
            if (type != null)
            {
                SceneObjectTools.UndoAddComponent(go, type);
                return JsonHelper.BuildJsonObject(("success", "true"));
            }

            return ErrorJson($"Component type '{componentType}' not found in any assembly");
        }

        [MCPTool(MCPMethodConst.REMOVE_COMPONENT, "Remove a component from a GameObject by instanceId or path and type name (e.g. BoxCollider, Rigidbody)")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("componentType", Type = "string", Required = true, Description = "Component type name, e.g. 'BoxCollider'")]
        public static string RemoveComponent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var componentType = GetRequiredString(args, "componentType");

            var component = FindComponentByTypeName(go, componentType);
            if (component == null)
                return ErrorJson($"Component '{componentType}' not found on object '{go.name}'");

#if UNITY_EDITOR
            UnityEditor.Undo.DestroyObjectImmediate(component);
#else
            Object.DestroyImmediate(component);
#endif
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

#if UNITY_EDITOR
        [MCPTool(MCPMethodConst.ENTER_PLAY_MODE, "Enter Play Mode in the Unity Editor")]
        public static string EnterPlayMode(string paramsJson)
        {
            var succeeded = UnityEditor.EditorApplication.isPlaying;
            if (!succeeded)
            {
                UnityEditor.EditorApplication.isPlaying = true;
                succeeded = UnityEditor.EditorApplication.isPlaying;
            }
            return JsonHelper.BuildJsonObject(
                ("success", succeeded ? "true" : "false"),
                ("isPlaying", succeeded ? "true" : "false")
            );
        }

        [MCPTool(MCPMethodConst.EXIT_PLAY_MODE, "Exit Play Mode in the Unity Editor")]
        public static string ExitPlayMode(string paramsJson)
        {
            // Defer exit so the JSON-RPC response is sent via WebSocket
            // before OnPlayModeStateChanged(ExitingPlayMode) fires and
            // potentially disconnects the bridge.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                UnityEditor.EditorApplication.isPlaying = false;
            };
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.PAUSE_PLAY_MODE, "Pause or resume Play Mode in the Unity Editor — set paused=true to pause, false to resume")]
        [MCPParam("paused", Type = "boolean", Required = true, Description = "True to pause, false to resume")]
        public static string PausePlayMode(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var paused = GetRequiredBool(args, "paused");
            UnityEditor.EditorApplication.isPaused = paused;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.REQUEST_COMPILE, "Request Unity to recompile all scripts. " +
            "Ensures compilation by minimizing/restoring the Editor window first " +
            "(triggers Unity's event processing which is required for compilation to start reliably).")]
        public static string RequestCompile(string paramsJson)
        {
            var hWnd = WindowTools.GetWindowHandle();

            // Step 1: Minimize (lose focus → force Unity to process pending events)
            WindowTools.Minimize(hWnd);

            // Step 2: After 300ms, restore and request compilation
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update += OnPostMinimize;

            void OnPostMinimize()
            {
                if (UnityEditor.EditorApplication.timeSinceStartup - startTime < 0.3)
                    return;
                UnityEditor.EditorApplication.update -= OnPostMinimize;

                // Step 3: Restore window (refocus)
                WindowTools.Restore(hWnd);

                // Step 4: Force reimport and request compilation
                UnityEditor.AssetDatabase.Refresh();
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.OPEN_WINDOW, "Open a Unity Editor window by menu path — use the exact path as shown in Unity's menu bar (e.g. 'Window/General/Console'). Returns an error if the menu item is not found.")]
        [MCPParam("menuPath", Type = "string", Required = true, Description = "Exact Unity menu path, e.g. 'Window/General/Console'")]
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

        [MCPTool(MCPMethodConst.SAVE_CURRENT_SCENE, "Save the current Unity scene. If savePath is not provided, saves the current scene in place. If the scene is untitled, savePath is required.")]
        [MCPParam("savePath", Type = "string", Description = "Scene asset path under Assets/ (required if untitled)")]
        public static string SaveCurrent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var savePath = GetString(args, "savePath");

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(savePath))
            {
                if (string.IsNullOrEmpty(scene.path))
                    return ErrorJson("Scene is untitled. Provide 'savePath' to specify where to save.");
                savePath = scene.path;
            }

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, savePath, true);
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("path", JsonHelper.EscapeString(savePath))
            );
        }
#endif

        [MCPTool(MCPMethodConst.SCENE_LOAD_SCENE, "Load a scene. In the Editor outside Play Mode this opens the scene asset; " +
            "otherwise it loads via SceneManager (works in Play Mode and built players). " +
            "Params: sceneName (required — scene name, or 'Assets/...unity' path), " +
            "mode ('single' | 'additive', default 'single'), async (bool, optional). " +
            "⚠ WARNING: after a scene load ALL previous instanceIds go stale — re-fetch scene.get_hierarchy.")]
        [MCPParam("sceneName", Type = "string", Required = true, Description = "Scene name, or 'Assets/...unity' path")]
        [MCPParam("mode", Type = "string", Description = "'single' (default) or 'additive'")]
        [MCPParam("async", Type = "boolean", Description = "Load asynchronously (default false)")]
        public static string LoadScene(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var sceneName = GetRequiredString(args, "sceneName");
            var modeStr = GetString(args, "mode", "single");
            var isAdditive = modeStr.Equals("additive", StringComparison.OrdinalIgnoreCase);
            var isAsync = GetOptionalBool(args, "async") ?? false;

            const string warning = "Scene loaded — ALL previous instanceIds are stale. Re-fetch scene.get_hierarchy.";

#if UNITY_EDITOR
            // Editor, not in Play Mode → open the scene asset directly (no play session).
            if (!Application.isPlaying)
            {
                var resolvedPath = ResolveScenePath(sceneName);
                if (string.IsNullOrEmpty(resolvedPath))
                    return ErrorJson($"Scene '{sceneName}' not found in project");
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    resolvedPath,
                    isAdditive
                        ? UnityEditor.SceneManagement.OpenSceneMode.Additive
                        : UnityEditor.SceneManagement.OpenSceneMode.Single);
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("mode", JsonHelper.EscapeString(isAdditive ? "additive" : "single")),
                    ("name", JsonHelper.EscapeString(sceneName)),
                    ("warning", JsonHelper.EscapeString(warning))
                );
            }
#endif

            // Play Mode / player → runtime load. By-name loads only resolve for scenes in
            // Build Settings; additive loads of non-build scenes are resolved name→path first.
            var loadName = sceneName;
            if (isAdditive && !sceneName.Contains("/"))
            {
                var resolved = ResolveScenePath(sceneName);
                if (!string.IsNullOrEmpty(resolved))
                    loadName = resolved;
            }

            var loadMode = isAdditive ? SceneManagement.LoadSceneMode.Additive : SceneManagement.LoadSceneMode.Single;
            if (isAsync)
                SceneManagement.SceneManager.LoadSceneAsync(loadName, loadMode);
            else
                SceneManagement.SceneManager.LoadScene(loadName, loadMode);

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("mode", JsonHelper.EscapeString(isAdditive ? "additive" : "single")),
                ("name", JsonHelper.EscapeString(sceneName)),
                ("warning", JsonHelper.EscapeString(warning))
            );
        }

        /// <summary>
        /// Resolve a scene name to its "Assets/...unity" path. Checks Build Settings
        /// first (works in players too), then AssetDatabase by name (Editor only).
        /// </summary>
        private static string ResolveScenePath(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return null;

            // Path input ("Assets/.../X.unity") — validate directly, don't mangle it into a
            // name filter (AssetDatabase.FindAssets matches names, never full paths).
            if (sceneName.Contains("/") || sceneName.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
#if UNITY_EDITOR
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(sceneName) != null)
                    return sceneName;
#endif
                // Player: SceneManager.LoadScene accepts build-settings paths — match below.
            }

            for (var i = 0; i < SceneManagement.SceneManager.sceneCountInBuildSettings; i++)
            {
                var p = SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                if (System.IO.Path.GetFileNameWithoutExtension(p).Equals(sceneName, StringComparison.OrdinalIgnoreCase) ||
                    p.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

#if UNITY_EDITOR
            var nameOnly = System.IO.Path.GetFileNameWithoutExtension(sceneName);
            var guids = UnityEditor.AssetDatabase.FindAssets($"t:SceneAsset {nameOnly}");
            foreach (var guid in guids)
            {
                var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p).Equals(nameOnly, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
#endif
            return null;
        }

        // ──────────────────────────────────────────────
        //  Object resolution
        // ──────────────────────────────────────────────

        /// <summary>
        /// Resolve a GameObject from tool arguments.
        /// Priority: instanceId (precise) → path (domain-reload safe).
        /// </summary>
        public static GameObject ResolveTarget(Dictionary<string, object> args)
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
        public static GameObject ResolveParentTarget(Dictionary<string, object> args)
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

        public static GameObject FindObjectById(int instanceId)
        {
            // Fast path: check cache
            if (s_instanceIdCache.TryGetValue(instanceId, out var go) && go != null)
                return go;

            // Cache miss (or stale entry from destroyed object) — full scan.
            // Must include inactive objects (e.g. after SetActive(false)).
            s_instanceIdCache.Clear();
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var obj in allObjects)
            {
                s_instanceIdCache[obj.GetInstanceID()] = obj;
            }

            // Bound steady-state memory: attacker-controlled instanceId misses trigger
            // rebuilds; if the scene has more than 512 objects, drop the cache so it can
            // never exceed the cap (next miss simply rebuilds again).
            if (s_instanceIdCache.Count > 512)
                s_instanceIdCache.Clear();

            s_instanceIdCache.TryGetValue(instanceId, out go);
            return go;
        }

        /// <summary>
        /// Find a GameObject by its transform path (e.g. "Canvas/Panel/Button").
        /// Delegates to SceneObjectTools for multi-segment paths.
        /// Inactive objects ARE included (root objects include all).
        /// </summary>
        public static GameObject FindObjectByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.Contains("/"))
            {
                // Single-segment: search root objects by name
                var roots = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                foreach (var root in roots)
                    if (root.name == path) return root;
                return null;
            }
            // Multi-segment: delegate to SceneObjectTools (returns Transform, unwrap .gameObject)
            var scene = SceneManagement.SceneManager.GetActiveScene();
            var result = scene.FindObjectByPath(path) as Transform;
            return result != null ? result.gameObject : null;
        }

        public static Component FindComponentByTypeName(GameObject go, string typeName)
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

        /// <summary>
        /// Insert into _typeCache with a size cap (256). On overflow the whole cache is
        /// cleared — simple, avoids eviction bookkeeping; a miss simply rebuilds one entry.
        /// </summary>
        private static void CacheType(string typeName, Type type)
        {
            if (_typeCache.Count >= 256)
                _typeCache.Clear();
            _typeCache[typeName] = type;
        }

        /// <summary>
        /// Find a Component type by name using a static cache to avoid iterating
        /// all assemblies on every call. Falls back to ResolveComponentType for
        /// Unity-internal types (UI, TMPro, Physics, etc.).
        /// </summary>
        private static Type FindTypeCached(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Check cache first
            if (_typeCache.TryGetValue(typeName, out var cached))
                return cached;

            // Try ResolveComponentType first (fast path for well-known Unity types)
            var resolved = ResolveComponentType(typeName);
            if (resolved != null)
            {
                CacheType(typeName, resolved);
                return resolved;
            }

            // Fallback: iterate all assemblies once, cache on success
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                    if (types == null) continue;
                }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    if (type.Name == typeName && type.IsSubclassOf(typeof(Component)) && !type.IsAbstract)
                    {
                        CacheType(typeName, type);
                        return type;
                    }
                }
            }

            CacheType(typeName, null);
            return null;
        }

        // Ordered: first match wins. Try without assembly first, then with CoreModule,
        // then common Unity modules for types like Collider (PhysicsModule), etc.
        public static readonly string[] s_tns = {
            "UnityEngine.", "UnityEngine.", "UnityEngine.UI.", "UnityEngine.EventSystems.",
            "TMPro.",       "UnityEngine.", "UnityEngine.",    "UnityEngine.",
        };
        public static readonly string[] s_tas = {
            null,            "UnityEngine.CoreModule", "UnityEngine.UI",  "UnityEngine.UI",
            "Unity.TextMeshPro", "UnityEngine.PhysicsModule", "UnityEngine.Physics2DModule", "UnityEngine.AnimationModule",
        };

        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            for (int i = 0; i < s_tns.Length; i++)
            {
                var fullName = s_tns[i] + typeName;
                var asmName = s_tas[i];
                var type = asmName != null
                    ? Type.GetType(fullName + "," + asmName, false, true)
                    : Type.GetType(fullName, false, true);
                if (type != null && type.IsSubclassOf(typeof(Component))) return type;
            }

            return null;
        }



#if UNITY_EDITOR
        // ── Asset tools (Editor only) ──

        [MCPTool(MCPMethodConst.INSTANTIATE_PREFAB, "Instantiate a prefab from project Assets into the scene by asset path")]
        [MCPParam("assetPath", Type = "string", Required = true, Description = "Prefab asset path, e.g. 'Assets/Prefabs/X.prefab'")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("parentId", Type = "integer", Description = "InstanceId of parent object")]
        [MCPParam("parentPath", Type = "string", Description = "Transform path of parent object")]
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

            var go = UnityEditor.PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (go == null)
                return ErrorJson("Failed to instantiate prefab (PrefabUtility.InstantiatePrefab returned null)");
            SceneObjectTools.UndoRegisterCreated(go, $"Instantiate {prefab.name}");

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

        [MCPTool(MCPMethodConst.SCENE_SAVE_PREFAB, "Save a GameObject (by instanceId or path) as a prefab asset. " +
            "Overwrites the prefab at assetPath if it exists. " +
            "NOTE: PrefabUtility/AssetDatabase operations are NOT Undo-trackable.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("assetPath", Type = "string", Required = true, Description = "Prefab asset path, e.g. 'Assets/Prefabs/X.prefab'")]
        public static string SavePrefab(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var assetPath = GetRequiredString(args, "assetPath");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, assetPath, out var success);
            return JsonHelper.BuildJsonObject(
                ("success", success ? "true" : "false"),
                ("assetPath", JsonHelper.EscapeString(assetPath))
            );
        }

        [MCPTool(MCPMethodConst.SET_MATERIAL, "⚠ Set material color and/or main texture on a Renderer (by instanceId or path). For asset-level material edits, prefer editing .meta GUIDs via filesystem — faster.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("materialIndex", Type = "integer", Description = "Material index on renderer (default 0)")]
        [MCPParam("color", Type = "array", Description = "[r,g,b] or [r,g,b,a] color")]
        [MCPParam("texturePath", Type = "string", Description = "Texture asset path for main texture")]
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
            SceneObjectTools.UndoRecord(mat, "Set Material");

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

