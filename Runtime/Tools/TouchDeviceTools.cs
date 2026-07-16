#if UNITY_INPUT_SYSTEM
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Touch simulation via Input System virtual Touchscreen.
    ///
    /// Provides atomic touch operations (Began / Moved / Ended) and
    /// a async swipe helper that internally spaces events across frames.
    ///
    /// For swipe: starts a background update loop (EditorApplication.update
    /// in Editor, TouchUpdateDriver MonoBehaviour in Runtime) that
    /// dispatches Moved events at the correct intervals.
    /// </summary>
    public static class TouchDeviceTools
    {
        // ── Virtual Touchscreen ──

        private static Touchscreen _virtualTouchscreen;

        public static Touchscreen VirtualTouchscreen
        {
            get
            {
                if (_virtualTouchscreen == null || !_virtualTouchscreen.added)
                {
                    if (_virtualTouchscreen != null)
                        _virtualTouchscreen = null;
                    _virtualTouchscreen = InputSystem.AddDevice<Touchscreen>("VirtualAgentTouch");
                }
                return _virtualTouchscreen;
            }
        }

        // ── Pixel coordinate helper ──

        /// <summary>Convert normalized (0-1) coordinates to screen pixel position.</summary>
        public static Vector2 ScreenPoint(float nx, float ny)
        {
            return new Vector2(nx * Screen.width, ny * Screen.height);
        }

        // ── Position tracking for delta calculation ──

        private static readonly Dictionary<int, Vector2> s_lastPositions = new();

        // ── Atomic touch operations ──

        /// <summary>Send touch began at a screen pixel position.
        /// Events are queued via QueueStateEvent — they'll be processed
        /// on the next natural Input System update (no forced Update() call,
        /// which can deadlock in Play Mode).</summary>
        public static void TouchBegan(int fingerId, Vector2 position)
        {
            var ts = VirtualTouchscreen;
            s_lastPositions[fingerId] = position;
            InputSystem.QueueStateEvent(ts, new TouchState
            {
                touchId = fingerId,
                phase = UnityEngine.InputSystem.TouchPhase.Began,
                position = position,
                delta = Vector2.zero,
            });
        }

        /// <summary>Send touch moved to a new screen pixel position.</summary>
        public static void TouchMoved(int fingerId, Vector2 position)
        {
            var ts = VirtualTouchscreen;
            var lastPos = s_lastPositions.TryGetValue(fingerId, out var lp) ? lp : position;
            s_lastPositions[fingerId] = position;
            InputSystem.QueueStateEvent(ts, new TouchState
            {
                touchId = fingerId,
                phase = UnityEngine.InputSystem.TouchPhase.Moved,
                position = position,
                delta = position - lastPos,
            });
        }

        /// <summary>Send touch ended for the given finger.</summary>
        public static void TouchEnded(int fingerId)
        {
            var ts = VirtualTouchscreen;
            var lastPos = s_lastPositions.TryGetValue(fingerId, out var lp) ? lp : Vector2.zero;
            s_lastPositions.Remove(fingerId);
            InputSystem.QueueStateEvent(ts, new TouchState
            {
                touchId = fingerId,
                phase = UnityEngine.InputSystem.TouchPhase.Ended,
                position = lastPos,
                delta = Vector2.zero,
            });
        }

        /// <summary>Immediate tap: Began + Ended in the same frame.</summary>
        public static void Tap(float nx, float ny)
        {
            var pos = ScreenPoint(nx, ny);
            TouchBegan(0, pos);
            TouchEnded(0);
        }

        // ── Swipe (async, multi-frame) ──

        private class SwipeState
        {
            public int FingerId;
            public Vector2[] Path;       // interpolated screen positions
            public float StepInterval;   // seconds between steps
            public int CurrentIndex;     // next step to send (= 0 means Began already sent)
            public double NextEventTime; // realtimeSinceStartup for next event
            public bool Done;
        }

        private static readonly List<SwipeState> s_activeSwipes = new();
        private static bool s_updateHooked;

        /// <summary>
        /// Start an async swipe gesture.
        /// Events (Began → Moved ×N → Ended) are dispatched across frames.
        /// The swipe duration = path.Length * stepInterval seconds.
        /// </summary>
        public static double StartSwipe(int fingerId, Vector2[] screenPath, float stepInterval)
        {
            var swipe = new SwipeState
            {
                FingerId = fingerId,
                Path = screenPath,
                StepInterval = Mathf.Max(stepInterval, 0.016f), // minimum ~1 frame
                CurrentIndex = 0,
                Done = false,
            };

            // Send began immediately
            TouchBegan(fingerId, screenPath[0]);
            swipe.CurrentIndex = 1;
            swipe.NextEventTime = Time.realtimeSinceStartupAsDouble + swipe.StepInterval;

            lock (s_activeSwipes)
            {
                s_activeSwipes.Add(swipe);
            }

            EnsureUpdateHook();

            // Return estimated total duration
            return screenPath.Length * stepInterval;
        }

        /// <summary>Check if all swipes have completed.</summary>
        public static bool IsSwiping => s_activeSwipes.Count > 0;

        /// <summary>Cancel all active swipes (send Ended immediately).</summary>
        public static void CancelAllSwipes()
        {
            lock (s_activeSwipes)
            {
                foreach (var s in s_activeSwipes)
                {
                    if (!s.Done)
                    {
                        TouchEnded(s.FingerId);
                        s.Done = true;
                    }
                }
                s_activeSwipes.Clear();
            }
        }

        // ── Per-frame processing ──

        /// <summary>
        /// Called every frame (by EditorApplication.update in Editor,
        /// or by TouchUpdateDriver in Runtime) to advance active swipes.
        /// </summary>
        public static void ProcessPendingTouches()
        {
            lock (s_activeSwipes)
            {
                double now = Time.realtimeSinceStartupAsDouble;

                for (int i = s_activeSwipes.Count - 1; i >= 0; i--)
                {
                    var s = s_activeSwipes[i];
                    if (s.Done)
                    {
                        s_activeSwipes.RemoveAt(i);
                        continue;
                    }

                    if (now < s.NextEventTime)
                        continue; // not yet time for the next event

                    // Send intermediate Moved events
                    while (s.CurrentIndex < s.Path.Length - 1 && now >= s.NextEventTime)
                    {
                        TouchMoved(s.FingerId, s.Path[s.CurrentIndex]);
                        s.CurrentIndex++;
                        s.NextEventTime += s.StepInterval;

                        // If we caught up, break to avoid sending too many at once
                        if (now < s.NextEventTime)
                            break;
                    }

                    // If we've reached the last position, send Ended
                    if (s.CurrentIndex >= s.Path.Length - 1)
                    {
                        TouchMoved(s.FingerId, s.Path[s.Path.Length - 1]);
                        TouchEnded(s.FingerId);
                        s.Done = true;
                        s_activeSwipes.RemoveAt(i);
                    }
                }
            }
        }

        // ── Update hook management ──

        private static void EnsureUpdateHook()
        {
            if (s_updateHooked) return;
            s_updateHooked = true;

#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= ProcessPendingTouches;
            UnityEditor.EditorApplication.update += ProcessPendingTouches;
#else
            // Runtime: spawn a DontDestroyOnLoad driver
            var go = new GameObject("TouchUpdateDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<TouchUpdateDriver>();
#endif
        }

        // ── Cleanup on domain reload ──

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitEditor()
        {
            // Re-hook after domain reload
            UnityEditor.EditorApplication.update -= ProcessPendingTouches;
            UnityEditor.EditorApplication.update += ProcessPendingTouches;
            s_updateHooked = true;
        }
#endif
    }

    // ── Runtime update driver ──

    /// <summary>
    /// Minimal MonoBehaviour that drives touch event processing in Runtime.
    /// Auto-created by TouchDeviceTools when a swipe starts.
    /// </summary>
    internal class TouchUpdateDriver : MonoBehaviour
    {
        private void Awake()
        {
            hideFlags = HideFlags.HideAndDontSave;
        }

        private void Update()
        {
            TouchDeviceTools.ProcessPendingTouches();
        }

        private void OnDestroy()
        {
            TouchDeviceTools.CancelAllSwipes();
        }
    }
}
#endif
