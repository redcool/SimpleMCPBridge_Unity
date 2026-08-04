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
        /// Singleton accessor for the most recently constructed registry.
        /// Set in the constructor; safe because MessageRouter owns one registry
        /// and ReRegisterTools rebuilds it. Used by ToolsHandler for list_categories.
        /// </summary>
        public static MCPToolRegistry Instance { get; private set; }

        public MCPToolRegistry()
        {
            Instance = this;
        }

        /// <summary>
        /// Categories currently enabled for registration. When null or empty,
        /// ALL categories are enabled (default). When non-empty, only tools whose
        /// category is in this set are registered. Managed by tools.enable/disable.
        /// Static so it survives MessageRouter re-creation (ReRegisterTools).
        /// </summary>
        private static HashSet<string> _enabledCategories;

        /// <summary>
        /// Set the enabled category set. Pass null or empty to enable all.
        /// </summary>
        public static void SetEnabledCategories(IEnumerable<string> categories)
        {
            if (categories == null)
            {
                _enabledCategories = null;
                return;
            }
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories)
            {
                if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim());
            }
            _enabledCategories = set.Count == 0 ? null : set;
        }

        /// <summary>
        /// Add categories to the enabled set. If no set is active, creates one
        /// seeded with all currently-known categories, then adds the new ones.
        /// </summary>
        public static void AddEnabledCategories(IEnumerable<string> categories)
        {
            var set = _enabledCategories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0 && _allCategoryCounts.Count > 0)
            {
                // Transition from "all enabled" to explicit set: seed with everything
                // so only explicitly disabled categories are actually removed.
                foreach (var cat in _allCategoryCounts.Keys)
                    set.Add(cat);
            }
            foreach (var c in categories)
            {
                if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim());
            }
            _enabledCategories = set.Count == 0 ? null : set;
        }

        /// <summary>
        /// Remove categories from the enabled set. When no explicit set exists
        /// (all enabled), builds one from all known categories minus the removed.
        /// </summary>
        public static void RemoveEnabledCategories(IEnumerable<string> categories)
        {
            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories)
            {
                if (!string.IsNullOrWhiteSpace(c)) toRemove.Add(c.Trim());
            }
            if (toRemove.Count == 0) return;

            var set = _enabledCategories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0 && _allCategoryCounts.Count > 0)
            {
                foreach (var cat in _allCategoryCounts.Keys)
                    set.Add(cat);
            }
            set.ExceptWith(toRemove);
            _enabledCategories = set.Count == 0 ? null : set;
        }

        /// <summary>
        /// All categories ever seen (across registrations), with tool counts.
        /// Static so tools.list_categories still shows disabled categories
        /// after re-registration removes their tools from the active _tools dict.
        /// Rebuilt on every AutoRegisterAll scan.
        /// </summary>
        private static readonly Dictionary<string, int> _allCategoryCounts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a category is currently enabled (null set = all enabled).
        /// </summary>
        public static bool IsCategoryEnabled(string category)
        {
            return _enabledCategories == null || _enabledCategories.Contains(category);
        }

        /// <summary>
        /// Register a single static [MCPTool] method.
        /// </summary>
        private void RegisterMethod(MethodInfo method, string name, string description, MCPToolPlatforms platform, bool requirePlayMode, string explicitCategory)
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

            // Play Mode filter: skip tools that require play mode when not playing
            if (requirePlayMode && !UnityEngine.Application.isPlaying)
            {
                DebugUtils.Log($"[MCPToolRegistry] Skipping '{name}' — requires Play Mode.");
                return;
            }

            // Category: derive, record global count, then filter
            var category = DeriveCategory(name, explicitCategory);
            _allCategoryCounts.TryGetValue(category, out var c);
            _allCategoryCounts[category] = c + 1;

            if (!IsCategoryEnabled(category))
            {
                DebugUtils.Log($"[MCPToolRegistry] Skipping '{name}' — category '{category}' disabled.");
                return;
            }

            if (_tools.ContainsKey(name))
            {
                DebugUtils.LogWarning(
                    $"[MCPToolRegistry] Duplicate tool name '{name}' from '{method.DeclaringType?.Name}.{method.Name}' — keeping first registration.");
                return;
            }

            var del = (Func<string, string>)method.CreateDelegate(typeof(Func<string, string>));

            _tools[name] = new ToolEntry(name, description, del, requirePlayMode, category);
            DebugUtils.Log($"[MCPToolRegistry] Registered '{method.DeclaringType?.Name}.{method.Name}' as '{name}' [{category}]");
        }

        /// <summary>
        /// Derive a tool's category. Uses the explicit attribute category if set,
        /// otherwise the tool-name prefix (before the first '.'), title-cased.
        /// "scene.get_hierarchy" → "Scene"; "assetbundle.hot_replace" → "AssetBundle".
        /// </summary>
        private static string DeriveCategory(string name, string explicitCategory)
        {
            if (!string.IsNullOrWhiteSpace(explicitCategory))
                return explicitCategory.Trim();

            var dot = name.IndexOf('.');
            var prefix = dot > 0 ? name.Substring(0, dot) : name;
            // Title-case: "assetbundle" → "AssetBundle", "scene_view" → "SceneView"
            var parts = prefix.Split('_');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            }
            return string.Join("", parts);
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
            _allCategoryCounts.Clear();

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
                        RegisterMethod(method, attr.Name, attr.Description, attr.Platform, attr.RequirePlayMode, attr.Category);
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

            // Runtime guard: reject PlayMode-only tools if not playing.
            // Belt-and-suspenders for cases where re-registration hasn't run yet.
            if (entry.RequirePlayMode && !UnityEngine.Application.isPlaying)
                return "{\"success\":false,\"error\":\"Tool requires Play Mode\"}";

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

        /// <summary>
        /// Build JSON listing all categories with tool counts and enabled state.
        /// Used by tools.list_categories. Counts come from the static all-category
        /// registry so disabled categories still appear (with count and enabled:false).
        /// </summary>
        public string ListCategoriesJson()
        {
            var items = new List<string>();
            foreach (var kv in _allCategoryCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var enabled = IsCategoryEnabled(kv.Key) ? "true" : "false";
                items.Add($@"{{""category"":{JsonHelper.EscapeString(kv.Key)},""count"":{kv.Value},""enabled"":{enabled}}}");
            }
            return $"[{string.Join(",", items)}]";
        }

        // ── Internal ──

        private class ToolEntry
        {
            public string Name { get; }
            public string Description { get; }
            public Func<string, string> Delegate { get; }
            public bool RequirePlayMode { get; }
            public string Category { get; }

            public ToolEntry(string name, string description, Func<string, string> del, bool requirePlayMode, string category)
            {
                Name = name;
                Description = description;
                Delegate = del;
                RequirePlayMode = requirePlayMode;
                Category = category;
            }

            public string ToJson()
            {
                // Prefix the description with the category so agents can quickly
                // scan/group tools. e.g. "[Scene] Get scene hierarchy tree."
                var desc = string.IsNullOrEmpty(Category)
                    ? Description
                    : $"[{Category}] {Description}";
                // params are embedded in paramsJson, so properties stays empty
                return $@"{{""name"":{JsonHelper.EscapeString(Name)},""description"":{JsonHelper.EscapeString(desc)},""inputSchema"":{{""type"":""object"",""properties"":{{}}}}}}";
            }
        }
    }
}
// mcp-revision: 181633
