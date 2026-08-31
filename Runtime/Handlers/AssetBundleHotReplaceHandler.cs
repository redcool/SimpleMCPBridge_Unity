using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
using SimpleMCPBridge.Runtime;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Generic AssetBundle hot-deploy handler.
    /// Downloads an AssetBundle, loads all assets, and auto-distributes them by type:
    ///   Shader → global swap by shader name (or per-path with cloned instance materials)
    ///   Material → replace renderer material slots by name
    ///   Texture → replace material texture properties by name
    ///   AudioClip → replace AudioSource clips by name
    ///   Mesh → replace MeshFilter meshes by name
    ///   ScriptableObject → replace component field references by name + type (current scene only)
    ///   GameObject (Prefab) → instantiate into scene
    ///
    /// Async pattern: fire-and-forget Task, poll via assetbundle.hot_replace_status.
    /// AssetBundles are kept loaded (not unloaded) so instantiated prefabs and replaced
    /// references stay valid. Call assetbundle.unload_all to free them (breaks references).
    ///
    /// Pattern mirrors ShaderHotReplaceHandler: WebClient download, lock-based state machine,
    /// [RuntimeInitializeOnLoadMethod] domain-reload reset, LoadAllAssets fallback.
    /// </summary>
    [MCPToolClass]
    public partial class AssetBundleHotReplaceHandler
    {
        // ─── Async state (mirrors ShaderHotReplaceHandler pattern) ───────────
        private static readonly object _stateLock = new();
        private static Task _replaceTask;
        private static string _replaceId;
        private static string _status;        // "idle" | "started" | "downloading" | "loading" | "completed" | "error"
        private static string _error;
        private static string[] _errorsArr;   // final errors array (elements already EscapeString'd)

        // Per-type counts
        private static int _shadersSwapped;
        private static int _materialsReplaced;
        private static int _texturesSwapped;
        private static int _audioClipsReplaced;
        private static int _meshesReplaced;
        private static int _scriptableObjectsReplaced;
        private static int _prefabsInstantiated;

        private static string _instanceIdsJson;  // JSON array string of instantiated prefab instanceIds
        private static bool _isolated;

        private const int DOWNLOAD_TIMEOUT_SECONDS = 20;

        // ─── Backup / Rollback ────────────────────────────────────────────
        private static bool _hasBackup;   // true when saveBackup was requested
        private static bool _wasDryRun;   // true when dryRun was requested

        /// <summary>
        /// Stored per-type original references before swap, keyed by replaceId.
        /// Only populated when saveBackup=true in hot_replace.
        /// </summary>
        private class HotReplaceBackup
        {
            public string replaceId;
            public bool isolated;

            // Shader (global): material ref → original shader
            public Dictionary<Material, Shader> shaderBackup = new();

            // Renderer-level snapshot: renderer ref → original sharedMaterials
            // Used by: isolated shader, isolated/global material (full-array replacement)
            public Dictionary<Renderer, Material[]> rendererSnapshot = new();

            // Material (global) slot-level: (renderer, slot) → original material at that slot
            public class MaterialSlotKey { public Renderer renderer; public int slot; }
            // Use simpler struct approach — Custom equality comparer for tuple with Unity refs
            public Dictionary<(Renderer, int), Material> materialSlots = new();

            // Texture: (material, propertyName) → original texture
            public Dictionary<(Material, string), Texture> textureBackup = new();

            // AudioClip: audioSource ref → original clip
            public Dictionary<AudioSource, AudioClip> audioBackup = new();

            // Mesh: meshFilter ref → original mesh
            public Dictionary<MeshFilter, Mesh> meshBackup = new();

            // SO: parallel lists
            public List<Component> soComponents = new();
            public List<string> soFieldNames = new();
            public List<ScriptableObject> soOriginals = new();

            // Prefab: instantiated GameObject refs
            public List<GameObject> prefabInstances = new();
        }

        private static readonly Dictionary<string, HotReplaceBackup> _backups = new();

        // ─── Bundle lifecycle: keyed by abUrl ──────────────────────────────
        // LoadBundle() unloads any old bundle with the same URL before loading
        // the new one, so redeploying the same URL works cleanly. Multiple URLs
        // accumulate — call assetbundle.unload_all to free all.
        private static readonly Dictionary<string, AssetBundle> _loadedBundles = new();

        // ─── ReplaceContext: shared state for RunAsync ─────────────────────
        private class ReplaceContext
        {
            // Input
            public string replaceId;
            public string abUrl;
            public List<string> types;
            public List<string> paths;
            public string oldShaderName;
            public Vector3 position;
            public Quaternion rotation;
            public Transform parent;
            public bool saveBackup;
            public bool dryRun;
            public bool isolated;

            // Bundle
            public byte[] bytes;
            public AssetBundle bundle;

            // Grouped assets
            public List<Shader> shaders;
            public List<Material> materials;
            public List<Texture> textures;
            public List<AudioClip> audioClips;
            public List<Mesh> meshes;
            public List<ScriptableObject> scriptableObjects;
            public List<GameObject> prefabs;

            // Results
            public int shadersSwapped;
            public int materialsReplaced;
            public int texturesSwapped;
            public int audioClipsReplaced;
            public int meshesReplaced;
            public int scriptableObjectsReplaced;
            public int prefabsInstantiated;
            public List<string> instanceIds = new();
            public List<string> errors = new();
            public HotReplaceBackup backup;

            public bool Want(string t) => types == null || types.Contains(t, StringComparer.OrdinalIgnoreCase);
        }

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
                _errorsArr = null;
                _shadersSwapped = 0;
                _materialsReplaced = 0;
                _texturesSwapped = 0;
                _audioClipsReplaced = 0;
                _meshesReplaced = 0;
                _scriptableObjectsReplaced = 0;
                _prefabsInstantiated = 0;
                _instanceIdsJson = null;
                _isolated = false;
                _hasBackup = false;
                _wasDryRun = false;
                _backups.Clear();
                _loadedBundles.Clear();
            }
        }

        // ─── Tool 1: assetbundle.hot_replace ───────────────────────────────

        [MCPTool(MCPMethodConst.ASSETBUNDLE_HOT_REPLACE, "Download an AssetBundle and auto-deploy its assets by type. "
            + "Shader→global swap by name; Material→replace scene renderers' same-named material; Texture→replace materials' same-named texture prop; "
            + "AudioClip→replace AudioSources' same-named clip; Mesh→replace MeshFilters' same-named mesh; ScriptableObject→replace component field refs by name+type; "
            + "GameObject(Prefab)→instantiate. Async: returns replaceId; poll assetbundle.hot_replace_status. "
            + "Params: abUrl (req), types (string[] opt — whitelist of type names e.g. ['Shader','Material']; default=all present), "
            + "paths (string[] opt — for Shader/Material: restrict to these GameObjects via cloned instance materials; omit=global), "
            + "oldShaderName (string opt — for Shader: which shader name to replace; default=the shader's own name), "
            + "prefabPosition [3] opt (default [0,0,0]), prefabRotation [3] opt (default [0,0,0]), prefabParentPath (string opt — parent Transform path, default=scene root).",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string HotReplace(string paramsJson)
        {
            lock (_stateLock)
            {
                if (_replaceTask != null && !_replaceTask.IsCompleted)
                    return ErrorJson("A deploy is in progress; poll assetbundle.hot_replace_status.");

                var args = ParseJsonObject(paramsJson);
                var abUrl = GetRequiredString(args, "abUrl");
                if (string.IsNullOrEmpty(abUrl))
                    return ErrorJson("Missing required parameter: 'abUrl'");

                // Parse optional types whitelist
                var typesRaw = GetRawValue(args, "types");
                List<string> types = null;
                if (typesRaw != null)
                {
                    var typesStr = typesRaw.ToString();
                    if (!string.IsNullOrEmpty(typesStr) && typesStr != "[]")
                        types = ParseStringArray(typesStr);
                }

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

                // Parse optional prefabPosition / prefabRotation
                var posArr = GetOptionalFloatArray(args, "prefabPosition");
                var rotArr = GetOptionalFloatArray(args, "prefabRotation");

                // Parse optional prefabParentPath
                var parentPath = GetString(args, "prefabParentPath", "");

                // ── New: saveBackup & dryRun ──
                var saveBackup = false;
                var sbRaw = GetRawValue(args, "saveBackup");
                if (sbRaw != null && bool.TryParse(sbRaw.ToString(), out var sbVal))
                    saveBackup = sbVal;

                var dryRun = false;
                var drRaw = GetRawValue(args, "dryRun");
                if (drRaw != null && bool.TryParse(drRaw.ToString(), out var drVal))
                    dryRun = drVal;

                var replaceId = Guid.NewGuid().ToString("N");

                // Reset state
                _replaceId = replaceId;
                _status = "started";
                _error = null;
                _errorsArr = null;
                _shadersSwapped = 0;
                _materialsReplaced = 0;
                _texturesSwapped = 0;
                _audioClipsReplaced = 0;
                _meshesReplaced = 0;
                _scriptableObjectsReplaced = 0;
                _prefabsInstantiated = 0;
                _instanceIdsJson = null;
                _isolated = (paths != null && paths.Count > 0);
                _hasBackup = saveBackup;
                _wasDryRun = dryRun;

                // Capture locals for async closure
                var capturedTypes = types != null ? new List<string>(types) : null;
                var capturedPaths = paths != null ? new List<string>(paths) : null;
                var capturedOldShaderName = oldShaderName;
                var capturedPosArr = posArr;
                var capturedRotArr = rotArr;
                var capturedParentPath = parentPath;
                var capturedSaveBackup = saveBackup;
                var capturedDryRun = dryRun;

                _replaceTask = RunAsync(replaceId, abUrl,
                    capturedTypes, capturedPaths, capturedOldShaderName,
                    capturedPosArr, capturedRotArr, capturedParentPath,
                    capturedSaveBackup, capturedDryRun);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("replaceId", JsonHelper.EscapeString(replaceId)),
                    ("status", JsonHelper.EscapeString("started")),
                    ("isolated", _isolated ? "true" : "false")
                );
            }
        }

        // ─── Async run (mirrors ShaderHotReplaceHandler.RunReplaceAsync) ───

        private static async Task RunAsync(string replaceId, string abUrl,
            List<string> types, List<string> paths, string oldShaderName,
            float[] posArr, float[] rotArr, string parentPath,
            bool saveBackup, bool dryRun)
        {
            var ctx = new ReplaceContext
            {
                replaceId = replaceId,
                abUrl = abUrl,
                types = types,
                paths = paths,
                oldShaderName = oldShaderName,
                saveBackup = saveBackup,
                dryRun = dryRun,
                isolated = (paths != null && paths.Count > 0),
                position = (posArr != null && posArr.Length >= 3)
                    ? new Vector3(posArr[0], posArr[1], posArr[2])
                    : Vector3.zero,
                rotation = (rotArr != null && rotArr.Length >= 3)
                    ? Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2])
                    : Quaternion.identity,
            };
            if (!string.IsNullOrEmpty(parentPath))
            {
                var pg = GameObject.Find(parentPath);
                if (pg != null) ctx.parent = pg.transform;
                else ctx.errors.Add("prefabParentPath not found: " + parentPath);
            }
            if (saveBackup) ctx.backup = new HotReplaceBackup();

            try
            {
                if (!await DownloadBundleAsync(ctx)) return;
                if (!LoadBundle(ctx)) return;
                GroupAssets(ctx);

                if (ctx.Want("Shader") && ctx.shaders.Count > 0) DispatchShader(ctx);
                if (ctx.Want("Material") && ctx.materials.Count > 0) DispatchMaterial(ctx);
                if (ctx.Want("Texture") && ctx.textures.Count > 0) DispatchTexture(ctx);
                if (ctx.Want("AudioClip") && ctx.audioClips.Count > 0) DispatchAudioClip(ctx);
                if (ctx.Want("Mesh") && ctx.meshes.Count > 0) DispatchMesh(ctx);
                if (ctx.Want("ScriptableObject") && ctx.scriptableObjects.Count > 0) DispatchScriptableObject(ctx);
                if (ctx.Want("GameObject") && ctx.prefabs.Count > 0) DispatchPrefab(ctx);

                StoreResults(ctx);
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _status = "error";
                    _error = "Deploy failed: " + ex.Message;
                }
                DebugUtils.LogError($"[AssetBundleHotReplace] Failed: {ex.Message}");
            }
        }

        private static async Task<bool> DownloadBundleAsync(ReplaceContext ctx)
        {
            lock (_stateLock) { _status = "downloading"; }
            DebugUtils.Log($"[AssetBundleHotReplace] Downloading: {ctx.abUrl}");

            var dlTask = Task.Run(() =>
            {
                using var wc = new WebClient();
                return wc.DownloadData(ctx.abUrl);
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
                DebugUtils.LogError("[AssetBundleHotReplace] Download timed out.");
                return false;
            }

            try
            {
                ctx.bytes = await dlTask;
                return true;
            }
            catch (Exception dex)
            {
                lock (_stateLock)
                {
                    _status = "error";
                    _error = "download failed: " + dex.Message;
                }
                DebugUtils.LogError($"[AssetBundleHotReplace] Download failed: {dex.Message}");
                return false;
            }
        }

        private static bool LoadBundle(ReplaceContext ctx)
        {
            lock (_stateLock) { _status = "loading"; }

            // Unload any existing bundle with the same URL first.
            // This avoids Unity's "another AssetBundle with the same files
            // is already loaded" error and ensures fresh content.
            UnloadBundleByUrl(ctx.abUrl);

            ctx.bundle = AssetBundle.LoadFromMemory(ctx.bytes);
            if (ctx.bundle == null)
            {
                // Maybe a different loaded bundle (different URL) has the
                // same content identity. Rare edge case — fall back to
                // unloading everything and retrying.
                DebugUtils.Log("[AssetBundleHotReplace] Load failed, retrying after unloading ALL old bundles...");
                UnloadAllLoadedBundles();

                ctx.bundle = AssetBundle.LoadFromMemory(ctx.bytes);
                if (ctx.bundle == null)
                {
                    lock (_stateLock)
                    {
                        _status = "error";
                        _error = "Failed to load AssetBundle from downloaded data";
                    }
                    DebugUtils.LogError("[AssetBundleHotReplace] Failed to load AssetBundle.");
                    return false;
                }
            }

            lock (_stateLock) { _loadedBundles[ctx.abUrl] = ctx.bundle; }
            return true;
        }

        private static void GroupAssets(ReplaceContext ctx)
        {
            var allAssets = ctx.bundle.LoadAllAssets();
            ctx.shaders = allAssets.OfType<Shader>().ToList();
            ctx.materials = allAssets.OfType<Material>().ToList();
            ctx.textures = allAssets.OfType<Texture>().ToList();
            ctx.audioClips = allAssets.OfType<AudioClip>().ToList();
            ctx.meshes = allAssets.OfType<Mesh>().ToList();
            ctx.scriptableObjects = allAssets.OfType<ScriptableObject>().ToList();
            ctx.prefabs = allAssets.OfType<GameObject>().ToList();
        }

        private static void StoreResults(ReplaceContext ctx)
        {
            lock (_stateLock)
            {
                _status = "completed";
                _shadersSwapped = ctx.shadersSwapped;
                _materialsReplaced = ctx.materialsReplaced;
                _texturesSwapped = ctx.texturesSwapped;
                _audioClipsReplaced = ctx.audioClipsReplaced;
                _meshesReplaced = ctx.meshesReplaced;
                _scriptableObjectsReplaced = ctx.scriptableObjectsReplaced;
                _prefabsInstantiated = ctx.prefabsInstantiated;
                _instanceIdsJson = ctx.instanceIds.Count > 0
                    ? JsonHelper.BuildJsonArray(ctx.instanceIds.ToArray())
                    : "[]";
                _errorsArr = ctx.errors.Count > 0
                    ? ctx.errors.Select(e => JsonHelper.EscapeString(e)).ToArray()
                    : new string[0];
                _error = null;

                if (ctx.saveBackup)
                {
                    ctx.backup.replaceId = ctx.replaceId;
                    ctx.backup.isolated = _isolated;
                    _backups[ctx.replaceId] = ctx.backup;
                }
            }

            DebugUtils.Log($"[AssetBundleHotReplace] done: shader={ctx.shadersSwapped} mat={ctx.materialsReplaced} tex={ctx.texturesSwapped} audio={ctx.audioClipsReplaced} mesh={ctx.meshesReplaced} so={ctx.scriptableObjectsReplaced} prefab={ctx.prefabsInstantiated}");
        }

        /// Unload all cached bundles. Called as fallback when loading a new
        /// bundle fails (possible "same files" conflict across different URLs).
        private static void UnloadAllLoadedBundles()
        {
            List<AssetBundle> toUnload;
            lock (_stateLock)
            {
                toUnload = new List<AssetBundle>(_loadedBundles.Values);
                _loadedBundles.Clear();
            }
            // Unload outside the lock — Unload(true) may trigger callbacks
            // that re-enter tool dispatch and would deadlock on _stateLock.
            foreach (var b in toUnload)
            {
                try { b.Unload(true); }
                catch { /* best-effort */ }
            }
        }

        /// Unload a specific bundle by its URL, if loaded. Called before
        /// loading the same URL again to avoid identity conflicts.
        private static void UnloadBundleByUrl(string abUrl)
        {
            AssetBundle old = null;
            lock (_stateLock)
            {
                if (_loadedBundles.TryGetValue(abUrl, out old))
                    _loadedBundles.Remove(abUrl);
            }
            // Unload outside the lock to avoid callback re-entrancy deadlock.
            if (old != null)
            {
                try { old.Unload(true); }
                catch { /* best-effort */ }
            }
        }

        // ─── Tool 2: assetbundle.hot_replace_status ────────────────────────

        [MCPTool(MCPMethodConst.ASSETBUNDLE_HOT_REPLACE_STATUS, "Poll assetbundle.hot_replace progress. "
            + "Params: id (req). Returns status(downloading|loading|completed|error) and on completed per-type counts + instanceIds + errors[].",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string GetStatus(string paramsJson)
        {
            lock (_stateLock)
            {
                var args = ParseJsonObject(paramsJson);
                var id = GetRequiredString(args, "id");

                if (string.IsNullOrEmpty(id) || id != _replaceId || _replaceTask == null)
                    return ErrorJson("No deploy in progress for id " + id);

                var invariant = CultureInfo.InvariantCulture;

                // ── Still in progress ──
                if (_replaceTask != null && !_replaceTask.IsCompleted)
                {
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("replaceId", JsonHelper.EscapeString(_replaceId)),
                        ("status", JsonHelper.EscapeString(_status ?? "running")),
                        ("hasBackup", _hasBackup ? "true" : "false"),
                        ("dryRun", _wasDryRun ? "true" : "false")
                    );
                }

                // ── Just completed — consume result ──
                if (_replaceTask != null && _replaceTask.IsCompleted)
                {
                    _replaceTask = null;

                    if (_status == "completed")
                    {
                        return JsonHelper.BuildJsonObject(
                            ("success", "true"),
                            ("replaceId", JsonHelper.EscapeString(_replaceId)),
                            ("status", JsonHelper.EscapeString("completed")),
                            ("isolated", _isolated ? "true" : "false"),
                            ("hasBackup", _hasBackup ? "true" : "false"),
                            ("dryRun", _wasDryRun ? "true" : "false"),
                            ("shadersSwapped", _shadersSwapped.ToString(invariant)),
                            ("materialsReplaced", _materialsReplaced.ToString(invariant)),
                            ("texturesSwapped", _texturesSwapped.ToString(invariant)),
                            ("audioClipsReplaced", _audioClipsReplaced.ToString(invariant)),
                            ("meshesReplaced", _meshesReplaced.ToString(invariant)),
                            ("scriptableObjectsReplaced", _scriptableObjectsReplaced.ToString(invariant)),
                            ("prefabsInstantiated", _prefabsInstantiated.ToString(invariant)),
                            ("instanceIds", _instanceIdsJson),
                            ("errors", JsonHelper.BuildJsonArray(_errorsArr ?? new string[0]))
                        );
                    }

                    // Error state
                    return JsonHelper.BuildJsonObject(
                        ("success", "false"),
                        ("replaceId", JsonHelper.EscapeString(_replaceId)),
                        ("status", JsonHelper.EscapeString("error")),
                        ("hasBackup", _hasBackup ? "true" : "false"),
                        ("dryRun", _wasDryRun ? "true" : "false"),
                        ("error", JsonHelper.EscapeString(_error ?? "Unknown error"))
                    );
                }

                return ErrorJson("No deploy in progress for id " + id);
            }
        }

        // ─── Tool 3: assetbundle.unload_all ────────────────────────────────

        [MCPTool(MCPMethodConst.ASSETBUNDLE_UNLOAD_ALL, "Unload ALL deployed AssetBundles. "
            + "WARNING: instantiated prefabs and replaced assets will lose their references (materials turn pink). "
            + "Use between iteration cycles to free memory, then redeploy. Returns count unloaded.",
            Platform = MCPToolPlatforms.All, RequirePlayMode = true)]
        public static string UnloadAll(string paramsJson)
        {
            List<AssetBundle> toUnload;
            int n;
            lock (_stateLock)
            {
                n = _loadedBundles.Count;
                toUnload = new List<AssetBundle>(_loadedBundles.Values);
                _loadedBundles.Clear();

                // Reset pending task state too
                _replaceTask = null;
                _replaceId = null;
                _status = "idle";
                _error = null;
                _errorsArr = null;
                _shadersSwapped = 0;
                _materialsReplaced = 0;
                _texturesSwapped = 0;
                _audioClipsReplaced = 0;
                _meshesReplaced = 0;
                _scriptableObjectsReplaced = 0;
                _prefabsInstantiated = 0;
                _instanceIdsJson = null;
                _isolated = false;
            }

            // Unload outside the lock — Unload(true) may trigger callbacks
            // that re-enter tool dispatch and would deadlock on _stateLock.
            foreach (var b in toUnload)
            {
                try { b.Unload(true); }
                catch { /* best-effort */ }
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("unloaded", n.ToString(CultureInfo.InvariantCulture)),
                ("message", JsonHelper.EscapeString("All bundles unloaded. Replaced assets and instances lost their references \u2014 redeploy to restore."))
            );
        }

        // ─── Tool 4: assetbundle.rollback ─────────────────────────────────

            }
}
