using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // SceneHandler 组件操作组（partial）：set/get_component_property / call_component_method / get_component* / add/remove_component
    public partial class SceneHandler
    {
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
    }
}
