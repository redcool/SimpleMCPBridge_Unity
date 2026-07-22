using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Screen-level interaction tools that work without Input System.
    /// Uses EventSystem (built-in Unity) for UI raycasting and event dispatch.
    /// Also supports 3D Physics raycasting with optional layer filter.
    /// </summary>
    [MCPToolClass]
    public class ScreenHandler
    {
        [MCPTool(MCPMethodConst.CLICK_SCREEN, "Simulate a user click at a screen position. " +
            "Coordinates are normalized 0.0~1.0 (0.5,0.5 = center). " +
            "Goes through Unity EventSystem: RaycastAll → PointerDown → PointerUp → PointerClick. " +
            "Returns hit objects and which one received the click. " +
            "Requires an active EventSystem in the scene (Play Mode or Runtime). " +
            "Works without Input System package — uses only built-in UnityEngine.EventSystems. " +
            "Optional 'layer' param (int layer index or string layer name) enables 3D Physics raycast " +
            "against that layer, returning 3D collider hits and executing click handlers on them.")]
        public static string ClickScreen(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("x", out var xVal) || !args.TryGetValue("y", out var yVal))
                return ErrorJson("click_screen requires 'x' and 'y' parameters (normalized 0-1)");

            // Optional: layer parameter for 3D physics raycast
            // Accepts: int (layer index 0-31), string (layer name like "Default"), or omitted (UI-only)
            int layerMask = -1; // -1 = not specified (UI-only mode)
            bool do3D = false;

            if (args.TryGetValue("layer", out var layerVal))
            {
                do3D = true;
                if (layerVal is long longVal)
                {
                    int layerIndex = (int)longVal;
                    layerMask = 1 << layerIndex;
                }
                else if (layerVal is int intVal)
                {
                    layerMask = 1 << intVal;
                }
                else if (layerVal is string layerName)
                {
                    // Support "All" or "-1" to raycast against all layers
                    if (layerName == "All" || layerName == "all" || layerName == "-1")
                    {
                        layerMask = -1; // all layers
                    }
                    else
                    {
                        int layerIndex = LayerMask.NameToLayer(layerName);
                        if (layerIndex >= 0)
                            layerMask = 1 << layerIndex;
                        else
                            return ErrorJson($"Layer '{layerName}' not found. Use LayerMask.NameToLayer compatible name.");
                    }
                }
            }

            // Optional: maxDistance for 3D raycast (default: float.MaxValue)
            float maxDistance = float.MaxValue;
            if (args.TryGetValue("maxDistance", out var distVal))
            {
                maxDistance = Convert.ToSingle(distVal, CultureInfo.InvariantCulture);
            }

            return ProcessClick(xVal, yVal, 0, do3D, layerMask, maxDistance);
        }

        public static string ProcessClick(object rawX, object rawY, int button)
        {
            return ProcessClick(rawX, rawY, button, false, -1, float.MaxValue);
        }

        public static string ProcessClick(object rawX, object rawY, int button,
            bool do3D, int layerMask, float maxDistance)
        {
            var nx = System.Convert.ToSingle(rawX, CultureInfo.InvariantCulture);
            var ny = System.Convert.ToSingle(rawY, CultureInfo.InvariantCulture);

            // Convert normalized coordinates to screen pixels
            // (0,0) = bottom-left, (1,1) = top-right
            var screenPos = new Vector2(nx * Screen.width, ny * Screen.height);

            var hitJsons = new List<string>();
            GameObject topHit = null;
            bool hadUIHits = false;

            // --- Phase 1: UI EventSystem raycast (always, if EventSystem exists) ---
            if (EventSystem.current != null)
            {
                var pointerData = new PointerEventData(EventSystem.current)
                {
                    position = screenPos,
                    button = PointerEventData.InputButton.Left,
                    pressPosition = screenPos,
                };

                var uiResults = new List<RaycastResult>();
                EventSystem.current.RaycastAll(pointerData, uiResults);

                foreach (var r in uiResults)
                {
                    hitJsons.Add(JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(r.gameObject.name)),
                        ("path", JsonHelper.EscapeString(GetObjectPath(r.gameObject))),
                        ("instanceId", r.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                        ("sortOrder", r.sortingOrder.ToString(CultureInfo.InvariantCulture)),
                        ("type", JsonHelper.EscapeString("UI"))
                    ));
                }

                if (uiResults.Count > 0)
                {
                    hadUIHits = true;
                    topHit = uiResults[0].gameObject;

                    // Execute pointer events on the topmost UI hit
                    ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerClickHandler);
                }
            }

            // --- Phase 2: 3D Physics raycast (optional, when layer is specified) ---
            RaycastHit hit3D = default;
            bool had3DHit = false;

            if (do3D)
            {
                // Find a camera: try Camera.main, then any active camera
                Camera cam = Camera.main;
                if (cam == null)
                {
                    var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                    foreach (var c in cams)
                    {
                        if (c.gameObject.activeInHierarchy && c.enabled)
                        {
                            cam = c;
                            break;
                        }
                    }
                }

                if (cam != null)
                {
                    var ray = cam.ScreenPointToRay(screenPos);
                    var finalMask = layerMask == -1 ? ~0 : layerMask;

                    if (Physics.Raycast(ray, out hit3D, maxDistance, finalMask))
                    {
                        had3DHit = true;
                        var hitGO = hit3D.collider.gameObject;

                        hitJsons.Add(JsonHelper.BuildJsonObject(
                            ("name", JsonHelper.EscapeString(hitGO.name)),
                            ("path", JsonHelper.EscapeString(GetObjectPath(hitGO))),
                            ("instanceId", hitGO.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                            ("point", JsonHelper.FloatArrayJson(new[] { hit3D.point.x, hit3D.point.y, hit3D.point.z })),
                            ("normal", JsonHelper.FloatArrayJson(new[] { hit3D.normal.x, hit3D.normal.y, hit3D.normal.z })),
                            ("distance", hit3D.distance.ToString(CultureInfo.InvariantCulture)),
                            ("collider", JsonHelper.EscapeString(hit3D.collider.GetType().Name)),
                            ("type", JsonHelper.EscapeString("3D"))
                        ));

                        // If no UI hit was on top, use the 3D hit as the click target
                        if (!hadUIHits)
                        {
                            topHit = hitGO;

                            // Try to execute pointer events on the 3D object
                            // This works if the camera has a PhysicsRaycaster component
                            if (EventSystem.current != null)
                            {
                                var pointerData = new PointerEventData(EventSystem.current)
                                {
                                    position = screenPos,
                                    button = PointerEventData.InputButton.Left,
                                    pressPosition = screenPos,
                                };

                                ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerDownHandler);
                                ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerUpHandler);
                                ExecuteEvents.ExecuteHierarchy(topHit, pointerData, ExecuteEvents.pointerClickHandler);
                            }
                        }
                    }
                }
            }

            // --- Build response ---
            if (hitJsons.Count == 0)
            {
                return JsonHelper.BuildJsonObject(
                    ("success", "false"),
                    ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                    ("normalizedPos", JsonHelper.FloatArrayJson(new[] { nx, ny })),
                    ("hitCount", "0"),
                    ("error", JsonHelper.EscapeString("No UI or 3D object found at position"))
                );
            }

            var clickedJson = topHit != null ? JsonHelper.BuildJsonObject(
                ("name", JsonHelper.EscapeString(topHit.name)),
                ("path", JsonHelper.EscapeString(GetObjectPath(topHit))),
                ("instanceId", topHit.GetInstanceID().ToString(CultureInfo.InvariantCulture))
            ) : "null";

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                ("normalizedPos", JsonHelper.FloatArrayJson(new[] { nx, ny })),
                ("hitCount", hitJsons.Count.ToString(CultureInfo.InvariantCulture)),
                ("clicked", clickedJson),
                ("hits", JsonHelper.BuildJsonArray(hitJsons.ToArray()))
            );
        }

    }
}
