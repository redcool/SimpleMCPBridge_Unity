#if UNITY_INPUT_SYSTEM
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge
{

    public static class MouseDeviceTools
    {
        // ── Win32 SendInput ──────────────────────────────────────────
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public int dwFlags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public MOUSEINPUT mi;
        }

        private const int INPUT_MOUSE = 0;
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;
        private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const int MOUSEEVENTF_MIDDLEUP = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// Win32 SendInput click at current OS cursor position.
        /// Cursor must be positioned first (via Cursor.WarpCursorPosition).
        /// </summary>
        private static void SendClickWin32(int buttonId)
        {
            int downFlag, upFlag;
            switch (buttonId)
            {
                case 1:
                    downFlag = MOUSEEVENTF_RIGHTDOWN; upFlag = MOUSEEVENTF_RIGHTUP; break;
                case 2:
                    downFlag = MOUSEEVENTF_MIDDLEDOWN; upFlag = MOUSEEVENTF_MIDDLEUP; break;
                default:
                    downFlag = MOUSEEVENTF_LEFTDOWN; upFlag = MOUSEEVENTF_LEFTUP; break;
            }

            var inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dwFlags = downFlag;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = upFlag;

            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
#endif

        // ── Input System fallback ────────────────────────────────────

        /// <summary>
        /// Ensure an UNNAMED mouse device exists as Mouse.current.
        /// For platforms without Win32 SendInput.
        /// </summary>
        private static Mouse EnsureDefaultMouse()
        {
            var named = InputSystem.GetDevice<Mouse>("virtualMouse");
            if (named != null)
                InputSystem.RemoveDevice(named);

            if (Mouse.current != null)
                return Mouse.current;

            return InputSystem.AddDevice<Mouse>();
        }

        // ── Deferred release (Input System fallback only) ────────────
        static float _lastClickTime;
        static Vector2 _pendingClickUV;
        static int _pendingClickButton;
        static bool _pendingRelease;

        /// <summary>
        /// Click at normalized screen UV.
        /// Uses Win32 SendInput on Windows Editor/Standalone (OS-level click).
        /// Falls back to InputSystem.QueueStateEvent on other platforms.
        /// </summary>
        public static void ClickMouse(Vector2 screenUV, int buttonId = 0)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // ── Win32 path: real OS mouse event ──
            // 1. Warp OS cursor to game view pixel
            var pixelPos = new Vector2(Screen.width * screenUV.x, Screen.height * screenUV.y);
            Mouse.current?.WarpCursorPosition(pixelPos);

            // 2. Send OS-level click at that position
            SendClickWin32(buttonId);
#else
            // ── Input System fallback path ──
            var mouse = EnsureDefaultMouse();
            var screenPos = new Vector2(Screen.width * screenUV.x, Screen.height * screenUV.y);
            var buttonsMask = 1 << buttonId;

            var mouseState = new MouseState
            {
                buttons = (ushort)buttonsMask,
                position = screenPos,
                clickCount = 1,
            };
            InputSystem.QueueStateEvent(mouse, mouseState);

            _pendingClickUV = screenUV;
            _pendingClickButton = buttonId;
            _pendingRelease = true;
            _lastClickTime = Time.unscaledTime;
#endif
        }

        /// <summary>
        /// Called from MCPBridge.Update per frame to complete deferred clicks.
        /// Only used by Input System fallback path (non-Windows).
        /// </summary>
        public static void TickDeferredClick()
        {
#if !(UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            if (!_pendingRelease) return;
            if (Time.unscaledTime - _lastClickTime < 0.05f) return;

            var mouse = EnsureDefaultMouse();
            var screenPos = new Vector2(Screen.width * _pendingClickUV.x, Screen.height * _pendingClickUV.y);
            var mouseState = new MouseState
            {
                buttons = 0,
                position = screenPos,
                clickCount = 0,
            };
            InputSystem.QueueStateEvent(mouse, mouseState);
            _pendingRelease = false;
#endif
        }

        /// <summary>
        /// Move mouse by pixel delta (for camera look/aim).
        /// Uses Input System (no Win32 equivalent needed).
        /// </summary>
        public static void MoveMouse(Vector2 delta)
        {
            var mouse = EnsureDefaultMouse();
            if (mouse == null)
                throw new System.InvalidOperationException("No mouse device available");

            uint buttons = 0;
            if (mouse.leftButton.isPressed) buttons |= 1;
            if (mouse.rightButton.isPressed) buttons |= 2;
            if (mouse.middleButton.isPressed) buttons |= 4;

            var mouseState = new MouseState
            {
                position = mouse.position.ReadValue(),
                delta = delta,
                buttons = (ushort)buttons,
            };

            InputSystem.QueueStateEvent(mouse, mouseState);
            InputSystem.Update();
        }
    }
}
#endif
