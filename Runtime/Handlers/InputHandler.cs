using System;
using System.Collections.Generic;
using System.Globalization;
using SimpleMCPBridge.Runtime.Tools;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Simulates user input through Unity's EventSystem pipeline.
    /// All methods work in both Editor Play Mode and Runtime builds.
    ///
    /// Tools:
    ///   - input.click_screen — simulate a click at a normalized screen position (0.0~1.0)
    ///     Goes through full pointer event sequence: RaycastAll → PointerDown → PointerUp → PointerClick
    /// </summary>
    [MCPToolClass]
    public class InputHandler
    {
        [MCPTool(MCPMethodConst.CLICK_SCREEN, "Simulate a user click at a screen position. " +
            "Coordinates are normalized 0.0~1.0 (0.5,0.5 = center). " +
            "Goes through Unity EventSystem: RaycastAll → PointerDown → PointerUp → PointerClick. " +
            "Returns hit objects and which one received the click. " +
            "Requires an active EventSystem in the scene (Play Mode or Runtime).")]
        public static string ClickScreen(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            return ProcessClick(args["x"], args["y"], 0);
        }

        public static string ProcessClick(object rawX, object rawY, int button)
        {
            var nx = System.Convert.ToSingle(rawX, CultureInfo.InvariantCulture);
            var ny = System.Convert.ToSingle(rawY, CultureInfo.InvariantCulture);

            // Validate EventSystem exists
            if (EventSystem.current == null)
                return ErrorJson("No active EventSystem in scene. This tool requires Play Mode or a Runtime build with an EventSystem.");

            // Convert normalized coordinates to screen pixels
            // (0,0) = bottom-left, (1,1) = top-right
            var screenPos = new Vector2(nx * Screen.width, ny * Screen.height);

            // Create PointerEventData
            var pointerData = new PointerEventData(EventSystem.current)
            {
                position = screenPos,
                button = PointerEventData.InputButton.Left,
                pressPosition = screenPos,
            };

            // Raycast to find UI objects under the position
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointerData, results);

            if (results.Count == 0)
            {
                return JsonHelper.BuildJsonObject(
                    ("success", "false"),
                    ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                    ("hitCount", "0"),
                    ("error", JsonHelper.EscapeString("No UI object found at position"))
                );
            }

            // Build hit list
            var hitJsons = new List<string>();
            foreach (var r in results)
            {
                hitJsons.Add(JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(r.gameObject.name)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(r.gameObject))),
                    ("instanceId", r.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("sortOrder", r.sortingOrder.ToString(CultureInfo.InvariantCulture))
                ));
            }

            // Execute pointer events on the topmost hit (first result = topmost in UI)
            var target = results[0].gameObject;

            // Full click sequence: Down → Up → Click (all bubble up via ExecuteHierarchy
            // so parent objects like Button receive the events even when the Raycast hits a child Text).
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerClickHandler);

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                ("normalizedPos", JsonHelper.FloatArrayJson(new[] { nx, ny })),
                ("hitCount", results.Count.ToString(CultureInfo.InvariantCulture)),
                ("clicked", JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(target.name)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(target))),
                    ("instanceId", target.GetInstanceID().ToString(CultureInfo.InvariantCulture))
                )),
                ("hits", JsonHelper.BuildJsonArray(hitJsons.ToArray()))
            );
        }

        // ── 鼠标模拟 ──
        // 使用 EventSystem ExecuteHierarchy 路径（与 click_screen 相同）。
        // 适用于 UI 元素（Button、Toggle 等）。如需直接操作 Input System（3D 对象），
        // 需后续扩展。

        [MCPTool(MCPMethodConst.MOUSE_CLICK, "Simulate a mouse click at a normalized screen position. " +
            "Coordinates are normalized 0.0~1.0 (0.5,0.5 = center). " +
            "Optional 'button' param: 0=left (default), 1=right, 2=middle. " +
            "Uses MouseDeviceTools (Input System virtual mouse). " +
            "Works on objects receiving Input System events.")]
        public static string MouseClick(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var nx = GetRequiredFloat(args, "x");
            var ny = GetRequiredFloat(args, "y");
            var button = args.TryGetValue("button", out var b) ? System.Convert.ToInt32(b) : 0;

            var uv = new Vector2(nx, ny);
            MouseDeviceTools.ClickMouse(uv, button);

            var screenPos = new Vector2(nx * Screen.width, ny * Screen.height);
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                ("normalizedPos", JsonHelper.FloatArrayJson(new[] { nx, ny })),
                ("button", button.ToString(CultureInfo.InvariantCulture))
            );
        }
        //public string MouseClick(string paramsJson)
        //{
        //    var args = ParseJsonObject(paramsJson);
        //    return ProcessClick(args["x"], args["y"], 0);
        //}

        // ── 鼠标移动 ──

        [MCPTool(MCPMethodConst.MOUSE_MOVE, "Move mouse by pixel delta (for camera look/aim). " +
            "Params: 'dx' (float, required) — horizontal delta in pixels (positive = right), " +
            "'dy' (float, required) — vertical delta in pixels (positive = up). " +
            "Preserves current button states so held clicks aren't interrupted. " +
            "Typical values: (dx=50, dy=0) = look right, (dx=0, dy=-30) = look up. " +
            "Uses InputSystem.QueueStateEvent on the physical mouse device.")]
        public static string MouseMove(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var dx = GetRequiredFloat(args, "dx");
            var dy = GetRequiredFloat(args, "dy");

            MouseDeviceTools.MoveMouse(new Vector2(dx, dy));

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("dx", dx.ToString(CultureInfo.InvariantCulture)),
                ("dy", dy.ToString(CultureInfo.InvariantCulture))
            );
        }

        // ── 键盘模拟 ──

        [MCPTool(MCPMethodConst.KEY_PRESS, "Simulate keyboard key press/release. " +
            "Params: 'key' (string, required) — key name such as 'w', 'space', 'enter', 'upArrow', " +
            "'leftShift', 'f1'. " +
            "'action' (string, optional, default 'tap') — one of: " +
            "'tap' = press + immediate release (for jumps, shooting, interactions), " +
            "'hold' = press and keep pressed (for WASD movement), " +
            "'release' = release a held key (use key='*' or omit key to release ALL keys). " +
            "Uses InputSystem.QueueStateEvent on the physical keyboard device. " +
            "Works in both Editor Play Mode and Runtime builds with Input System package.")]
        public static string KeyPress(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var keyName = GetString(args, "key", "");
            var action = GetString(args, "action", "tap");

            // Release all: key omitted, empty, or "*"
            if (action == "release" && (string.IsNullOrEmpty(keyName) || keyName == "*"))
            {
                KeyboardTools.ReleaseAllKeys();
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("action", JsonHelper.EscapeString("release_all"))
                );
            }

            if (string.IsNullOrEmpty(keyName))
                return ErrorJson("Missing required parameter: 'key'");

            var key = KeyboardTools.ParseKey(keyName);

            switch (action)
            {
                case "tap":
                    KeyboardTools.TapKey(key);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("tap")),
                        ("key", JsonHelper.EscapeString(keyName))
                    );
                case "hold":
                    KeyboardTools.HoldKey(key);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("hold")),
                        ("key", JsonHelper.EscapeString(keyName))
                    );
                case "release":
                    KeyboardTools.ReleaseKey(key);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("release")),
                        ("key", JsonHelper.EscapeString(keyName))
                    );
                default:
                    return ErrorJson($"Unknown action: '{action}'. Use 'tap', 'hold', or 'release'.");
            }
        }

        // ── Touch Debug (temp) ───────────────────────────────────────

        // ── Touch ────────────────────────────────────────────────────

        [MCPTool(MCPMethodConst.TOUCH,
            "Simulate touch input. " +
            "Params: 'action' (string, required) — 'tap', 'start', 'move', or 'end'. " +
            "'x', 'y' (float, required for tap/start/move) — normalized screen position 0.0~1.0. " +
            "'fingerId' (int, optional, default 0) — touch finger ID for multi-touch. " +
            "Actions: 'tap' = immediate touch+release, 'start' = begin touching, " +
            "'move' = move held finger to new position, 'end' = release finger. " +
            "For swipe/drag: call start → move (x N) → end in sequence.")]
        public static string Touch(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var action = GetRequiredString(args, "action").ToLowerInvariant();
            var fingerId = (int)GetOptionalInt(args, "fingerId").GetValueOrDefault(0);

            switch (action)
            {
                case "tap":
                {
                    var nx = GetRequiredFloat(args, "x");
                    var ny = GetRequiredFloat(args, "y");
                    TouchDeviceTools.Tap(nx, ny);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("tap")),
                        ("x", nx.ToString("G", CultureInfo.InvariantCulture)),
                        ("y", ny.ToString("G", CultureInfo.InvariantCulture))
                    );
                }
                case "start":
                case "begin":
                {
                    var nx = GetRequiredFloat(args, "x");
                    var ny = GetRequiredFloat(args, "y");
                    var pos = TouchDeviceTools.ScreenPoint(nx, ny);
                    TouchDeviceTools.TouchBegan(fingerId, pos);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("start")),
                        ("fingerId", fingerId.ToString(CultureInfo.InvariantCulture)),
                        ("x", nx.ToString("G", CultureInfo.InvariantCulture)),
                        ("y", ny.ToString("G", CultureInfo.InvariantCulture))
                    );
                }
                case "move":
                {
                    var nx = GetRequiredFloat(args, "x");
                    var ny = GetRequiredFloat(args, "y");
                    var pos = TouchDeviceTools.ScreenPoint(nx, ny);
                    TouchDeviceTools.TouchMoved(fingerId, pos);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("move")),
                        ("fingerId", fingerId.ToString(CultureInfo.InvariantCulture)),
                        ("x", nx.ToString("G", CultureInfo.InvariantCulture)),
                        ("y", ny.ToString("G", CultureInfo.InvariantCulture))
                    );
                }
                case "end":
                {
                    TouchDeviceTools.TouchEnded(fingerId);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("end")),
                        ("fingerId", fingerId.ToString(CultureInfo.InvariantCulture))
                    );
                }
                default:
                    return ErrorJson($"Unknown touch action '{action}'. Use 'tap', 'start', 'move', or 'end'.");
            }
        }

        // ── Swipe ───────────────────────────────────────────────────

        [MCPTool(MCPMethodConst.SWIPE,
            "Perform a smooth swipe/drag gesture from one point to another over time. " +
            "Params: 'startX', 'startY' (float, required) — normalized start position 0.0~1.0. " +
            "'endX', 'endY' (float, required) — normalized end position 0.0~1.0. " +
            "'duration' (float, optional, default 0.3) — total swipe duration in seconds. " +
            "'steps' (int, optional, default 15) — number of intermediate move events. " +
            "'fingerId' (int, optional, default 0) — touch finger ID. " +
            "Returns immediately with estimated duration. Use game.wait afterwards to let it complete.")]
        public static string Swipe(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var startX = GetRequiredFloat(args, "startX");
            var startY = GetRequiredFloat(args, "startY");
            var endX = GetRequiredFloat(args, "endX");
            var endY = GetRequiredFloat(args, "endY");
            var duration = GetFloat(args, "duration", 0.3f);
            var steps = (int)(args.TryGetValue("steps", out var s) ? Convert.ToInt32(s) : 15);
            var fingerId = (int)GetOptionalInt(args, "fingerId").GetValueOrDefault(0);

            steps = Mathf.Clamp(steps, 3, 60);
            duration = Mathf.Max(duration, 0.05f);

            // Interpolate path
            var startPos = TouchDeviceTools.ScreenPoint(startX, startY);
            var endPos = TouchDeviceTools.ScreenPoint(endX, endY);
            var path = new Vector2[steps + 2]; // begin + intermediate + end
            path[0] = startPos;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / (steps + 1);
                path[i] = Vector2.Lerp(startPos, endPos, t);
            }
            path[steps + 1] = endPos;

            float stepInterval = duration / (path.Length - 1);
            double totalDuration = TouchDeviceTools.StartSwipe(fingerId, path, stepInterval);

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("fingerId", fingerId.ToString(CultureInfo.InvariantCulture)),
                ("steps", path.Length.ToString(CultureInfo.InvariantCulture)),
                ("duration", duration.ToString("G", CultureInfo.InvariantCulture)),
                ("estimate", totalDuration.ToString("G", CultureInfo.InvariantCulture)),
                ("note", JsonHelper.EscapeString("swipe started asynchronously — use game.wait seconds to wait for completion"))
            );
        }

        // ── Gamepad ──────────────────────────────────────────────────

        [MCPTool(MCPMethodConst.GAMEPAD,
            "Control a virtual gamepad. " +
            "Params: 'action' determines operation:" +
            "  1. button — press/release/tap a button. Needs 'button' (string) and 'press' (string: tap/press/release)." +
            "  2. axis — set an axis value. Needs 'axis' (string) and 'value' (float, -1..1)." +
            "  3. set — batch operation. Needs 'buttons' (array of {button, press}) and/or 'axes' (object of name→value)." +
            "  4. reset — reset all buttons and axes to neutral." +
            "  5. state — get current button/axis state." +
            "Button names: south/east/north/west, a/b/x/y, leftShoulder/rightShoulder, lb/rb," +
            "leftStick/rightStick, start/select/back, dpadUp/down/left/right. " +
            "Axis names: leftStickX/Y, rightStickX/Y, leftTrigger, rightTrigger.")]
        public static string Gamepad(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var action = GetRequiredString(args, "action").ToLowerInvariant();

            switch (action)
            {
                case "button":
                {
                    var buttonName = GetRequiredString(args, "button");
                    var press = GetString(args, "press", "tap").ToLowerInvariant();
                    var btn = GamepadTools.ParseButton(buttonName);
                    switch (press)
                    {
                        case "tap":
                            GamepadTools.TapButton(btn);
                            return JsonHelper.BuildJsonObject(
                                ("success", "true"),
                                ("action", JsonHelper.EscapeString("button")),
                                ("button", JsonHelper.EscapeString(buttonName)),
                                ("press", JsonHelper.EscapeString("tap"))
                            );
                        case "press":
                        case "hold":
                            GamepadTools.PressButton(btn);
                            return JsonHelper.BuildJsonObject(
                                ("success", "true"),
                                ("action", JsonHelper.EscapeString("button")),
                                ("button", JsonHelper.EscapeString(buttonName)),
                                ("press", JsonHelper.EscapeString("press"))
                            );
                        case "release":
                            GamepadTools.ReleaseButton(btn);
                            return JsonHelper.BuildJsonObject(
                                ("success", "true"),
                                ("action", JsonHelper.EscapeString("button")),
                                ("button", JsonHelper.EscapeString(buttonName)),
                                ("press", JsonHelper.EscapeString("release"))
                            );
                        default:
                            return ErrorJson($"Unknown press action '{press}'. Use 'tap', 'press', or 'release'.");
                    }
                }

                case "axis":
                {
                    var axisName = GetRequiredString(args, "axis");
                    var value = GetRequiredFloat(args, "value");
                    GamepadTools.SetAxis(axisName, value);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("axis")),
                        ("axis", JsonHelper.EscapeString(axisName)),
                        ("value", value.ToString("G", CultureInfo.InvariantCulture))
                    );
                }

                case "set":
                {
                    var details = new List<string>();

                    // Buttons
                    var buttons = new List<(string, string)>();
                    if (args.TryGetValue("buttons", out var btnObj) && btnObj is List<object> btnList)
                    {
                        foreach (var item in btnList)
                        {
                            if (item is Dictionary<string, object> btnDict)
                            {
                                var b = GetRequiredString(btnDict, "button");
                                var p = GetString(btnDict, "press", "tap");
                                buttons.Add((b, p));
                            }
                        }
                    }

                    // Axes
                    Dictionary<string, float> axes = null;
                    if (args.TryGetValue("axes", out var axObj) && axObj is Dictionary<string, object> axDict)
                    {
                        axes = new Dictionary<string, float>();
                        foreach (var kvp in axDict)
                            axes[kvp.Key] = Convert.ToSingle(kvp.Value, CultureInfo.InvariantCulture);
                    }

                    var resultDetails = GamepadTools.ExecuteBatch(buttons, axes);
                    foreach (var d in resultDetails)
                        details.Add(d);

                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("set")),
                        ("applied", JsonHelper.EscapeString(string.Join(", ", details)))
                    );
                }

                case "reset":
                {
                    GamepadTools.ResetAll();
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("reset"))
                    );
                }

                case "state":
                {
                    var state = GamepadTools.GetCurrentState();
                    var pressedButtons = new List<string>();
                    foreach (GamepadButton btn in Enum.GetValues(typeof(GamepadButton)))
                    {
                        if ((state.buttons & (1 << (int)btn)) != 0)
                            pressedButtons.Add(btn.ToString());
                    }
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("action", JsonHelper.EscapeString("state")),
                        ("buttons", JsonHelper.EscapeString(string.Join(",", pressedButtons))),
                        ("leftStickX", state.leftStick.x.ToString("G", CultureInfo.InvariantCulture)),
                        ("leftStickY", state.leftStick.y.ToString("G", CultureInfo.InvariantCulture)),
                        ("rightStickX", state.rightStick.x.ToString("G", CultureInfo.InvariantCulture)),
                        ("rightStickY", state.rightStick.y.ToString("G", CultureInfo.InvariantCulture)),
                        ("leftTrigger", state.leftTrigger.ToString("G", CultureInfo.InvariantCulture)),
                        ("rightTrigger", state.rightTrigger.ToString("G", CultureInfo.InvariantCulture))
                    );
                }

                default:
                    return ErrorJson($"Unknown gamepad action '{action}'. " +
                        "Use 'button', 'axis', 'set', 'reset', or 'state'.");
            }
        }

        // ── Helpers ──

        public static float GetRequiredFloat(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new ArgumentException($"Missing required parameter: '{key}'");
            return Convert.ToSingle(v, CultureInfo.InvariantCulture);
        }

        public static float GetFloat(Dictionary<string, object> dict, string key, float defaultValue = 0f)
        {
            if (!dict.TryGetValue(key, out var v))
                return defaultValue;
            return Convert.ToSingle(v, CultureInfo.InvariantCulture);
        }

    }
}

// debug_touch 16:06:14
