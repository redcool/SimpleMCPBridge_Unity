using System;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Cross-platform window control facade.
    ///
    /// Platform            Implementation
    /// ─────────────────────────────────────────────────
    /// Windows (Editor)   Win32Tools  — user32.dll P/Invoke
    /// macOS   (Editor)   MacWindowTools — osascript (AppleScript)
    /// Other              All methods return safe defaults / no-op
    ///
    /// Usage (from any Editor tool):
    ///   var hWnd = WindowTools.GetWindowHandle();
    ///   var state = WindowTools.GetWindowState(hWnd);
    ///   WindowTools.Minimize(hWnd);
    ///   WindowTools.Restore(hWnd);
    ///   WindowTools.Focus(hWnd);
    ///   WindowTools.Maximize(hWnd);
    ///
    /// On macOS the hWnd parameter is ignored (AppleScript targets the
    /// process by PID). Pass IntPtr.Zero or any value.
    /// </summary>
    public static class WindowTools
    {
        /// <summary>
        /// Get the Unity Editor main window handle.
        /// On macOS this returns IntPtr.Zero (AppleScript doesn't need handles).
        /// </summary>
        public static IntPtr GetWindowHandle()
        {
#if UNITY_EDITOR_WIN
            return Win32Tools.GetWindowHandle();
#else
            return IntPtr.Zero;
#endif
        }

        /// <summary>
        /// Returns current window state: "normal", "minimized", "maximized", "hidden", or "unknown".
        /// hWnd is used on Windows, ignored on macOS.
        /// </summary>
        public static string GetWindowState(IntPtr hWnd)
        {
#if UNITY_EDITOR_WIN
            return Win32Tools.GetWindowState(hWnd);
#elif UNITY_EDITOR_OSX
            return MacWindowTools.GetWindowState();
#else
            return "unknown";
#endif
        }

        /// <summary>Minimize the Editor window.</summary>
        public static void Minimize(IntPtr hWnd)
        {
#if UNITY_EDITOR_WIN
            Win32Tools.Minimize(hWnd);
#elif UNITY_EDITOR_OSX
            MacWindowTools.Minimize();
#endif
        }

        /// <summary>Restore the Editor window (un-minimize / un-zoom).</summary>
        public static void Restore(IntPtr hWnd)
        {
#if UNITY_EDITOR_WIN
            Win32Tools.Restore(hWnd);
#elif UNITY_EDITOR_OSX
            MacWindowTools.Restore();
#endif
        }

        /// <summary>Bring the Editor window to front.</summary>
        public static void Focus(IntPtr hWnd)
        {
#if UNITY_EDITOR_WIN
            Win32Tools.Focus(hWnd);
#elif UNITY_EDITOR_OSX
            MacWindowTools.Focus();
#endif
        }

        /// <summary>Maximize (zoom) the Editor window.</summary>
        public static void Maximize(IntPtr hWnd)
        {
#if UNITY_EDITOR_WIN
            Win32Tools.Maximize(hWnd);
#elif UNITY_EDITOR_OSX
            MacWindowTools.Maximize();
#endif
        }
    }
}
