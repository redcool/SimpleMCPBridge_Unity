using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// UI Toolkit (UnityEngine.UIElements) analysis and interaction tools.
    /// Independent from the uGUI tool set (ui.*) and NGUI (ngui.*) — each can be
    /// toggled off via tools.disable by category (Uitk / Ui / Ngui).
    ///
    /// Tools:
    ///   - uitk.get_panels   — list all UIDocument panels
    ///   - uitk.get_texts    — read all Label/TextElement/TextField text (memory-based)
    ///   - uitk.find         — find interactive elements (Button/Toggle/Slider/...)
    ///   - uitk.get_elements — dump the visual tree of a panel
    ///   - uitk.click        — click an element by path or by normalized coordinates
    ///   - uitk.set_value    — set value on Toggle/Slider/SliderInt/DropdownField/TextField
    ///
    /// UI Toolkit is a built-in Unity module (UnityEngine.UIElementsModule) — no
    /// package dependency, no #if guard. All tools gracefully return empty results
    /// when no attached panels exist (no Play Mode requirement).
    /// </summary>
    [MCPToolClass]
    public class UIToolkitHandler
    {
        // ══════════════════════════════════════════════════════════════
        //  uitk.get_panels
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_GET_PANELS,
            "List all UI Toolkit (UIDocument) panels in the scene. Each panel returns: " +
            "name, gameObjectPath, enabled, activeInHierarchy, attached (root panel " +
            "resolved), sortingOrder, panelSettingsSortingOrder, and screen dimensions. " +
            "UI Toolkit is a built-in Unity module — always available. Use the returned " +
            "gameObjectPath as 'panelPath' for uitk.get_elements / uitk.click / uitk.set_value.")]
        public static string GetPanels(string paramsJson)
        {
            try
            {
                var panelJsons = UIToolkitAnalysisTools.ScanPanels();

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", panelJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("screenWidth", Screen.width.ToString(CultureInfo.InvariantCulture)),
                    ("screenHeight", Screen.height.ToString(CultureInfo.InvariantCulture)),
                    ("panels", JsonHelper.BuildJsonArray(panelJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI Toolkit panel scan failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  uitk.get_texts
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_GET_TEXTS,
            "Read all UI Toolkit text from memory (no OCR, zero token cost). Scans attached " +
            "UIDocument panels for Label / TextElement / TextField / TextInputBaseField " +
            "elements. Each text element returns: text, typeName, name, fullPath, " +
            "normalized screen rect [xMin,yMin,xMax,yMax], font size, and color. " +
            "Optional filter: 'contains' (substring, case-insensitive). " +
            "Buttons are excluded — their text is covered by uitk.find.")]
        public static string GetTexts(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var filter = GetString(args, "contains", "");
                var textJsons = UIToolkitAnalysisTools.ScanTexts(filter);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", textJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("texts", JsonHelper.BuildJsonArray(textJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI Toolkit text scan failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  uitk.find
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_FIND,
            "Find interactive UI Toolkit elements (Button, Toggle, Slider, SliderInt, " +
            "DropdownField, TextField, ScrollView) with their screen positions and state. " +
            "Each element returns: typeName, name, fullPath, interactable, text/value " +
            "where applicable, dropdown choices count, normalized screen rect " +
            "[xMin,yMin,xMax,yMax], and center. Optional filters: 'type' (partial match, " +
            "e.g. Button/Toggle), 'contains' (substring on text/value/name). " +
            "Use 'center' with uitk.click or input.click_screen to interact.")]
        public static string Find(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var typeFilter = GetString(args, "type", "");
                var containsFilter = GetString(args, "contains", "");

                var elemJsons = UIToolkitAnalysisTools.ScanInteractive(typeFilter, containsFilter);

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
                return ErrorJson($"UI Toolkit find failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  uitk.get_elements
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_GET_ELEMENTS,
            "Dump the UI Toolkit visual tree of a panel as structured JSON. Each element " +
            "returns: name, typeName, fullPath, classes, normalized screen rect " +
            "[xMin,yMin,xMax,yMax], enabled, visible, text (TextElement) and value " +
            "(Toggle/Slider/SliderInt/DropdownField/TextField). Per panel: panelName, " +
            "panelPath, elementCount, truncated (true when more than 500 elements — only " +
            "the first 500 are returned). Optional: 'panelIndex' (0-based, uitk.get_panels " +
            "order) or 'panelPath' (gameObjectPath from uitk.get_panels) — omit for all panels.")]
        public static string GetElements(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var panels = UIToolkitAnalysisTools.CollectPanels();
                var panel = UIToolkitAnalysisTools.ResolvePanel(panels,
                    GetOptionalInt(args, "panelIndex") ?? -1,
                    GetString(args, "panelPath", ""));

                List<string> panelJsons;
                if (panel != null)
                    panelJsons = UIToolkitAnalysisTools.ScanElementsForPanel(panel);
                else
                    panelJsons = UIToolkitAnalysisTools.ScanElements();

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", panelJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("panels", JsonHelper.BuildJsonArray(panelJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI Toolkit element dump failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  uitk.click
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_CLICK,
            "Click a UI Toolkit element. Two modes: (1) element mode — pass 'panelIndex' " +
            "or 'panelPath' plus 'path' (element path from uitk.get_elements fullPath); " +
            "(2) coordinate mode — pass 'panelIndex'/'panelPath' plus normalized 'x','y' " +
            "(0-1, top-left origin — the same space as the normalizedRect/center values " +
            "returned by uitk.find and uitk.get_elements). Dispatches pooled MouseDown + " +
            "MouseUp events to the target element with a panel-space position inside the " +
            "panel layout, so Clickable generates the ClickEvent. Returns clickedPath and " +
            "clickedType. The panel must be attached and laid out (runtime / Play Mode).")]
        public static string Click(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var panel = ResolvePanelArg(args);
                if (panel == null)
                    return ErrorJson("uitk.click: panel not found (provide a valid 'panelIndex' or 'panelPath')");

                var root = panel.rootVisualElement;
                if (root == null || root.panel == null)
                    return ErrorJson("uitk.click: panel is not attached (runtime / Play Mode required)");

                // Element mode: path
                var path = GetString(args, "path", "");
                if (!string.IsNullOrEmpty(path))
                {
                    var el = UIToolkitAnalysisTools.ResolveElement(root, path);
                    if (el == null)
                        return ErrorJson($"uitk.click: element not found for path '{path}'");

                    var center = el.worldBound.center; // already in panel space
                    var clickErr = DispatchClick(el, center);
                    if (clickErr != null)
                        return ErrorJson(clickErr);

                    return JsonHelper.BuildJsonObject(
                        ("success", "true"),
                        ("clickedPath", JsonHelper.EscapeString(UIToolkitAnalysisTools.GetElementPath(el))),
                        ("clickedType", JsonHelper.EscapeString(el.GetType().Name))
                    );
                }

                // Coordinate mode: x,y normalized 0-1, top-left origin — the same
                // space as the normalizedRect values from uitk.find / uitk.get_elements
                // (normalized against the panel root's worldBound).
                var x = GetOptionalFloat(args, "x");
                var y = GetOptionalFloat(args, "y");
                if (x == null || y == null)
                    return ErrorJson("uitk.click: provide either 'path' (element mode) or 'x'/'y' normalized coords (coordinate mode)");

                var rootBound = root.worldBound;
                if (rootBound.width <= 0 || rootBound.height <= 0)
                    return ErrorJson("uitk.click: panel root has no layout yet (defer a frame / Play Mode required)");

                var panelPos = new Vector2(
                    rootBound.x + x.Value * rootBound.width,
                    rootBound.y + y.Value * rootBound.height);
                var picked = root.panel.Pick(panelPos);
                if (picked == null)
                    return ErrorJson($"uitk.click: no element at normalized position ({x.Value:G}, {y.Value:G})");

                var pickErr = DispatchClick(picked, panelPos);
                if (pickErr != null)
                    return ErrorJson(pickErr);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("clickedPath", JsonHelper.EscapeString(UIToolkitAnalysisTools.GetElementPath(picked))),
                    ("clickedType", JsonHelper.EscapeString(picked.GetType().Name))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI Toolkit click failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  uitk.set_value
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.UITK_SET_VALUE,
            "Set the value of a UI Toolkit element. Params: 'panelIndex' or 'panelPath' " +
            "plus 'path' (element path from uitk.get_elements) and 'value' (string, parsed " +
            "per element type: Toggle=bool, Slider=float, SliderInt=int, DropdownField=int " +
            "index or string choice, TextField=string). Optional 'silent' (bool, default " +
            "false): when true uses SetValueWithoutNotify so no ChangeEvent fires. " +
            "Returns success, value, silent.")]
        public static string SetValue(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var panel = ResolvePanelArg(args);
                if (panel == null)
                    return ErrorJson("uitk.set_value: panel not found (provide a valid 'panelIndex' or 'panelPath')");

                var root = panel.rootVisualElement;
                if (root == null || root.panel == null)
                    return ErrorJson("uitk.set_value: panel is not attached (runtime / Play Mode required)");

                var path = GetString(args, "path", "");
                if (string.IsNullOrEmpty(path))
                    return ErrorJson("uitk.set_value: missing required parameter 'path'");

                var el = UIToolkitAnalysisTools.ResolveElement(root, path);
                if (el == null)
                    return ErrorJson($"uitk.set_value: element not found for path '{path}'");

                var rawValue = GetString(args, "value", "");
                var silent = GetOptionalBool(args, "silent") ?? false;

                if (!TrySetValue(el, rawValue, silent))
                    return ErrorJson($"uitk.set_value: unsupported element type '{el.GetType().Name}' " +
                                     "(supported: Toggle, Slider, SliderInt, DropdownField, TextField)");

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("value", JsonHelper.EscapeString(rawValue)),
                    ("silent", JsonHelper.BoolJson(silent))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"UI Toolkit set_value failed: {ex.Message}");
            }
        }

        // ── Internal helpers ──

        private static UIDocument ResolvePanelArg(Dictionary<string, object> args)
        {
            var panels = UIToolkitAnalysisTools.CollectPanels();
            return UIToolkitAnalysisTools.ResolvePanel(panels,
                GetOptionalInt(args, "panelIndex") ?? -1,
                GetString(args, "panelPath", ""));
        }

        private static float? GetOptionalFloat(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var v) || v == null) return null;
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var pf))
                return pf;
            return null;
        }

        /// <summary>
        /// Dispatch a pooled MouseDown + MouseUp to an element so Clickable generates
        /// the ClickEvent. Returns null on success, or an error string describing the
        /// failed gate (the caller returns it as a tool error instead of silently
        /// no-op'ing).
        ///
        /// Why this exact sequence (verified against UnityCsReference 2022.3):
        /// Clickable's `clicked` fires only in ProcessUpEvent, gated by
        /// ContainsPointer(pointerId), which reads an element-under-pointer cache.
        /// That cache is written only when BOTH hold:
        ///   (a) the event's PreDispatch ran SavePointerPosition — this happens only
        ///       when triggeredByOS == true, which ONLY MouseDownEvent/MouseUpEvent
        ///       .GetPooled(Event) sets (public pointer-event factories leave it
        ///       false; MouseButtonEventBase.GetPooled(Vector2,...) sets fromOS=false).
        ///   (b) the position lies inside panel.visualTree.layout in PANEL space —
        ///       otherwise the pointer is flagged OutsidePanel and the cache stays
        ///       null. Positions are therefore always panel-space (worldBound is
        ///       already panel space for UIDocument panels) and clamped into the
        ///       panel layout defensively.
        /// Pointer events (Route B) are NOT used: they can't drive the cache reliably
        /// without OS-synthesized compat-mouse events, and hand-built pointerIds
        /// outside the 32-slot range classify as touch and double-fire via the
        /// compat-mouse machinery. The mouse route fires clicked exactly once.
        /// </summary>
        private static string DispatchClick(VisualElement el, Vector2 position)
        {
            var panel = el.panel;
            if (panel == null)
                return "uitk.click: element is not attached to a panel (SendEvent is a silent no-op when detached)";

            var layout = panel.visualTree.layout;
            if (layout.width <= 0 || layout.height <= 0)
                return "uitk.click: panel has no layout yet (defer a frame — panel must be laid out before clicking)";

            // Gate (b): position must be inside the panel layout in panel space.
            if (!layout.Contains(position))
            {
                position = new Vector2(
                    Mathf.Clamp(position.x, layout.xMin, layout.xMax),
                    Mathf.Clamp(position.y, layout.yMin, layout.yMax));
                if (!layout.Contains(position))
                    return $"uitk.click: click position {position} is outside panel layout {layout}";
            }

            using (var downEvt = MouseDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                mousePosition = position,
                button = 0,
                clickCount = 1,
                modifiers = 0,
            }))
            {
                downEvt.target = el;
                el.SendEvent(downEvt);
            }

            // Gate (a) + (b) checkpoint: after the Down, verify the element under
            // the pointer still resolves to the target (or a descendant) — the
            // element-under-pointer cache used by ProcessUpEvent's ContainsPointer
            // would otherwise fail and clicked never fires. Report the gate failure
            // instead of silently no-op'ing. (IPanel.Pick is the public hit-test;
            // GetTopElementUnderPointer is not exposed on IPanel in this 2022.3 patch.)
            var underPointer = panel.Pick(position);
            if (underPointer == null || (underPointer != el && !el.Contains(underPointer)))
                return $"uitk.click: no element under pointer after MouseDown at {position} " +
                       $"(position space, panel layout, or a covering sibling)";

            using (var upEvt = MouseUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp,
                mousePosition = position,
                button = 0,
                clickCount = 1,
                modifiers = 0,
            }))
            {
                upEvt.target = el;
                el.SendEvent(upEvt);
            }

            return null; // clicked fired exactly once
        }

        /// <summary>
        /// Set an element's value by concrete type. Returns false for unsupported types;
        /// throws ArgumentException with a descriptive message on parse failures.
        /// </summary>
        private static bool TrySetValue(VisualElement el, string rawValue, bool silent)
        {
            if (el is Toggle toggle)
            {
                bool bv;
                if (bool.TryParse(rawValue, out bv))
                {
                    if (silent) toggle.SetValueWithoutNotify(bv);
                    else toggle.value = bv;
                    return true;
                }
                if (rawValue == "0" || rawValue == "1")
                {
                    bv = rawValue == "1";
                    if (silent) toggle.SetValueWithoutNotify(bv);
                    else toggle.value = bv;
                    return true;
                }
                throw new ArgumentException($"cannot parse '{rawValue}' as bool for Toggle");
            }

            if (el is Slider slider)
            {
                if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                    throw new ArgumentException($"cannot parse '{rawValue}' as float for Slider");
                if (silent) slider.SetValueWithoutNotify(fv);
                else slider.value = fv;
                return true;
            }

            if (el is SliderInt sliderInt)
            {
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                    throw new ArgumentException($"cannot parse '{rawValue}' as int for SliderInt");
                if (silent) sliderInt.SetValueWithoutNotify(iv);
                else sliderInt.value = iv;
                return true;
            }

            if (el is DropdownField dropdown)
            {
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                {
                    var choices = dropdown.choices;
                    if (choices == null || idx < 0 || idx >= choices.Count)
                        throw new ArgumentException($"dropdown index {idx} out of range (0..{choices?.Count ?? 0})");
                    if (silent) dropdown.SetValueWithoutNotify(choices[idx]);
                    else dropdown.index = idx;
                }
                else
                {
                    if (silent) dropdown.SetValueWithoutNotify(rawValue);
                    else dropdown.value = rawValue;
                }
                return true;
            }

            if (el is TextField textField)
            {
                if (silent) textField.SetValueWithoutNotify(rawValue);
                else textField.value = rawValue;
                return true;
            }

            return false;
        }
    }
}
