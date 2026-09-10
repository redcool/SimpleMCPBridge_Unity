#if UNITY_INPUT_SYSTEM
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge
{
    /// <summary>
    /// Keyboard input simulation via InputSystem.QueueStateEvent on the physical device.
    /// Follows the same pattern as MouseDeviceTools.ClickMouse — sends state events
    /// directly to Keyboard.current, NOT a virtual device.
    ///
    /// 🎯 TRACKED STATE: Tracks which keys were pressed via this tool.
    /// Physical keyboard keys are NEVER released by building a zeroed snapshot —
    /// each applied state copies the CURRENT device state (physical keys stay held),
    /// then only keys the bridge previously injected and is now retracting are
    /// unpressed (P7 zero-pop fix). HoldKey replaces ALL tracked keys with a new set.
    /// </summary>
    public static class KeyboardTools
    {
        // Track keys pressed by the bridge so ReleaseKey only touches these,
        // never interfering with the physical keyboard.
        private static readonly System.Collections.Generic.HashSet<Key> _trackedKeys = new();

        /// <summary>Get the set of keys currently tracked (held by the bridge via HoldKey/HoldKeys).</summary>
        public static System.Collections.Generic.IReadOnlyCollection<Key> TrackedKeys => _trackedKeys;

        /// <summary>
        /// Tap a key (press + immediate release). Use for one-shot actions: jump, shoot, interact.
        /// </summary>
        public static void TapKey(Key key)
        {
            var kbd = Keyboard.current;
            if (kbd == null)
                throw new InvalidOperationException("No physical keyboard device found (Keyboard.current is null)");

            // Capture the physical baseline ONCE (keys the user actually holds +
            // currently tracked AI keys). Both the press and the release reuse this
            // same snapshot so the tapped key never leaks into the release state
            // and physical keys are never popped (P7 zero-pop fix).
            var baseState = BuildTrackedState(null);
            var downState = baseState; // struct copy
            downState.Press(key);
            InputSystem.QueueStateEvent(kbd, downState);
            InputSystem.Update();

            // Release: restore the baseline (tapped key not included → released)
            InputSystem.QueueStateEvent(kbd, baseState);
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

            // Retract whatever the bridge previously held but is no longer tracked,
            // so replacing the hold set never leaves stale AI keys pressed — and
            // never touches physical keys (P7 zero-pop fix).
            var retract = new System.Collections.Generic.HashSet<Key>(_trackedKeys);
            retract.Remove(key);
            _trackedKeys.Clear();
            _trackedKeys.Add(key);
            ApplyTrackedState(retract);
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

            // Retract any previously held AI keys that are not in the new set
            // (P7 zero-pop fix — physical keys are preserved via the copy).
            var retract = new System.Collections.Generic.HashSet<Key>(_trackedKeys);
            foreach (var k in keys)
                retract.Remove(k);
            _trackedKeys.Clear();
            foreach (var k in keys)
                _trackedKeys.Add(k);
            ApplyTrackedState(retract);
        }

        /// <summary>
        /// Release a specific tracked key while preserving other tracked keys.
        /// Physical keyboard keys are NEVER affected.
        /// </summary>
        public static void ReleaseKey(Key key)
        {
            if (!_trackedKeys.Remove(key)) return; // not held by bridge — nothing to do
            ApplyTrackedState(new[] { key });
        }

        /// <summary>
        /// Release all tracked keys. Physical keyboard keys are NOT affected.
        /// </summary>
        public static void ReleaseAllKeys()
        {
            if (_trackedKeys.Count == 0) return;
            var retract = new System.Collections.Generic.HashSet<Key>(_trackedKeys);
            _trackedKeys.Clear();
            ApplyTrackedState(retract);
        }

        /// <summary>
        /// Build a KeyboardState from the CURRENT device state (physical keys stay
        /// held — P7 zero-pop fix) with the given keys retracted (unpressed) and all
        /// currently tracked keys forced pressed.
        /// </summary>
        private static KeyboardState BuildTrackedState(System.Collections.Generic.ICollection<Key> retract)
        {
            var state = new KeyboardState();
            var kbd = Keyboard.current;
            if (kbd == null) return state;

            // Copy the current device state: every key the user physically holds
            // (plus still-tracked injected keys) stays pressed.
            foreach (var ctrl in kbd.allControls)
            {
                if (ctrl is KeyControl kc && kc.isPressed && (retract == null || !retract.Contains(kc.keyCode)))
                    state.Press(kc.keyCode);
            }
            // Ensure all tracked keys are pressed (covers keys queued this frame that
            // the device state has not yet reflected).
            foreach (var k in _trackedKeys)
                state.Press(k);
            return state;
        }

        /// <summary>
        /// Apply the current tracked state to the physical keyboard device.
        /// </summary>
        private static void ApplyTrackedState(System.Collections.Generic.ICollection<Key> retract = null)
        {
            var kbd = Keyboard.current;
            if (kbd == null) return;

            InputSystem.QueueStateEvent(kbd, BuildTrackedState(retract));
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
