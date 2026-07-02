using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Discovers [MCPTool]-annotated methods at startup and provides
    /// tool listing + dispatch.
    /// </summary>
    public class MCPToolRegistry
    {
        private readonly Dictionary<string, ToolEntry> _tools = new();

        /// <summary>
        /// Scan an object instance for [MCPTool] methods and register them.
        /// Call this for each handler instance during startup.
        /// </summary>
        public void Register(object handlerInstance)
        {
            if (handlerInstance == null)
                throw new ArgumentNullException(nameof(handlerInstance));

            var type = handlerInstance.GetType();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<MCPToolAttribute>();
                if (attr == null) continue;

                // Validate method signature: must be (string) -> string
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[MCPToolRegistry] Skipping '{type.Name}.{method.Name}': " +
                        $"must accept a single string parameter (paramsJson).");
                    continue;
                }
                if (method.ReturnType != typeof(string))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[MCPToolRegistry] Skipping '{type.Name}.{method.Name}': " +
                        $"must return string.");
                    continue;
                }

                if (_tools.ContainsKey(attr.Name))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[MCPToolRegistry] Duplicate tool name '{attr.Name}' from " +
                        $"'{type.Name}.{method.Name}' — keeping first registration.");
                    continue;
                }

                _tools[attr.Name] = new ToolEntry(attr.Name, attr.Description, method, handlerInstance);
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

            try
            {
                return (string)entry.Method.Invoke(entry.Instance, new object[] { paramsJson ?? "{}" });
            }
            catch (TargetInvocationException ex)
            {
                // Unwrap reflection-invoked exceptions
                throw ex.InnerException ?? ex;
            }
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
            return "[" + string.Join(",", entries) + "]";
        }

        // ── Internal ──

        private class ToolEntry
        {
            public string Name { get; }
            public string Description { get; }
            public MethodInfo Method { get; }
            public object Instance { get; }

            public ToolEntry(string name, string description, MethodInfo method, object instance)
            {
                Name = name;
                Description = description;
                Method = method;
                Instance = instance;
            }

            public string ToJson()
            {
                // Build inputSchema from method parameters
                // Currently all tools take a single "paramsJson" string
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append("\"name\":").Append(JsonHelper.EscapeString(Name)).Append(',');
                sb.Append("\"description\":").Append(JsonHelper.EscapeString(Description)).Append(',');
                sb.Append("\"inputSchema\":{");
                sb.Append("\"type\":\"object\",");
                sb.Append("\"properties\":{}");  // params are embedded in paramsJson
                sb.Append("}");
                sb.Append('}');
                return sb.ToString();
            }
        }
    }
}
// mcp-revision: 181632
