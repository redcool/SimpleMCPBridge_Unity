using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
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

        // ── Watch / Delta state (continuous perception) ──
        private static readonly Dictionary<string, Dictionary<string, object>> _watchConfigs = new();
        private static readonly Dictionary<string, string> _watchBaselines = new();
        private const int MaxWatchEntries = 100;

        // Cached reflection targets per watch signal, keyed by "path|component|property".
        // Avoids GameObject.Find (full linear scene scan) + GetProperty/GetField on every
        // tick — the resolved Component + Member are reused until the reference is
        // null/destroyed (Unity's overloaded == treats destroyed objects as null),
        // at which point the next read re-resolves from the scene.
        private static readonly Dictionary<string, WatchPropertyCache> _watchPropertyCache = new();
        private const int MaxWatchPropertyCacheEntries = MaxWatchEntries * 2;

        private class WatchPropertyCache
        {
            public Component Component;
            public MemberInfo Member; // PropertyInfo or FieldInfo
        }

        // Continuous monitoring: Unity side polls watched signals every N frames and
        // accumulates changes into a cache. game.get_delta then just reads (and clears)
        // the cache instead of doing reflection reads on every call.
        private static readonly List<string> _watchChanges = new();
        private const int WatchTickIntervalFrames = 10; // ~167ms at 60fps
        private static int _watchFrameCounter;

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
        [MCPParam("contains", Type = "string", Description = "Text substring filter (optional)")]
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
        [MCPParam("type", Type = "string", Description = "Element type filter, e.g. 'Button'")]
        [MCPParam("contains", Type = "string", Description = "Label/text substring filter")]
        [MCPParam("interactable", Type = "boolean", Description = "Filter by interactable state")]
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

        // ══════════════════════════════════════════════════════════════
        //  game.watch
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_WATCH,
            "Register signals to watch for value changes. " +
            "Params: 'signals' (array, required) — each signal has: " +
            "'id' (unique name), 'type' ('property'), " +
            "'path' (GameObject path), 'component' (component type name), " +
            "'property' (field/property name). " +
            "Returns current values as baselines. " +
            "Use game.get_delta to poll for changes since last check. " +
            "Example: {\"signals\":[{\"id\":\"hp\",\"type\":\"property\"," +
            "\"path\":\"Player\",\"component\":\"Health\",\"property\":\"currentHP\"}]}")]
        [MCPParam("signals", Type = "array", Required = true, Description = "Signals [{id, type, path, component, property}]")]
        public static string Watch(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                if (!args.TryGetValue("signals", out var signalsObj) || signalsObj is not string signalsJson)
                    return ErrorJson("Missing required parameter: 'signals' (array)");

                var signals = ParseJsonArrayOfObjects(signalsJson);
                if (signals.Count == 0)
                    return ErrorJson("'signals' array is empty");

                var registered = new List<string>();
                var errors = new List<string>();

                foreach (var sig in signals)
                {
                    var id = GetRequiredString(sig, "id");
                    var type = GetString(sig, "type", "property");

                    var config = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["type"] = type,
                    };

                    string baseline = "";
                    string errMsg = null;

                    switch (type)
                    {
                        case "property":
                        {
                            var path = GetRequiredString(sig, "path");
                            var comp = GetRequiredString(sig, "component");
                            var prop = GetRequiredString(sig, "property");
                            config["path"] = path;
                            config["component"] = comp;
                            config["property"] = prop;
                            baseline = ReadPropertyValue(path, comp, prop, out errMsg);
                            break;
                        }
                        default:
                            errMsg = $"unknown type '{type}' (supported: property)";
                            break;
                    }

                    if (errMsg != null)
                    {
                        errors.Add($"{id}: {errMsg}");
                    }
                    else
                    {
                        _watchConfigs[id] = config;
                        _watchBaselines[id] = baseline;
                        registered.Add(id);

                        // FIFO eviction: remove oldest entries when over limit
                        while (_watchConfigs.Count > MaxWatchEntries)
                        {
                            var oldestKey = _watchConfigs.Keys.First();
                            _watchConfigs.Remove(oldestKey);
                            _watchBaselines.Remove(oldestKey);
                        }
                        // Bound the reflection cache — stale entries from removed watches
                        // are cleared wholesale once it outgrows the live set (re-resolve
                        // on next tick is cheap and self-healing).
                        if (_watchPropertyCache.Count > MaxWatchPropertyCacheEntries)
                            _watchPropertyCache.Clear();
                    }
                }

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("registered", JsonHelper.BuildJsonArray(
                        registered.Select(JsonHelper.EscapeString).ToArray())),
                    ("count", registered.Count.ToString(CultureInfo.InvariantCulture)),
                    ("errors", JsonHelper.BuildJsonArray(
                        errors.Select(JsonHelper.EscapeString).ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"Watch failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  game.get_delta
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.GAME_GET_DELTA,
            "Get value changes since last game.get_delta call. " +
            "Only returns signals whose value changed. " +
            "Each change includes: id, value (new), old (previous). " +
            "Use game.watch to register signals first. " +
            "Changes are detected continuously by the bridge (every ~167ms / 10 frames) " +
            "and returned from cache — this call never blocks on reflection reads. " +
            "Returns empty when nothing changed.")]
        public static string GetDelta(string paramsJson)
        {
            try
            {
                if (_watchChanges.Count == 0)
                {
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("changes", "[]"),
                        ("count", "0")
                    );
                }

                // Read and clear the accumulated changes (cache semantics: each change
                // is delivered exactly once to the next get_delta caller).
                var changes = new List<string>(_watchChanges);
                _watchChanges.Clear();

                var hasErrors = changes.Any(c => c.Contains("\"error\""));
                return JsonHelper.BuildJsonObject(
                    ("success", hasErrors ? "false" : "true"),
                    ("changes", JsonHelper.BuildJsonArray(changes.ToArray())),
                    ("count", changes.Count.ToString(CultureInfo.InvariantCulture))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"GetDelta failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Internal helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Continuous monitoring tick — called from the main-thread update loop
        /// (MCPBridge.Update / InstanceUpdate). Polls all watched signals every
        /// WatchTickIntervalFrames frames and accumulates value changes into
        /// _watchChanges. game.get_delta reads and clears this cache, so agents
        /// never miss short-lived events that occur between their own polls.
        /// </summary>
        public static void TickWatch()
        {
            if (_watchConfigs.Count == 0) return;

            if (++_watchFrameCounter < WatchTickIntervalFrames) return;
            _watchFrameCounter = 0;

            var staleKeys = new List<string>();

            foreach (var kvp in _watchConfigs)
            {
                var id = kvp.Key;
                var config = kvp.Value;
                var type = config["type"] as string;
                string current;
                string errMsg = null;

                switch (type)
                {
                    case "property":
                    {
                        current = ReadPropertyValue(
                            config["path"] as string,
                            config["component"] as string,
                            config["property"] as string,
                            out errMsg);
                        break;
                    }
                    default:
                        current = "";
                        errMsg = $"unknown watch type '{type}'";
                        break;
                }

                if (errMsg != null)
                {
                    _watchChanges.Add(JsonHelper.BuildJsonObject(
                        ("id", JsonHelper.EscapeString(id)),
                        ("error", JsonHelper.EscapeString(errMsg))
                    ));
                    // Mark stale entries where the GameObject no longer exists
                    if (errMsg.Contains("not found"))
                    {
                        staleKeys.Add(id);
                    }
                    continue;
                }

                var old = _watchBaselines.GetValueOrDefault(id, "");
                if (current != old)
                {
                    _watchChanges.Add(JsonHelper.BuildJsonObject(
                        ("id", JsonHelper.EscapeString(id)),
                        ("value", JsonHelper.EscapeString(current)),
                        ("old", JsonHelper.EscapeString(old))
                    ));
                    _watchBaselines[id] = current;
                }
            }

            // Clean up stale entries (GameObject no longer exists)
            foreach (var key in staleKeys)
            {
                _watchConfigs.Remove(key);
                _watchBaselines.Remove(key);
            }
            // Bound the reflection cache alongside stale/evicted watches.
            if (_watchPropertyCache.Count > MaxWatchPropertyCacheEntries)
                _watchPropertyCache.Clear();
        }

        /// <summary>
        /// Read a component property/field value by path. Returns "" on error with message in out param.
        /// Caches the resolved Component + MemberInfo per (path|component|property) so the
        /// per-tick poll skips GameObject.Find (full scene scan) + reflection lookup. The cache
        /// is re-resolved ONLY when the cached component is null/destroyed (Unity fake-null);
        /// destroyed objects are handled gracefully — re-resolve once, report "not found"
        /// (which marks the watch stale upstream), never throw.
        /// </summary>
        private static string ReadPropertyValue(string objectPath, string componentType,
            string propertyName, out string error)
        {
            error = null;
            try
            {
                var cacheKey = objectPath + "|" + componentType + "|" + propertyName;
                if (!_watchPropertyCache.TryGetValue(cacheKey, out var cache))
                {
                    cache = new WatchPropertyCache();
                    _watchPropertyCache[cacheKey] = cache;
                }

                // Re-resolve only when the cached reference is null or destroyed.
                if (cache.Component == null)
                {
                    var go = GameObject.Find(objectPath);
                    if (go == null) { error = $"Object '{objectPath}' not found"; return ""; }

                    cache.Component = go.GetComponent(componentType);
                    if (cache.Component == null) { error = $"Component '{componentType}' not found on '{objectPath}'"; return ""; }

                    // Re-resolve the member too — the object at this path may have
                    // respawned as a different type.
                    cache.Member = ResolvePropertyOrField(cache.Component.GetType(), propertyName);
                }

                if (cache.Member == null)
                {
                    error = $"Property/field '{propertyName}' not found on '{componentType}'";
                    return "";
                }

                // Try property first, then field
                if (cache.Member is PropertyInfo prop)
                    return prop.GetValue(cache.Component)?.ToString() ?? "null";
                return ((FieldInfo)cache.Member).GetValue(cache.Component)?.ToString() ?? "null";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        private static MemberInfo ResolvePropertyOrField(Type type, string name)
        {
            var prop = type.GetProperty(name);
            if (prop != null) return prop;
            return type.GetField(name);
        }

        // ══════════════════════════════════════════════════════════════
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

        [MCPTool(MCPMethodConst.UI_SET_INPUT_FIELD_TEXT,
            "Set the text of an InputField (legacy or TMP). " +
            "Params: 'path' (string) OR 'instanceId' (int) to identify the input, " +
            "'text' (string, required) — the new text to set. " +
            "Fires onEndEdit after setting the value. " +
            "Returns: success, path, text.")]
        [MCPParam("path", Type = "string", Description = "InputField transform path")]
        [MCPParam("instanceId", Type = "integer", Description = "InputField instanceId")]
        [MCPParam("text", Type = "string", Required = true, Description = "New text to set")]
        public static string UISetInputFieldText(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var text = GetRequiredString(args, "text");
                var go = ResolveUITarget(args);
                if (go == null)
                    return ErrorJson("InputField not found — provide valid 'path' or 'instanceId'");

                // Try legacy InputField first
                var inputField = go.GetComponent<InputField>();
                if (inputField != null)
                {
                    inputField.text = text;
                    inputField.onEndEdit.Invoke(text);
                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                        ("text", JsonHelper.EscapeString(text)),
                        ("type", "\"InputField\"")
                    );
                }

                // Try TMP_InputField via reflection
                var tmpIfType = GetTMPInputFieldType();
                if (tmpIfType != null)
                {
                    var tmpComp = go.GetComponent(tmpIfType);
                    if (tmpComp != null)
                    {
                        var textProp = tmpIfType.GetProperty("text");
                        textProp?.SetValue(tmpComp, text);

                        var onEndEditProp = tmpIfType.GetProperty("onEndEdit");
                        var onEndEdit = onEndEditProp?.GetValue(tmpComp);
                        if (onEndEdit != null)
                        {
                            var invokeMethod = onEndEdit.GetType().GetMethod("Invoke", new[] { typeof(string) });
                            invokeMethod?.Invoke(onEndEdit, new object[] { text });
                        }

                        return JsonHelper.BuildJsonObject(
                            ("success", "true"),
                            ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                            ("text", JsonHelper.EscapeString(text)),
                            ("type", "\"TMP_InputField\"")
                        );
                    }
                }

                return ErrorJson("GameObject has no InputField or TMP_InputField component");
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.set_input_field_text failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.set_toggle
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_SET_TOGGLE,
            "Set the state of a Toggle (UnityEngine.UI.Toggle). " +
            "Params: 'path' OR 'instanceId' to identify the toggle, " +
            "'value' (bool, required) — the new isOn state. " +
            "Fires onValueChanged after setting. " +
            "Returns: success, path, isOn.")]
        [MCPParam("path", Type = "string", Description = "Toggle transform path")]
        [MCPParam("instanceId", Type = "integer", Description = "Toggle instanceId")]
        [MCPParam("value", Type = "boolean", Required = true, Description = "New isOn state")]
        public static string UISetToggle(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var value = GetRequiredBool(args, "value");
                var go = ResolveUITarget(args);
                if (go == null)
                    return ErrorJson("Toggle not found — provide valid 'path' or 'instanceId'");

                var toggle = go.GetComponent<Toggle>();
                if (toggle == null)
                    return ErrorJson("GameObject has no Toggle component");

                toggle.isOn = value;
                // onValueChanged fires automatically when isOn is set
                if (toggle.onValueChanged != null)
                    toggle.onValueChanged.Invoke(value);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("isOn", JsonHelper.BoolJson(toggle.isOn))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.set_toggle failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.set_slider
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_SET_SLIDER,
            "Set the value of a Slider (UnityEngine.UI.Slider). " +
            "Params: 'path' OR 'instanceId', " +
            "'value' (float, required) — the value to set. " +
            "'normalized' (bool, optional, default true) — if true, value is 0-1 " +
            "and gets mapped to the slider's [minValue, maxValue] range. " +
            "If false, value is used directly (clamped to slider range). " +
            "Fires onValueChanged after setting. " +
            "Returns: success, path, value, normalized.")]
        [MCPParam("path", Type = "string", Description = "Slider transform path")]
        [MCPParam("instanceId", Type = "integer", Description = "Slider instanceId")]
        [MCPParam("value", Type = "number", Required = true, Description = "Slider value to set")]
        [MCPParam("normalized", Type = "boolean", Description = "Value is 0-1 mapped to range (default true)")]
        public static string UISetSlider(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var rawValue = Convert.ToSingle(GetRequiredString(args, "value"), CultureInfo.InvariantCulture);
                var normalized = !(args.TryGetValue("normalized", out var normObj) && normObj is bool normBool && !normBool);
                var go = ResolveUITarget(args);
                if (go == null)
                    return ErrorJson("Slider not found — provide valid 'path' or 'instanceId'");

                var slider = go.GetComponent<Slider>();
                if (slider == null)
                    return ErrorJson("GameObject has no Slider component");

                float finalValue;
                if (normalized)
                {
                    var clampedNorm = Mathf.Clamp01(rawValue);
                    finalValue = Mathf.Lerp(slider.minValue, slider.maxValue, clampedNorm);
                }
                else
                {
                    finalValue = Mathf.Clamp(rawValue, slider.minValue, slider.maxValue);
                }

                slider.value = finalValue;
                if (slider.onValueChanged != null)
                    slider.onValueChanged.Invoke(finalValue);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("value", finalValue.ToString("G", CultureInfo.InvariantCulture)),
                    ("normalized", JsonHelper.BoolJson(normalized))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.set_slider failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.select_dropdown_option
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_SELECT_DROPDOWN_OPTION,
            "Select an option in a Dropdown (legacy or TMP). " +
            "Params: 'path' OR 'instanceId', " +
            "'option' (int, index) OR 'optionText' (string, matches label text). " +
            "Sets dropdown.value and fires onValueChanged. " +
            "Returns: success, path, selectedIndex, selectedText.")]
        [MCPParam("path", Type = "string", Description = "Dropdown transform path")]
        [MCPParam("instanceId", Type = "integer", Description = "Dropdown instanceId")]
        [MCPParam("option", Type = "integer", Description = "Option index to select")]
        [MCPParam("optionText", Type = "string", Description = "Option label text to select")]
        public static string UISelectDropdownOption(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var go = ResolveUITarget(args);
                if (go == null)
                    return ErrorJson("Dropdown not found — provide valid 'path' or 'instanceId'");

                // Try legacy Dropdown first
                var dropdown = go.GetComponent<Dropdown>();
                if (dropdown != null)
                {
                    return SetDropdownSelection(dropdown, args, go);
                }

                // Try TMP_Dropdown via reflection
                var tmpDdType = GetTMPDropdownType();
                if (tmpDdType != null)
                {
                    var tmpDd = go.GetComponent(tmpDdType);
                    if (tmpDd != null)
                    {
                        return SetTMPDropdownSelection(tmpDd, tmpDdType, args, go);
                    }
                }

                return ErrorJson("GameObject has no Dropdown or TMP_Dropdown component");
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.select_dropdown_option failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.drag
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_DRAG,
            "Simulate a drag operation from one UI element to another. " +
            "Params: 'fromPath' OR 'fromInstanceId' (source), " +
            "'toPath' OR 'toInstanceId' (target), both required. " +
            "Uses EventSystem to fire BeginDrag, Drag (at target), and EndDrag. " +
            "Returns: success, from, to, droppedOnTarget.")]
        [MCPParam("fromPath", Type = "string", Description = "Source UI element path")]
        [MCPParam("fromInstanceId", Type = "integer", Description = "Source UI element instanceId")]
        [MCPParam("toPath", Type = "string", Description = "Target UI element path")]
        [MCPParam("toInstanceId", Type = "integer", Description = "Target UI element instanceId")]
        public static string UIDrag(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var fromGo = ResolveUITargetWithKeys(args, "fromPath", "fromInstanceId");
                var toGo = ResolveUITargetWithKeys(args, "toPath", "toInstanceId");

                if (fromGo == null)
                    return ErrorJson("Source UI element not found — provide valid 'fromPath' or 'fromInstanceId'");
                if (toGo == null)
                    return ErrorJson("Target UI element not found — provide valid 'toPath' or 'toInstanceId'");

                if (EventSystem.current == null)
                    return ErrorJson("No active EventSystem in scene");

                // Get screen positions from RectTransforms
                var fromPos = GetUIElementScreenCenter(fromGo);
                var toPos = GetUIElementScreenCenter(toGo);

                // Create PointerEventData
                var pointerData = new PointerEventData(EventSystem.current)
                {
                    position = fromPos,
                    button = PointerEventData.InputButton.Left,
                    pressPosition = fromPos,
                    pointerPress = fromGo,
                    pointerDrag = fromGo,
                    delta = Vector2.zero,
                    clickCount = 1,
                };

                // Begin drag on source
                ExecuteEvents.Execute(fromGo, pointerData, ExecuteEvents.beginDragHandler);
                ExecuteEvents.Execute(fromGo, pointerData, ExecuteEvents.initializePotentialDrag);

                // Fire a drag update at the target position
                pointerData.position = toPos;
                pointerData.delta = toPos - fromPos;
                ExecuteEvents.Execute(fromGo, pointerData, ExecuteEvents.dragHandler);

                // End drag on source
                ExecuteEvents.Execute(fromGo, pointerData, ExecuteEvents.endDragHandler);

                // Check if target has a Drop handler
                var droppedOnTarget = ExecuteEvents.ExecuteHierarchy(toGo, pointerData, ExecuteEvents.dropHandler);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("from", JsonHelper.EscapeString(GetObjectPath(fromGo))),
                    ("to", JsonHelper.EscapeString(GetObjectPath(toGo))),
                    ("droppedOnTarget", JsonHelper.BoolJson(droppedOnTarget != null))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.drag failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ui.get_tooltip
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UI_GET_TOOLTIP,
            "Simulate hovering over a UI element to reveal tooltip text. " +
            "Params: 'path' OR 'instanceId' of the element, OR 'x','y' (normalized 0-1 screen position). " +
            "Fires IPointerEnterHandler on the target and scans visible text that just appeared. " +
            "Returns: success, target, tooltips (array of text entries). " +
            "Note: this fires synchronously; some tooltip systems may need a frame delay.")]
        [MCPParam("path", Type = "string", Description = "Target UI element path")]
        [MCPParam("instanceId", Type = "integer", Description = "Target UI element instanceId")]
        [MCPParam("x", Type = "number", Description = "Normalized X 0-1 (screen position mode)")]
        [MCPParam("y", Type = "number", Description = "Normalized Y 0-1 (screen position mode)")]
        public static string UIGetTooltip(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                GameObject go = null;

                // Resolve by path/instanceId or by screen position
                if (args.ContainsKey("path") || args.ContainsKey("instanceId"))
                {
                    go = ResolveUITarget(args);
                }
                else if (args.TryGetValue("x", out var xObj) && args.TryGetValue("y", out var yObj))
                {
                    var nx = Convert.ToSingle(xObj, CultureInfo.InvariantCulture);
                    var ny = Convert.ToSingle(yObj, CultureInfo.InvariantCulture);
                    // Use EventSystem raycast to find element at position
                    if (EventSystem.current != null)
                    {
                        var screenPos = new Vector2(nx * Screen.width, ny * Screen.height);
                        var pointerData = new PointerEventData(EventSystem.current)
                        {
                            position = screenPos,
                            button = PointerEventData.InputButton.Left,
                        };
                        var results = new List<RaycastResult>();
                        EventSystem.current.RaycastAll(pointerData, results);
                        if (results.Count > 0)
                            go = results[0].gameObject;
                    }
                }

                if (go == null)
                    return ErrorJson("Target UI element not found — provide 'path', 'instanceId', or 'x'/'y'");

                // Snapshot current visible texts before hover
                var preTexts = UIAnalysisTools.ScanAllTexts();
                var preSet = new HashSet<string>(preTexts);

                // Fire PointerEnter
                var enterData = new PointerEventData(EventSystem.current)
                {
                    position = GetUIElementScreenCenter(go),
                };
                ExecuteEvents.ExecuteHierarchy(go, enterData, ExecuteEvents.pointerEnterHandler);

                // Scan texts again and find new ones
                var postTexts = UIAnalysisTools.ScanAllTexts();
                var newTexts = postTexts.Where(t => !preSet.Contains(t)).ToList();

                // Also collect all Text/TMP children of the target
                var childTexts = new List<string>();
                var legacyTexts = go.GetComponentsInChildren<Text>(true);
                foreach (var txt in legacyTexts)
                {
                    if (txt.isActiveAndEnabled && !string.IsNullOrEmpty(txt.text))
                        childTexts.Add(JsonHelper.BuildJsonObject(
                            ("text", JsonHelper.EscapeString(txt.text)),
                            ("component", "\"Text\""),
                            ("path", JsonHelper.EscapeString(GetObjectPath(txt.gameObject)))
                        ));
                }

                // TMP texts via reflection
                var tmpType = UIAnalysisTools.TMPTextType;
                if (tmpType != null)
                {
                    try
                    {
                        var tmpComponents = go.GetComponentsInChildren(tmpType, true) as Component[];
                        if (tmpComponents != null)
                        {
                            foreach (var tmp in tmpComponents)
                            {
                                if (!(tmp as Behaviour)?.isActiveAndEnabled ?? false) continue;
                                var textProp = tmpType.GetProperty("text");
                                var txt = textProp?.GetValue(tmp)?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(txt))
                                    childTexts.Add(JsonHelper.BuildJsonObject(
                                        ("text", JsonHelper.EscapeString(txt)),
                                        ("component", "\"TextMeshProUGUI\""),
                                        ("path", JsonHelper.EscapeString(GetObjectPath(tmp.gameObject)))
                                    ));
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ui.get_tooltip] TMP read error: {ex.Message}"); }
                }

                // Return combined results
                var allTooltips = new List<string>();
                allTooltips.AddRange(newTexts);
                allTooltips.AddRange(childTexts);

                // Fire PointerExit to clean up
                ExecuteEvents.ExecuteHierarchy(go, enterData, ExecuteEvents.pointerExitHandler);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("target", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("tooltipCount", allTooltips.Count.ToString(CultureInfo.InvariantCulture)),
                    ("newTexts", JsonHelper.BuildJsonArray(newTexts.ToArray())),
                    ("childTexts", JsonHelper.BuildJsonArray(childTexts.ToArray())),
                    ("tooltips", JsonHelper.BuildJsonArray(allTooltips.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"ui.get_tooltip failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  UI tool helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolve a GameObject from "path" or "instanceId" in args.
        /// </summary>
        private static GameObject ResolveUITarget(Dictionary<string, object> args)
        {
            var path = GetString(args, "path", "");
            if (!string.IsNullOrEmpty(path))
            {
                var go = GameObject.Find(path);
                if (go != null) return go;
            }

            if (args.TryGetValue("instanceId", out var idObj))
            {
                var instanceId = Convert.ToInt32(idObj, CultureInfo.InvariantCulture);
                var allGos = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                foreach (var go in allGos)
                {
                    if (go.GetInstanceID() == instanceId)
                        return go;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve a GameObject with custom parameter key names (for drag from/to).
        /// </summary>
        private static GameObject ResolveUITargetWithKeys(Dictionary<string, object> args, string pathKey, string instanceIdKey)
        {
            var path = GetString(args, pathKey, "");
            if (!string.IsNullOrEmpty(path))
            {
                var go = GameObject.Find(path);
                if (go != null) return go;
            }

            if (args.TryGetValue(instanceIdKey, out var idObj))
            {
                var instanceId = Convert.ToInt32(idObj, CultureInfo.InvariantCulture);
                var allGos = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                foreach (var go in allGos)
                {
                    if (go.GetInstanceID() == instanceId)
                        return go;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the screen center point of a UI element from its RectTransform.
        /// Falls back to world-to-screen from transform position.
        /// </summary>
        private static Vector2 GetUIElementScreenCenter(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var center = (corners[0] + corners[2]) * 0.5f;
                return RectTransformUtility.WorldToScreenPoint(
                    rt.GetComponentInParent<Canvas>()?.worldCamera ?? Camera.main, center);
            }

            // Fallback: world position to screen
            var worldPos = go.transform.position;
            var cam = Camera.main;
            if (cam != null)
                return cam.WorldToScreenPoint(worldPos);

            return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        private static Type GetTMPInputFieldType()
        {
#if TEXT_MESH_PRO_ON
            return typeof(TMPro.TMP_InputField);
#else
            return null;
#endif
        }

        private static Type GetTMPDropdownType()
        {
#if TEXT_MESH_PRO_ON
            return typeof(TMPro.TMP_Dropdown);
#else
            return null;
#endif
        }

        /// <summary>
        /// Set selection on a legacy Dropdown by index or text match.
        /// </summary>
        private static string SetDropdownSelection(Dropdown dropdown, Dictionary<string, object> args, GameObject go)
        {
            int index = -1;

            if (args.TryGetValue("option", out var optObj))
            {
                index = Convert.ToInt32(optObj, CultureInfo.InvariantCulture);
            }
            else if (args.TryGetValue("optionText", out var textObj) && textObj is string optText)
            {
                for (int i = 0; i < dropdown.options.Count; i++)
                {
                    if (string.Equals(dropdown.options[i].text, optText, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    return ErrorJson($"Option text '{optText}' not found in dropdown options");
            }
            else
            {
                return ErrorJson("Provide either 'option' (int index) or 'optionText' (string)");
            }

            if (index < 0 || index >= dropdown.options.Count)
                return ErrorJson($"Index {index} out of range (0-{dropdown.options.Count - 1})");

            dropdown.value = index;
            dropdown.RefreshShownValue();
            if (dropdown.onValueChanged != null)
                dropdown.onValueChanged.Invoke(index);

            var selectedText = index >= 0 && index < dropdown.options.Count
                ? dropdown.options[index].text
                : "";

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                ("selectedIndex", index.ToString(CultureInfo.InvariantCulture)),
                ("selectedText", JsonHelper.EscapeString(selectedText))
            );
        }

        /// <summary>
        /// Set selection on a TMP_Dropdown via reflection.
        /// </summary>
        private static string SetTMPDropdownSelection(object tmpDd, Type tmpDdType, Dictionary<string, object> args, GameObject go)
        {
            var optionsProp = tmpDdType.GetProperty("options");
            var valueProp = tmpDdType.GetProperty("value");
            var onValueChangedProp = tmpDdType.GetProperty("onValueChanged");
            var refreshMethod = tmpDdType.GetMethod("RefreshShownValue");

            var options = optionsProp?.GetValue(tmpDd);
            var optionsCount = 0;
            var optionTexts = new List<string>();

            if (options != null)
            {
                // options is List<TMP_Dropdown.OptionData> — access via IList
                if (options is System.Collections.IList optList)
                {
                    optionsCount = optList.Count;
                    foreach (var opt in optList)
                    {
                        var textProp = opt.GetType().GetProperty("text");
                        var txt = textProp?.GetValue(opt)?.ToString() ?? "";
                        optionTexts.Add(txt);
                    }
                }
            }

            int index = -1;
            if (args.TryGetValue("option", out var optObj))
            {
                index = Convert.ToInt32(optObj, CultureInfo.InvariantCulture);
            }
            else if (args.TryGetValue("optionText", out var textObj) && textObj is string optText)
            {
                for (int i = 0; i < optionTexts.Count; i++)
                {
                    if (string.Equals(optionTexts[i], optText, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    return ErrorJson($"Option text '{optText}' not found in TMP dropdown options");
            }
            else
            {
                return ErrorJson("Provide either 'option' (int index) or 'optionText' (string)");
            }

            if (index < 0 || index >= optionsCount)
                return ErrorJson($"Index {index} out of range (0-{optionsCount - 1})");

            valueProp?.SetValue(tmpDd, index);
            refreshMethod?.Invoke(tmpDd, null);

            var onValueChanged = onValueChangedProp?.GetValue(tmpDd);
            if (onValueChanged != null)
            {
                var invokeMethod = onValueChanged.GetType().GetMethod("Invoke", new[] { typeof(int) });
                invokeMethod?.Invoke(onValueChanged, new object[] { index });
            }

            var selectedText = index >= 0 && index < optionTexts.Count ? optionTexts[index] : "";

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                ("selectedIndex", index.ToString(CultureInfo.InvariantCulture)),
                ("selectedText", JsonHelper.EscapeString(selectedText))
            );
        }

        private static float GetRequiredFloat(Dictionary<string, object> dict, string key)
        {
            return Convert.ToSingle(GetRequiredString(dict, key), CultureInfo.InvariantCulture);
        }

        private static float GetFloat(Dictionary<string, object> dict, string key, float defaultValue = 0f)
        {
            if (!dict.TryGetValue(key, out var v)) return defaultValue;
            return Convert.ToSingle(v, CultureInfo.InvariantCulture);
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

// touch 14:48:43

// test_request_compile 14:54:52
