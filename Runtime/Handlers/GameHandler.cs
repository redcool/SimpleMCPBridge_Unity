using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// High-level game interaction tools that combine multiple low-level operations
    /// into single AI-friendly calls.
    ///
    /// Tools:
    ///   - ui.get_texts    — read all on-screen UI text (memory-based, no OCR)
    ///   - ui.find         — find interactive UI elements with screen positions
    ///   - input.action    — unified input: keys + mouse + axes + scroll
    ///   - game.get_state  — composite scene/time/UI/player perception
    ///   - game.wait       — async timing (seconds, scene load, UI appear)
    ///   - game.wait_check — poll wait completion status
    /// </summary>
    [MCPToolClass]
    public class GameHandler
    {
        // ── Pending wait state ──
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

        // ══════════════════════════════════════════════════════════════
        //  ui.get_texts
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_GET_TEXTS,
            "Read all on-screen UI text from memory (no OCR, zero token cost). " +
            "Scans active Canvas objects for both Text and TextMeshProUGUI components. " +
            "Returns each text element with: text content, type, transform path, " +
            "normalized screen rect [xMin,yMin,xMax,yMax], center position, font size, " +
            "and color. Also returns screen dimensions for coordinate reference. " +
            "Ideal for reading menus, HUD values, dialogue, labels without screenshots.")]
        public static string GetUITexts(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var textJsons = UIAnalysisTools.ScanAllTexts();
                var filter = GetString(args, "contains", "");

                // Optional filter
                if (!string.IsNullOrEmpty(filter))
                {
                    textJsons = textJsons.Where(j =>
                        j.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", textJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("screenWidth", Screen.width.ToString(CultureInfo.InvariantCulture)),
                    ("screenHeight", Screen.height.ToString(CultureInfo.InvariantCulture)),
                    ("texts", JsonHelper.BuildJsonArray(textJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI text scan failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.find
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_FIND,
            "Find interactive UI elements (Button, Toggle, Slider, Dropdown, InputField) " +
            "with their screen positions and state. " +
            "Each element returns: type, path, label/text, interactable, " +
            "normalized screen rect [xMin,yMin,xMax,yMax], and center position. " +
            "Optional filter: 'type' (Button/Toggle/Slider/Dropdown/InputField), " +
            "'contains' (label text contains), 'interactable' (bool). " +
            "Also returns screen dimensions. " +
            "Use the 'center' position to target clicks with input.click_screen.")]
        public static string FindUI(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var elemJsons = UIAnalysisTools.ScanInteractiveUI();
                var typeFilter = GetString(args, "type", "");
                var textFilter = GetString(args, "contains", "");

                if (args.TryGetValue("interactable", out var iaObj) && iaObj is bool interactableFilter)
                {
                    elemJsons = elemJsons.Where(j =>
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(j, "\"interactable\":(true|false)");
                        return match.Success && match.Groups[1].Value == (interactableFilter ? "true" : "false");
                    }).ToList();
                }

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    elemJsons = elemJsons.Where(j =>
                        j.IndexOf($"\"type\":\"{typeFilter}\"", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                if (!string.IsNullOrEmpty(textFilter))
                {
                    elemJsons = elemJsons.Where(j =>
                    {
                        var labelMatch = System.Text.RegularExpressions.Regex.Match(j, "\"label\":\"([^\"]*)\"");
                        var textMatch = System.Text.RegularExpressions.Regex.Match(j, "\"text\":\"([^\"]*)\"");
                        var label = labelMatch.Success ? labelMatch.Groups[1].Value : "";
                        var text = textMatch.Success ? textMatch.Groups[1].Value : "";
                        return label.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    }).ToList();
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", elemJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("screenWidth", Screen.width.ToString(CultureInfo.InvariantCulture)),
                    ("screenHeight", Screen.height.ToString(CultureInfo.InvariantCulture)),
                    ("elements", JsonHelper.BuildJsonArray(elemJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI find failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  input.action
        // ══════════════════════════════════════════════════════════════

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
                    catch { }
                }

                // 7. Active axes (Input System)
                string axesJson = "[]";
#if UNITY_INPUT_SYSTEM
                try
                {
                    var axisInfo = new List<string>();
                    if (UnityEngine.InputSystem.InputSystem.actions != null)
                    {
                        foreach (var map in UnityEngine.InputSystem.InputSystem.actions.actionMaps)
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
                catch { }
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
                    ("activeAxes", axesJson),
                    ("uiTextCount", includeUI ? (uiTexts.Count > 0 ? uiTexts.Count.ToString(CultureInfo.InvariantCulture) : "0") : "skipped"),
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

        // ══════════════════════════════════════════════════════════════
        //  Internal helpers
        // ══════════════════════════════════════════════════════════════

        private static float GetRequiredFloat(Dictionary<string, object> dict, string key)
        {
            return Convert.ToSingle(GetRequiredString(dict, key), CultureInfo.InvariantCulture);
        }

        private static bool EvaluateCondition(string value, WaitState wait)
        {
            // Try numeric comparison first
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

            // String comparison fallback
            return wait.Operator switch
            {
                "==" => string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(value, wait.TargetValue, StringComparison.OrdinalIgnoreCase)
            };
        }

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
    }
}

// touch 14:48:43

// test_request_compile 14:54:52
