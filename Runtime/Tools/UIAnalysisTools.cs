using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Scans Unity UI (uGUI) elements and returns structured data:
    /// - All on-screen text (Text / TextMeshProUGUI)
    /// - Interactive elements (Button, Toggle, Slider, Dropdown, InputField)
    /// - Screen-space rects normalized to 0-1
    ///
    /// Designed to be used by GameHandler (ui.get_texts, ui.find).
    /// No MCPTool attributes here — these are utility methods.
    /// </summary>
    public static class UIAnalysisTools
    {
        private static Type _tmpTextType;

        /// <summary>
        /// Resolve TextMeshProUGUI type. With TEXT_MESH_PRO_ON (asmdef versionDefine for
        /// com.unity.textmeshpro + "Unity.TextMeshPro" soft reference) the type is a
        /// compile-time constant; otherwise falls back to null (no TMP scanning).
        /// </summary>
        public static Type TMPTextType
        {
            get
            {
#if TEXT_MESH_PRO_ON
                return typeof(TMPro.TextMeshProUGUI);
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// Get the normalized screen-space rect [xMin, yMin, xMax, yMax] in 0-1 range
        /// for any UI element's RectTransform.
        /// Handles all Canvas render modes.
        /// Returns null if the RectTransform is not on any active canvas.
        /// </summary>
        public static float[] GetNormalizedScreenRect(RectTransform rt)
        {
            if (rt == null) return null;

            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return null;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            Camera cam = null;
            switch (canvas.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    // World corners == screen pixels already
                    break;
                case RenderMode.ScreenSpaceCamera:
                case RenderMode.WorldSpace:
                    cam = canvas.worldCamera ?? Camera.main;
                    if (cam == null) return null;
                    break;
            }

            float sw = Screen.width;
            float sh = Screen.height;
            if (sw <= 0 || sh <= 0) return null;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var corner in corners)
            {
                Vector2 screenPt;
                if (cam != null)
                    screenPt = RectTransformUtility.WorldToScreenPoint(cam, corner);
                else
                    screenPt = corner; // overlay: already screen coords

                float nx = screenPt.x / sw;
                float ny = screenPt.y / sh;
                if (nx < minX) minX = nx;
                if (ny < minY) minY = ny;
                if (nx > maxX) maxX = nx;
                if (ny > maxY) maxY = ny;
            }

            // Early reject: off-screen
            if (maxX < 0 || maxY < 0 || minX > 1 || minY > 1)
                return null;

            return new[] { minX, minY, maxX, maxY };
        }

        /// <summary>
        /// Rect center (normalized) from a normalized rect array.
        /// </summary>
        public static float[] RectCenter(float[] rect)
        {
            if (rect == null || rect.Length < 4) return null;
            return new[] { (rect[0] + rect[2]) * 0.5f, (rect[1] + rect[3]) * 0.5f };
        }

        // ── Text scanning ──

        /// <summary>
        /// Scan all active Canvas objects for text components.
        /// Returns a list of JSON-ready field tuples for each text found.
        /// </summary>
        public static List<string> ScanAllTexts()
        {
            var results = new List<string>();

            // Legacy Text components
            var legacyTexts = UnityEngine.Object.FindObjectsByType<Text>(FindObjectsSortMode.None);
            foreach (var txt in legacyTexts)
            {
                if (!txt.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(txt.rectTransform);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("text", JsonHelper.EscapeString(txt.text)),
                    ("type", "\"Text\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(txt.transform))),
                    ("instanceId", txt.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect))),
                    ("fontSize", txt.fontSize.ToString(CultureInfo.InvariantCulture)),
                    ("fontStyle", JsonHelper.EscapeString(txt.fontStyle.ToString())),
                    ("alignment", JsonHelper.EscapeString(txt.alignment.ToString())),
                    ("color", ColorToHex(txt.color))
                ));
            }

            // TextMeshProUGUI components (if available)
            if (TMPTextType != null)
            {
                try
                {
                    var tmpTexts = UnityEngine.Object.FindObjectsByType(TMPTextType, FindObjectsSortMode.None) as Component[];
                    if (tmpTexts != null)
                    {
                        foreach (var tmp in tmpTexts)
                        {
                            if (!(tmp as Behaviour)?.isActiveAndEnabled ?? false) continue;

                            var rt = tmp.transform as RectTransform;
                            if (rt == null) continue;
                            var rect = GetNormalizedScreenRect(rt);
                            if (rect == null) continue;

                            var textProp = TMPTextType.GetProperty("text");
                            var fontSizeProp = TMPTextType.GetProperty("fontSize");
                            var colorProp = TMPTextType.GetProperty("color");
                            var alignmentProp = TMPTextType.GetProperty("alignment");

                            var text = textProp?.GetValue(tmp)?.ToString() ?? "";
                            var fontSize = fontSizeProp?.GetValue(tmp)?.ToString() ?? "0";
                            var color = colorProp?.GetValue(tmp);
                            var align = alignmentProp?.GetValue(tmp)?.ToString() ?? "";

                            results.Add(JsonHelper.BuildJsonObject(
                                ("text", JsonHelper.EscapeString(text)),
                                ("type", "\"TextMeshProUGUI\""),
                                ("path", JsonHelper.EscapeString(GetTransformPath(tmp.transform))),
                                ("instanceId", tmp.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                                ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                                ("center", JsonHelper.FloatArrayJson(RectCenter(rect))),
                                ("fontSize", fontSize),
                                ("alignment", JsonHelper.EscapeString(align)),
                                ("color", ColorToHex(color))
                            ));
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[UIAnalysisTools] TMP scan skipped: {ex.Message}");
                }
            }

            return results;
        }

        // ── Interactive UI scanning ──

        /// <summary>
        /// Scan for interactive UI elements (Button, Toggle, Slider, Dropdown, InputField).
        /// Returns a list of JSON-ready field tuples.
        /// </summary>
        public static List<string> ScanInteractiveUI()
        {
            var results = new List<string>();

            // Buttons
            var buttons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None);
            foreach (var btn in buttons)
            {
                if (!btn.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(btn.GetComponent<RectTransform>());
                if (rect == null) continue;

                var label = GetButtonLabel(btn);
                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"Button\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(btn.transform))),
                    ("instanceId", btn.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("label", JsonHelper.EscapeString(label)),
                    ("interactable", JsonHelper.BoolJson(btn.interactable)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect))),
                    ("transition", JsonHelper.EscapeString(btn.transition.ToString()))
                ));
            }

            // Toggles
            var toggles = UnityEngine.Object.FindObjectsByType<Toggle>(FindObjectsSortMode.None);
            foreach (var tog in toggles)
            {
                if (!tog.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(tog.GetComponent<RectTransform>());
                if (rect == null) continue;

                var label = GetToggleLabel(tog);
                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"Toggle\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(tog.transform))),
                    ("instanceId", tog.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("label", JsonHelper.EscapeString(label)),
                    ("interactable", JsonHelper.BoolJson(tog.interactable)),
                    ("isOn", JsonHelper.BoolJson(tog.isOn)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // Sliders
            var sliders = UnityEngine.Object.FindObjectsByType<Slider>(FindObjectsSortMode.None);
            foreach (var sl in sliders)
            {
                if (!sl.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(sl.GetComponent<RectTransform>());
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"Slider\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(sl.transform))),
                    ("instanceId", sl.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("interactable", JsonHelper.BoolJson(sl.interactable)),
                    ("value", sl.value.ToString("G", CultureInfo.InvariantCulture)),
                    ("minValue", sl.minValue.ToString("G", CultureInfo.InvariantCulture)),
                    ("maxValue", sl.maxValue.ToString("G", CultureInfo.InvariantCulture)),
                    ("wholeNumbers", JsonHelper.BoolJson(sl.wholeNumbers)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // Dropdowns
            var dropdowns = UnityEngine.Object.FindObjectsByType<Dropdown>(FindObjectsSortMode.None);
            foreach (var dd in dropdowns)
            {
                if (!dd.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(dd.GetComponent<RectTransform>());
                if (rect == null) continue;

                var optionsList = new List<string>();
                foreach (var opt in dd.options)
                    optionsList.Add(JsonHelper.EscapeString(opt.text));

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"Dropdown\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(dd.transform))),
                    ("instanceId", dd.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("interactable", JsonHelper.BoolJson(dd.interactable)),
                    ("value", dd.value.ToString(CultureInfo.InvariantCulture)),
                    ("options", JsonHelper.BuildJsonArray(optionsList.ToArray())),
                    ("captionText", JsonHelper.EscapeString(dd.captionText?.text ?? "")),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // InputFields
            var inputFields = UnityEngine.Object.FindObjectsByType<InputField>(FindObjectsSortMode.None);
            foreach (var inf in inputFields)
            {
                if (!inf.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(inf.GetComponent<RectTransform>());
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"InputField\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(inf.transform))),
                    ("instanceId", inf.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("interactable", JsonHelper.BoolJson(inf.interactable)),
                    ("text", JsonHelper.EscapeString(inf.text)),
                    ("placeholder", JsonHelper.EscapeString(inf.placeholder?.GetComponent<Text>()?.text ?? "")),
                    ("contentType", JsonHelper.EscapeString(inf.contentType.ToString())),
                    ("characterLimit", inf.characterLimit.ToString(CultureInfo.InvariantCulture)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            return results;
        }

        /// <summary>
        /// Get the interactable element at a given normalized position.
        /// Returns the element info JSON, or null if nothing hit.
        /// </summary>
        public static string HitTestUI(float nx, float ny)
        {
            var elements = ScanInteractiveUI();
            foreach (var elemJson in elements)
            {
                // Parse rect from the JSON
                // Since we built it, we know the format:
                // "normalizedRect": [xMin, yMin, xMax, yMax]
                var rectMatch = System.Text.RegularExpressions.Regex.Match(elemJson,
                    "\"normalizedRect\":\\[([^\\]]+)\\]");
                if (!rectMatch.Success) continue;

                var parts = rectMatch.Groups[1].Value.Split(',');
                if (parts.Length < 4) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var xMin) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var yMin) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xMax) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var yMax))
                {
                    if (nx >= xMin && nx <= xMax && ny >= yMin && ny <= yMax)
                        return elemJson;
                }
            }
            return null;
        }

        // ── Helpers ──

        private static string GetTransformPath(Transform t)
        {
            var segments = new List<string>();
            while (t != null)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        private static string GetButtonLabel(Button btn)
        {
            // Try TMP text first (via reflection), then legacy Text
            if (TMPTextType != null)
            {
                try
                {
                    var tmp = btn.GetComponentInChildren(TMPTextType) as Component;
                    if (tmp != null)
                    {
                        var prop = TMPTextType.GetProperty("text");
                        var txt = prop?.GetValue(tmp)?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(txt)) return txt;
                    }
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[UIAnalysisTools] button label reflection error: {ex.Message}"); }
            }

            var text = btn.GetComponentInChildren<Text>();
            if (text != null && !string.IsNullOrEmpty(text.text))
                return text.text;

            return btn.name;
        }

        private static string GetToggleLabel(Toggle tog)
        {
            if (TMPTextType != null)
            {
                try
                {
                    var tmp = tog.GetComponentInChildren(TMPTextType) as Component;
                    if (tmp != null)
                    {
                        var prop = TMPTextType.GetProperty("text");
                        var txt = prop?.GetValue(tmp)?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(txt)) return txt;
                    }
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[UIAnalysisTools] toggle label reflection error: {ex.Message}"); }
            }

            var text = tog.GetComponentInChildren<Text>();
            if (text != null && !string.IsNullOrEmpty(text.text))
                return text.text;

            return tog.name;
        }

        private static string ColorToHex(object colorObj)
        {
            if (colorObj == null) return "\"\"";
            try
            {
                var color = (Color)colorObj;
                return JsonHelper.EscapeString(ColorUtility.ToHtmlStringRGB(color));
            }
            catch
            {
                return "\"\"";
            }
        }
    }
}
