using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles camera-related MCP operations (screenshot).
    /// Relative savePath roots: Editor → ProjectRoot/VideoRecord, Runtime → Application.temporaryCachePath/VideoRecord.
    /// </summary>
    [MCPToolClass]
    public class CameraHandler
    {
        [MCPTool(MCPMethodConst.CAMERA_SCREENSHOT,
            "Capture a camera view and save as PNG. " +
            "Params: savePath (string, required — relative path under VideoRecord/, or absolute path), " +
            "cameraName (string, optional, default 'Main Camera'), " +
            "width (int, optional, default screen width), " +
            "height (int, optional, default screen height). " +
            "Relative paths are rooted at: Editor → <Project>/VideoRecord/, Runtime → <temporaryCachePath>/VideoRecord/. " +
            "Returns the absolute file path of the saved screenshot.",
            Platform = MCPToolPlatforms.All)]
        [MCPParam("savePath", Type = "string", Required = true, Description = "Relative path under VideoRecord/")]
        [MCPParam("cameraName", Type = "string", Description = "Camera name (default 'Main Camera')")]
        [MCPParam("width", Type = "integer", Description = "Output width px (default screen width)")]
        [MCPParam("height", Type = "integer", Description = "Output height px (default screen height)")]
        public static string Screenshot(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var savePath = GetRequiredString(args, "savePath");
            var cameraName = GetString(args, "cameraName", "Main Camera");
            var width = (int)GetOptionalInt(args, "width").GetValueOrDefault(Screen.width);
            var height = (int)GetOptionalInt(args, "height").GetValueOrDefault(Screen.height);
            const int maxScreenshotSize = 4096;
            width = Mathf.Clamp(width, 1, maxScreenshotSize);
            height = Mathf.Clamp(height, 1, maxScreenshotSize);

            // Find camera by name
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            Camera targetCam = null;
            foreach (var cam in cameras)
            {
                if (cam.name.Equals(cameraName, StringComparison.OrdinalIgnoreCase))
                {
                    targetCam = cam;
                    break;
                }
            }

            if (targetCam == null)
                return ErrorJson($"Camera '{cameraName}' not found in scene");

            // Resolve save path with platform-specific VideoRecord root
            // Reject absolute paths to prevent path traversal
            if (Path.IsPathRooted(savePath))
                return ErrorJson("Absolute paths not allowed; use a relative path under VideoRecord/");

            string rootDir;
#if UNITY_EDITOR
            rootDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "VideoRecord");
#else
            rootDir = Path.Combine(Application.temporaryCachePath, "VideoRecord");
#endif

            // Normalize and validate path stays within rootDir (prevents ../../ traversal)
            var fullRoot = Path.GetFullPath(rootDir);
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, savePath));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return ErrorJson("Path traversal not allowed; savePath must stay under VideoRecord/");

            // Ensure directory exists
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Render camera to RenderTexture (HDR if camera allows it)
            var rtFormat = targetCam.allowHDR
                ? RenderTextureFormat.DefaultHDR
                : RenderTextureFormat.ARGB32;
            var rt = new RenderTexture(width, height, 24, rtFormat);
            var oldRt = targetCam.targetTexture;
            targetCam.targetTexture = rt;
            targetCam.Render();
            targetCam.targetTexture = oldRt;

            // Read pixels
            var activeRt = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = activeRt;

            // Cleanup RT
            RenderTexture.DestroyImmediate(rt);

            // Encode to PNG and save
            var bytes = tex.EncodeToPNG();
            File.WriteAllBytes(fullPath, bytes);
            RenderTexture.DestroyImmediate(tex);

#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("filePath", JsonHelper.EscapeString(fullPath)),
                ("width", width.ToString(CultureInfo.InvariantCulture)),
                ("height", height.ToString(CultureInfo.InvariantCulture))
            );
        }
    }
}

