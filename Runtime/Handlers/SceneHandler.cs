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
    public partial class SceneHandler
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



    }
}
// mcp-revision: 181633