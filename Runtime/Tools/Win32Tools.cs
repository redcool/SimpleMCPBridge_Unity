using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Win32 user32.dll P/Invoke wrappers for Unity Editor window control.
    /// All members are public for maximum flexibility.
    ///
    /// Usage:
    ///   var hWnd = Win32Tools.GetWindowHandle();
    ///   var state = Win32Tools.GetWindowState(hWnd); // "normal" | "minimized" | "maximized" | "hidden"
    ///   Win32Tools.Minimize(hWnd);
    ///   Win32Tools.Restore(hWnd);
    ///   Win32Tools.Focus(hWnd);   // restore + SetForeground
    ///   Win32Tools.Maximize(hWnd);
    /// </summary>
    public static class Win32Tools
    {
        // ─── Win32 constants ──────────────────────────────────────────────

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        // ─── Structs ──────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public int ptMinPosition_x;
            public int ptMinPosition_y;
            public int ptMaxPosition_x;
            public int ptMaxPosition_y;
            public int rcNormalPosition_left;
            public int rcNormalPosition_top;
            public int rcNormalPosition_right;
            public int rcNormalPosition_bottom;
        }

        // ─── P/Invoke declarations ────────────────────────────────────────

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        // ─── High-level helpers ───────────────────────────────────────────

        /// <summary>
        /// Returns the main window handle of the current Unity Editor process.
        /// Returns IntPtr.Zero if no window handle is available.
        /// </summary>
        public static IntPtr GetWindowHandle()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.MainWindowHandle;
            }
        }

        /// <summary>
        /// Returns the current window state as a string:
        /// "normal", "minimized", "maximized", or "hidden".
        /// </summary>
        public static string GetWindowState(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return "unknown";

            if (IsIconic(hWnd))
                return "minimized";

            if (IsZoomed(hWnd))
                return "maximized";

            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            if (GetWindowPlacement(hWnd, ref wp) && wp.showCmd == SW_HIDE)
                return "hidden";

            return "normal";
        }

        /// <summary>Minimize the window.</summary>
        public static void Minimize(IntPtr hWnd)
        {
            ShowWindowAsync(hWnd, SW_MINIMIZE);
        }

        /// <summary>Restore the window (un-minimize / un-maximize).</summary>
        public static void Restore(IntPtr hWnd)
        {
            ShowWindowAsync(hWnd, SW_RESTORE);
        }

        /// <summary>
        /// Restore the window (if minimized) and bring it to the foreground.
        /// </summary>
        public static void Focus(IntPtr hWnd)
        {
            ShowWindowAsync(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }

        /// <summary>Maximize the window.</summary>
        public static void Maximize(IntPtr hWnd)
        {
            ShowWindowAsync(hWnd, SW_SHOWMAXIMIZED);
        }
    }
}
