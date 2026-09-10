using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers.Game
{
    // 输入 + 状态工具组（input.action / game.get_state）— 从 GameHandler 抽离
    [MCPToolClass]
    public static class GameStateHandler
    {
[MCPTool(MCPMethodConst.INPUT_ACTION,
            "Execute a combined input action in one call. " +
            "Params (all optional):" +
            "'keys' — array of {key, action} where action is tap|hold|release " +
            "(key names: 'w', 'space', 'enter', 'leftShift', etc.)" +
            "'mouse' — object with optional fields: " +
            "  x/y (normalized 0-1 for absolute position), " +
            "  dx/dy (pixel delta for camera look), " +
            "  scroll (float, +up/-down), " +
            "  buttons (array of {button:0|1|2, action:'click'|'hold'|'release'})" +
            "'axes' — dict of Input System action names to float values " +
            "(e.g. {\"Horizontal\":1.0, \"Vertical\":0.5, \"Fire1\":1.0})" +
            "Input System actions are applied via ApplyOverrideValue (also feeds legacy " +
            "Input.GetAxis when Active Input Handling is 'Both' or 'New')." +
            "For legacy-only Input Manager, use keys instead of axes." +
            "Returns list of executed actions with results.")]
        [MCPParam("keys", Type = "array", Description = "Keys [{key, action tap/hold/release}]")]
        [MCPParam("mouse", Type = "object", Description = "Mouse {x/y, dx/dy, scroll, buttons}")]
        [MCPParam("axes", Type = "object", Description = "Input action name to value map")]
        public static string InputAction(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);

                // Parse keys
                var keys = new List<(string key, string action)>();
                if (args.TryGetValue("keys", out var keysObj) && keysObj is string keysJson)
                {
                    // keysJson is the raw JSON array string
                    var keyItems = ParseJsonArrayOfObjects(keysJson);
                    foreach (var item in keyItems)
                    {
                        var k = GetString(item, "key", "");
                        var a = GetString(item, "action", "tap");
                        if (!string.IsNullOrEmpty(k))
                            keys.Add((k, a));
                    }
                }

                // Parse mouse
                (float? x, float? y, float? dx, float? dy, float? scroll,
                    List<(int button, string action)> buttons)? mouse = null;
                if (args.TryGetValue("mouse", out var mouseObj) && mouseObj is string mouseJson)
                {
                    var mouseArgs = ParseJsonObject(mouseJson);
                    float? mx = null, my = null, mdx = null, mdy = null, ms = null;
                    List<(int, string)> btns = null;

                    if (mouseArgs.TryGetValue("x", out var xv)) mx = Convert.ToSingle(xv, CultureInfo.InvariantCulture);
                    if (mouseArgs.TryGetValue("y", out var yv)) my = Convert.ToSingle(yv, CultureInfo.InvariantCulture);
                    if (mouseArgs.TryGetValue("dx", out var dxv)) mdx = Convert.ToSingle(dxv, CultureInfo.InvariantCulture);
                    if (mouseArgs.TryGetValue("dy", out var dyv)) mdy = Convert.ToSingle(dyv, CultureInfo.InvariantCulture);
                    if (mouseArgs.TryGetValue("scroll", out var sv)) ms = Convert.ToSingle(sv, CultureInfo.InvariantCulture);
                    if (mouseArgs.TryGetValue("buttons", out var bv) && bv is string bJson)
                    {
                        btns = new List<(int, string)>();
                        var bItems = ParseJsonArrayOfObjects(bJson);
                        foreach (var b in bItems)
                        {
                            var btn = GetRequiredInt(b, "button");
                            var ba = GetString(b, "action", "click");
                            btns.Add((btn, ba));
                        }
                    }

                    mouse = (mx, my, mdx, mdy, ms, btns);
                }

                // Parse axes
                Dictionary<string, float> axes = null;
                if (args.TryGetValue("axes", out var axesObj) && axesObj is string axesJson)
                {
                    axes = ParseJsonObject(axesJson)
                        .ToDictionary(kvp => kvp.Key, kvp => Convert.ToSingle(kvp.Value, CultureInfo.InvariantCulture));
                }

                // Execute
#if UNITY_INPUT_SYSTEM
                var result = InputActionTools.ExecuteAction(keys, mouse, axes);
#else
                var result = new Dictionary<string, object>();
                result["actions"] = new List<string>();
#endif

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("actions", JsonHelper.BuildJsonArray(
                        (result["actions"] as List<string>)?.Select(s => JsonHelper.EscapeString(s)).ToArray() ?? Array.Empty<string>()
                    ))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"Input action failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_state
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_STATE,
            "Get a comprehensive snapshot of the current game state in one call. " +
            "Returns: active scene name & build index, time (time/deltaTime/timeScale/frameCount), " +
            "screen dimensions, play mode state, all on-screen UI texts, all interactive UI " +
            "elements, and optional player position (if 'playerPath' is specified). " +
            "Optional param 'includeUI' (bool, default true) — set false to skip UI scan " +
            "for faster response. " +
            "This is the primary perception tool — one call gives the agent 80% of what it " +
            "needs to decide the next action.")]
        [MCPParam("includeUI", Type = "boolean", Description = "Skip UI scan when false (default true)")]
        [MCPParam("playerPath", Type = "string", Description = "Player GameObject path for position")]
        public static string GetGameState(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var includeUI = !(args.TryGetValue("includeUI", out var iuObj) && iuObj is bool iu && !iu);
                var playerPath = GetString(args, "playerPath", "");

                // 1. Scene info
                var activeScene = SceneManager.GetActiveScene();
                var sceneName = activeScene.name;
                var sceneIndex = activeScene.buildIndex;

                // 2. Time
                var timeJson = JsonHelper.BuildJsonObject(
                    ("time", Time.time.ToString("G", CultureInfo.InvariantCulture)),
                    ("deltaTime", Time.deltaTime.ToString("G", CultureInfo.InvariantCulture)),
                    ("unscaledDeltaTime", Time.unscaledDeltaTime.ToString("G", CultureInfo.InvariantCulture)),
                    ("timeScale", Time.timeScale.ToString("G", CultureInfo.InvariantCulture)),
                    ("frameCount", Time.frameCount.ToString(CultureInfo.InvariantCulture)),
                    ("realtimeSinceStartup", Time.realtimeSinceStartup.ToString("G", CultureInfo.InvariantCulture))
                );

                // 3. Screen
                var screenJson = JsonHelper.BuildJsonObject(
                    ("width", Screen.width.ToString(CultureInfo.InvariantCulture)),
                    ("height", Screen.height.ToString(CultureInfo.InvariantCulture)),
                    ("dpi", Screen.dpi.ToString("G", CultureInfo.InvariantCulture))
                );

                // 4. Play mode
                string playMode;
#if UNITY_EDITOR
                playMode = UnityEditor.EditorApplication.isPlaying
                    ? (UnityEditor.EditorApplication.isPaused ? "paused" : "playing")
                    : "editing";
#else
                playMode = Application.isPlaying ? "playing" : "not_playing";
#endif

                // 5. UI texts
                string textsJson = "[]";
                string uiElementsJson = "[]";
                var uiTexts = new List<string>();
                if (includeUI)
                {
                    try
                    {
                        uiTexts = UIAnalysisTools.ScanAllTexts();
                        textsJson = JsonHelper.BuildJsonArray(uiTexts.ToArray());

                        var elements = UIAnalysisTools.ScanInteractiveUI();
                        uiElementsJson = JsonHelper.BuildJsonArray(elements.ToArray());
                    }
                    catch
                    {
                        // UI scan errors are not fatal
                    }
                }

                // 6. Player position
                string playerJson = "null";
                if (!string.IsNullOrEmpty(playerPath))
                {
                    try
                    {
                        var go = GameObject.Find(playerPath);
                        if (go != null)
                        {
                            var pos = go.transform.position;
                            playerJson = JsonHelper.BuildJsonObject(
                                ("path", JsonHelper.EscapeString(playerPath)),
                                ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                                ("localPosition", JsonHelper.FloatArrayJson(new[] {
                                    go.transform.localPosition.x,
                                    go.transform.localPosition.y,
                                    go.transform.localPosition.z })),
                                ("active", JsonHelper.BoolJson(go.activeInHierarchy))
                            );
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[game.get_state] transform data error: {ex.Message}"); }
                }

                // 7. Camera info
                string cameraJson = "null";
                try
                {
                    var mainCam = Camera.main;
                    if (mainCam != null)
                    {
                        var camPos = mainCam.transform.position;
                        var camFwd = mainCam.transform.forward;
                        cameraJson = JsonHelper.BuildJsonObject(
                            ("position", JsonHelper.FloatArrayJson(new[] { camPos.x, camPos.y, camPos.z })),
                            ("forward", JsonHelper.FloatArrayJson(new[] { camFwd.x, camFwd.y, camFwd.z })),
                            ("fov", mainCam.fieldOfView.ToString("G", CultureInfo.InvariantCulture)),
                            ("isOrthographic", JsonHelper.BoolJson(mainCam.orthographic)),
                            ("nearClipPlane", mainCam.nearClipPlane.ToString("G", CultureInfo.InvariantCulture)),
                            ("farClipPlane", mainCam.farClipPlane.ToString("G", CultureInfo.InvariantCulture))
                        );
                    }
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[game.get_state] camera info error: {ex.Message}"); }

                // 8. Active axes (Input System)
                string axesJson = "[]";
#if UNITY_INPUT_SYSTEM
                try
                {
                    var axisInfo = new List<string>();
                    var allAssets = Resources.FindObjectsOfTypeAll<UnityEngine.InputSystem.InputActionAsset>();
                    foreach (var asset in allAssets)
                    {
                        foreach (var map in asset.actionMaps)
                        {
                            foreach (var action in map.actions)
                            {
                                var val = action.ReadValue<float>();
                                if (Mathf.Abs(val) > 0.01f) // only non-zero
                                {
                                    axisInfo.Add(JsonHelper.BuildJsonObject(
                                        ("name", JsonHelper.EscapeString(action.name)),
                                        ("value", val.ToString("G", CultureInfo.InvariantCulture)),
                                        ("map", JsonHelper.EscapeString(map.name))
                                    ));
                                }
                            }
                        }
                    }
                    axesJson = JsonHelper.BuildJsonArray(axisInfo.ToArray());
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[game.get_state] axis info error: {ex.Message}"); }
#endif

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("scene", JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(sceneName)),
                        ("buildIndex", sceneIndex.ToString(CultureInfo.InvariantCulture))
                    )),
                    ("time", timeJson),
                    ("screen", screenJson),
                    ("playMode", JsonHelper.EscapeString(playMode)),
                    ("player", playerJson),
                    ("camera", cameraJson),
                    ("activeAxes", axesJson),
                    ("uiTextCount", includeUI ? (uiTexts.Count > 0 ? uiTexts.Count.ToString(CultureInfo.InvariantCulture) : "0") : "\"skipped\""),
                    ("uiTexts", textsJson),
                    ("interactiveUI", uiElementsJson)
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetGameState failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.wait
        // ══════════════════════════════════════════════════════════════

        
[MCPTool(MCPMethodConst.GAME_WAIT,
            "Start an async wait operation. Returns immediately with a wait ID — " +
            "poll game.wait_check with the ID to check completion. " +
            "Wait types:" +
            "1. seconds (value: float) — wait N seconds of real time" +
            "2. sceneLoaded (sceneName: string) — wait until a scene is loaded" +
            "3. uiAppears (text: string) — wait until UI text appears on screen" +
            "4. uiDisappears (text: string) — wait until UI text disappears" +
            "5. property (path, component, property, operator, value) — " +
            "   wait until a GameObject's component property meets a condition" +
            "Optional: 'timeout' (float, default 30) — max seconds to wait." +
            "Examples:" +
            "  {\"type\":\"seconds\",\"value\":2.0}" +
            "  {\"type\":\"uiAppears\",\"text\":\"Continue\",\"timeout\":10}" +
            "  {\"type\":\"property\",\"path\":\"Player\",\"component\":\"Health\",\"property\":\"currentHP\",\"operator\":\"<=\",\"value\":0}")]
        [MCPParam("type", Type = "string", Required = true, Description = "seconds/sceneLoaded/uiAppears/uiDisappears/property", EnumValues = new[] { "seconds", "sceneLoaded", "uiAppears", "uiDisappears", "property" })]
        [MCPParam("timeout", Type = "number", Description = "Max wait seconds (default 30)")]
        [MCPParam("value", Type = "string", Description = "seconds: float; property: target value")]
        [MCPParam("sceneName", Type = "string", Description = "Scene name (type=sceneLoaded)")]
        [MCPParam("text", Type = "string", Description = "UI text (type=uiAppears/uiDisappears)")]
        [MCPParam("path", Type = "string", Description = "Object path (type=property)")]
        [MCPParam("component", Type = "string", Description = "Component type (type=property)")]
        [MCPParam("property", Type = "string", Description = "Property name (type=property)")]
        [MCPParam("operator", Type = "string", Description = "==/!=/</>/<=/>= (type=property)")]
        public static string Wait(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var type = GetRequiredString(args, "type");
                var timeout = args.TryGetValue("timeout", out var to)
                    ? Convert.ToSingle(to, CultureInfo.InvariantCulture)
                    : 30f;

                var wait = new WaitState
                {
                    Type = type,
                    EndTime = DateTime.UtcNow.AddSeconds(timeout),
                };

                switch (type)
                {
                    case "seconds":
                    {
                        var value = GetRequiredFloat(args, "value");
                        wait.EndTime = DateTime.UtcNow.AddSeconds(value);
                        break;
                    }
                    case "sceneLoaded":
                    {
                        wait.SceneName = GetRequiredString(args, "sceneName");
                        break;
                    }
                    case "uiAppears":
                    case "uiDisappears":
                    {
                        wait.UIText = GetRequiredString(args, "text");
                        break;
                    }
                    case "property":
                    {
                        wait.ObjectPath = GetRequiredString(args, "path");
                        wait.ComponentType = GetRequiredString(args, "component");
                        wait.PropertyName = GetRequiredString(args, "property");
                        wait.Operator = GetString(args, "operator", "==");
                        var rawVal = GetRequiredString(args, "value");
                        wait.TargetValue = rawVal;
                        // Try to parse as float for numeric comparison
                        double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out wait.FloatTarget);
                        break;
                    }
                    default:
                        return ErrorJson($"Unknown wait type: '{type}'. " +
                            "Use: seconds, sceneLoaded, uiAppears, uiDisappears, property");
                }

                var id = $"wait_{++_waitCounter}";
                _pendingWaits[id] = wait;

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("status", "\"started\""),
                    ("id", JsonHelper.EscapeString(id)),
                    ("type", JsonHelper.EscapeString(type)),
                    ("timeout", timeout.ToString("G", CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"Wait failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.wait_check
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_WAIT_CHECK,
            "Poll a previously started game.wait operation. " +
            "Param: 'id' — the wait ID returned by game.wait. " +
            "Returns status: 'completed', 'waiting', 'timeout', or 'error'. " +
            "For 'waiting', also returns a 'debug' field explaining what's being waited on. " +
            "Poll at ~0.5s intervals until status is 'completed'.")]
        [MCPParam("id", Type = "string", Required = true, Description = "Wait ID from game.wait")]
        public static string WaitCheck(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var id = GetRequiredString(args, "id");

                if (!_pendingWaits.TryGetValue(id, out var wait))
                    return ErrorJson($"Unknown wait id: '{id}'. It may have already completed or expired.");

                // Check completion FIRST (so "seconds" type returns completed, not timeout)
                bool done = false;
                string debug = "";

                switch (wait.Type)
                {
                    case "seconds":
                    {
                        done = DateTime.UtcNow >= wait.EndTime;
                        var remaining = (wait.EndTime - DateTime.UtcNow).TotalSeconds;
                        debug = $"Waiting {remaining:F1}s more";
                        break;
                    }
                    case "sceneLoaded":
                    {
                        var scene = SceneManager.GetActiveScene();
                        done = string.Equals(scene.name, wait.SceneName, StringComparison.OrdinalIgnoreCase);
                        debug = done
                            ? $"Scene '{wait.SceneName}' is now active"
                            : $"Current scene: '{scene.name}', waiting for '{wait.SceneName}'";
                        break;
                    }
                    case "uiAppears":
                    {
                        var texts = UIAnalysisTools.ScanAllTexts();
                        done = texts.Any(t =>
                            t.IndexOf(wait.UIText, StringComparison.OrdinalIgnoreCase) >= 0);
                        debug = done
                            ? $"Text '{wait.UIText}' found on screen"
                            : $"Waiting for text '{wait.UIText}' to appear";
                        break;
                    }
                    case "uiDisappears":
                    {
                        var texts = UIAnalysisTools.ScanAllTexts();
                        done = !texts.Any(t =>
                            t.IndexOf(wait.UIText, StringComparison.OrdinalIgnoreCase) >= 0);
                        debug = done
                            ? $"Text '{wait.UIText}' no longer on screen"
                            : $"Waiting for text '{wait.UIText}' to disappear";
                        break;
                    }
                    case "property":
                    {
                        try
                        {
                            var go = GameObject.Find(wait.ObjectPath);
                            if (go == null)
                            {
                                debug = $"Object '{wait.ObjectPath}' not found in scene";
                            }
                            else
                            {
                                var comp = go.GetComponent(wait.ComponentType);
                                if (comp == null)
                                {
                                    debug = $"Component '{wait.ComponentType}' not found on '{wait.ObjectPath}'";
                                }
                                else
                                {
                                    var prop = comp.GetType().GetProperty(wait.PropertyName);
                                    if (prop == null)
                                    {
                                        debug = $"Property '{wait.PropertyName}' not found on '{wait.ComponentType}'";
                                    }
                                    else
                                    {
                                        var val = prop.GetValue(comp)?.ToString() ?? "";
                                        done = EvaluateCondition(val, wait);
                                        debug = $"Property '{wait.PropertyName}' = {val}, waiting for {wait.Operator} {wait.TargetValue}";
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            debug = $"Check error: {ex.Message}";
                        }
                        break;
                    }
                }

                if (done)
                {
                    _pendingWaits.Remove(id);
                    return JsonHelper.BuildJsonObject(
                        ("status", "\"completed\""),
                        ("id", JsonHelper.EscapeString(id)),
                        ("type", JsonHelper.EscapeString(wait.Type))
                    );
                }

                // Check timeout
                if (DateTime.UtcNow > wait.EndTime)
                {
                    _pendingWaits.Remove(id);
                    return JsonHelper.BuildJsonObject(
                        ("status", "\"timeout\""),
                        ("id", JsonHelper.EscapeString(id)),
                        ("type", JsonHelper.EscapeString(wait.Type))
                    );
                }

                return JsonHelper.BuildJsonObject(
                    ("status", "\"waiting\""),
                    ("id", JsonHelper.EscapeString(id)),
                    ("type", JsonHelper.EscapeString(wait.Type)),
                    ("remaining", Math.Max(0, (wait.EndTime - DateTime.UtcNow).TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)),
                    ("debug", JsonHelper.EscapeString(debug))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"WaitCheck failed: {ex.Message}");
}
        }

        // ── Pending wait state（自 GameHandler 迁入，随 game.wait/wait_check 内聚）──
        private static readonly Dictionary<string, WaitState> _pendingWaits = new();
        private static int _waitCounter;

        private class WaitState
        {
            public string Type;  // "seconds", "sceneLoaded", "uiAppears", "property"
            public DateTime EndTime;
            public string SceneName;
            public string UIText;
            public string ObjectPath;
            public string ComponentType;
            public string PropertyName;
            public string TargetValue;
            public string Operator;  // "==", "!=", "<", "<=", ">", ">="
            public double FloatTarget;
            public bool Completed;
            public string Error;
            public float CheckInterval = 0.2f;
        }

        private static bool EvaluateCondition(string value, WaitState wait)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numVal))
            {
                return wait.Operator switch
                {
                    "==" => Math.Abs(numVal - wait.FloatTarget) < 0.001,
                    "!=" => Math.Abs(numVal - wait.FloatTarget) >= 0.001,
                    "<" => numVal < wait.FloatTarget,
                    "<=" => numVal <= wait.FloatTarget,
                    ">" => numVal > wait.FloatTarget,
                    ">=" => numVal >= wait.FloatTarget,
                    _ => string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase)
                };
            }
            return wait.Operator switch
            {
                "==" => string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase)
            };
        }
        //  game.do_sequence
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_DO_SEQUENCE,
            "Execute a sequence of actions on the Unity side in one call. " +
            "Params: 'steps' (array, required) — ordered list of action steps. " +
            "'timeout' (float, optional, default 30) — max seconds for the full sequence. " +
            "Each step has 'type' and type-specific params:" +
            "  type='wait' — params: duration(float, seconds). Pauses execution." +
            "  type='key' — params: name(string, key name), action(tap|hold|release). Requires Input System." +
            "  type='mouse_click' — params: x(float, 0-1), y(float, 0-1), button(0|1|2). Requires Input System." +
            "  type='mouse_move' — params: dx(float), dy(float) in pixels. Requires Input System." +
            "  type='gamepad' — params: action(button|axis|reset), button, press, axis, value. Requires Input System." +
            "  type='click_screen' — params: x(float, 0-1), y(float, 0-1). Uses EventSystem, no Input System needed." +
            "  type='if_property' — conditional branch. params: path|instanceId, component, property, operator(==/!=/</>/<=/>=), value, then[], else[]. Evaluates property and inserts then or else sub-steps." +
            "  type='try' — error recovery. params: steps[], catch[]. Runs steps; if any errors, inserts catch sub-steps." +
            "  type='repeat' — loop. params: steps[], count(int|optional), condition{}(optional), maxCount(int|optional). Repeats steps until count reached or condition met." +
            "Returns sequence ID immediately. Poll game.sequence_status for completion." +
            "Example: [{\"type\":\"key\",\"name\":\"w\",\"action\":\"hold\"}," +
            "{\"type\":\"wait\",\"duration\":0.5}," +
            "{\"type\":\"key\",\"name\":\"w\",\"action\":\"release\"}]")]
        [MCPParam("steps", Type = "array", Required = true, Description = "Ordered action steps [{type, ...}]")]
        [MCPParam("timeout", Type = "number", Description = "Max sequence seconds (default 30)")]
        public static string DoSequence(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                if (!args.TryGetValue("steps", out var stepsObj) || stepsObj is not string stepsJson)
                    return ErrorJson("Missing required parameter: 'steps' (array)");

                var steps = ParseJsonArrayOfObjects(stepsJson);
                if (steps.Count == 0)
                    return ErrorJson("'steps' array is empty");

                var timeout = args.TryGetValue("timeout", out var to)
                    ? Convert.ToSingle(to, CultureInfo.InvariantCulture)
                    : 30f;

                var id = Tools.SequenceRunner.StartSequence(steps, timeout);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("id", JsonHelper.EscapeString(id)),
                    ("steps", steps.Count.ToString(CultureInfo.InvariantCulture)),
                    ("timeout", timeout.ToString("G", CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"DoSequence failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.sequence_status
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_SEQUENCE_STATUS,
            "Poll a running sequence's status. " +
            "Param: 'id' (string, required) — the sequence ID from game.do_sequence. " +
            "Returns: status (running|completed|error), step (current step index), " +
            "total (total steps), log (recent step log entries).")]
        [MCPParam("id", Type = "string", Required = true, Description = "Sequence ID from game.do_sequence")]
        public static string SequenceStatus(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var id = GetRequiredString(args, "id");

                var seq = Tools.SequenceRunner.GetSequence(id);
                if (seq == null)
                    return ErrorJson($"Sequence '{id}' not found (already completed and cleaned up)");

                string status;
                if (seq.Error != null) status = "error";
                else if (seq.Completed) status = "completed";
                else status = "running";

                var logSlice = seq.StepLog.Skip(Math.Max(0, seq.StepLog.Count - 10)).ToList();
                var logEntries = logSlice.Count > 0 ? string.Join("; ", logSlice) : "";

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("status", JsonHelper.EscapeString(status)),
                    ("id", JsonHelper.EscapeString(id)),
                    ("step", seq.CurrentStep.ToString(CultureInfo.InvariantCulture)),
                    ("total", seq.Steps.Count.ToString(CultureInfo.InvariantCulture)),
                    ("log", JsonHelper.EscapeString(logEntries)),
                    ("error", seq.Error != null ? JsonHelper.EscapeString(seq.Error) : "null")
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"SequenceStatus failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_spatial
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_SPATIAL,
            "Get 3D spatial information about objects near a reference point. " +
            "Params: 'origin' (float[3], optional) — world position to search around. " +
            "'playerPath' (string, optional) — use this GameObject's position as origin " +
            "(overrides 'origin' if both provided). Default origin = (0,0,0). " +
            "'radius' (float, optional, default 10) — search radius in world units. " +
            "'maxObjects' (int, optional, default 20) — max objects to return. " +
            "'tag' (string, optional) — filter by Unity tag. " +
            "'layerName' (string, optional) — filter by layer name. " +
            "'typeFilter' (string, optional) — filter by component type name (e.g. 'Enemy', 'Item'). " +
            "Returns: array of nearby objects with name, instanceId, components, " +
            "position, distance, direction (normalized vector from origin), and tags.")]
        [MCPParam("origin", Type = "object", Description = "[x,y,z] world position (object form)")]
        [MCPParam("playerPath", Type = "string", Description = "Player path as search origin")]
        [MCPParam("radius", Type = "number", Description = "Search radius (default 10)")]
        [MCPParam("maxObjects", Type = "integer", Description = "Max results (default 20)")]
        [MCPParam("tag", Type = "string", Description = "Filter by Unity tag")]
        [MCPParam("layerName", Type = "string", Description = "Filter by layer name")]
        [MCPParam("typeFilter", Type = "string", Description = "Filter by component type name")]
        public static string GetSpatial(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var radius = GetFloat(args, "radius", 10f);
                var maxObjects = (int)(args.TryGetValue("maxObjects", out var mo)
                    ? Convert.ToInt32(mo) : 20);
                var tagFilter = GetString(args, "tag", "");
                var layerNameFilter = GetString(args, "layerName", "");
                var typeFilter = GetString(args, "typeFilter", "");

                // Resolve origin
                Vector3 origin;
                var playerPath = GetString(args, "playerPath", "");
                if (!string.IsNullOrEmpty(playerPath))
                {
                    var playerObj = GameObject.Find(playerPath);
                    if (playerObj == null)
                        return ErrorJson($"Player object '{playerPath}' not found");
                    origin = playerObj.transform.position;
                }
                else if (args.TryGetValue("origin", out var originObj) && originObj is string originJson)
                {
                    var parts = ParseJsonObject(originJson);
                    float ox = 0, oy = 0, oz = 0;
                    if (parts.TryGetValue("0", out var xv) || parts.TryGetValue("x", out xv))
                        ox = Convert.ToSingle(xv, CultureInfo.InvariantCulture);
                    if (parts.TryGetValue("1", out var yv) || parts.TryGetValue("y", out yv))
                        oy = Convert.ToSingle(yv, CultureInfo.InvariantCulture);
                    if (parts.TryGetValue("2", out var zv) || parts.TryGetValue("z", out zv))
                        oz = Convert.ToSingle(zv, CultureInfo.InvariantCulture);
                    origin = new Vector3(ox, oy, oz);
                }
                else
                {
                    origin = Vector3.zero;
                }

                var radiusSq = radius * radius;
                var results = new List<string>();
                int layerMask = -1;
                if (!string.IsNullOrEmpty(layerNameFilter))
                    layerMask = LayerMask.NameToLayer(layerNameFilter);

                // Collect all GameObjects via root scene traversal (avoids 10k+ allocation from FindObjectsByType)
                var allObjects = new List<GameObject>(256);
                var roots = SceneManager.GetActiveScene().GetRootGameObjects();
                foreach (var root in roots)
                    CollectObjectsRecursive(root.transform, allObjects, maxObjects * 2);
                var counted = 0;

                foreach (var go in allObjects)
                {
                    if (counted >= maxObjects) break;

                    // Filters
                    if (!string.IsNullOrEmpty(tagFilter) && !go.CompareTag(tagFilter))
                        continue;

                    if (layerMask >= 0 && go.layer != layerMask)
                        continue;

                    if (!string.IsNullOrEmpty(typeFilter))
                    {
                        var comp = go.GetComponent(typeFilter);
                        if (comp == null) continue;
                    }

                    // Distance check
                    var pos = go.transform.position;
                    var distSq = (pos - origin).sqrMagnitude;
                    if (distSq > radiusSq) continue;

                    // M6: Removed heuristic Renderer/Collider/childCount filter — existing
                    // typeFilter/tag/layer params already handle filtering.

                    var dist = Mathf.Sqrt(distSq);
                    var dir = dist > 0.001f ? (pos - origin) / dist : Vector3.zero;

                    // Get component names (limited)
                    var comps = go.GetComponents<Component>();
                    var compNames = new List<string>();
                    foreach (var c in comps)
                    {
                        if (c != null)
                            compNames.Add(c.GetType().Name);
                        if (compNames.Count >= 5) break; // limit
                    }

                    results.Add(JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(go.name)),
                        ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                        ("components", JsonHelper.BuildJsonArray(
                            compNames.Select(JsonHelper.EscapeString).ToArray())),
                        ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                        ("distance", dist.ToString("F2", CultureInfo.InvariantCulture)),
                        ("direction", JsonHelper.FloatArrayJson(new[] { dir.x, dir.y, dir.z })),
                        ("tag", JsonHelper.EscapeString(go.tag)),
                        ("layer", go.layer.ToString(CultureInfo.InvariantCulture))
                    ));
                    counted++;
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("origin", JsonHelper.FloatArrayJson(new[] { origin.x, origin.y, origin.z })),
                    ("radius", radius.ToString("F1", CultureInfo.InvariantCulture)),
                    ("count", counted.ToString(CultureInfo.InvariantCulture)),
                    ("objects", JsonHelper.BuildJsonArray(results.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetSpatial failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.set_input_field_text
        // ══════════════════════════════════════════════════════════════

        private static float GetFloat(Dictionary<string, object> dict, string key, float defaultValue = 0f)
        {
            if (!dict.TryGetValue(key, out var v)) return defaultValue;
            // Reject NaN/Infinity - they would corrupt game state / scenes (P7).
            try { return HandlerUtils.ToFiniteSingle(v, key, CultureInfo.InvariantCulture); }
            catch (System.Exception) { return defaultValue; }
        }

        /// <summary>
        /// Recursively collects GameObjects from the transform hierarchy.
        /// Avoids allocating a giant array from FindObjectsByType and allows
        /// early termination when maxCount is reached.
        /// </summary>
        private static void CollectObjectsRecursive(Transform t, List<GameObject> results, int maxCount)
        {
            if (results.Count >= maxCount) return;
            results.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++)
                CollectObjectsRecursive(t.GetChild(i), results, maxCount);
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_player
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_PLAYER,
            "Get player GameObject's complete state in one call — position, rotation, velocity, animator state, and custom component properties. " +
            "Params: tag (string, optional, default 'Player'), path (string, optional — overrides tag), " +
            "components (JSON array of {'type':'TypeName'} objects, optional — component type names to read properties from). " +
            "Returns: name, instanceId, path, position, rotation, velocity, animatorState, components.",
            Platform = MCPToolPlatforms.All)]
        [MCPParam("tag", Type = "string", Description = "Player tag (default 'Player')")]
        [MCPParam("path", Type = "string", Description = "Player path (overrides tag)")]
        [MCPParam("components", Type = "array", Description = "Component types to read properties from")]
        public static string GetPlayer(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var tag = GetString(args, "tag", "Player");
                var path = GetString(args, "path", "");

                // Find the player GameObject
                GameObject player = null;
                if (!string.IsNullOrEmpty(path))
                {
                    player = GameObject.Find(path);
                }
                else
                {
                    var players = GameObject.FindGameObjectsWithTag(tag);
                    if (players.Length > 0)
                        player = players[0];
                }

                if (player == null)
                    return ErrorJson("Player not found");

                var pos = player.transform.position;
                var rot = player.transform.eulerAngles;

                // Build result fields
                var fields = new List<(string key, string valueJson)>
                {
                    ("name", JsonHelper.EscapeString(player.name)),
                    ("instanceId", player.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(player))),
                    ("active", JsonHelper.BoolJson(player.activeInHierarchy)),
                    ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                    ("rotation", JsonHelper.FloatArrayJson(new[] { rot.x, rot.y, rot.z })),
                };

                // Velocity from Rigidbody / CharacterController / NavMeshAgent
                var velocity = Vector3.zero;
                var rb = player.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    velocity = rb.velocity;
                }
                else
                {
                    var cc = player.GetComponent<CharacterController>();
                    if (cc != null)
                    {
                        velocity = cc.velocity;
                    }
                    else
                    {
                        var navAgent = player.GetComponent("NavMeshAgent");
                        if (navAgent != null)
                        {
                            var velProp = navAgent.GetType().GetProperty("velocity");
                            if (velProp != null)
                                velocity = (Vector3)(velProp.GetValue(navAgent) ?? Vector3.zero);
                        }
                    }
                }
                fields.Add(("velocity", JsonHelper.FloatArrayJson(new[] { velocity.x, velocity.y, velocity.z })));

                // Animator state
                var animator = player.GetComponent<Animator>();
                string animatorJson = "null";
                if (animator != null)
                {
                    var animParams = new List<string>();
                    var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                    try
                    {
                        foreach (var param in animator.parameters)
                        {
                            string val;
                            switch (param.type)
                            {
                                case AnimatorControllerParameterType.Float:
                                    val = animator.GetFloat(param.name).ToString("G", CultureInfo.InvariantCulture);
                                    break;
                                case AnimatorControllerParameterType.Int:
                                    val = animator.GetInteger(param.name).ToString(CultureInfo.InvariantCulture);
                                    break;
                                case AnimatorControllerParameterType.Bool:
                                    val = JsonHelper.BoolJson(animator.GetBool(param.name));
                                    break;
                                case AnimatorControllerParameterType.Trigger:
                                    val = "\"trigger\"";
                                    break;
                                default:
                                    val = "\"unknown\"";
                                    break;
                            }
                            animParams.Add(JsonHelper.BuildJsonObject(
                                ("name", JsonHelper.EscapeString(param.name)),
                                ("type", JsonHelper.EscapeString(param.type.ToString())),
                                ("value", val)
                            ));
                        }
                    }
                    catch
                    {
                        // Animator may be in invalid state
                    }

                    animatorJson = JsonHelper.BuildJsonObject(
                        ("stateHash", stateInfo.fullPathHash.ToString(CultureInfo.InvariantCulture)),
                        ("normalizedTime", stateInfo.normalizedTime.ToString("G", CultureInfo.InvariantCulture)),
                        ("speed", animator.speed.ToString("G", CultureInfo.InvariantCulture)),
                        ("parameters", JsonHelper.BuildJsonArray(animParams.ToArray()))
                    );
                }
                fields.Add(("animatorState", animatorJson));

                // Local scale
                var scale = player.transform.localScale;
                fields.Add(("localScale", JsonHelper.FloatArrayJson(new[] { scale.x, scale.y, scale.z })));

                // Tags
                fields.Add(("tag", JsonHelper.EscapeString(player.tag)));
                fields.Add(("layer", player.layer.ToString(CultureInfo.InvariantCulture)));

                // Custom component properties
                string componentsJson = "[]";
                if (args.TryGetValue("components", out var compsObj) && compsObj is string compsJson)
                {
                    try
                    {
                        var typeNames = ParseComponentTypeList(compsJson);
                        var compResults = new List<string>();
                        foreach (var typeName in typeNames)
                        {
                            var comp = player.GetComponent(typeName);
                            if (comp == null)
                                continue;

                            var propList = new List<string>();
                            var props = comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                            foreach (var prop in props)
                            {
                                try
                                {
                                    if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                                    {
                                        var val = prop.GetValue(comp);
                                        propList.Add(JsonHelper.BuildJsonObject(
                                            ("name", JsonHelper.EscapeString(prop.Name)),
                                            ("value", JsonHelper.EscapeString(val?.ToString() ?? "null")),
                                            ("type", JsonHelper.EscapeString(prop.PropertyType.Name))
                                        ));
                                    }
                                }
                                catch { /* skip problematic properties */ }
                            }

                            compResults.Add(JsonHelper.BuildJsonObject(
                                ("type", JsonHelper.EscapeString(typeName)),
                                ("properties", JsonHelper.BuildJsonArray(propList.ToArray()))
                            ));
                        }
                        componentsJson = JsonHelper.BuildJsonArray(compResults.ToArray());
                    }
                    catch (Exception ex)
                    {
                        componentsJson = $"\"error: {JsonHelper.EscapeString(ex.Message)}\"";
                    }
                }
                fields.Add(("components", componentsJson));

                return JsonHelper.BuildJsonObject(fields.ToArray());
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetPlayer failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_entities
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_ENTITIES,
            "Get all entities with AI/Health/CharacterController in range — batch query to avoid N+1 round trips. " +
            "Params: radius (float, optional, default 50), origin (JSON array [x,y,z], optional — default player position), " +
            "maxCount (int, optional, default 30), " +
            "componentTypes (JSON array of type name strings, optional — default ['Health','AIController','CharacterController','Enemy','NPC']). " +
            "Returns: count, entities[] with name/instanceId/path/position/distance/direction/type/health/isAlive/tags/layer.",
            Platform = MCPToolPlatforms.All)]
        [MCPParam("radius", Type = "number", Description = "Search radius (default 50)")]
        [MCPParam("origin", Type = "object", Description = "[x,y,z] world position (object form)")]
        [MCPParam("maxCount", Type = "integer", Description = "Max entities (default 30)")]
        [MCPParam("componentTypes", Type = "array", Description = "Component type names to match")]
        public static string GetEntities(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var radius = GetFloat(args, "radius", 50f);
                var maxCount = (int)(args.TryGetValue("maxCount", out var mc)
                    ? Convert.ToInt32(mc, CultureInfo.InvariantCulture) : 30);

                // Parse component type filter list
                var defaultTypes = new[] { "Health", "AIController", "CharacterController", "Enemy", "NPC" };
                string[] componentTypes;
                if (args.TryGetValue("componentTypes", out var ctObj) && ctObj is string ctJson)
                {
                    componentTypes = ParseComponentTypeList(ctJson);
                    if (componentTypes.Length == 0)
                        componentTypes = defaultTypes;
                }
                else
                {
                    componentTypes = defaultTypes;
                }

                // Resolve origin
                Vector3 origin;
                if (args.TryGetValue("origin", out var originObj) && originObj is string originJson)
                {
                    var parts = ParseJsonObject(originJson);
                    float ox = 0, oy = 0, oz = 0;
                    if (parts.TryGetValue("0", out var xv) || parts.TryGetValue("x", out xv))
                        ox = Convert.ToSingle(xv, CultureInfo.InvariantCulture);
                    if (parts.TryGetValue("1", out var yv) || parts.TryGetValue("y", out yv))
                        oy = Convert.ToSingle(yv, CultureInfo.InvariantCulture);
                    if (parts.TryGetValue("2", out var zv) || parts.TryGetValue("z", out zv))
                        oz = Convert.ToSingle(zv, CultureInfo.InvariantCulture);
                    origin = new Vector3(ox, oy, oz);
                }
                else
                {
                    // Default to player position
                    var playerObj = GameObject.FindGameObjectWithTag("Player");
                    origin = playerObj != null ? playerObj.transform.position : Vector3.zero;
                }

                var radiusSq = radius * radius;

                // Collect all GameObjects via root traversal (same as GetSpatial)
                var allObjects = new List<GameObject>(512);
                var roots = SceneManager.GetActiveScene().GetRootGameObjects();
                foreach (var root in roots)
                    CollectObjectsRecursive(root.transform, allObjects, maxCount * 5);

                // Secondary filter: must have at least one of the component types
                var matchedObjects = new List<(GameObject go, float dist, string primaryType)>();
                foreach (var go in allObjects)
                {
                    // Distance check
                    var pos = go.transform.position;
                    var distSq = (pos - origin).sqrMagnitude;
                    if (distSq > radiusSq) continue;

                    // Component type check — find first matching type
                    string matchedType = null;
                    foreach (var ct in componentTypes)
                    {
                        var comp = go.GetComponent(ct);
                        if (comp != null)
                        {
                            matchedType = ct;
                            break;
                        }
                    }
                    if (matchedType == null) continue;

                    var dist = Mathf.Sqrt(distSq);
                    matchedObjects.Add((go, dist, matchedType));
                }

                // Sort by distance, cap at maxCount
                matchedObjects.Sort((a, b) => a.dist.CompareTo(b.dist));
                if (matchedObjects.Count > maxCount)
                    matchedObjects = matchedObjects.GetRange(0, maxCount);

                // Build entity JSON entries
                var entities = new List<string>();
                foreach (var (go, dist, primaryType) in matchedObjects)
                {
                    var pos = go.transform.position;
                    var dir = dist > 0.001f ? (pos - origin) / dist : Vector3.zero;

                    // Try to read health value via reflection
                    string healthStr = "null";
                    string isAliveStr = "null";
                    try
                    {
                        // Check for health property
                        var healthComp = go.GetComponent("Health") ?? go.GetComponent(primaryType);
                        if (healthComp != null)
                        {
                            var healthProps = new[] { "health", "hp", "currentHealth", "life" };
                            foreach (var hp in healthProps)
                            {
                                var prop = healthComp.GetType().GetProperty(hp, BindingFlags.Public | BindingFlags.Instance);
                                if (prop != null && prop.CanRead)
                                {
                                    var val = prop.GetValue(healthComp);
                                    healthStr = val?.ToString() ?? "null";
                                    break;
                                }
                                // Try field
                                var field = healthComp.GetType().GetField(hp, BindingFlags.Public | BindingFlags.Instance);
                                if (field != null)
                                {
                                    var val = field.GetValue(healthComp);
                                    healthStr = val?.ToString() ?? "null";
                                    break;
                                }
                            }

                            // Check for isAlive/isDead property
                            var aliveProps = new[] { "isAlive", "IsAlive", "isDead", "IsDead" };
                            foreach (var ap in aliveProps)
                            {
                                var prop = healthComp.GetType().GetProperty(ap, BindingFlags.Public | BindingFlags.Instance);
                                if (prop != null && prop.CanRead)
                                {
                                    var val = prop.GetValue(healthComp);
                                    isAliveStr = val?.ToString() ?? "null";
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Health read failures are non-fatal
                    }

                    entities.Add(JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(go.name)),
                        ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                        ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                        ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                        ("distance", dist.ToString("F2", CultureInfo.InvariantCulture)),
                        ("direction", JsonHelper.FloatArrayJson(new[] { dir.x, dir.y, dir.z })),
                        ("type", JsonHelper.EscapeString(primaryType)),
                        ("health", healthStr),
                        ("isAlive", isAliveStr),
                        ("tags", JsonHelper.EscapeString(go.tag)),
                        ("layer", go.layer.ToString(CultureInfo.InvariantCulture))
                    ));
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("origin", JsonHelper.FloatArrayJson(new[] { origin.x, origin.y, origin.z })),
                    ("radius", radius.ToString("F1", CultureInfo.InvariantCulture)),
                    ("count", entities.Count.ToString(CultureInfo.InvariantCulture)),
                    ("entities", JsonHelper.BuildJsonArray(entities.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetEntities failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_animator_state
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_ANIMATOR_STATE,
            "Get Animator current state — stateHash, normalizedTime, speed, all parameters. " +
            "Params: instanceId (int) or path (string) to locate the object, " +
            "layer (int, optional, default 0), component (string, optional, default 'Animator'). " +
            "Returns: layer, stateHash, normalizedTime, speed, isTransitioning, parameters[{name,type,value}].",
            Platform = MCPToolPlatforms.All)]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId")]
        [MCPParam("path", Type = "string", Description = "Object transform path")]
        [MCPParam("layer", Type = "integer", Description = "Animator layer (default 0)")]
        [MCPParam("component", Type = "string", Description = "Animator component type (default 'Animator')")]
        public static string GetAnimatorState(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var layer = (int)(args.TryGetValue("layer", out var layerObj)
                    ? Convert.ToInt32(layerObj, CultureInfo.InvariantCulture) : 0);
                var componentType = GetString(args, "component", "Animator");

                // Resolve target using SceneHandler.ResolveTarget (same pattern)
                var go = SceneHandler.ResolveTarget(args);
                if (go == null)
                    return ErrorJson("GameObject not found — provide valid 'instanceId' or 'path'");

                // Get the animator component
                var animator = go.GetComponent(componentType) as Animator;
                if (animator == null)
                {
                    // Try as Animator directly (componentType may be "Animator")
                    animator = go.GetComponent<Animator>();
                    if (animator == null)
                        return ErrorJson($"Animator component not found on '{GetObjectPath(go)}'");
                }

                var stateInfo = animator.GetCurrentAnimatorStateInfo(layer);
                var isTransitioning = animator.IsInTransition(layer);

                // Read all parameters
                var paramList = new List<string>();
                foreach (var param in animator.parameters)
                {
                    string val;
                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            val = animator.GetFloat(param.name).ToString("G", CultureInfo.InvariantCulture);
                            break;
                        case AnimatorControllerParameterType.Int:
                            val = animator.GetInteger(param.name).ToString(CultureInfo.InvariantCulture);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            val = JsonHelper.BoolJson(animator.GetBool(param.name));
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            val = "\"trigger\"";
                            break;
                        default:
                            val = "\"unknown\"";
                            break;
                    }
                    paramList.Add(JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(param.name)),
                        ("type", JsonHelper.EscapeString(param.type.ToString())),
                        ("value", val)
                    ));
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("layer", layer.ToString(CultureInfo.InvariantCulture)),
                    ("stateHash", stateInfo.fullPathHash.ToString(CultureInfo.InvariantCulture)),
                    ("normalizedTime", stateInfo.normalizedTime.ToString("G", CultureInfo.InvariantCulture)),
                    ("speed", animator.speed.ToString("G", CultureInfo.InvariantCulture)),
                    ("isTransitioning", JsonHelper.BoolJson(isTransitioning)),
                    ("parameterCount", animator.parameters.Length.ToString(CultureInfo.InvariantCulture)),
                    ("parameters", JsonHelper.BuildJsonArray(paramList.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetAnimatorState failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_time_scale
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_TIME_SCALE,
            "Get current Time.timeScale and fixedDeltaTime. Returns timeScale (1.0=normal, 0=paused, 2.0=2x speed) and fixedDeltaTime.",
            Platform = MCPToolPlatforms.All)]
        public static string GetTimeScale(string paramsJson)
        {
            try
            {
                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("timeScale", Time.timeScale.ToString("G", CultureInfo.InvariantCulture)),
                    ("fixedDeltaTime", Time.fixedDeltaTime.ToString("G", CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetTimeScale failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.set_time_scale
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_SET_TIME_SCALE,
            "Set Time.timeScale (0=paused, 1=normal, 2=2x speed). Also sets fixedDeltaTime = 0.02 * value to keep physics stable. " +
            "Params: value (float, required). " +
            "Returns: newTimeScale, newFixedDeltaTime.",
            Platform = MCPToolPlatforms.All)]
        [MCPParam("value", Type = "number", Description = "Time scale 0-10 (default 1.0)")]
        public static string SetTimeScale(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var value = GetFloat(args, "value", 1.0f);
                value = Mathf.Clamp(value, 0f, 10f);
                Time.timeScale = value;
                Time.fixedDeltaTime = 0.02f * value;

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("timeScale", Time.timeScale.ToString("G", CultureInfo.InvariantCulture)),
                    ("fixedDeltaTime", Time.fixedDeltaTime.ToString("G", CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"SetTimeScale failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Internal helpers for new tools
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Parse a JSON array into an array of strings.
        /// Supports both ["a","b"] and [{"type":"a"},{"type":"b"}] formats.
        /// </summary>
        private static string[] ParseComponentTypeList(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Trim() == "[]")
                return Array.Empty<string>();

            var trimmed = json.Trim();
            if (trimmed.Contains('{'))
            {
                // Array of objects with "type" field: [{"type":"Health"},{"type":"Animator"}]
                var items = ParseJsonArrayOfObjects(trimmed);
                return items
                    .Select(item => GetString(item, "type", ""))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }
            else
            {
                // Array of plain strings: ["Health","Animator"]
                if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
                    return Array.Empty<string>();

                var inner = trimmed.Substring(1, trimmed.Length - 2);
                var parts = inner.Split(',');
                var result = new List<string>();
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (p.StartsWith("\"") && p.EndsWith("\""))
                        p = p.Substring(1, p.Length - 2);
                    if (!string.IsNullOrEmpty(p))
                        result.Add(p);
                }
                return result.ToArray();
            }
        }
    }
}