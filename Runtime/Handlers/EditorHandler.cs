#if UNITY_EDITOR
using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles Unity Editor window manipulation tools.
    /// Editor only — uses Win32 API to control the Editor window.
    /// </summary>
    [MCPToolClass]
    public class EditorHandler
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;

        private static IntPtr GetUnityWindowHandle()
        {
            // Get the main window handle of the current Unity Editor process
            using (var process = Process.GetCurrentProcess())
            {
                return process.MainWindowHandle;
            }
        }

        [MCPTool(MCPMethodConst.EDITOR_WINDOW_FOCUS,
            "Minimize, restore, or focus the Unity Editor window via Win32 API. " +
            "Params: action (string, required) — 'minimize' (send to taskbar), 'restore' (restore from minimized), " +
            "or 'focus' (restore + bring to foreground). " +
            "Useful for testing Editor behavior when losing/regaining focus.")]
        public static string WindowFocus(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var action = GetRequiredString(args, "action").ToLowerInvariant();

            var hWnd = GetUnityWindowHandle();
            if (hWnd == IntPtr.Zero)
                return ErrorJson("Could not find Unity Editor window handle");

            switch (action)
            {
                case "minimize":
                    ShowWindowAsync(hWnd, SW_MINIMIZE);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("minimize"))
                    );
                case "restore":
                    ShowWindowAsync(hWnd, SW_RESTORE);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("restore"))
                    );
                case "focus":
                    ShowWindowAsync(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("focus"))
                    );
                default:
                    return ErrorJson($"Unknown action '{action}'. Use 'minimize', 'restore', or 'focus'.");
            }
        }
    }
}
#endif
