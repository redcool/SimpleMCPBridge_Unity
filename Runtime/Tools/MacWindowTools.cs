#if UNITY_EDITOR_OSX
using System;
using System.Diagnostics;
using System.IO;

namespace SimpleMCPBridge
{
    /// <summary>
    /// macOS window control via AppleScript (osascript).
    /// Targets the Unity Editor application by PID for reliability.
    ///
    /// Note: `get window state` uses basic AppleScript properties
    /// (miniaturized, zoomed, visible) which work without special permissions.
    /// </summary>
    public static class MacWindowTools
    {
        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Run an AppleScript command and return stdout.
        /// Returns empty string on failure (never throws).
        /// </summary>
        private static string RunScript(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("osascript", "-e " + script)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    var output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                    return output;
                }
            }
            catch
            {
                return "";
            }
        }

        private static int GetPid() => Process.GetCurrentProcess().Id;

        // ─── API ──────────────────────────────────────────────────────────

        /// <summary>Returns the current window state as a string.</summary>
        public static string GetWindowState()
        {
            // Check miniaturized (minimized to Dock)
            var r = RunScript(
                "tell application \"System Events\" to tell process id " + GetPid() +
                " to return value of attribute \"AXMinimized\" of window 1");
            if (r == "1") return "minimized";

            // Check zoomed  (fullscreen / green button)
            r = RunScript(
                "tell application \"System Events\" to tell process id " + GetPid() +
                " to return value of attribute \"AXZoomed\" of window 1");
            if (r == "1") return "maximized";

            // Check visible
            r = RunScript(
                "tell application \"System Events\" to tell process id " + GetPid() +
                " to return value of attribute \"AXRoleDescription\" of window 1");
            if (string.IsNullOrEmpty(r)) return "hidden";

            return "normal";
        }

        /// <summary>Minimize the window to the Dock.</summary>
        public static void Minimize()
        {
            RunScript(
                "tell application \"System Events\" to tell process id " + GetPid() +
                " to set value of attribute \"AXMinimized\" of window 1 to true");
        }

        /// <summary>Restore the window from minimized / zoomed state.</summary>
        public static void Restore()
        {
            var pid = GetPid();
            RunScript(
                "tell application \"System Events\" to tell process id " + pid +
                " to set value of attribute \"AXMinimized\" of window 1 to false");
            RunScript(
                "tell application \"System Events\" to tell process id " + pid +
                " to set value of attribute \"AXZoomed\" of window 1 to false");
        }

        /// <summary>Bring the Unity Editor window to front.</summary>
        public static void Focus()
        {
            RunScript("tell application \"Unity\" to activate");
        }

        /// <summary>Maximize (zoom) the window.</summary>
        public static void Maximize()
        {
            RunScript(
                "tell application \"System Events\" to tell process id " + GetPid() +
                " to set value of attribute \"AXZoomed\" of window 1 to true");
        }
    }
}
#endif
