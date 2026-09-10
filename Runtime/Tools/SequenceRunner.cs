using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;
#if UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Executes multi-step action sequences on the Unity side, driven by
    /// EditorApplication.update (Editor) or a hidden MonoBehaviour (Runtime).
    /// Each step runs on its own tick, avoiding main-thread blocking.
    ///
    /// Supported step types:
    ///   key, mouse_click, mouse_move, gamepad, click_screen, wait (existing)
    ///   if_property, try, repeat (conditional / error recovery / loop)
    /// </summary>
    public static class SequenceRunner
    {
        private static readonly List<ActiveSequence> _sequences = new();
        private static bool _updateHooked;
        private static int _seqCounter;

        /// <summary>Max nesting depth for if_property/try/repeat to prevent stack overflow.</summary>
        private const int MaxNestingDepth = 10;
        private const int DefaultMaxRepeatCount = 100;

        // ── State classes for control flow steps ──

        /// <summary>Tracks an active try/catch block.</summary>
        public class TryContext
        {
            /// <summary>Index after the last sub-step of this try block (exclusive).</summary>
            public int EndIndex;
            /// <summary>Sub-steps to execute when an error is caught (can be null/empty).</summary>
            public List<Dictionary<string, object>> CatchSteps;
        }

        /// <summary>Tracks an active repeat loop.</summary>
        public class RepeatState
        {
            /// <summary>Exact iteration count (null if using condition).</summary>
            public int? Count;
            /// <summary>Current iteration (0-based).</summary>
            public int CurrentIteration;
            /// <summary>Safety limit when using condition (or default).</summary>
            public int MaxCount;
            /// <summary>Index after the last sub-step of this repeat block (exclusive).</summary>
            public int EndIndex;
            /// <summary>Original sub-steps to re-insert on each iteration.</summary>
            public List<Dictionary<string, object>> Steps;
            /// <summary>Optional stop condition — repeat until condition is true.</summary>
            public PropertyCondition Condition;
        }

        /// <summary>Describes a property-based condition (used by if_property and repeat).</summary>
        public class PropertyCondition
        {
            public string Path;
            public int? InstanceId;
            public string Component;
            public string Property;
            public string Operator;
            public string Value;
            public double FloatValue;
            /// <summary>Cached GameObject to avoid repeated FindObjectsByType calls.</summary>
            public GameObject CachedGo;
        }

        public class ActiveSequence
        {
            public string Id;
            public List<Dictionary<string, object>> Steps;
            public int CurrentStep;
            public double StepStartTime;
            public double StartTime;
            public double Timeout;
            public bool Completed;
            public string Error;
            public bool Running;
            public double CompletedAt; // P7 leak fix: reliable cleanup stamp for ALL completion paths
            public List<string> StepLog;

            // ── Control-flow state ──
            public List<TryContext> TryStack = new();
            public List<RepeatState> RepeatStack = new();
        }

        /// <summary>
        /// Mark a sequence finished (completed or failed). Records CompletedAt so the
        /// cleanup pass can reap it — the old code relied on callers adding the id to
        /// a `done` local list, and #else branches (no Input System builds) skipped
        /// that, leaking every such sequence forever (P7).
        /// </summary>
        private static void CompleteSequence(ActiveSequence seq, string error, double now)
        {
            seq.Completed = true;
            seq.Running = false;
            seq.CompletedAt = now;
            if (error != null) seq.Error = error;
        }

        /// <summary>
        /// Start executing a sequence. Returns immediately with a sequence ID.
        /// </summary>
        public static string StartSequence(List<Dictionary<string, object>> steps, float timeout)
        {
            var id = $"seq_{++_seqCounter}";
            var seq = new ActiveSequence
            {
                Id = id,
                Steps = steps,
                CurrentStep = 0,
                StepStartTime = Time.realtimeSinceStartup,
                StartTime = Time.realtimeSinceStartup,
                Timeout = timeout,
                Completed = false,
                Running = true,
                StepLog = new List<string>(),
            };
            _sequences.Add(seq);
            EnsureUpdateHook();
            return id;
        }

        public static ActiveSequence GetSequence(string id)
        {
            return _sequences.FirstOrDefault(s => s.Id == id);
        }

        // ── Update hook ──

        private static void EnsureUpdateHook()
        {
            if (_updateHooked) return;
            _updateHooked = true;

#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= ProcessSequences;
            UnityEditor.EditorApplication.update += ProcessSequences;
#else
            var go = new GameObject("SequenceDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<SequenceDriver>();
#endif
        }

        internal static void ProcessSequences()
        {
            if (_sequences.Count == 0) return;

            var now = Time.realtimeSinceStartup;
            var done = new List<string>();

            foreach (var seq in _sequences)
            {
                if (!seq.Running) continue;

                // Check timeout
                if (now - seq.StartTime > seq.Timeout)
                {
                    seq.StepLog.Add($"TIMEOUT at step {seq.CurrentStep}/{seq.Steps.Count}");
                    CompleteSequence(seq, "timeout", now);
                    done.Add(seq.Id);
                    continue;
                }

                // Execute steps until we hit a wait
                bool waiting = false;
                while (seq.CurrentStep < seq.Steps.Count && !waiting)
                {
                    var step = seq.Steps[seq.CurrentStep];
                    var type = GetString(step, "type", "");
                    var log = $"Step {seq.CurrentStep + 1}/{seq.Steps.Count} [{type}]";
                    int stepBeforeExec = seq.CurrentStep;

                    // Wrap step execution to catch exceptions for try/catch
                    try
                    {
                        switch (type)
                        {
                            case "wait":
                            {
                                var dur = GetFloat(step, "duration", 0.5f);
                                if (seq.StepStartTime == 0)
                                {
                                    seq.StepStartTime = now; // first time seeing this wait
                                }

                                var elapsed = now - seq.StepStartTime;
                                if (elapsed >= dur)
                                {
                                    seq.StepStartTime = 0;
                                    seq.CurrentStep++;
                                    seq.StepLog.Add($"{log} done ({dur:F1}s)");
                                }
                                else
                                {
                                    seq.StepLog.Add($"{log} waiting ({dur - elapsed:F1}s remain)");
                                    waiting = true; // pause execution until next tick
                                }
                                break;
                            }

                            case "key":
                            {
#if UNITY_INPUT_SYSTEM
                                var keyName = GetRequiredString(step, "name");
                                var action = GetString(step, "action", "tap");
                                var key = KeyboardTools.ParseKey(keyName);
                                switch (action)
                                {
                                    case "tap": KeyboardTools.TapKey(key); break;
                                    case "hold": KeyboardTools.HoldKey(key); break;
                                    case "release": KeyboardTools.ReleaseKey(key); break;
                                }
                                seq.StepLog.Add($"{log} key={keyName} action={action}");
#else
                                seq.StepLog.Add($"{log} SKIPPED (no Input System)");
                                CompleteSequence(seq, $"Step {seq.CurrentStep + 1}: key requires Input System", now);
#endif
                                seq.CurrentStep++;
                                break;
                            }

                            case "mouse_click":
                            {
#if UNITY_INPUT_SYSTEM
                                var mx = GetFloat(step, "x", 0.5f);
                                var my = GetFloat(step, "y", 0.5f);
                                var btn = (int)GetFloat(step, "button", 0);
                                MouseDeviceTools.ClickMouse(new Vector2(mx, my), btn);
                                seq.StepLog.Add($"{log} ({mx:F2},{my:F2}) btn={btn}");
#else
                                seq.StepLog.Add($"{log} ERROR: no Input System");
                                CompleteSequence(seq, "mouse_click requires Input System (not available in this build)", now);
#endif
                                seq.CurrentStep++;
                                break;
                            }

                            case "mouse_move":
                            {
#if UNITY_INPUT_SYSTEM
                                var dx = GetFloat(step, "dx", 0);
                                var dy = GetFloat(step, "dy", 0);
                                MouseDeviceTools.MoveMouse(new Vector2(dx, dy));
                                seq.StepLog.Add($"{log} delta=({dx:F0},{dy:F0})");
#else
                                seq.StepLog.Add($"{log} ERROR: no Input System");
                                CompleteSequence(seq, "mouse_move requires Input System (not available in this build)", now);
#endif
                                seq.CurrentStep++;
                                break;
                            }

                            case "gamepad":
                            {
#if UNITY_INPUT_SYSTEM
                                var gAction = GetRequiredString(step, "action");
                                var config = step; // pass the whole step as config
                                // Reuse GamepadTools directly
                                switch (gAction)
                                {
                                    case "button":
                                    {
                                        var btn = GetRequiredString(config, "button");
                                        var press = GetString(config, "press", "tap");
                                        var gb = GamepadTools.ParseButton(btn);
                                        switch (press)
                                        {
                                            case "tap": GamepadTools.TapButton(gb); break;
                                            case "hold":
                                            case "press": GamepadTools.PressButton(gb); break;
                                            case "release": GamepadTools.ReleaseButton(gb); break;
                                        }
                                        seq.StepLog.Add($"{log} button={btn} press={press}");
                                        break;
                                    }
                                    case "axis":
                                    {
                                        var axis = GetRequiredString(config, "axis");
                                        var val = GetRequiredFloat(config, "value");
                                        GamepadTools.SetAxis(axis, val);
                                        seq.StepLog.Add($"{log} axis={axis}={val:F2}");
                                        break;
                                    }
                                    case "reset":
                                        GamepadTools.ResetAll();
                                        seq.StepLog.Add($"{log} reset");
                                        break;
                                }
#else
                                seq.StepLog.Add($"{log} SKIPPED (no Input System)");
#endif
                                seq.CurrentStep++;
                                break;
                            }

                            case "click_screen":
                            {
                                var cx = GetFloat(step, "x", 0.5f);
                                var cy = GetFloat(step, "y", 0.5f);
                                Handlers.ScreenHandler.ProcessClick(cx, cy, 0);
                                seq.StepLog.Add($"{log} ({cx:F2},{cy:F2})");
                                seq.CurrentStep++;
                                break;
                            }

                            // ══════════════════════════════════════════════
                            //  if_property — conditional branch
                            // ══════════════════════════════════════════════
                            case "if_property":
                            {
                                var path = GetString(step, "path", "");
                                var component = GetString(step, "component", "");
                                var property = GetString(step, "property", "");
                                var op = GetString(step, "operator", "==");
                                var value = GetString(step, "value", "0");
                                var thenRaw = GetString(step, "then", "[]");
                                var elseRaw = GetString(step, "else", "[]");

                                // Parse sub-steps
                                var thenSteps = ParseJsonArrayOfObjects(thenRaw);
                                var elseSteps = ParseJsonArrayOfObjects(elseRaw);

                                // Check nesting depth
                                if (!CheckNestingDepth(seq, log)) break;

                                // Resolve object: path or instanceId
                                int? instanceId = null;
                                if (string.IsNullOrEmpty(path))
                                {
                                    if (step.TryGetValue("instanceId", out var idObj))
                                        instanceId = Convert.ToInt32(idObj, CultureInfo.InvariantCulture);
                                }

                                // Evaluate condition
                                var cond = new PropertyCondition
                                {
                                    Path = path,
                                    InstanceId = instanceId,
                                    Component = component,
                                    Property = property,
                                    Operator = op,
                                    Value = value,
                                };
                                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out cond.FloatValue);

                                bool condResult = EvaluatePropertyCondition(cond);
                                seq.StepLog.Add($"{log} if_property: {GetConditionDesc(cond)} = {condResult}");

                                // Insert appropriate branch
                                var stepsToInsert = condResult ? thenSteps : elseSteps;
                                if (stepsToInsert != null && stepsToInsert.Count > 0)
                                {
                                    seq.Steps.InsertRange(seq.CurrentStep + 1, stepsToInsert);
                                    AdjustContextIndices(seq, seq.CurrentStep + 1, stepsToInsert.Count);
                                }

                                seq.CurrentStep++;
                                break;
                            }

                            // ══════════════════════════════════════════════
                            //  try — error recovery
                            // ══════════════════════════════════════════════
                            case "try":
                            {
                                var stepsRaw = GetString(step, "steps", "[]");
                                var catchRaw = GetString(step, "catch", "[]");

                                var trySteps = ParseJsonArrayOfObjects(stepsRaw);
                                var catchSteps = ParseJsonArrayOfObjects(catchRaw);

                                if (!CheckNestingDepth(seq, log)) break;

                                if (trySteps.Count == 0)
                                {
                                    seq.StepLog.Add($"{log} try has no steps, skipping");
                                    seq.CurrentStep++;
                                    break;
                                }

                                // Insert try sub-steps after this step
                                seq.Steps.InsertRange(seq.CurrentStep + 1, trySteps);
                                AdjustContextIndices(seq, seq.CurrentStep + 1, trySteps.Count);

                                // Calculate EndIndex: after all inserted sub-steps
                                int endIndex = seq.CurrentStep + 1 + trySteps.Count;

                                // Push try context
                                seq.TryStack.Add(new TryContext
                                {
                                    EndIndex = endIndex,
                                    CatchSteps = catchSteps.Count > 0 ? catchSteps : null,
                                });

                                seq.StepLog.Add($"{log} try: {trySteps.Count} sub-steps, {(catchSteps.Count > 0 ? catchSteps.Count + " catch steps" : "no catch")}");
                                seq.CurrentStep++;
                                break;
                            }

                            // ══════════════════════════════════════════════
                            //  repeat — loop
                            // ══════════════════════════════════════════════
                            case "repeat":
                            {
                                var stepsRaw = GetString(step, "steps", "[]");
                                var repeatSteps = ParseJsonArrayOfObjects(stepsRaw);

                                if (!CheckNestingDepth(seq, log)) break;

                                if (repeatSteps.Count == 0)
                                {
                                    seq.StepLog.Add($"{log} repeat has no steps, skipping");
                                    seq.CurrentStep++;
                                    break;
                                }

                                // Parse count / condition / maxCount
                                int? count = null;
                                if (step.TryGetValue("count", out var countObj) && countObj != null)
                                    count = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);

                                int maxCount = DefaultMaxRepeatCount;
                                if (step.TryGetValue("maxCount", out var maxObj) && maxObj != null)
                                    maxCount = Convert.ToInt32(maxObj, CultureInfo.InvariantCulture);

                                PropertyCondition condition = null;
                                var condRaw = GetString(step, "condition", "");
                                if (!string.IsNullOrEmpty(condRaw))
                                {
                                    var condDict = ParseJsonObject(condRaw);
                                    condition = new PropertyCondition
                                    {
                                        Path = GetString(condDict, "path", ""),
                                        Component = GetString(condDict, "component", ""),
                                        Property = GetString(condDict, "property", ""),
                                        Operator = GetString(condDict, "operator", "=="),
                                        Value = GetString(condDict, "value", "0"),
                                    };
                                    if (string.IsNullOrEmpty(condition.Path) && condDict.TryGetValue("instanceId", out var cidObj))
                                        condition.InstanceId = Convert.ToInt32(cidObj, CultureInfo.InvariantCulture);
                                    double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out condition.FloatValue);

                                    // Default maxCount if not specified when using condition
                                    if (!step.ContainsKey("maxCount") && count == null)
                                        maxCount = DefaultMaxRepeatCount;
                                }

                                // Insert repeat sub-steps
                                seq.Steps.InsertRange(seq.CurrentStep + 1, repeatSteps);
                                AdjustContextIndices(seq, seq.CurrentStep + 1, repeatSteps.Count);

                                int repEndIndex = seq.CurrentStep + 1 + repeatSteps.Count;

                                seq.RepeatStack.Add(new RepeatState
                                {
                                    Count = count,
                                    CurrentIteration = 0,
                                    MaxCount = maxCount,
                                    EndIndex = repEndIndex,
                                    Steps = repeatSteps,
                                    Condition = condition,
                                });

                                var iterInfo = count.HasValue
                                    ? $"count={count.Value}"
                                    : (condition != null ? $"condition until {condition.Property} {condition.Operator} {condition.Value}" : "infinite");
                                seq.StepLog.Add($"{log} repeat: {repeatSteps.Count} sub-steps, {iterInfo}, maxCount={maxCount}");
                                seq.CurrentStep++;
                                break;
                            }

                            default:
                                seq.StepLog.Add($"{log} UNKNOWN type '{type}'");
                                seq.Error = $"Unknown step type: '{type}'";
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        seq.Error = $"{type} threw: {ex.Message}";
                        seq.StepLog.Add($"{log} EXCEPTION: {ex.Message}");
                    }

                    // ── Post-step processing ──

                    // 1. Error handling — try to catch with active try contexts
                    if (seq.Error != null)
                    {
                        if (!HandleTryError(seq, stepBeforeExec))
                        {
                            // Not caught — terminate sequence
                            CompleteSequence(seq, seq.Error, now);
                            done.Add(seq.Id);
                            break; // Exit while loop to prevent spin on errored step
                        }
                        // If caught, error is cleared and we continue
                    }

                    // 2. Pop completed try contexts (sub-steps all finished without error)
                    while (seq.TryStack.Count > 0 && seq.CurrentStep >= seq.TryStack[seq.TryStack.Count - 1].EndIndex)
                    {
                        var ctx = seq.TryStack[seq.TryStack.Count - 1];
                        seq.TryStack.RemoveAt(seq.TryStack.Count - 1);
                        seq.StepLog.Add($"TRY completed (endIndex={ctx.EndIndex})");
                    }

                    // 3. Check repeat loop-back
                    int safety = 0;
                    while (seq.RepeatStack.Count > 0 && seq.CurrentStep >= seq.RepeatStack[seq.RepeatStack.Count - 1].EndIndex)
                    {
                        if (++safety > 10) // Prevent runaway loop
                        {
                            seq.Error = "REPEAT loop-back exceeded safety limit";
                            break;
                        }

                        var rep = seq.RepeatStack[seq.RepeatStack.Count - 1];
                        rep.CurrentIteration++;

                        bool shouldRepeat = false;
                        if (rep.Count.HasValue)
                        {
                            shouldRepeat = rep.CurrentIteration < rep.Count.Value;
                        }
                        else if (rep.Condition != null)
                        {
                            bool condResult = EvaluatePropertyCondition(rep.Condition);
                            shouldRepeat = !condResult && rep.CurrentIteration < rep.MaxCount;
                            seq.StepLog.Add($"REPEAT condition: {rep.Condition.Property} {rep.Condition.Operator} {rep.Condition.Value} = {condResult}, iter {rep.CurrentIteration}/{rep.MaxCount}");
                        }
                        else
                        {
                            shouldRepeat = rep.CurrentIteration < rep.MaxCount;
                        }

                        if (shouldRepeat)
                        {
                            // Deep-copy the original sub-steps and re-insert at EndIndex
                            var stepsCopy = rep.Steps.Select(s => new Dictionary<string, object>(s)).ToList();
                            seq.Steps.InsertRange(rep.EndIndex, stepsCopy);
                            AdjustContextIndices(seq, rep.EndIndex, stepsCopy.Count);
                            seq.CurrentStep = rep.EndIndex;
                            rep.EndIndex += stepsCopy.Count;
                            seq.StepStartTime = 0;
                            seq.StepLog.Add($"REPEAT iteration {rep.CurrentIteration + 1}");
                        }
                        else
                        {
                            seq.RepeatStack.RemoveAt(seq.RepeatStack.Count - 1);
                            var reason = rep.Count.HasValue
                                ? $"{rep.Count.Value} iterations"
                                : (rep.Condition != null
                                    ? $"condition met or max ({rep.MaxCount})"
                                    : $"{rep.CurrentIteration} iterations");
                            seq.StepLog.Add($"REPEAT done ({reason})");
                        }
                    }

                    if (seq.Error != null && !HandleTryError(seq, stepBeforeExec))
                    {
                        CompleteSequence(seq, seq.Error, now);
                        done.Add(seq.Id);
                        break;
                    }
                }

                // All steps done
                if (seq.CurrentStep >= seq.Steps.Count && !waiting)
                {
                    var totalTime = now - seq.StartTime;
                    seq.StepLog.Add($"SEQUENCE DONE in {totalTime:F2}s");
                    CompleteSequence(seq, null, now);
                    done.Add(seq.Id);
                }
            }

            // Cleanup completed sequences after 5 seconds.
            // P7 leak fix: reap by CompletedAt (stamped on EVERY completion path),
            // not by membership in `done` — #else branches used to complete a
            // sequence without adding its id here, leaking it forever.
            var reapAt = now - 5.0;
            for (int i = _sequences.Count - 1; i >= 0; i--)
            {
                var s = _sequences[i];
                if (s.Completed && !s.Running && s.CompletedAt <= reapAt && s.CompletedAt > 0)
                {
                    _sequences.RemoveAt(i);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Condition evaluation (mirrors EvaluateCondition in GameHandler)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Evaluate a property condition: find GameObject → get component → read property → compare.
        /// </summary>
        private static bool EvaluatePropertyCondition(PropertyCondition cond)
        {
            try
            {
                GameObject go = cond.CachedGo;

                // Unity null check (object may have been destroyed)
                if (go == null)
                {
                    cond.CachedGo = null; // clear stale reference
                }

                if (go == null)
                {
                    // Find by path (fast)
                    if (!string.IsNullOrEmpty(cond.Path))
                    {
                        go = GameObject.Find(cond.Path);
                    }
                    // Find by instanceId (slow — only on cache miss)
                    else if (cond.InstanceId.HasValue)
                    {
                        var allGos = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                        foreach (var g in allGos)
                        {
                            if (g.GetInstanceID() == cond.InstanceId.Value)
                            {
                                go = g;
                                break;
                            }
                        }
                    }
                    cond.CachedGo = go; // cache for next time
                }

                if (go == null)
                {
                    return false;
                }

                var comp = go.GetComponent(cond.Component);
                if (comp == null)
                {
                    return false;
                }

                // Try property first, then field
                string rawValue = null;
                var prop = comp.GetType().GetProperty(cond.Property);
                if (prop != null)
                {
                    rawValue = prop.GetValue(comp)?.ToString();
                }
                else
                {
                    var field = comp.GetType().GetField(cond.Property);
                    if (field != null)
                    {
                        rawValue = field.GetValue(comp)?.ToString();
                    }
                }

                if (rawValue == null)
                {
                    return false;
                }

                return CompareValues(rawValue, cond.Value, cond.FloatValue, cond.Operator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Compare a string value against a target using the given operator.
        /// Tries numeric comparison first, falls back to string equality.
        /// </summary>
        private static bool CompareValues(string value, string targetValue, double floatTarget, string op)
        {
            // Try numeric comparison first
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numVal))
            {
                return op switch
                {
                    "==" => Math.Abs(numVal - floatTarget) < 0.001,
                    "!=" => Math.Abs(numVal - floatTarget) >= 0.001,
                    "<" => numVal < floatTarget,
                    "<=" => numVal <= floatTarget,
                    ">" => numVal > floatTarget,
                    ">=" => numVal >= floatTarget,
                    _ => string.Equals(value, targetValue, StringComparison.OrdinalIgnoreCase)
                };
            }

            // String comparison fallback
            return op switch
            {
                "==" => string.Equals(value, targetValue, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(value, targetValue, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(value, targetValue, StringComparison.OrdinalIgnoreCase)
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Control-flow helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if nesting depth limit is exceeded. Logs error and breaks if so.
        /// </summary>
        private static bool CheckNestingDepth(ActiveSequence seq, string log)
        {
            int depth = seq.TryStack.Count + seq.RepeatStack.Count;
            if (depth >= MaxNestingDepth)
            {
                seq.Error = $"Max nesting depth ({MaxNestingDepth}) exceeded";
                seq.StepLog.Add($"{log} ERROR: max nesting depth exceeded");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Try to catch an error with the innermost applicable try context.
        /// Returns true if the error was caught, false otherwise.
        /// </summary>
        private static bool HandleTryError(ActiveSequence seq, int errorStepIndex)
        {
            // Find the innermost try context that contains the errored step
            for (int i = seq.TryStack.Count - 1; i >= 0; i--)
            {
                var ctx = seq.TryStack[i];
                if (errorStepIndex < ctx.EndIndex)
                {
                    int removeStart = errorStepIndex;
                    int removeCount = ctx.EndIndex - errorStepIndex;

                    // Remove errored step + remaining try sub-steps
                    if (removeCount > 0)
                    {
                        seq.Steps.RemoveRange(removeStart, removeCount);
                        // Adjust outer contexts' EndIndex values
                        AdjustContextIndicesForStack(seq.TryStack, i, removeStart, -removeCount);
                        AdjustContextIndices(seq.RepeatStack, removeStart, -removeCount);
                    }

                    // Insert catch steps at the same position
                    if (ctx.CatchSteps != null && ctx.CatchSteps.Count > 0)
                    {
                        seq.Steps.InsertRange(removeStart, ctx.CatchSteps);
                        AdjustContextIndicesForStack(seq.TryStack, i, removeStart, ctx.CatchSteps.Count);
                        AdjustContextIndices(seq.RepeatStack, removeStart, ctx.CatchSteps.Count);
                    }

                    seq.CurrentStep = removeStart;
                    seq.StepStartTime = 0;
                    seq.StepLog.Add($"TRY caught error at step {errorStepIndex}: {seq.Error}");

                    // Pop this and all inner try contexts
                    seq.TryStack.RemoveRange(i, seq.TryStack.Count - i);

                    seq.Error = null;
                    seq.Completed = false;
                    seq.Running = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Adjust EndIndex values for all contexts in a list.
        /// For indices >= fromIndex, add delta.
        /// </summary>
        private static void AdjustContextIndices(List<TryContext> contexts, int fromIndex, int delta)
        {
            foreach (var ctx in contexts)
            {
                if (ctx.EndIndex > fromIndex)
                    ctx.EndIndex += delta;
            }
        }

        private static void AdjustContextIndices(List<RepeatState> contexts, int fromIndex, int delta)
        {
            foreach (var rep in contexts)
            {
                if (rep.EndIndex > fromIndex)
                    rep.EndIndex += delta;
            }
        }

        /// <summary>
        /// Adjust EndIndex for all try contexts, skipping the one at skipIndex (which is being removed).
        /// </summary>
        private static void AdjustContextIndicesForStack(List<TryContext> contexts, int skipIndex, int fromIndex, int delta)
        {
            for (int j = 0; j < contexts.Count; j++)
            {
                if (j == skipIndex) continue;
                if (contexts[j].EndIndex > fromIndex)
                    contexts[j].EndIndex += delta;
            }
        }

        /// <summary>
        /// Adjust EndIndex for all contexts in both stacks.
        /// </summary>
        private static void AdjustContextIndices(ActiveSequence seq, int fromIndex, int delta)
        {
            AdjustContextIndices(seq.TryStack, fromIndex, delta);
            AdjustContextIndices(seq.RepeatStack, fromIndex, delta);
        }

        /// <summary>
        /// Build a human-readable description of a property condition (for logging).
        /// </summary>
        private static string GetConditionDesc(PropertyCondition cond)
        {
            var target = !string.IsNullOrEmpty(cond.Path) ? cond.Path : $"instanceId={cond.InstanceId}";
            return $"{target}.{cond.Component}.{cond.Property} {cond.Operator} {cond.Value}";
        }

        // ══════════════════════════════════════════════════════════════
        //  JSON parsing helpers (mirrors GameHandler.ParseJsonArrayOfObjects)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Parse a JSON array of objects into a list of dictionaries.
        /// Handles the minimal JSON format produced by the bridge's JSON helper.
        /// </summary>
        private static List<Dictionary<string, object>> ParseJsonArrayOfObjects(string json)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(json) || json.Trim() == "[]") return result;

            json = json.Trim();
            if (!json.StartsWith("[") || !json.EndsWith("]")) return result;

            var inner = json.Substring(1, json.Length - 2);
            var depth = 0;
            var start = 0;
            bool inStr = false;

            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inStr = !inStr;
                if (!inStr)
                {
                    if (c == '{') depth++;
                    if (c == '}') depth--;
                    if (c == ',' && depth == 0)
                    {
                        var objStr = inner.Substring(start, i - start);
                        if (!string.IsNullOrWhiteSpace(objStr))
                            result.Add(ParseJsonObject(objStr));
                        start = i + 1;
                    }
                }
            }

            var last = inner.Substring(start);
            if (!string.IsNullOrWhiteSpace(last))
                result.Add(ParseJsonObject(last));

            return result;
        }

        // ── Helpers ──

        private static string GetRequiredString(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return v?.ToString() ?? "";
        }

        private static string GetString(Dictionary<string, object> dict, string key, string defaultValue = "")
        {
            return dict.TryGetValue(key, out var v) ? v?.ToString() ?? defaultValue : defaultValue;
        }

        private static float GetFloat(Dictionary<string, object> dict, string key, float defaultValue = 0f)
        {
            if (!dict.TryGetValue(key, out var v)) return defaultValue;
            // Reject NaN/Infinity — they would corrupt game state (P7).
            try { return ToFiniteSingle(v, key, System.Globalization.CultureInfo.InvariantCulture); }
            catch (System.Exception) { return defaultValue; }
        }

        private static float GetRequiredFloat(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return Convert.ToSingle(v, CultureInfo.InvariantCulture);
        }
    }

    // ── Runtime update driver ──

    internal class SequenceDriver : MonoBehaviour
    {
        private void Awake()
        {
            hideFlags = HideFlags.HideAndDontSave;
        }

        private void Update()
        {
            SequenceRunner.ProcessSequences();
        }
    }
}
