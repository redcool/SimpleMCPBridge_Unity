using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Discovers [MCPTool]-annotated methods at startup and provides
    /// tool listing + dispatch.
    ///
    /// AutoRegisterAll scans all loaded assemblies for [MCPTool] handlers.
    ///
    /// PowerUtilities.ReflectionTools 提供更多反射工具方法，
    /// 如需使用请确保已安装 PowerUtilities 包。
    /// </summary>
    public class MCPToolRegistry
    {
        private readonly Dictionary<string, ToolEntry> _tools = new();

        /// <summary>
        /// Register a single static [MCPTool] method.
        /// </summary>
        private void RegisterMethod(MethodInfo method, string name, string description, MCPToolPlatforms platform)
        {
            // Validate: (string) -> string
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
            {
                DebugUtils.LogWarning(
                    $"[MCPToolRegistry] Skipping '{method.DeclaringType?.Name}.{method.Name}': must accept a single string parameter.");
                return;
            }
            if (method.ReturnType != typeof(string))
            {
                DebugUtils.LogWarning(
                    $"[MCPToolRegistry] Skipping '{method.DeclaringType?.Name}.{method.Name}': must return string.");
                return;
            }

            // Platform filter
            if (platform != MCPToolPlatforms.All)
            {
                var current = CurrentPlatform();
                if ((platform & current) == 0)
                {
                    DebugUtils.Log($"[MCPToolRegistry] Skipping '{name}' — filtered to {platform}, current is {current}.");
                    return;
                }
            }

            if (_tools.ContainsKey(name))
            {
                DebugUtils.LogWarning(
                    $"[MCPToolRegistry] Duplicate tool name '{name}' from '{method.DeclaringType?.Name}.{method.Name}' — keeping first registration.");
                return;
            }

            var del = (Func<string, string>)method.CreateDelegate(typeof(Func<string, string>));

            _tools[name] = new ToolEntry(name, description, del);
            DebugUtils.Log($"[MCPToolRegistry] Registered '{method.DeclaringType?.Name}.{method.Name}' as '{name}'");
        }

        /// <summary>
        /// Map UnityEngine.RuntimePlatform to our MCPToolPlatforms flags.
        /// </summary>
        private static MCPToolPlatforms CurrentPlatform()
        {
#if UNITY_EDITOR
            return MCPToolPlatforms.Editor;
#elif UNITY_ANDROID
            return MCPToolPlatforms.Android;
#elif UNITY_IOS
            return MCPToolPlatforms.iOS;
#elif UNITY_STANDALONE
            return MCPToolPlatforms.Standalone;
#elif UNITY_WEBGL
            return MCPToolPlatforms.WebGL;
#else
            return MCPToolPlatforms.All;
#endif
        }

        /// <summary>
        /// Auto-discover all [MCPTool] static handlers in all loaded assemblies.
        /// All MCPTool methods are expected to be public static (string → string).
        /// </summary>
        public void AutoRegisterAll()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types?.Where(t => t != null).ToArray();
                    if (types == null || types.Length == 0) continue;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if ( type.IsInterface) continue;
                    // Skip types without [MCPToolClass] — this is the primary
                    // performance gate that avoids scanning every type's methods.
                    if (!type.IsDefined(typeof(MCPToolClassAttribute), inherit: false)) continue;

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                    {
                        var attr = method.GetCustomAttribute<MCPToolAttribute>();
                        if (attr == null) continue;
                        RegisterMethod(method, attr.Name, attr.Description, attr.Platform);
                    }
                }
            }
        }

        /// <summary>
        /// Check if a tool name is registered.
        /// </summary>
        public bool HasTool(string name) => _tools.ContainsKey(name);

        /// <summary>
        /// Dispatch a tool call by name.
        /// Returns the result JSON (or throws on error).
        /// </summary>
        public string Dispatch(string name, string paramsJson)
        {
            if (!_tools.TryGetValue(name, out var entry))
                throw new NotImplementedException($"Unknown method: {name}");

            // Direct delegate call — orders of magnitude faster than MethodInfo.Invoke
            return entry.Delegate(paramsJson ?? "{}");
        }

        /// <summary>
        /// Build JSON for mcp.list_tools response.
        /// </summary>
        public string ListToolsJson()
        {
            var entries = new List<string>();
            foreach (var tool in _tools.Values)
            {
                entries.Add(tool.ToJson());
            }
            return $"[{string.Join(",", entries)}]";
        }

        // ── Internal ──

        private class ToolEntry
        {
            public string Name { get; }
            public string Description { get; }
            public Func<string, string> Delegate { get; }

            public ToolEntry(string name, string description, Func<string, string> del)
            {
                Name = name;
                Description = description;
                Delegate = del;
            }

            public string ToJson()
            {
                // params are embedded in paramsJson, so properties stays empty
                return $@"{{""name"":{JsonHelper.EscapeString(Name)},""description"":{JsonHelper.EscapeString(Description)},""inputSchema"":{{""type"":""object"",""properties"":{{}}}}}}";
            }
        }
    }
}
// mcp-revision: 181633
