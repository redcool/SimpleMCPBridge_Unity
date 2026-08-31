using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers.Game
{
    [MCPToolClass]
    // UI 工具组（ui.get_texts / ui.find）— 从 GameHandler 抽离
    public static class GameUIHandler
    {
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

        
    }
}
