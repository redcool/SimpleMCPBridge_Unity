#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using SimpleMCPBridge;
using UnityEditor;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles project Asset-related MCP operations.
    /// All methods are Editor-only (uses AssetDatabase).
    ///
    /// Tools:
    ///   - asset.find_assets     — search Assets/ by name and/or type
    ///   - asset.find_references — find all assets that reference a given asset (reverse dependency)
    /// </summary>
    [MCPToolClass]
    public class AssetHandler
    {
        /// <summary>
        /// Triggers AssetDatabase.Refresh and returns immediately.
        /// If new scripts are imported, a domain reload will occur
        /// and the MCP connection will drop — this is expected.
        /// The client should wait for the bridge to reconnect to
        /// confirm the refresh completed successfully.
        /// </summary>
        [MCPTool(MCPMethodConst.REFRESH_ASSETS, "Refresh Unity's asset database to import new files or detect changes. " +
            "If new scripts are imported, a domain reload will occur and the MCP connection will drop. " +
            "The client should poll /health until bridgeConnected=true to confirm completion.")]
        public static string RefreshAssets(string paramsJson)
        {
            // Parse empty params (but accept any input)
            _ = ParseJsonObject(paramsJson ?? "{}");

            // Trigger asset refresh — this may cause a domain reload if new scripts are detected
            AssetDatabase.Refresh();

            // Return success — but if a domain reload occurs, this response won't reach the server
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("message", JsonHelper.EscapeString("Asset refresh triggered. If new scripts were imported, expect a domain reload — poll /health for bridge reconnection."))
            );
        }

        [MCPTool(MCPMethodConst.FIND_ASSETS,
            "Search Assets/ by name and/or type. " +
            "Params: nameContains (string, optional) — substring to match against asset names. " +
            "typeFilter (string, optional) — Unity asset type name, e.g. 'Prefab', 'Material', 'Texture', 'Scene'. " +
            "Returns { filter, count, assets: [{ path, name, type, guid }] }.",
            Platform = MCPToolPlatforms.Editor)]
        public static string FindAssets(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var nameContains = GetString(args, "nameContains");
            var typeFilter = GetString(args, "typeFilter"); // e.g. "Prefab", "Material", "Texture"

            // Build AssetDatabase filter string
            // Format: "t:Prefab Player" or just "Player" or just "t:Material"
            var filter = "";
            if (!string.IsNullOrEmpty(typeFilter))
                filter += "t:" + typeFilter;
            if (!string.IsNullOrEmpty(nameContains))
                filter += (filter.Length > 0 ? " " : "") + nameContains;

            if (string.IsNullOrEmpty(filter))
                filter = "*"; // return everything (AssetDatabase treats empty as nothing)

            var guids = AssetDatabase.FindAssets(filter);
            var results = new List<string>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var assetName = Path.GetFileNameWithoutExtension(path);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);

                results.Add(JsonHelper.BuildJsonObject(
                    ("path", JsonHelper.EscapeString(path)),
                    ("name", JsonHelper.EscapeString(assetName)),
                    ("type", assetType != null ? JsonHelper.EscapeString(assetType.Name) : "\"unknown\""),
                    ("guid", JsonHelper.EscapeString(guid))
                ));
            }

            return JsonHelper.BuildJsonObject(
                ("filter", JsonHelper.EscapeString(filter)),
                ("count", results.Count.ToString(CultureInfo.InvariantCulture)),
                ("assets", JsonHelper.BuildJsonArray(results.ToArray()))
            );
        }

        [MCPTool(MCPMethodConst.FIND_REFERENCES, "Find all assets that reference a given asset by scanning file content for the target's GUID. " +
            "Reliable but slower than ref: filter — scans all text-based asset files in Assets/. " +
            "Params: assetPath (required). Returns array of referencing assets with their GUIDs.")]
        public static string FindReferences(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var assetPath = GetRequiredString(args, "assetPath");

            // Step 1: Get the target's GUID
            var targetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(targetGuid))
                return ErrorJson($"Asset not found at path: {assetPath}");

            // Step 2: Scan all text-based asset files in Assets/ for the GUID
            var results = new List<string>();
            var assetsDir = Application.dataPath;
            var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".psd", ".exr", ".hdr",
                ".fbx", ".obj", ".dae", ".3ds", ".blend", ".mb", ".ma",
                ".wav", ".mp3", ".ogg", ".aiff", ".aif",
                ".mp4", ".mov", ".avi", ".webm",
                ".asset",  // binary serialized
            };

            var files = Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                // Skip .meta and .cs files
                var ext = Path.GetExtension(file);
                if (ext == ".meta" || ext == ".cs" || ext == ".cs.meta")
                    continue;
                // Skip binary files
                if (binaryExtensions.Contains(ext))
                    continue;

                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains(targetGuid))
                    {
                        // Convert absolute path to "Assets/..." relative path
                        var relativePath = "Assets" + file.Substring(assetsDir.Length).Replace('\\', '/');

                        var refGuid = AssetDatabase.AssetPathToGUID(relativePath);
                        var assetName = Path.GetFileNameWithoutExtension(file);
                        var assetType = AssetDatabase.GetMainAssetTypeAtPath(relativePath);

                        results.Add(JsonHelper.BuildJsonObject(
                            ("path", JsonHelper.EscapeString(relativePath)),
                            ("name", JsonHelper.EscapeString(assetName)),
                            ("type", assetType != null ? JsonHelper.EscapeString(assetType.Name) : "\"unknown\""),
                            ("guid", JsonHelper.EscapeString(refGuid))
                        ));
                    }
                }
                catch
                {
                    // Skip files that can't be read (binary, locked, etc.)
                }
            }

return JsonHelper.BuildJsonObject(
                ("targetPath", JsonHelper.EscapeString(assetPath)),
                ("targetGuid", JsonHelper.EscapeString(targetGuid)),
                ("referenceCount", results.Count.ToString(CultureInfo.InvariantCulture)),
                ("references", JsonHelper.BuildJsonArray(results.ToArray()))
            );
        }

        [MCPTool(MCPMethodConst.BUILD_BUNDLE,
            "Build a shader AssetBundle and upload it to the server, returning a phone-reachable abUrl for shader.hot_replace. Editor only. "
            + "Params: shaderPaths (string[] req — Unity asset paths like 'Assets/Shaders/My.shader'), "
            + "fileName (string opt — bundle/upload name, default 'shaders_hot'), "
            + "buildTarget (string opt — e.g. 'Android','StandaloneWindows64'; default current Editor target). "
            + "Returns: success, abUrl, fileName, bytes, shaderNames[].",
            Platform = MCPToolPlatforms.Editor)]
        public static string BuildBundle(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var invariant = CultureInfo.InvariantCulture;

            // Parse shaderPaths (required string array)
            var shaderPathsRaw = GetRawValue(args, "shaderPaths");
            if (shaderPathsRaw == null)
                return ErrorJson("Missing required parameter: 'shaderPaths'");
            var shaderPaths = ParseStringArray(shaderPathsRaw.ToString());
            if (shaderPaths.Count == 0)
                return ErrorJson("shaderPaths must contain at least one shader path");

            // Parse fileName (optional, with validation)
            var fileName = GetString(args, "fileName", "shaders_hot");
            if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Za-z0-9._-]+$"))
                return ErrorJson("fileName must match ^[A-Za-z0-9._-]+$");

            // Parse buildTarget (optional)
            var buildTargetStr = GetString(args, "buildTarget", null);
            BuildTarget bt;
            if (!string.IsNullOrEmpty(buildTargetStr))
            {
                if (!Enum.TryParse<BuildTarget>(buildTargetStr, out bt))
                    return ErrorJson("Invalid buildTarget: " + buildTargetStr);
            }
            else
            {
                bt = EditorUserBuildSettings.activeBuildTarget;
            }

            // Build AssetBundle
            var tmpDir = Path.Combine(Application.temporaryCachePath, "mcp_ab_build_" + fileName);
            Directory.CreateDirectory(tmpDir);

            var builds = new AssetBundleBuild[1];
            builds[0].assetBundleName = fileName;
            builds[0].assetNames = shaderPaths.ToArray();

            var options = BuildAssetBundleOptions.None;
            var manifest = BuildPipeline.BuildAssetBundles(tmpDir, builds, options, bt);

            if (manifest == null)
            {
                // Clean up on failure
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, true);
                return ErrorJson("AssetBundle build failed. Check that shaderPaths are valid Unity asset paths.");
            }

            var outputPath = Path.Combine(tmpDir, fileName);
            if (!File.Exists(outputPath))
            {
                Directory.Delete(tmpDir, true);
                return ErrorJson("Build succeeded but output file not found at: " + outputPath);
            }

            var fileBytes = File.ReadAllBytes(outputPath);
            var fileSize = fileBytes.Length;

            // Transfer to server — same-PC fast path first: server reads the local build file
            // directly (no multi-MB upload). Cross-PC: server can't access localpath → falls back
            // to streaming upload. Either way the server returns a phone-reachable abUrl.
            BridgeConfig.LoadConfig(out var ip, out var port);
            string abUrl;
            string transferMode;

            try
            {
                // Probe: empty-body POST with localpath — server copies the file locally if reachable.
                var probeUrl = $"http://{ip}:{port}/ab?name={Uri.EscapeDataString(fileName)}&localpath={Uri.EscapeDataString(outputPath)}";
                byte[] respBytes;
                using (var wc = new WebClient())
                {
                    respBytes = wc.UploadData(probeUrl, "POST", new byte[0]);
                }
                var respStr = Encoding.UTF8.GetString(respBytes);
                var respJson = ParseJsonObject(respStr);
                var ok = GetString(respJson, "ok", "");
                var code = GetString(respJson, "code", "");
                abUrl = GetString(respJson, "url", null);

                if (string.Equals(ok, "true", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(abUrl))
                {
                    transferMode = "local-copy";
                }
                else if (code == "local_unavailable")
                {
                    // Cross-PC: stream the bytes up.
                    var streamUrl = $"http://{ip}:{port}/ab?name={Uri.EscapeDataString(fileName)}";
                    using (var wc2 = new WebClient())
                    {
                        respBytes = wc2.UploadData(streamUrl, "POST", fileBytes);
                    }
                    respStr = Encoding.UTF8.GetString(respBytes);
                    respJson = ParseJsonObject(respStr);
                    abUrl = GetString(respJson, "url", null);
                    if (string.IsNullOrEmpty(abUrl))
                    {
                        Directory.Delete(tmpDir, true);
                        return ErrorJson("Server upload response missing 'url'. Response: " + respStr);
                    }
                    transferMode = "upload";
                }
                else
                {
                    Directory.Delete(tmpDir, true);
                    return ErrorJson("Unexpected server response to local probe: " + respStr);
                }
            }
            catch (Exception ex)
            {
                Directory.Delete(tmpDir, true);
                return ErrorJson("Asset transfer failed: " + ex.Message);
            }

            // Clean up temp directory (server has already copied/collected the bytes)
            try { Directory.Delete(tmpDir, true); }
            catch { /* best-effort cleanup */ }

            // Build shader names array for response
            var shaderNamesJson = shaderPaths
                .Select(p => JsonHelper.EscapeString(Path.GetFileNameWithoutExtension(p)))
                .ToArray();

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("abUrl", JsonHelper.EscapeString(abUrl)),
                ("fileName", JsonHelper.EscapeString(fileName)),
                ("bytes", fileSize.ToString(invariant)),
                ("shaderNames", JsonHelper.BuildJsonArray(shaderNamesJson)),
                ("buildTarget", JsonHelper.EscapeString(bt.ToString())),
                ("transferMode", JsonHelper.EscapeString(transferMode)),
                ("message", JsonHelper.EscapeString("AB ready. Pass abUrl to shader.hot_replace on the runtime bridge."))
            );
        }

        /// <summary>
        /// Parse a JSON string array like ["a","b","c"] into a List&lt;string&gt;.
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
    }
}
#endif

