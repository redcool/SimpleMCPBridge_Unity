#if UNITY_INPUT_SYSTEM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Unified input simulation that supports both Input System and (where possible)
    /// Legacy Input Manager axes.
    ///
    /// Design:
    /// - Keyboard keys: delegate to KeyboardTools (QueueStateEvent on physical keyboard)
    /// - Mouse click/move: delegate to MouseDeviceTools (QueueStateEvent on physical mouse)
    /// - Mouse scroll: direct QueueStateEvent on Mouse.current
    /// - Axes: virtual Gamepad device for Input System action maps + common axis names
    /// - Legacy Input Manager: axes work when Active Input Handling = "Both" or "New"
    ///   (Input.GetAxis reads from Input System actions). For "Old" only,
    ///   axis simulation requires OS-level input — use key_press as fallback.
    ///
    /// No MCPTool attributes — utility only.
    /// </summary>
    public static class InputActionTools
    {
        // ── Virtual Gamepad for axis simulation ──

        private static Gamepad _virtualGamepad;

        /// <summary>
        /// Get or create a virtual Gamepad device for axis simulation.
        /// The Input System maps virtual device input to actions bound to gamepad controls,
        /// and when Active Input Handling is "Both" or "New", legacy Input.GetAxis also
        /// reads from these actions.
        /// </summary>
        public static Gamepad VirtualGamepad
        {
            get
            {
                if (_virtualGamepad == null || !_virtualGamepad.added)
                {
                    // Remove stale reference if device was removed
                    if (_virtualGamepad != null)
                        _virtualGamepad = null;

                    _virtualGamepad = InputSystem.AddDevice<Gamepad>("VirtualAgentGamepad");
                }
                return _virtualGamepad;
            }
        }

        /// <summary>
        /// Remove the virtual gamepad device.
        /// </summary>
        public static void RemoveVirtualGamepad()
        {
            if (_virtualGamepad != null && _virtualGamepad.added)
            {
                InputSystem.RemoveDevice(_virtualGamepad);
            }
            _virtualGamepad = null;
        }

        // ── Axis mapping (common names → gamepad controls) ──

        private static readonly Dictionary<string, (AxisControl axis, float multiplier)> s_axisMap = new()
        {
            // Movement axes
            { "Horizontal",       (null, 1f) },  // special: leftStick.x
            { "Vertical",         (null, 1f) },  // special: leftStick.y
            { "Mouse X",          (null, 1f) },  // special: rightStick.x
            { "Mouse Y",          (null, 1f) },  // special: rightStick.y

            // Analog trigger buttons
            { "Fire1",            (null, 1f) },
            { "Fire2",            (null, 1f) },
            { "Fire3",            (null, 1f) },

            // Submit / Cancel (UI)
            { "Submit",           (null, 1f) },
            { "Cancel",           (null, 1f) },
        };

        // Track current virtual gamepad state for incremental updates
        private static GamepadState s_lastGamepadState;

        /// <summary>
        /// Apply axis values to the virtual gamepad.
        /// Returns a list of applied axis names.
        /// </summary>
        public static List<string> ApplyAxes(Dictionary<string, float> axes)
        {
            var applied = new List<string>();
            if (axes == null || axes.Count == 0) return applied;

            var gp = VirtualGamepad;
            var state = s_lastGamepadState;

            foreach (var kvp in axes)
            {
                var name = kvp.Key.ToLowerInvariant();
                var val = Mathf.Clamp(kvp.Value, -1f, 1f);

                switch (name)
                {
                    case "horizontal":
                        state.leftStick.x = val;
                        applied.Add(kvp.Key);
                        break;
                    case "vertical":
                        state.leftStick.y = val;
                        applied.Add(kvp.Key);
                        break;
                    case "mouse x":
                        state.rightStick.x = val;
                        applied.Add(kvp.Key);
                        break;
                    case "mouse y":
                        state.rightStick.y = val;
                        applied.Add(kvp.Key);
                        break;
                    case "fire1":
                        state.rightTrigger = Mathf.Abs(val);
                        applied.Add(kvp.Key);
                        break;
                    case "fire2":
                        state.leftTrigger = Mathf.Abs(val);
                        applied.Add(kvp.Key);
                        break;
                    case "fire3":
                        if (val > 0.5f) state.buttons |= (ushort)(1 << (int)GamepadButton.RightStick);
                        applied.Add(kvp.Key);
                        break;
                    case "submit":
                        if (val > 0.5f) state.buttons |= (ushort)(1 << (int)GamepadButton.South);
                        applied.Add(kvp.Key);
                        break;
                    case "cancel":
                        if (val > 0.5f) state.buttons |= (ushort)(1 << (int)GamepadButton.East);
                        applied.Add(kvp.Key);
                        break;
                    default:
                        // Unknown axis — try to find a matching Input Action by name
                        // and queue a generic float state change
                        TryApplyUnknownAxis(name, val, gp, ref state);
                        applied.Add(kvp.Key);
                        break;
                }
            }

            InputSystem.QueueStateEvent(gp, state);
            InputSystem.Update();

            s_lastGamepadState = state;

            return applied;
        }

        private static void TryApplyUnknownAxis(string name, float val, Gamepad gp, ref GamepadState state)
        {
            // Map by name prefixes for common patterns
            if (name.StartsWith("look") || name.Contains("look"))
            {
                state.rightStick.x = val;
                return;
            }
            if (name.Contains("trigger") || name.Contains("throttle"))
            {
                state.rightTrigger = Mathf.Abs(val);
                return;
            }
            // Default: apply to left stick X
            state.leftStick.x = val;
        }

        /// <summary>
        /// Reset virtual gamepad axes to neutral.
        /// </summary>
        public static void ResetAxes()
        {
            if (_virtualGamepad == null || !_virtualGamepad.added) return;

            s_lastGamepadState = new GamepadState(); // all zero = neutral
            InputSystem.QueueStateEvent(_virtualGamepad, s_lastGamepadState);
            InputSystem.Update();
        }

        // ── Scroll Wheel ──

        /// <summary>
        /// Simulate mouse scroll wheel by delta.
        /// Positive = scroll up, negative = scroll down.
        /// Uses QueueStateEvent on the physical mouse device.
        /// </summary>
        public static void ScrollWheel(float delta)
        {
            var mouse = Mouse.current;
            if (mouse == null)
                throw new InvalidOperationException("No physical mouse device found (Mouse.current is null)");

            var state = new MouseState
            {
                position = mouse.position.ReadValue(),
                scroll = Vector2.up * delta,
            };

            InputSystem.QueueStateEvent(mouse, state);
            InputSystem.Update();
        }

        // ── Combined action ──

        /// <summary>
        /// Execute a combined input action: keys + mouse + axes + scroll.
        /// This is the implementation behind input.action MCP tool.
        /// </summary>
        public static Dictionary<string, object> ExecuteAction(
            List<(string key, string action)> keys,
            (float? x, float? y, float? dx, float? dy, float? scroll, List<(int button, string action)> buttons)? mouse,
            Dictionary<string, float> axes)
        {
            var result = new Dictionary<string, object>();
            var details = new List<string>();

            // 1. Keys
            if (keys != null)
            {
                foreach (var (key, action) in keys)
                {
                    try
                    {
                        var k = KeyboardTools.ParseKey(key);
                        switch (action)
                        {
                            case "tap": KeyboardTools.TapKey(k); break;
                            case "hold": KeyboardTools.HoldKey(k); break;
                            case "release": KeyboardTools.ReleaseKey(k); break;
                        }
                        details.Add($"key:{key}/{action}");
                    }
                    catch (Exception ex)
                    {
                        details.Add($"key:{key}/error:{ex.Message}");
                    }
                }
            }

            // 2. Mouse
            if (mouse.HasValue)
            {
                var m = mouse.Value;

                // Absolute position + click
                if (m.x.HasValue && m.y.HasValue)
                {
                    if (m.buttons != null)
                    {
                        foreach (var (btn, btnAction) in m.buttons)
                        {
                            if (btnAction == "click" || btnAction == "tap")
                            {
                                MouseDeviceTools.ClickMouse(new Vector2(m.x.Value, m.y.Value), btn);
                                details.Add($"mouse:click({m.x.Value:F3},{m.y.Value:F3})/btn{btn}");
                            }
                        }
                    }
                }

                // Delta move (camera look)
                if (m.dx.HasValue || m.dy.HasValue)
                {
                    float dx = m.dx ?? 0;
                    float dy = m.dy ?? 0;
                    if (dx != 0 || dy != 0)
                    {
                        MouseDeviceTools.MoveMouse(new Vector2(dx, dy));
                        details.Add($"mouse:move({dx:F1},{dy:F1})");
                    }
                }

                // Scroll
                if (m.scroll.HasValue && m.scroll.Value != 0)
                {
                    ScrollWheel(m.scroll.Value);
                    details.Add($"scroll:{m.scroll.Value:F1}");
                }
            }

            // 3. Axes
            if (axes != null && axes.Count > 0)
            {
                var matchedAxes = ApplyAxes(axes);
                foreach (var a in matchedAxes)
                    details.Add($"axis:{a}={axes[a]:F2}");
                foreach (var a in axes.Keys.Except(matchedAxes))
                    details.Add($"axis:{a}/unmatched");
            }

            result["actions"] = details;
            return result;
        }
    }
}
#endif
