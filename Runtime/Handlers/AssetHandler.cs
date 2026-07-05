#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        public string RefreshAssets(string paramsJson)
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

        [MCPTool(MCPMethodConst.FIND_ASSETS, "Search project Assets by name and/or type. " +
            "Examples: nameContains='Player', typeFilter='Prefab', or both. " +
            "Returns array of {path, name, type, guid}.")]
        public string FindAssets(string paramsJson)
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
        public string FindReferences(string paramsJson)
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
    }
}
#endif
