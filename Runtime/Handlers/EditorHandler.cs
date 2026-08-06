#if UNITY_EDITOR
using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles Unity Editor window manipulation and utility tools.
    /// Editor only — wrapped in #if UNITY_EDITOR.
    /// </summary>
    [MCPToolClass]
    public class EditorHandler
    {
        // ─── editor.eval enabled flag (persisted via EditorPrefs) ──────────

        /// <summary>
        /// Global toggle for the editor.eval tool. Default ON.
        /// Persisted via EditorPrefs to survive domain reload.
        /// Safe to use here — the entire class is wrapped in #if UNITY_EDITOR.
        /// </summary>
        public static bool EvalEnabled
        {
            get => EditorPrefs.GetBool("SimpleMCPBridge_EvalEnabled", true);
            set => EditorPrefs.SetBool("SimpleMCPBridge_EvalEnabled", value);
        }

        // ─── Win32 window control (delegates to Win32Tools) ────────────────

        [MCPTool(MCPMethodConst.EDITOR_WINDOW_FOCUS,
            "Control the Unity Editor window state via Win32 API. " +
            "Params: action (string, required) — 'minimize', 'restore', 'focus', 'maximize', or 'get_state'. " +
            "'get_state' returns { state: 'normal'|'minimized'|'maximized'|'hidden' } without changing anything.")]
        public static string WindowFocus(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var action = GetRequiredString(args, "action").ToLowerInvariant();

            var hWnd = WindowTools.GetWindowHandle();

            if (action == "get_state")
            {
                var state = WindowTools.GetWindowState(hWnd);
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("action", JsonHelper.EscapeString("get_state")),
                    ("state", JsonHelper.EscapeString(state))
                );
            }

            switch (action)
            {
                case "minimize":
                    WindowTools.Minimize(hWnd);
                    return JsonHelper.BuildJsonObject(("success", "true"), ("action", JsonHelper.EscapeString("minimize")));
                case "restore":
                    WindowTools.Restore(hWnd);
                    return JsonHelper.BuildJsonObject(("success", "true"), ("action", JsonHelper.EscapeString("restore")));
                case "focus":
                    WindowTools.Focus(hWnd);
                    return JsonHelper.BuildJsonObject(("success", "true"), ("action", JsonHelper.EscapeString("focus")));
                case "maximize":
                    WindowTools.Maximize(hWnd);
                    return JsonHelper.BuildJsonObject(("success", "true"), ("action", JsonHelper.EscapeString("maximize")));
                default:
                    return ErrorJson($"Unknown action '{action}'. Use 'minimize', 'restore', 'focus', 'maximize', or 'get_state'.");
            }
        }

        // ─── editor.eval — delegates to MonoCSharpTools ───────────────────

        [MCPTool(MCPMethodConst.EVAL,
            "Execute C# code in the Unity Editor process using Mono.CSharp (in-memory, instant, no domain reload). " +
            "Params: code (string, required) — any valid C# statement or expression. " +
            "The evaluator maintains state across calls — variables persist. " +
            "Pre-imported namespaces: System, System.Linq, System.Collections.Generic, " +
            "UnityEngine, UnityEditor, UnityEngine.UI, UnityEngine.EventSystems. " +
            "UnityEngine.Object aliased as UnityObject. " +
            "Example: 'GameObject.Find(\"Main Camera\").transform.position.ToString()'")]
        public static string Eval(string paramsJson)
        {
            if (!EvalEnabled)
                return ErrorJson("editor.eval is disabled. Enable it via the MCPBridge Inspector toggle.");

            var args = ParseJsonObject(paramsJson);
            var code = GetRequiredString(args, "code");
            if (string.IsNullOrWhiteSpace(code))
                return ErrorJson("Missing required parameter 'code'.");

            try
            {
                var result = MonoCSharpTools.Evaluate(code);

                if (result == null)
                    return JsonHelper.BuildJsonObject(("success", "true"),
                        ("note", JsonHelper.EscapeString("statement executed")));

                var json = MonoCSharpTools.SerializeEvalResult(result);
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("result", JsonHelper.EscapeString(json)),
                    ("type", JsonHelper.EscapeString(result.GetType().Name))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"Eval failed: {ex.Message}");
            }
        }

        // ─── editor.get_console ───────────────────────────────────────────

        // Circular buffer for console log cache
        private const int CONSOLE_CACHE_SIZE = 200;
        private static readonly List<ConsoleEntry> _consoleCache = new(CONSOLE_CACHE_SIZE);
        private class ConsoleEntry
        {
            public string message;
            public string stackTrace;
            public string type; // "Log", "Warning", "Error", "Exception", "Assert"
            public string time;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (_consoleCache)
            {
                _consoleCache.Add(new ConsoleEntry
                {
                    message = condition,
                    stackTrace = stackTrace,
                    type = type.ToString(),
                    time = DateTime.Now.ToString("HH:mm:ss.fff"),
                });
                if (_consoleCache.Count > CONSOLE_CACHE_SIZE)
                    _consoleCache.RemoveRange(0, _consoleCache.Count - CONSOLE_CACHE_SIZE);
            }
        }

        [InitializeOnLoadMethod]
        private static void InitConsoleCapture()
        {
            // Unsubscribe first to prevent double-subscription across domain reloads
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        [MCPTool(MCPMethodConst.GET_CONSOLE,
            "Get recent Editor console log entries. " +
            "Params: count (int, optional, default 50) — max entries to return. " +
            "Returns object with { entries: [...], count: N }. " +
            "Each entry: { message, stackTrace, type, time }. " +
            "Type values: Log, Warning, Error, Exception, Assert.")]
        public static string GetConsole(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var count = (int)GetOptionalInt(args, "count").GetValueOrDefault(50);
            count = Mathf.Clamp(count, 1, CONSOLE_CACHE_SIZE);

            List<ConsoleEntry> entries;
            lock (_consoleCache)
            {
                entries = _consoleCache.Skip(Math.Max(0, _consoleCache.Count - count)).ToList();
            }

            var jsonEntries = new List<string>();
            foreach (var e in entries)
            {
                jsonEntries.Add($@"{{""message"":{JsonHelper.EscapeString(e.message)},""stackTrace"":{JsonHelper.EscapeString(e.stackTrace)},""type"":{JsonHelper.EscapeString(e.type)},""time"":{JsonHelper.EscapeString(e.time)}}}");
            }
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("count", entries.Count.ToString(CultureInfo.InvariantCulture)),
                ("entries", $"[{string.Join(",", jsonEntries)}]")
            );
        }

        // ─── editor.undo / editor.redo ────────────────────────────────────

        [MCPTool(MCPMethodConst.UNDO,
            "Undo the last operation in the Unity Editor.")]
        public static string UndoTool(string paramsJson)
        {
            UnityEditor.Undo.PerformUndo();
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.REDO,
            "Redo the last undone operation in the Unity Editor.")]
        public static string RedoTool(string paramsJson)
        {
            UnityEditor.Undo.PerformRedo();
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        // ─── editor.get_preferences ───────────────────────────────────────

        [MCPTool(MCPMethodConst.GET_PREFERENCES,
            "Read common Unity Editor and Project settings. " +
            "Params: keys (string[], optional) — specific setting keys to read. " +
            "If omitted, returns a useful default set: " +
            "productName, companyName, scriptingBackend, apiCompatibilityLevel, " +
            "buildTarget, activeBuildTargetGroup, " +
            "Editor/applicationContentsPath, Editor/unityVersion. " +
            "Custom EditorPrefs keys can be queried by passing keys array.")]
        public static string GetPreferences(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var result = new Dictionary<string, string>();

            // If specific keys requested, read those from EditorPrefs
            if (args.TryGetValue("keys", out var keysObj) && keysObj is List<object> keysList)
            {
                foreach (var key in keysList)
                {
                    var keyStr = key?.ToString() ?? "";
                    if (EditorPrefs.HasKey(keyStr))
                        result[keyStr] = EditorPrefs.GetString(keyStr);
                    else
                        result[keyStr] = "(not set)";
                }
            }
            else
            {
                // Default useful set of project info
                result["productName"] = PlayerSettings.productName;
                result["companyName"] = PlayerSettings.companyName;
                result["applicationIdentifier"] = PlayerSettings.applicationIdentifier;
                result["scriptingBackend"] = PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString();
                result["apiCompatibilityLevel"] = PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup).ToString();
                result["buildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString();
                result["activeBuildTargetGroup"] = EditorUserBuildSettings.selectedBuildTargetGroup.ToString();
                result["Editor/unityVersion"] = Application.unityVersion;
                result["Editor/applicationContentsPath"] = EditorApplication.applicationContentsPath;
                result["Editor/applicationPath"] = EditorApplication.applicationPath;
                result["Editor/dataPath"] = Application.dataPath;

                // Try to get color space and other project settings
                result["Graphics/colorSpace"] = PlayerSettings.colorSpace.ToString();
            }

            // Build JSON with proper nesting using JsonHelper
            var json = JsonHelper.BuildJsonObject(
                result.Select(kv => (kv.Key, JsonHelper.EscapeString(kv.Value))).ToArray()
            );
            return json;
        }

        // ─── editor.get_project_tree ──────────────────────────────────────

        [MCPTool(MCPMethodConst.GET_PROJECT_TREE,
            "Get the Assets directory tree. " +
            "Params: path (string, optional) — subdirectory under Assets/ (default 'Assets'). " +
            "maxDepth (int, optional, default 5, max 10). " +
            "Returns a nested JSON array of { name, path, type (folder/file), size (bytes, 0 for folders) }.")]
        public static string GetProjectTree(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var subPath = GetString(args, "path", "Assets");
            var maxDepth = (int)Math.Min(GetOptionalInt(args, "maxDepth").GetValueOrDefault(5), 10);
            var rootDir = Path.GetFullPath(subPath);

            if (!rootDir.StartsWith(Path.GetFullPath(Application.dataPath)) && subPath != "Assets")
                return ErrorJson($"Path must be under '{Application.dataPath}'.");

            try
            {
                var sb = new StringBuilder();
                BuildTreeJson(sb, rootDir, 0, maxDepth);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorJson($"Failed to read project tree: {ex.Message}");
            }
        }

        // Total-node cap for the project tree — large projects must not produce
        // unbounded JSON (server max payload is 256KB). When the cap is hit a
        // "truncated":true field is added to the node where listing stopped;
        // existing fields are unchanged.
        private const int MaxProjectTreeNodes = 2000;

        private sealed class TreeNodeCounter { public int Count; }

        public static void BuildTreeJson(StringBuilder sb, string dir, int depth, int maxDepth)
        {
            var counter = new TreeNodeCounter();
            BuildTreeJson(sb, dir, depth, maxDepth, counter);
        }

        private static bool BuildTreeJson(StringBuilder sb, string dir, int depth, int maxDepth, TreeNodeCounter counter)
        {
            if (depth > maxDepth) return false;
            if (depth == 0) sb.Append("[");

            bool truncated = false;

            try
            {
                var dirInfo = new DirectoryInfo(dir);
                bool first = true;

                // Directories first
                foreach (var d in dirInfo.GetDirectories().OrderBy(d => d.Name))
                {
                    if (d.Name.StartsWith(".") || d.Name == "~") continue;
                    if (counter.Count >= MaxProjectTreeNodes) { truncated = true; break; }
                    if (!first) sb.Append(","); first = false;
                    counter.Count++;
                    sb.Append($@"{{""name"":{JsonHelper.EscapeString(d.Name)},""path"":{JsonHelper.EscapeString(d.FullName)},""type"":""folder"",""size"":0,");
                    sb.Append("\"children\":[");
                    var childTruncated = BuildTreeJson(sb, d.FullName, depth + 1, maxDepth, counter);
                    sb.Append("]");
                    if (childTruncated) sb.Append(",\"truncated\":true");
                    sb.Append("}");
                }

                // Files
                foreach (var f in dirInfo.GetFiles().OrderBy(f => f.Name))
                {
                    if (f.Name.StartsWith(".") || f.Name.EndsWith(".meta")) continue;
                    if (counter.Count >= MaxProjectTreeNodes) { truncated = true; break; }
                    if (!first) sb.Append(","); first = false;
                    counter.Count++;
                    sb.Append($@"{{""name"":{JsonHelper.EscapeString(f.Name)},""path"":{JsonHelper.EscapeString(f.FullName)},""type"":""file"",""size"":{f.Length}}}");
                }
            }
            catch (UnauthorizedAccessException ex) { UnityEngine.Debug.LogWarning($"[EditorHandler] access denied: {ex.Message}"); }

            if (depth == 0)
            {
                if (truncated)
                {
                    // The root is an array, so it has no parent object to carry the
                    // marker — annotate the last emitted entry instead.
                    var lastBrace = sb.ToString().LastIndexOf('}');
                    if (lastBrace >= 0) sb.Insert(lastBrace, ",\"truncated\":true");
                }
                sb.Append("]");
            }
            return truncated;
        }
    }
}
#endif
