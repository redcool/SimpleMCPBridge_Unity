#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles camera-related MCP operations (screenshot, camera queries).
    /// Editor only — uses ScreenCapture API + AssetDatabase for save path resolution.
    /// </summary>
    [MCPToolClass]
    public class CameraHandler
    {
        [MCPTool(MCPMethodConst.CAMERA_SCREENSHOT, "Capture the main camera view and save as PNG. " +
            "Params: savePath (string, required — where to save, relative to project root or absolute), " +
            "cameraName (string, optional, default 'Main Camera'), " +
            "width (int, optional, default screen width), " +
            "height (int, optional, default screen height). " +
            "Returns the absolute file path of the saved screenshot.")]
        public static string Screenshot(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var savePath = GetRequiredString(args, "savePath");
            var cameraName = GetString(args, "cameraName", "Main Camera");
            var width = (int)GetOptionalInt(args, "width").GetValueOrDefault(Screen.width);
            var height = (int)GetOptionalInt(args, "height").GetValueOrDefault(Screen.height);

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

            // Resolve save path: if relative, resolve from project root
            string fullPath;
            if (Path.IsPathRooted(savePath))
            {
                fullPath = savePath;
            }
            else
            {
                // Relative to project root (where Assets/ lives)
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                fullPath = Path.Combine(projectRoot, savePath);
            }

            // Ensure directory exists
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Render camera to RenderTexture
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
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
            targetCam.targetTexture = oldRt;
            RenderTexture.DestroyImmediate(rt);

            // Encode to PNG and save
            var bytes = tex.EncodeToPNG();
            File.WriteAllBytes(fullPath, bytes);
            RenderTexture.DestroyImmediate(tex);

            AssetDatabase.Refresh();

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("filePath", JsonHelper.EscapeString(fullPath)),
                ("width", width.ToString(CultureInfo.InvariantCulture)),
                ("height", height.ToString(CultureInfo.InvariantCulture))
            );
        }
    }
}
#endif

