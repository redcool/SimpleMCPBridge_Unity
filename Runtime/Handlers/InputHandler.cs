using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
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
    public class InputHandler
    {
        [MCPTool(MCPMethodConst.CLICK_SCREEN, "Simulate a user click at a screen position. " +
            "Coordinates are normalized 0.0~1.0 (0.5,0.5 = center). " +
            "Goes through Unity EventSystem: RaycastAll → PointerDown → PointerUp → PointerClick. " +
            "Returns hit objects and which one received the click. " +
            "Requires an active EventSystem in the scene (Play Mode or Runtime).")]
        public string ClickScreen(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var nx = GetRequiredFloat(args, "x");
            var ny = GetRequiredFloat(args, "y");

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

            // Full click sequence: Down (hierarchy bubble) → Up → Click
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerClickHandler);

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

        // ── Helpers ──

        private static float GetRequiredFloat(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v))
                throw new System.ArgumentException($"Missing required parameter: '{key}'");
            return System.Convert.ToSingle(v, CultureInfo.InvariantCulture);
        }

        private static string GetObjectPath(GameObject go)
        {
            var segments = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
