#if UNITY_INPUT_SYSTEM
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Keyboard input simulation via InputSystem.QueueStateEvent on the physical device.
    /// Follows the same pattern as MouseDeviceTools.ClickMouse — sends state events
    /// directly to Keyboard.current, NOT a virtual device.
    ///
    /// 🎯 TRACKED STATE: Tracks which keys were pressed via this tool.
    /// Physical keyboard keys are NEVER released — only AI-pressed keys are managed.
    /// Each HoldKey replaces ALL tracked keys with the new set (full state snapshot).
    /// </summary>
    public static class KeyboardTools
    {
        // Track keys pressed by the bridge so ReleaseKey only touches these,
        // never interfering with the physical keyboard.
        private static readonly System.Collections.Generic.HashSet<Key> _trackedKeys = new();

        /// <summary>
        /// Tap a key (press + immediate release). Use for one-shot actions: jump, shoot, interact.
        /// </summary>
        public static void TapKey(Key key)
        {
            var kbd = Keyboard.current;
            if (kbd == null)
                throw new InvalidOperationException("No physical keyboard device found (Keyboard.current is null)");

            // Build a state snapshot with just the tracked keys + this new key
            var state = BuildTrackedState();
            state.Press(key);
            InputSystem.QueueStateEvent(kbd, state);
            InputSystem.Update();

            // Release: restore tracked state (without the tapped key)
            var releaseState = BuildTrackedState();
            InputSystem.QueueStateEvent(kbd, releaseState);
            InputSystem.Update();
        }

        /// <summary>
        /// Hold a key down. Replaces ALL tracked keys with just this key.
        /// Any key not included (including previously held AI keys) is released.
        /// Use for continuous movement (WASD).
        /// </summary>
        public static void HoldKey(Key key)
        {
            var kbd = Keyboard.current;
            if (kbd == null)
                throw new InvalidOperationException("No physical keyboard device found (Keyboard.current is null)");

            _trackedKeys.Clear();
            _trackedKeys.Add(key);

            var state = new KeyboardState();
            state.Press(key);
            InputSystem.QueueStateEvent(kbd, state);
            InputSystem.Update();
        }

        /// <summary>
        /// Hold multiple keys simultaneously (e.g. W + LeftShift for sprinting forward).
        /// Replaces ALL tracked keys. Previous AI-held keys are released.
        /// </summary>
        public static void HoldKeys(Key[] keys)
        {
            var kbd = Keyboard.current;
            if (kbd == null)
                throw new InvalidOperationException("No physical keyboard device found (Keyboard.current is null)");

            _trackedKeys.Clear();
            var state = new KeyboardState();
            foreach (var k in keys)
            {
                _trackedKeys.Add(k);
                state.Press(k);
            }
            InputSystem.QueueStateEvent(kbd, state);
            InputSystem.Update();
        }

        /// <summary>
        /// Release a specific tracked key while preserving other tracked keys.
        /// Physical keyboard keys are NEVER affected.
        /// </summary>
        public static void ReleaseKey(Key key)
        {
            _trackedKeys.Remove(key);
            ApplyTrackedState();
        }

        /// <summary>
        /// Release all tracked keys. Physical keyboard keys are NOT affected.
        /// </summary>
        public static void ReleaseAllKeys()
        {
            _trackedKeys.Clear();
            ApplyTrackedState();
        }

        /// <summary>
        /// Build a KeyboardState with only the currently tracked keys pressed.
        /// All other keys get 0 (released).
        /// </summary>
        private static KeyboardState BuildTrackedState()
        {
            var state = new KeyboardState();
            foreach (var k in _trackedKeys)
                state.Press(k);
            return state;
        }

        /// <summary>
        /// Apply the current tracked state to the physical keyboard device.
        /// </summary>
        private static void ApplyTrackedState()
        {
            var kbd = Keyboard.current;
            if (kbd == null) return;

            InputSystem.QueueStateEvent(kbd, BuildTrackedState());
            InputSystem.Update();
        }

        /// <summary>
        /// Parse a key name string to InputSystem.Key enum.
        /// Case-insensitive. Examples: "w", "space", "enter", "upArrow", "leftShift", "f1".
        /// </summary>
        public static Key ParseKey(string keyName)
        {
            if (Enum.TryParse(keyName, true, out Key key))
                return key;
            throw new System.ArgumentException($"Unknown key name: '{keyName}'. " +
                "Use standard Unity Input System key names (e.g., 'w', 'space', 'enter', 'upArrow', 'leftShift').");
        }
    }
}
#endif
