#if UNITY_INPUT_SYSTEM
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Gamepad input simulation via Input System virtual Gamepad.
    ///
    /// Provides dedicated gamepad operations with friendly button names:
    ///   face: south (A), east (B), north (Y), west (X)
    ///   shoulder: leftShoulder (LB), rightShoulder (RB)
    ///   trigger: leftTrigger, rightTrigger (digital press)
    ///   stick: leftStick, rightStick (thumbstick click)
    ///   system: start, select
    ///   dpad: dpadUp, dpadDown, dpadLeft, dpadRight
    ///   axis: leftStickX/Y, rightStickX/Y, leftTrigger, rightTrigger
    ///
    /// Uses the same virtual Gamepad device as InputActionTools.
    /// </summary>
    public static class GamepadTools
    {
        // ── Button name → GamepadButton mapping ──

        private static readonly Dictionary<string, GamepadButton> s_buttonNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Face buttons (Xbox / PlayStation naming)
            { "south",          GamepadButton.South },       // A / Cross
            { "east",           GamepadButton.East },        // B / Circle
            { "north",          GamepadButton.North },       // Y / Triangle
            { "west",           GamepadButton.West },        // X / Square
            { "a",              GamepadButton.South },
            { "b",              GamepadButton.East },
            { "y",              GamepadButton.North },
            { "x",              GamepadButton.West },
            // Shoulder
            { "leftshoulder",   GamepadButton.LeftShoulder },
            { "rightshoulder",  GamepadButton.RightShoulder },
            { "lb",             GamepadButton.LeftShoulder },
            { "rb",             GamepadButton.RightShoulder },
            // Thumbstick click
            { "leftstick",      GamepadButton.LeftStick },
            { "rightstick",     GamepadButton.RightStick },
            // System
            { "start",          GamepadButton.Start },
            { "select",         GamepadButton.Select },
            { "back",           GamepadButton.Select },
            // D-pad
            { "dpadup",         GamepadButton.DpadUp },
            { "dpaddown",       GamepadButton.DpadDown },
            { "dpadleft",       GamepadButton.DpadLeft },
            { "dpadright",      GamepadButton.DpadRight },
        };

        // ── Axis name → setter ──

        private delegate void AxisSetter(ref GamepadState state, float val);

        private static readonly Dictionary<string, AxisSetter> s_axisSetters = new(StringComparer.OrdinalIgnoreCase)
        {
            { "leftstickx",     (ref GamepadState s, float v) => { s.leftStick.x = Mathf.Clamp(v, -1f, 1f); } },
            { "leftsticky",     (ref GamepadState s, float v) => { s.leftStick.y = Mathf.Clamp(v, -1f, 1f); } },
            { "rightstickx",    (ref GamepadState s, float v) => { s.rightStick.x = Mathf.Clamp(v, -1f, 1f); } },
            { "rightsticky",    (ref GamepadState s, float v) => { s.rightStick.y = Mathf.Clamp(v, -1f, 1f); } },
            { "lefttrigger",    (ref GamepadState s, float v) => { s.leftTrigger = Mathf.Clamp01(v); } },
            { "righttrigger",   (ref GamepadState s, float v) => { s.rightTrigger = Mathf.Clamp01(v); } },
            // Legacy aliases
            { "horizontal",     (ref GamepadState s, float v) => { s.leftStick.x = Mathf.Clamp(v, -1f, 1f); } },
            { "vertical",       (ref GamepadState s, float v) => { s.leftStick.y = Mathf.Clamp(v, -1f, 1f); } },
            { "lookx",          (ref GamepadState s, float v) => { s.rightStick.x = Mathf.Clamp(v, -1f, 1f); } },
            { "looky",          (ref GamepadState s, float v) => { s.rightStick.y = Mathf.Clamp(v, -1f, 1f); } },
        };

        // ── Shared virtual gamepad + tracked state ──

        private static Gamepad _virtualGamepad;
        private static GamepadState s_lastGamepadState;

        private static Gamepad VirtualGamepad
        {
            get
            {
                if (_virtualGamepad == null || !_virtualGamepad.added)
                {
                    if (_virtualGamepad != null)
                        _virtualGamepad = null;
                    _virtualGamepad = InputSystem.AddDevice<Gamepad>("VirtualAgentGamepad");
                }
                return _virtualGamepad;
            }
        }

        // ── Button helpers ──

        /// <summary>Parse a button name string into GamepadButton.</summary>
        public static GamepadButton ParseButton(string name)
        {
            if (s_buttonNames.TryGetValue(name, out var btn))
                return btn;
            // Try parsing as integer
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                && idx >= 0 && idx <= 13)
                return (GamepadButton)idx;
            throw new ArgumentException($"Unknown gamepad button '{name}'. " +
                "Valid: south/north/east/west (a/b/x/y), leftShoulder/rightShoulder (lb/rb), " +
                "leftStick/rightStick, start/select, dpadUp/down/left/right");
        }

        /// <summary>Get current gamepad state from the virtual device.</summary>
        public static GamepadState GetCurrentState()
        {
            return s_lastGamepadState;
        }

        /// <summary>Apply a modified GamepadState to the virtual device.</summary>
        public static void ApplyState(GamepadState state)
        {
            var gp = VirtualGamepad;
            s_lastGamepadState = state;
            InputSystem.QueueStateEvent(gp, state);
            // No forced InputSystem.Update() — let the system process events
            // on its natural update cycle. Forcing an update in Play Mode
            // can cause long stalls or deadlocks.
        }

        /// <summary>Press a button (set bit).</summary>
        public static void PressButton(GamepadButton button)
        {
            var state = s_lastGamepadState;
            state.buttons |= (ushort)(1 << (int)button);
            ApplyState(state);
        }

        /// <summary>Release a button (clear bit).</summary>
        public static void ReleaseButton(GamepadButton button)
        {
            var state = s_lastGamepadState;
            state.buttons &= (ushort)~(1 << (int)button);
            ApplyState(state);
        }

        /// <summary>Tap a button: press + immediate release.</summary>
        public static void TapButton(GamepadButton button)
        {
            PressButton(button);
            ReleaseButton(button);
        }

        /// <summary>Check if a button is currently pressed in the virtual gamepad state.</summary>
        public static bool IsButtonPressed(GamepadButton button)
        {
            var state = GetCurrentState();
            return (state.buttons & (1 << (int)button)) != 0;
        }

        // ── Axis helpers ──

        /// <summary>Set a single axis value.</summary>
        public static void SetAxis(string axisName, float value)
        {
            if (!s_axisSetters.TryGetValue(axisName, out var setter))
                throw new ArgumentException($"Unknown gamepad axis '{axisName}'. " +
                    "Valid: leftStickX/Y, rightStickX/Y, leftTrigger, rightTrigger");

            var state = s_lastGamepadState;
            setter(ref state, value);
            ApplyState(state);
        }

        /// <summary>Set multiple axes at once.</summary>
        public static void SetAxes(Dictionary<string, float> axes)
        {
            if (axes == null || axes.Count == 0) return;

            var state = s_lastGamepadState;

            foreach (var kvp in axes)
            {
                if (s_axisSetters.TryGetValue(kvp.Key, out var setter))
                    setter(ref state, kvp.Value);
            }

            ApplyState(state);
        }

        /// <summary>Reset all buttons and axes to neutral.</summary>
        public static void ResetAll()
        {
            if (_virtualGamepad == null || !_virtualGamepad.added) return;
            ApplyState(new GamepadState()); // all zero = neutral
            StopRumble();
        }

        /// <summary>Remove the virtual gamepad device.</summary>
        public static void RemoveDevice()
        {
            if (_virtualGamepad != null && _virtualGamepad.added)
            {
                InputSystem.RemoveDevice(_virtualGamepad);
            }
            _virtualGamepad = null;
        }

        // ── Rumble (motor speeds on the virtual device) ──

        private static float s_rumbleLow;
        private static float s_rumbleHigh;
        private static double s_rumbleEndTime;

        /// <summary>
        /// Trigger rumble on the virtual gamepad for a duration.
        /// Frequencies are clamped to 0-1; duration &lt;= 0 stops immediately.
        /// </summary>
        public static void SetRumble(float lowFreq, float highFreq, float duration)
        {
            if (duration <= 0f)
            {
                StopRumble();
                return;
            }

            var low = Mathf.Clamp01(lowFreq);
            var high = Mathf.Clamp01(highFreq);

            VirtualGamepad.SetMotorSpeeds(low, high);

            s_rumbleLow = low;
            s_rumbleHigh = high;
            s_rumbleEndTime = Time.unscaledTimeAsDouble + duration;
        }

        /// <summary>Stop rumble and clear the motor speeds.</summary>
        public static void StopRumble()
        {
            VirtualGamepad.SetMotorSpeeds(0f, 0f);
            s_rumbleLow = 0f;
            s_rumbleHigh = 0f;
            s_rumbleEndTime = 0d;
        }

        /// <summary>
        /// Per-frame auto zeroing — stops the rumble once the duration has elapsed.
        /// Hooked into MCPBridge.Update / InstanceUpdate.
        /// </summary>
        public static void TickRumble()
        {
            if (s_rumbleEndTime > 0d && Time.unscaledTimeAsDouble >= s_rumbleEndTime)
                StopRumble();
        }

        /// <summary>Current rumble state: active flag, low motor speed, high motor speed.</summary>
        public static (bool active, float low, float high) GetRumbleState()
        {
            if (s_rumbleEndTime > 0d)
                return (true, s_rumbleLow, s_rumbleHigh);
            return (false, 0f, 0f);
        }

        // ── Apply multiple button operations ──

        /// <summary>
        /// Execute a batch of button operations and axis sets.
        /// Returns a list of action descriptions for the response.
        /// </summary>
        public static List<string> ExecuteBatch(
            List<(string button, string action)> buttons,
            Dictionary<string, float> axes)
        {
            var details = new List<string>();

            if (buttons != null)
            {
                foreach (var (btnName, action) in buttons)
                {
                    try
                    {
                        var btn = ParseButton(btnName);
                        switch (action)
                        {
                            case "tap":
                                TapButton(btn);
                                details.Add($"{btnName}:tap");
                                break;
                            case "press":
                            case "hold":
                                PressButton(btn);
                                details.Add($"{btnName}:press");
                                break;
                            case "release":
                                ReleaseButton(btn);
                                details.Add($"{btnName}:release");
                                break;
                            default:
                                details.Add($"{btnName}:unknown_action({action})");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        details.Add($"{btnName}:error({ex.Message})");
                    }
                }
            }

            if (axes != null && axes.Count > 0)
            {
                SetAxes(axes);
                foreach (var kvp in axes)
                    details.Add($"axis:{kvp.Key}={kvp.Value:F2}");
            }

            return details;
        }
    }
}
#endif
