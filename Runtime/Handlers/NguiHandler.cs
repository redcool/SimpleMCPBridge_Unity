#if NGUI_ON
using SimpleMCPBridge.Runtime.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// NGUI (legacy third-party UI framework) analysis tools — memory-based perception,
    /// independent from the uGUI tool set (ui.*). Both can be enabled at once, or either
    /// can be toggled off via tools.disable by category (Ngui / Ui).
    ///
    /// Tools:
    ///   - ngui.get_texts    — read all UILabel text (memory-based, no OCR)
    ///   - ngui.find         — find interactive NGUI elements (UIButton/UIToggle/UISlider/UIInput)
    ///   - ngui.find_widgets — find all UIWidget (UITexture/UISprite/UILabel/...) with screen rects
    ///
    /// Compiled only when the NGUI package is present (NGUI_ON symbol defined via
    /// asmdef versionDefines for com.tasharen.ngui). When NGUI is absent, this class
    /// does not exist and no ngui.* tools are registered.
    /// </summary>
    [MCPToolClass]
    public class NguiHandler
    {
        // ══════════════════════════════════════════════════════════════
        //  ngui.get_texts
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NGUI_GET_TEXTS,
            "Read all on-screen NGUI text from memory (no OCR, zero token cost). " +
            "Scans UILabel components. Returns each text element with: text content, type, " +
            "transform path, normalized screen rect [xMin,yMin,xMax,yMax], center position, " +
            "font size, alignment, and color. Also returns screen dimensions for coordinate " +
            "reference. Ideal for reading menus, HUD values, dialogue in legacy NGUI projects. " +
            "Optional filter: 'contains' (substring, case-insensitive).")]
        public static string GetNGUITexts(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var textJsons = NGUIAnalysisTools.ScanAllTexts();
                var filter = GetString(args, "contains", "");

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
                return ErrorJson($"NGUI text scan failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ngui.find
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NGUI_FIND,
            "Find interactive NGUI elements (UIButton, UIToggle, UISlider, UIInput) " +
            "with their screen positions and state. " +
            "Each element returns: type, path, label/text, interactable, " +
            "normalized screen rect [xMin,yMin,xMax,yMax], and center position. " +
            "Optional filter: 'type' (UIButton/UIToggle/UISlider/UIInput), " +
            "'contains' (label/text contains), 'interactable' (bool). " +
            "Also returns screen dimensions. " +
            "Use the 'center' position to target clicks with input.click_screen " +
            "(NGUI's UICamera responds to screen-space clicks too).")]
        public static string FindNGUI(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var elemJsons = NGUIAnalysisTools.ScanInteractiveUI();
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
                return ErrorJson($"NGUI find failed: {ex.Message}");
            }
        }
        // ══════════════════════════════════════════════════════════════
        //  ngui.find_widgets
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NGUI_FIND_WIDGETS,
            "Find all NGUI UIWidget components (UITexture, UISprite, UILabel, ...) " +
            "with their screen positions. Each widget returns: type, name, path, " +
            "instanceId, normalized screen rect [xMin,yMin,xMax,yMax], and center. " +
            "UILabel additionally includes text/fontSize/alignment/color. " +
            "Optional filter: 'type' (exact widget type, e.g. UITexture/UISprite/UILabel), " +
            "'contains' (substring on widget name, or label text for UILabel). " +
            "Also returns screen dimensions. Use 'center' to target clicks with " +
            "input.click_screen (NGUI's UICamera responds to screen-space clicks too).")]
        public static string FindNGUIWidgets(string paramsJson)
        {
            try
            {
                var args = ParseJsonObject(paramsJson);
                var typeFilter = GetString(args, "type", "");
                var containsFilter = GetString(args, "contains", "");

                var widgetJsons = NGUIAnalysisTools.ScanWidgets(typeFilter, containsFilter);

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("count", widgetJsons.Count.ToString(CultureInfo.InvariantCulture)),
                    ("screenWidth", Screen.width.ToString(CultureInfo.InvariantCulture)),
                    ("screenHeight", Screen.height.ToString(CultureInfo.InvariantCulture)),
                    ("widgets", JsonHelper.BuildJsonArray(widgetJsons.ToArray()))
                );
            }
            catch (Exception ex)
            {
                return ErrorJson($"NGUI widget scan failed: {ex.Message}");
            }
        }
    }
}
#endif
