using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Runtime Shader hot-replacement via AssetBundle download.
    ///
    /// Tools:
    ///   - shader.hot_replace        — async: download AB, load shader, swap materials
    ///   - shader.hot_replace_status — poll progress / get result
    ///
    /// Async pattern mirrors RecordingHandler: fire-and-forget Task, poll via status tool.
    /// Supports two modes:
    ///   - global: replace all materials using a given shader name
    ///   - paths:  replace materials on specific GameObjects (by Transform path)
    /// </summary>
    [MCPToolClass]
    public class ShaderHotReplaceHandler
    {
        // ─── Async state ───────────────────────────────────────────────────
        private static readonly object _stateLock = new();
        private static Task _replaceTask;
        private static string _replaceId;
        private static string _status;       // "idle" | "started" | "downloading" | "swapping" | "completed" | "error"
        private static string _error;
        private static string _mode;         // "global" | "paths"
        private static string _shaderName;
        private static int _swappedMaterials;
        private static int _swappedObjects;
        private static bool _isolated;
        private static List<string> _errors = new();

        // ─── AssetBundle lifecycle ─────────────────────────────────────────
        private static AssetBundle _loadedBundle;   // most recently loaded bundle
        private static Shader _loadedShader;        // shader loaded from that bundle

        private const int DOWNLOAD_TIMEOUT_SECONDS = 20;

        // ─── Domain-reload reset ───────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            lock (_stateLock)
            {
                _replaceTask = null;
                _replaceId = null;
                _status = "idle";
                _error = null;
                _mode = null;
                _shaderName = null;
                _swappedMaterials = 0;
                _swappedObjects = 0;
                _isolated = false;
                _errors = new List<string>();
                // _loadedBundle / _loadedShader are destroyed by Unity on domain reload
                _loadedBundle = null;
                _loadedShader = null;
            }
        }

        // ─── shader.hot_replace ────────────────────────────────────────────

        [MCPTool(MCPMethodConst.SHADER_HOT_REPLACE, "Runtime hot-swap a Shader from an AssetBundle. "
            + "Downloads AB from abUrl (via UnityWebRequest), loads Shader by name, then swaps it. "
            + "Params: abUrl (req, phone-reachable http URL), shaderName (req, Shader asset name in AB), "
            + "paths (string[] opt — Transform paths of target GameObjects; empty=GLOBAL), "
            + "oldShaderName (string opt — global: which shader name to replace, default=shaderName i.e. reload same-name; "
            + "path mode: only touch sub-materials whose current shader==this name, default=touch all sub-materials), "
            + "materialIndex (int opt — path mode: only this sub-material index, default=all). "
            + "Async: returns replaceId+status='started'; poll shader.hot_replace_status.",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string HotReplace(string paramsJson)
        {
            lock (_stateLock)
            {
                if (_replaceTask != null && !_replaceTask.IsCompleted)
                    return ErrorJson("A replace is in progress; poll shader.hot_replace_status.");

                var args = ParseJsonObject(paramsJson);
                var abUrl = GetRequiredString(args, "abUrl");
                var shaderName = GetRequiredString(args, "shaderName");

                if (string.IsNullOrEmpty(abUrl))
                    return ErrorJson("Missing required parameter: 'abUrl'");
                if (string.IsNullOrEmpty(shaderName))
                    return ErrorJson("Missing required parameter: 'shaderName'");

                var replaceId = Guid.NewGuid().ToString("N");

                // Parse optional paths
                var pathsRaw = GetRawValue(args, "paths");
                List<string> paths = null;
                if (pathsRaw != null)
                {
                    var pathsStr = pathsRaw.ToString();
                    if (!string.IsNullOrEmpty(pathsStr) && pathsStr != "[]")
                        paths = ParseStringArray(pathsStr);
                }

                // Parse optional oldShaderName
                var oldShaderName = GetString(args, "oldShaderName", null);
                if (string.IsNullOrEmpty(oldShaderName))
                    oldShaderName = null;

                // Parse optional materialIndex
                var materialIndex = GetOptionalInt(args, "materialIndex");

                // Reset state
                _replaceId = replaceId;
                _status = "started";
                _error = null;
                _mode = (paths == null || paths.Count == 0) ? "global" : "paths";
                _shaderName = shaderName;
                _swappedMaterials = 0;
                _swappedObjects = 0;
                _isolated = false;
                _errors = new List<string>();

                // Capture locals for async closure (avoids closure-over-lock issues)
                var capturedPaths = paths != null ? new List<string>(paths) : null;
                var capturedOldShaderName = oldShaderName;
                var capturedMaterialIndex = materialIndex;

                _replaceTask = RunReplaceAsync(replaceId, abUrl, shaderName,
                    capturedPaths, capturedOldShaderName, capturedMaterialIndex);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("replaceId", JsonHelper.EscapeString(replaceId)),
                    ("status", JsonHelper.EscapeString("started")),
                    ("mode", JsonHelper.EscapeString(_mode)),
                    ("shaderName", JsonHelper.EscapeString(shaderName))
                );
            }
        }

        // ─── Async replace ─────────────────────────────────────────────────

        /// <summary>
        /// Downloads the AssetBundle, loads the shader, and swaps materials.
        /// Runs on the Unity main thread (default SynchronizationContext).
        /// Has a 20-second download timeout.
        /// Stores results in static fields for polling by GetReplaceStatus.
        /// </summary>
        private static async Task RunReplaceAsync(string replaceId, string abUrl, string shaderName,
            List<string> paths, string oldShaderName, int? materialIndex)
        {
            try
            {
                // ── Step 1: Download ──
                // Use WebClient (managed HttpWebRequest → raw sockets) rather than UnityWebRequest,
                // because UnityWebRequest blocks cleartext http:// on Android ("Insecure connection
                // not allowed"). Managed sockets bypass that block — the bridge's own WebSocket
                // connection uses raw sockets for the same reason. Runs on a thread-pool thread;
                // the continuation (LoadFromMemory + swap) resumes on the Unity main thread.
                lock (_stateLock) { _status = "downloading"; }
                DebugUtils.Log($"[ShaderHotReplace] Downloading: {abUrl}");

                var dlTask = Task.Run(() =>
                {
                    using var wc = new WebClient();
                    return wc.DownloadData(abUrl);
                });
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(DOWNLOAD_TIMEOUT_SECONDS));
                var completed = await Task.WhenAny(dlTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    lock (_stateLock)
                    {
                        _status = "error";
                        _error = "Download timed out after " + DOWNLOAD_TIMEOUT_SECONDS + "s";
                    }
                    DebugUtils.LogError("[ShaderHotReplace] Download timed out.");
                    return;
                }

                byte[] bytes;
                try
                {
                    bytes = await dlTask; // observe errors (404, connection refused, etc.)
                }
                catch (Exception dex)
                {
                    lock (_stateLock)
                    {
                        _status = "error";
                        _error = "download failed: " + dex.Message;
                    }
                    DebugUtils.LogError($"[ShaderHotReplace] Download failed: {dex.Message}");
                    return;
                }

                // ── Step 2: Load AssetBundle ──
                lock (_stateLock) { _status = "swapping"; }

                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    lock (_stateLock)
                    {
                        _status = "error";
                        _error = "Failed to load AssetBundle from downloaded data";
                    }
                    DebugUtils.LogError("[ShaderHotReplace] Failed to load AssetBundle.");
                    return;
                }

                var shader = bundle.LoadAsset<Shader>(shaderName);
                if (shader == null)
                {
                    // An AssetBundle may key a shader by its file name rather than the
                    // ShaderLab name; fall back to scanning all shaders for a name match.
                    foreach (var s in bundle.LoadAllAssets<Shader>())
                    {
                        if (s != null && s.name == shaderName) { shader = s; break; }
                    }
                }
                if (shader == null)
                {
                    bundle.Unload(true);
                    lock (_stateLock)
                    {
                        _status = "error";
                        _error = "shader not found in bundle: " + shaderName;
                    }
                    DebugUtils.LogError($"[ShaderHotReplace] Shader '{shaderName}' not found in bundle.");
                    return;
                }

                // ── Step 3: Swap shaders ──
                int swappedMaterials = 0;
                int swappedObjects = 0;
                bool isolated = false;
                var errors = new List<string>();

                if (paths == null || paths.Count == 0)
                {
                    // ── Global mode ──
                    isolated = false;
                    var targetShaderName = oldShaderName ?? shaderName;
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    foreach (var mat in allMats)
                    {
                        if (mat != null && mat.shader != null && mat.shader.name.Equals(targetShaderName, StringComparison.OrdinalIgnoreCase))
                        {
                            mat.shader = shader;
                            swappedMaterials++;
                        }
                    }
                }
                else
                {
                    // ── Path mode ──
                    isolated = true;
                    foreach (var path in paths)
                    {
                        GameObject go = GameObject.Find(path);
                        if (go == null)
                        {
                            // Fallback: search all root GameObjects recursively
                            go = FindGameObjectByPath(path);
                        }
                        if (go == null)
                        {
                            errors.Add(path + ": not found");
                            continue;
                        }

                        Renderer r = go.GetComponent<Renderer>();
                        if (r == null)
                        {
                            errors.Add(path + ": no Renderer");
                            continue;
                        }

                        // Use r.materials (cloned instance materials) — only affects this object
                        var mats = r.materials;
                        bool touched = false;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            if (materialIndex.HasValue && i != materialIndex.Value)
                                continue;
                            if (mats[i] != null && mats[i].shader != null)
                            {
                                if (string.IsNullOrEmpty(oldShaderName) || mats[i].shader.name.Equals(oldShaderName, StringComparison.OrdinalIgnoreCase))
                                {
                                    mats[i].shader = shader;
                                    swappedMaterials++;
                                    touched = true;
                                }
                            }
                        }
                        if (touched)
                        {
                            r.materials = mats;
                            swappedObjects++;
                        }
                    }
                }

                // ── Step 4: Manage AssetBundle lifecycle ──
                // Store new bundle/shader; unload old bundle only after materials point to new shader
                AssetBundle oldBundle = null;
                lock (_stateLock)
                {
                    oldBundle = _loadedBundle;
                    _loadedBundle = bundle;
                    _loadedShader = shader;
                }
                if (oldBundle != null)
                {
                    oldBundle.Unload(true);
                    DebugUtils.Log("[ShaderHotReplace] Unloaded previous AssetBundle.");
                }

                // ── Step 5: Store results ──
                lock (_stateLock)
                {
                    _status = "completed";
                    _swappedMaterials = swappedMaterials;
                    _swappedObjects = swappedObjects;
                    _isolated = isolated;
                    _errors = errors;
                    _error = null;
                }

                DebugUtils.Log($"[ShaderHotReplace] Completed: swapped {swappedMaterials} materials on {swappedObjects} objects (mode={_mode})");
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _status = "error";
                    _error = "Replace failed: " + ex.Message;
                }
                DebugUtils.LogError($"[ShaderHotReplace] Failed: {ex.Message}");
            }
        }

        // ─── shader.hot_replace_status ─────────────────────────────────────

        [MCPTool(MCPMethodConst.SHADER_HOT_REPLACE_STATUS, "Poll shader.hot_replace progress. "
            + "Params: id (req, replaceId from shader.hot_replace). "
            + "Returns: status(downloading|swapping|completed|error), and on completed: swappedMaterials, swappedObjects, isolated, errors[].",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string GetReplaceStatus(string paramsJson)
        {
            lock (_stateLock)
            {
                var args = ParseJsonObject(paramsJson);
                var id = GetRequiredString(args, "id");

                if (string.IsNullOrEmpty(id) || id != _replaceId || _replaceTask == null)
                    return ErrorJson("No replace in progress for id " + id);

                var invariant = CultureInfo.InvariantCulture;

                // ── Still in progress ──
                if (_replaceTask != null && !_replaceTask.IsCompleted)
                {
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("replaceId", JsonHelper.EscapeString(_replaceId)),
                        ("status", JsonHelper.EscapeString(_status ?? "running"))
                    );
                }

                // ── Just completed — consume result ──
                if (_replaceTask != null && _replaceTask.IsCompleted)
                {
                    _replaceTask = null;

                    if (_status == "completed")
                    {
                        var errorJsons = _errors.Select(e => JsonHelper.EscapeString(e)).ToArray();

                        return JsonHelper.BuildJsonObject(
                            ("success", "true"),
                            ("replaceId", JsonHelper.EscapeString(_replaceId)),
                            ("status", JsonHelper.EscapeString("completed")),
                            ("mode", JsonHelper.EscapeString(_mode)),
                            ("shaderName", JsonHelper.EscapeString(_shaderName)),
                            ("swappedMaterials", _swappedMaterials.ToString(invariant)),
                            ("swappedObjects", _swappedObjects.ToString(invariant)),
                            ("isolated", _isolated ? "true" : "false"),
                            ("errors", JsonHelper.BuildJsonArray(errorJsons))
                        );
                    }

                    // Error state
                    return JsonHelper.BuildJsonObject(
                        ("success", "false"),
                        ("replaceId", JsonHelper.EscapeString(_replaceId)),
                        ("status", JsonHelper.EscapeString("error")),
                        ("error", JsonHelper.EscapeString(_error ?? "Unknown error"))
                    );
                }

                return ErrorJson("No replace in progress for id " + id);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Parse a JSON string array like ["a","b","c"] into a List&lt;string&gt;.
        /// Handles quoted strings with commas inside.
        /// </summary>
        private static List<string> ParseStringArray(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json) || json.Trim() == "[]")
                return result;

            var trimmed = json.Trim().TrimStart('[').TrimEnd(']').Trim();
            if (string.IsNullOrEmpty(trimmed))
                return result;

            bool inStr = false;
            int start = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                    inStr = !inStr;
                if (!inStr && c == ',')
                {
                    var item = trimmed.Substring(start, i - start).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(item))
                        result.Add(item);
                    start = i + 1;
                }
            }
            if (start < trimmed.Length)
            {
                var item = trimmed.Substring(start).Trim().Trim('"');
                if (!string.IsNullOrEmpty(item))
                    result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Find a GameObject by path. Tries GameObject.Find first (handles
        /// "Canvas/Panel/Button" style paths), then falls back to a recursive
        /// search through all root GameObjects in the active scene.
        /// </summary>
        private static GameObject FindGameObjectByPath(string path)
        {
            var go = GameObject.Find(path);
            if (go != null) return go;

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                var found = SearchTransformRecursive(root.transform, path);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject SearchTransformRecursive(Transform parent, string targetName)
        {
            if (parent.name == targetName)
                return parent.gameObject;

            for (int i = 0; i < parent.childCount; i++)
            {
                var found = SearchTransformRecursive(parent.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }
    }
}