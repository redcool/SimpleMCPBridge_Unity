using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace SimpleMCPBridge.Runtime.Tools
{
    /// <summary>
    /// Scans UI Toolkit (UnityEngine.UIElements) panels and elements and returns
    /// structured data:
    /// - All text (Label / TextElement / TextField)
    /// - Interactive elements (Button, Toggle, Slider, SliderInt, DropdownField, TextField, ScrollView)
    /// - Full visual-tree dumps with normalized screen rects
    ///
    /// UI Toolkit is a built-in Unity module (UnityEngine.UIElementsModule) — always
    /// available, no package dependency, no #if guard needed.
    ///
    /// Used by UIToolkitHandler (uitk.*). This is an INDEPENDENT tool set from
    /// uGUI (ui.*) and NGUI (ngui.*); each can be toggled off via tools.disable
    /// by category (Uitk / Ui / Ngui).
    ///
    /// No MCPTool attributes here — these are utility methods.
    /// </summary>
    public static class UIToolkitAnalysisTools
    {
        // ── Screen rect ──

        /// <summary>
        /// Get the normalized screen-space rect [xMin, yMin, xMax, yMax] in 0-1 range
        /// (top-left origin) for a UI Toolkit element. Normalizes against the panel's
        /// root element worldBound. Returns null if the element has no valid layout
        /// (worldBound width/height &lt;= 0) or is fully off-screen.
        /// </summary>
        public static float[] GetNormalizedScreenRect(VisualElement el)
        {
            if (el == null) return null;

            var root = GetRootElement(el);
            if (root == null) return null;

            Rect elBound = el.worldBound;
            Rect rootBound = root.worldBound;
            if (elBound.width <= 0 || elBound.height <= 0) return null;
            if (rootBound.width <= 0 || rootBound.height <= 0) return null;

            float minX = (elBound.x - rootBound.x) / rootBound.width;
            float minY = (elBound.y - rootBound.y) / rootBound.height;
            float maxX = minX + elBound.width / rootBound.width;
            float maxY = minY + elBound.height / rootBound.height;

            // Early reject: fully off-screen
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

        // ── Panel discovery ──

        /// <summary>
        /// Collect all UIDocument components in the scene (in FindObjectsByType order).
        /// </summary>
        public static List<UIDocument> CollectPanels()
        {
            return UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None).ToList();
        }

        /// <summary>
        /// Get a panel by index (index in CollectPanels order). Returns null if out of range.
        /// </summary>
        public static UIDocument GetPanelAt(List<UIDocument> panels, int index)
        {
            if (panels == null || index < 0 || index >= panels.Count) return null;
            return panels[index];
        }

        /// <summary>
        /// Get a panel by its GameObject transform path (case-insensitive). Returns null if not found.
        /// </summary>
        public static UIDocument GetPanelByPath(List<UIDocument> panels, string path)
        {
            if (panels == null || string.IsNullOrEmpty(path)) return null;
            foreach (var p in panels)
            {
                if (GetPanelPath(p).Equals(path, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// Resolve a panel from optional panelIndex (int, -1 = none) / panelPath (string).
        /// panelPath takes priority when both are provided. Returns null when neither
        /// resolves (the caller then decides whether to fall back to "all panels").
        /// </summary>
        public static UIDocument ResolvePanel(List<UIDocument> panels, int panelIndex, string panelPath)
        {
            if (!string.IsNullOrEmpty(panelPath))
                return GetPanelByPath(panels, panelPath);
            return GetPanelAt(panels, panelIndex);
        }

        // ── Panel scanning ──

        /// <summary>
        /// Scan all UIDocument panels and return a JSON string for each (see GetPanelJson).
        /// </summary>
        public static List<string> ScanPanels()
        {
            var results = new List<string>();
            foreach (var doc in CollectPanels())
            {
                var json = GetPanelJson(doc);
                if (json != null) results.Add(json);
            }
            return results;
        }

        /// <summary>
        /// JSON for one UIDocument panel: name, gameObjectPath, enabled,
        /// activeInHierarchy, attached, sortingOrder, panelSettingsSortingOrder,
        /// screenWidth, screenHeight.
        /// </summary>
        public static string GetPanelJson(UIDocument doc)
        {
            if (doc == null) return null;

            var root = doc.rootVisualElement;
            bool attached = root != null && root.panel != null;

            return JsonHelper.BuildJsonObject(
                ("name", JsonHelper.EscapeString(doc.gameObject.name)),
                ("gameObjectPath", JsonHelper.EscapeString(GetPanelPath(doc))),
                ("enabled", JsonHelper.BoolJson(doc.enabled)),
                ("activeInHierarchy", JsonHelper.BoolJson(doc.gameObject.activeInHierarchy)),
                ("attached", JsonHelper.BoolJson(attached)),
                ("sortingOrder", doc.sortingOrder.ToString(CultureInfo.InvariantCulture)),
                ("panelSettingsSortingOrder", (doc.panelSettings != null ? doc.panelSettings.sortingOrder : 0).ToString(CultureInfo.InvariantCulture)),
                ("screenWidth", Screen.width.ToString(CultureInfo.InvariantCulture)),
                ("screenHeight", Screen.height.ToString(CultureInfo.InvariantCulture))
            );
        }

        // ── Text scanning ──

        /// <summary>
        /// Scan text across all attached UI Toolkit panels.
        /// Text sources: Label / TextElement-derived (Button excluded — covered by
        /// uitk.find), TextField / TextInputBaseField (empty fields skipped).
        /// Optional filter: contains (case-insensitive substring on text).
        /// </summary>
        public static List<string> ScanTexts(string containsFilter = "")
        {
            var results = new List<string>();

            foreach (var doc in GetAttachedPanels())
            {
                var root = doc.rootVisualElement;
                if (root == null) continue;

                var all = root.Query<VisualElement>().Build().ToList();
                foreach (var el in all)
                {
                    try
                    {
                        string text = null;
                        if (el is TextElement te)
                        {
                            if (el is Button) continue;              // buttons are covered by uitk.find
                            if (HasTextInputAncestor(el)) continue;  // field internals — reported via the field
                            text = te.text;
                        }
                        else if (el is TextField tf)
                        {
                            text = tf.value;
                            if (string.IsNullOrEmpty(text)) continue; // skip empty fields
                        }
                        else if (el is TextInputBaseField<string> input)
                        {
                            text = input.value;
                            if (string.IsNullOrEmpty(text)) continue;
                        }
                        else
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(containsFilter) &&
                            text.IndexOf(containsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        results.Add(BuildTextJson(el, text));
                    }
                    catch
                    {
                        // Skip elements that throw during style/layout inspection
                    }
                }
            }

            return results;
        }

        private static string BuildTextJson(VisualElement el, string text)
        {
            var fields = new List<(string, string)>
            {
                ("text", JsonHelper.EscapeString(text)),
                ("typeName", JsonHelper.EscapeString(el.GetType().Name)),
                ("name", JsonHelper.EscapeString(el.name)),
                ("fullPath", JsonHelper.EscapeString(GetElementPath(el))),
                ("fontSize", el.resolvedStyle.fontSize.ToString("G", CultureInfo.InvariantCulture)),
                ("color", JsonHelper.EscapeString(ColorUtility.ToHtmlStringRGB(el.resolvedStyle.color)))
            };

            var rect = GetNormalizedScreenRect(el);
            if (rect != null)
            {
                fields.Add(("normalizedRect", JsonHelper.FloatArrayJson(rect)));
            }

            return JsonHelper.BuildJsonObject(fields.ToArray());
        }

        // ── Interactive element scanning ──

        /// <summary>
        /// Scan interactive UI Toolkit elements across all attached panels:
        /// Button, Toggle, Slider, SliderInt, DropdownField, TextField, ScrollView.
        /// Optional filters: type (partial match, case-insensitive), contains (substring
        /// on text/value/name).
        /// </summary>
        public static List<string> ScanInteractive(string typeFilter = "", string containsFilter = "")
        {
            var results = new List<string>();

            foreach (var doc in GetAttachedPanels())
            {
                var root = doc.rootVisualElement;
                if (root == null) continue;

                var all = root.Query<VisualElement>().Build().ToList();
                foreach (var el in all)
                {
                    try
                    {
                        if (!(el is Button || el is Toggle || el is Slider || el is SliderInt ||
                              el is DropdownField || el is TextField || el is ScrollView))
                            continue;

                        var typeName = el.GetType().Name;
                        if (!string.IsNullOrEmpty(typeFilter) &&
                            typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string text = null;    // raw text (Button / DropdownField)
                        string valueRaw = null; // raw value for filtering
                        string valueJson = null; // pre-encoded JSON value (bool/number/escaped string)
                        int choicesCount = 0;

                        if (el is Button b) text = b.text;
                        else if (el is DropdownField dd)
                        {
                            text = dd.text;
                            valueRaw = dd.value;
                            valueJson = JsonHelper.EscapeString(dd.value);
                            choicesCount = dd.choices?.Count ?? 0;
                        }
                        else if (el is Toggle tog)
                        {
                            valueRaw = tog.value ? "true" : "false";
                            valueJson = JsonHelper.BoolJson(tog.value);
                        }
                        else if (el is Slider sl)
                        {
                            valueRaw = sl.value.ToString("G", CultureInfo.InvariantCulture);
                            valueJson = valueRaw;
                        }
                        else if (el is SliderInt sli)
                        {
                            valueRaw = sli.value.ToString(CultureInfo.InvariantCulture);
                            valueJson = valueRaw;
                        }
                        else if (el is TextField tf)
                        {
                            valueRaw = tf.value;
                            valueJson = JsonHelper.EscapeString(tf.value);
                        }

                        if (!string.IsNullOrEmpty(containsFilter))
                        {
                            var haystack = (text ?? "") + (valueRaw ?? "") + el.name;
                            if (haystack.IndexOf(containsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                        }

                        var fields = new List<(string, string)>
                        {
                            ("typeName", JsonHelper.EscapeString(typeName)),
                            ("name", JsonHelper.EscapeString(el.name)),
                            ("fullPath", JsonHelper.EscapeString(GetElementPath(el))),
                            ("interactable", JsonHelper.BoolJson(el.enabledInHierarchy && !IsInvisible(el)))
                        };

                        if (text != null) fields.Add(("text", JsonHelper.EscapeString(text)));
                        if (valueJson != null) fields.Add(("value", valueJson));
                        if (el is DropdownField)
                            fields.Add(("choices", choicesCount.ToString(CultureInfo.InvariantCulture)));

                        var rect = GetNormalizedScreenRect(el);
                        if (rect != null)
                        {
                            fields.Add(("normalizedRect", JsonHelper.FloatArrayJson(rect)));
                            fields.Add(("center", JsonHelper.FloatArrayJson(RectCenter(rect))));
                        }

                        results.Add(JsonHelper.BuildJsonObject(fields.ToArray()));
                    }
                    catch
                    {
                        // Skip elements that throw during style/layout inspection
                    }
                }
            }

            return results;
        }

        // ── Visual tree dump ──

        /// <summary>
        /// Dump the full visual tree of every attached panel. Per panel: panelName,
        /// panelPath, truncated, elementCount, elements[]. Output is capped at 500
        /// elements per panel ("truncated":true when more exist).
        /// </summary>
        public static List<string> ScanElements()
        {
            var results = new List<string>();
            foreach (var doc in GetAttachedPanels())
            {
                var json = BuildPanelElementJson(doc);
                if (json != null) results.Add(json);
            }
            return results;
        }

        /// <summary>
        /// Visual-tree dump for a single panel (same JSON shape as ScanElements entries).
        /// </summary>
        public static List<string> ScanElementsForPanel(UIDocument doc)
        {
            var json = BuildPanelElementJson(doc);
            return json != null ? new List<string> { json } : new List<string>();
        }

        private const int MaxElementDump = 500;

        private static string BuildPanelElementJson(UIDocument doc)
        {
            if (doc == null) return null;
            var root = doc.rootVisualElement;
            if (root == null || root.panel == null) return null;

            var all = root.Query<VisualElement>().Build().ToList();
            bool truncated = all.Count > MaxElementDump;
            int count = Math.Min(all.Count, MaxElementDump);

            var elems = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    elems.Add(BuildElementJson(all[i]));
                }
                catch
                {
                    // Skip elements that throw during style/layout inspection
                }
            }

            return JsonHelper.BuildJsonObject(
                ("panelName", JsonHelper.EscapeString(doc.gameObject.name)),
                ("panelPath", JsonHelper.EscapeString(GetPanelPath(doc))),
                ("truncated", JsonHelper.BoolJson(truncated)),
                ("elementCount", count.ToString(CultureInfo.InvariantCulture)),
                ("elements", JsonHelper.BuildJsonArray(elems.ToArray()))
            );
        }

        private static string BuildElementJson(VisualElement el)
        {
            var fields = new List<(string, string)>
            {
                ("name", JsonHelper.EscapeString(el.name)),
                ("typeName", JsonHelper.EscapeString(el.GetType().Name)),
                ("fullPath", JsonHelper.EscapeString(GetElementPath(el))),
                ("classes", JsonHelper.BuildJsonArray(el.GetClasses().Select(c => JsonHelper.EscapeString(c)).ToArray())),
                ("enabled", JsonHelper.BoolJson(el.enabledInHierarchy)),
                ("visible", JsonHelper.BoolJson(!IsInvisible(el)))
            };

            if (el is TextElement te)
                fields.Add(("text", JsonHelper.EscapeString(te.text)));

            string valueJson = null;
            if (el is Toggle tog) valueJson = JsonHelper.BoolJson(tog.value);
            else if (el is Slider sl) valueJson = sl.value.ToString("G", CultureInfo.InvariantCulture);
            else if (el is SliderInt sli) valueJson = sli.value.ToString(CultureInfo.InvariantCulture);
            else if (el is DropdownField dd) valueJson = JsonHelper.EscapeString(dd.value);
            else if (el is TextField tf) valueJson = JsonHelper.EscapeString(tf.value);
            else if (el is TextInputBaseField<string> tb) valueJson = JsonHelper.EscapeString(tb.value);
            if (valueJson != null) fields.Add(("value", valueJson));

            var rect = GetNormalizedScreenRect(el);
            if (rect != null)
                fields.Add(("normalizedRect", JsonHelper.FloatArrayJson(rect)));

            return JsonHelper.BuildJsonObject(fields.ToArray());
        }

        // ── Path helpers ──

        /// <summary>
        /// Transform hierarchy path of a UIDocument's GameObject, e.g. "Canvas/MyPanel".
        /// </summary>
        public static string GetPanelPath(UIDocument doc)
        {
            if (doc == null) return "";
            var segments = new List<string>();
            var t = doc.transform;
            while (t != null)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        /// <summary>
        /// Full path from the panel root to the element, "/"-separated.
        /// Each segment is the element's name when non-empty, else "{TypeName}#{index}"
        /// where index is the element's sibling index (deterministic, no name needed).
        /// Relative to the panel root (the root itself is not part of the path).
        /// </summary>
        public static string GetElementPath(VisualElement el)
        {
            if (el == null) return "";

            var segments = new List<string>();
            var cur = el;
            while (cur != null)
            {
                if (!string.IsNullOrEmpty(cur.name))
                {
                    segments.Add(cur.name);
                }
                else
                {
                    var typeName = cur.GetType().Name;
                    var index = cur.parent != null ? cur.parent.hierarchy.IndexOf(cur) : 0;
                    segments.Add($"{typeName}#{index}");
                }
                cur = cur.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        /// <summary>
        /// Resolve an element by its GetElementPath path, walking the children of the
        /// given root. Segments match by element name first; when no named child matches,
        /// a "{TypeName}#{index}" segment is resolved via sibling index.
        /// Returns null if not found.
        ///
        /// The path produced by GetElementPath starts at the panel's visualTree root
        /// (e.g. "Default Panel Settings/UIDocument-container/TestButton"), while the
        /// caller passes panel.rootVisualElement as root. To keep both forms working we
        /// try multiple start points: the given root, then the panel visualTree. Path
        /// segments equal to the current element's own name are skipped so full paths
        /// (which include the root itself) resolve as well.
        /// </summary>
        public static VisualElement ResolveElement(VisualElement root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;

            var startPoints = new List<VisualElement> { root };
            if (root.panel != null && root.panel.visualTree != null &&
                root.panel.visualTree != root)
                startPoints.Add(root.panel.visualTree);

            foreach (var start in startPoints)
            {
                var el = ResolveElementFrom(start, path);
                if (el != null) return el;
            }
            return null;
        }

        private static VisualElement ResolveElementFrom(VisualElement root, string path)
        {
            var segments = path.Split('/');
            var cur = root;
            foreach (var segment in segments)
            {
                if (string.IsNullOrEmpty(segment)) return null;
                if (segment == cur.name) continue; // path includes the root itself
                var next = FindChildByName(cur, segment) ?? FindChildByIndexSegment(cur, segment);
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        private static VisualElement FindChildByName(VisualElement parent, string name)
        {
            int count = parent.hierarchy.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = parent.hierarchy.ElementAt(i);
                if (child.name == name) return child;
            }
            return null;
        }

        private static VisualElement FindChildByIndexSegment(VisualElement parent, string segment)
        {
            // Format: "TypeName#index"
            var hashIdx = segment.LastIndexOf('#');
            if (hashIdx <= 0 || hashIdx == segment.Length - 1) return null;
            var typeName = segment.Substring(0, hashIdx);
            var indexStr = segment.Substring(hashIdx + 1);
            if (!int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return null;

            int count = parent.hierarchy.childCount;
            if (index < 0 || index >= count) return null;
            var child = parent.hierarchy.ElementAt(index);
            if (child.GetType().Name != typeName) return null;
            return child;
        }

        // ── Internal helpers ──

        private static List<UIDocument> GetAttachedPanels()
        {
            var result = new List<UIDocument>();
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc.rootVisualElement != null && doc.rootVisualElement.panel != null)
                    result.Add(doc);
            }
            return result;
        }

        private static VisualElement GetRootElement(VisualElement el)
        {
            if (el.panel != null && el.panel.visualTree != null)
                return el.panel.visualTree;

            // Fallback: walk up the parent chain to the root
            var cur = el;
            while (cur.parent != null) cur = cur.parent;
            return cur;
        }

        private static bool IsInvisible(VisualElement el)
        {
            return el.resolvedStyle.display == DisplayStyle.None ||
                   el.resolvedStyle.visibility == Visibility.Hidden;
        }

        private static bool HasTextInputAncestor(VisualElement el)
        {
            var p = el.parent;
            while (p != null)
            {
                if (p is TextInputBaseField<string>) return true;
                p = p.parent;
            }
            return false;
        }
    }
}
