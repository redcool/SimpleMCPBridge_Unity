#if NGUI_ON
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Scans NGUI (legacy third-party UI framework) elements and returns structured data:
    /// - All on-screen text (UILabel)
    /// - Interactive elements (UIButton, UIToggle, UISlider, UIInput)
    /// - Screen-space rects normalized to 0-1
    ///
    /// Used by NguiHandler (ngui.get_texts, ngui.find).
    /// This is an INDEPENDENT tool set from uGUI (ui.*) — both can coexist,
    /// and either can be toggled off via tools.disable by category (Ngui / Ui).
    ///
    /// Compiled only when the NGUI package is present (NGUI_ON symbol).
    /// No MCPTool attributes here — these are utility methods.
    /// </summary>
    public static class NGUIAnalysisTools
    {
        // ── Screen rect ──

        /// <summary>
        /// Get the normalized screen-space rect [xMin, yMin, xMax, yMax] in 0-1 range
        /// for any NGUI component. Prefers a UIWidget on the component or its children
        /// (worldCorners); falls back to a single point at the transform position.
        /// Returns null if no usable camera or fully off-screen.
        /// </summary>
        public static float[] GetNormalizedScreenRect(Component comp)
        {
            if (comp == null) return null;

            var cam = UICamera.mainCamera ?? NGUITools.FindCameraForLayer(comp.gameObject.layer);
            if (cam == null) return null;

            float sw = Screen.width;
            float sh = Screen.height;
            if (sw <= 0 || sh <= 0) return null;

            // Prefer a UIWidget on self or children for a real rect
            var widget = comp.GetComponent<UIWidget>() ?? comp.GetComponentInChildren<UIWidget>();
            if (widget != null)
            {
                var corners = widget.worldCorners; // Vector3[4] in world space
                if (corners != null && corners.Length >= 4)
                {
                    float minX = float.MaxValue, minY = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue;

                    foreach (var corner in corners)
                    {
                        Vector3 screenPt = cam.WorldToScreenPoint(corner);
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
            }

            // Fallback: single point at transform position
            Vector3 pos = cam.WorldToScreenPoint(comp.transform.position);
            float cx = pos.x / sw;
            float cy = pos.y / sh;
            if (cx < 0 || cx > 1 || cy < 0 || cy > 1)
                return null;

            return new[] { cx, cy, cx, cy };
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
        /// Scan all active UILabel components.
        /// Returns a list of JSON-ready field tuples for each label found.
        /// </summary>
        public static List<string> ScanAllTexts()
        {
            var results = new List<string>();

            var labels = UnityEngine.Object.FindObjectsByType<UILabel>(FindObjectsSortMode.None);
            foreach (var lbl in labels)
            {
                if (!lbl.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(lbl);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("text", JsonHelper.EscapeString(lbl.text)),
                    ("type", "\"UILabel\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(lbl.transform))),
                    ("instanceId", lbl.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect))),
                    ("fontSize", lbl.fontSize.ToString(CultureInfo.InvariantCulture)),
                    ("alignment", JsonHelper.EscapeString(lbl.alignment.ToString())),
                    ("color", JsonHelper.EscapeString(ColorUtility.ToHtmlStringRGB(lbl.color)))
                ));
            }

            return results;
        }

        // ── Widget scanning (UITexture / UISprite / UILabel / any UIWidget) ──

        /// <summary>
        /// Scan all active NGUI UIWidget components (UITexture, UISprite, UILabel, ...).
        /// Each widget returns: type, name, path, instanceId, normalized screen rect,
        /// and center. UILabel additionally includes text/fontSize/alignment/color.
        /// Optional filters: type (exact widget type name, e.g. "UITexture"),
        /// contains (substring match on widget name, or label text for UILabel).
        /// </summary>
        public static List<string> ScanWidgets(string typeFilter = "", string containsFilter = "")
        {
            var results = new List<string>();

            var widgets = UnityEngine.Object.FindObjectsByType<UIWidget>(FindObjectsSortMode.None);
            foreach (var w in widgets)
            {
                if (!w.isActiveAndEnabled) continue;
                var type = w.GetType().Name;

                if (!string.IsNullOrEmpty(typeFilter) &&
                    !type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(containsFilter))
                {
                    string matchText = w.name;
                    if (w is UILabel lbl) matchText = lbl.text;
                    if (matchText.IndexOf(containsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                var rect = GetNormalizedScreenRect(w);
                if (rect == null) continue;

                var fields = new List<(string, string)>
                {
                    ("type", JsonHelper.EscapeString(type)),
                    ("name", JsonHelper.EscapeString(w.name)),
                    ("path", JsonHelper.EscapeString(GetTransformPath(w.transform))),
                    ("instanceId", w.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                };

                if (w is UILabel label)
                {
                    fields.Add(("text", JsonHelper.EscapeString(label.text)));
                    fields.Add(("fontSize", label.fontSize.ToString(CultureInfo.InvariantCulture)));
                    fields.Add(("alignment", JsonHelper.EscapeString(label.alignment.ToString())));
                    fields.Add(("color", JsonHelper.EscapeString(ColorUtility.ToHtmlStringRGB(label.color))));
                }

                var entries = fields.Select(f => (f.Item1, f.Item2)).ToArray();
                results.Add(JsonHelper.BuildJsonObject(entries));
            }

            return results;
        }

        // ── Interactive UI scanning ──

        /// <summary>
        /// Scan for interactive NGUI elements (UIButton, UIToggle, UISlider, UIInput).
        /// Returns a list of JSON-ready field tuples.
        /// </summary>
        public static List<string> ScanInteractiveUI()
        {
            var results = new List<string>();

            // Buttons
            var buttons = UnityEngine.Object.FindObjectsByType<UIButton>(FindObjectsSortMode.None);
            foreach (var btn in buttons)
            {
                if (!btn.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(btn);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"UIButton\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(btn.transform))),
                    ("instanceId", btn.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("label", JsonHelper.EscapeString(GetChildLabelText(btn.transform, btn.name))),
                    ("interactable", JsonHelper.BoolJson(btn.isEnabled)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // Toggles
            var toggles = UnityEngine.Object.FindObjectsByType<UIToggle>(FindObjectsSortMode.None);
            foreach (var tog in toggles)
            {
                if (!tog.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(tog);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"UIToggle\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(tog.transform))),
                    ("instanceId", tog.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("label", JsonHelper.EscapeString(GetChildLabelText(tog.transform, tog.name))),
                    ("interactable", JsonHelper.BoolJson(tog.isColliderEnabled)),
                    ("isOn", JsonHelper.BoolJson(tog.value)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // Sliders
            var sliders = UnityEngine.Object.FindObjectsByType<UISlider>(FindObjectsSortMode.None);
            foreach (var sl in sliders)
            {
                if (!sl.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(sl);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"UISlider\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(sl.transform))),
                    ("instanceId", sl.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("interactable", JsonHelper.BoolJson(sl.isColliderEnabled)),
                    ("value", sl.value.ToString("G", CultureInfo.InvariantCulture)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            // Inputs
            var inputs = UnityEngine.Object.FindObjectsByType<UIInput>(FindObjectsSortMode.None);
            foreach (var input in inputs)
            {
                if (!input.isActiveAndEnabled) continue;
                var rect = GetNormalizedScreenRect(input);
                if (rect == null) continue;

                results.Add(JsonHelper.BuildJsonObject(
                    ("type", "\"UIInput\""),
                    ("path", JsonHelper.EscapeString(GetTransformPath(input.transform))),
                    ("instanceId", input.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("label", JsonHelper.EscapeString(input.label != null ? input.label.text : input.name)),
                    ("interactable", JsonHelper.BoolJson(input.enabled)),
                    ("text", JsonHelper.EscapeString(input.value)),
                    ("normalizedRect", JsonHelper.FloatArrayJson(rect)),
                    ("center", JsonHelper.FloatArrayJson(RectCenter(rect)))
                ));
            }

            return results;
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

        /// <summary>
        /// NGUI buttons/toggles/sliders don't expose a label property — find the first
        /// child UILabel text as the display label.
        /// </summary>
        private static string GetChildLabelText(Transform t, string fallback)
        {
            var label = t.GetComponentInChildren<UILabel>();
            if (label != null && !string.IsNullOrEmpty(label.text))
                return label.text;
            return fallback;
        }
    }
}
#endif
