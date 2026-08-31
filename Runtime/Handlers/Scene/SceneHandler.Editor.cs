using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using System;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // SceneHandler 编辑器组（partial）：enter/exit/pause_play_mode / request_compile / open_window / get_play_mode / save_current
    public partial class SceneHandler
    {
#if UNITY_EDITOR
        [MCPTool(MCPMethodConst.ENTER_PLAY_MODE, "Enter Play Mode in the Unity Editor")]
        public static string EnterPlayMode(string paramsJson)
        {
            var succeeded = UnityEditor.EditorApplication.isPlaying;
            if (!succeeded)
            {
                UnityEditor.EditorApplication.isPlaying = true;
                succeeded = UnityEditor.EditorApplication.isPlaying;
            }
            return JsonHelper.BuildJsonObject(
                ("success", succeeded ? "true" : "false"),
                ("isPlaying", succeeded ? "true" : "false")
            );
        }

        [MCPTool(MCPMethodConst.EXIT_PLAY_MODE, "Exit Play Mode in the Unity Editor")]
        public static string ExitPlayMode(string paramsJson)
        {
            // Defer exit so the JSON-RPC response is sent via WebSocket
            // before OnPlayModeStateChanged(ExitingPlayMode) fires and
            // potentially disconnects the bridge.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                UnityEditor.EditorApplication.isPlaying = false;
            };
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.PAUSE_PLAY_MODE, "Pause or resume Play Mode in the Unity Editor — set paused=true to pause, false to resume")]
        [MCPParam("paused", Type = "boolean", Required = true, Description = "True to pause, false to resume")]
        public static string PausePlayMode(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var paused = GetRequiredBool(args, "paused");
            UnityEditor.EditorApplication.isPaused = paused;
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.REQUEST_COMPILE, "Request Unity to recompile all scripts. " +
            "Ensures compilation by minimizing/restoring the Editor window first " +
            "(triggers Unity's event processing which is required for compilation to start reliably).")]
        public static string RequestCompile(string paramsJson)
        {
            var hWnd = WindowTools.GetWindowHandle();

            // Step 1: Minimize (lose focus → force Unity to process pending events)
            WindowTools.Minimize(hWnd);

            // Step 2: After 300ms, restore and request compilation
            double startTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update += OnPostMinimize;

            void OnPostMinimize()
            {
                if (UnityEditor.EditorApplication.timeSinceStartup - startTime < 0.3)
                    return;
                UnityEditor.EditorApplication.update -= OnPostMinimize;

                // Step 3: Restore window (refocus)
                WindowTools.Restore(hWnd);

                // Step 4: Force reimport and request compilation
                UnityEditor.AssetDatabase.Refresh();
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.OPEN_WINDOW, "Open a Unity Editor window by menu path — use the exact path as shown in Unity's menu bar (e.g. 'Window/General/Console'). Returns an error if the menu item is not found.")]
        [MCPParam("menuPath", Type = "string", Required = true, Description = "Exact Unity menu path, e.g. 'Window/General/Console'")]
        public static string OpenWindow(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var menuPath = GetRequiredString(args, "menuPath");
            var success = UnityEditor.EditorApplication.ExecuteMenuItem(menuPath);
            return JsonHelper.BuildJsonObject(("success", success ? "true" : "false"));
        }

        [MCPTool(MCPMethodConst.GET_PLAY_MODE, "Get current Unity Editor play mode state — returns playing/paused/edit mode status")]
        public static string GetPlayMode(string paramsJson)
        {
            var mode = "edit";
            if (UnityEditor.EditorApplication.isPlaying)
            {
                mode = UnityEditor.EditorApplication.isPaused ? "paused" : "playing";
            }
            return JsonHelper.BuildJsonObject(
                ("isPlaying", JsonHelper.BoolJson(UnityEditor.EditorApplication.isPlaying)),
                ("isPaused", JsonHelper.BoolJson(UnityEditor.EditorApplication.isPaused)),
                ("mode", JsonHelper.EscapeString(mode))
            );
        }

        [MCPTool(MCPMethodConst.SAVE_CURRENT_SCENE, "Save the current Unity scene. If savePath is not provided, saves the current scene in place. If the scene is untitled, savePath is required.")]
        [MCPParam("savePath", Type = "string", Description = "Scene asset path under Assets/ (required if untitled)")]
        public static string SaveCurrent(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var savePath = GetString(args, "savePath");

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(savePath))
            {
                if (string.IsNullOrEmpty(scene.path))
                    return ErrorJson("Scene is untitled. Provide 'savePath' to specify where to save.");
                savePath = scene.path;
            }

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, savePath, true);
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("path", JsonHelper.EscapeString(savePath))
            );
        }
#endif
    }
}
