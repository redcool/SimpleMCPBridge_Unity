using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Castle building click simulation via Lua FakeHitResultEvent.
    /// Works around Input System Both-mode bridging not mirroring
    /// programmatic unnamed-mouse events to legacy Input.GetMouseButton().
    /// </summary>
    [MCPToolClass]
    public class BuildingHandler
    {
        private const int Layer_WorldClick = 20;
        private const int Layer_TopMost3DTransparent = 22;
        private static readonly int LayerMask_Building = (1 << Layer_WorldClick) | (1 << Layer_TopMost3DTransparent);

        private static int _lastDispatchFrame = -1;
        private static string _lastDispatchObject = "";
        private static string _lastError = "";

        /// <summary>
        /// Cached reflection handles for LuaManager access.
        /// Populated on first successful call (avoids repeated reflection overhead).
        /// </summary>
        private static object _luaEnv = null;
        private static MethodInfo _doStringMethod = null;

        [MCPTool(MCPMethodConst.CLICK_BUILDING,
            "Click a 3D castle building at a normalized screen position. " +
            "Uses Physics.Raycast (layers 20 + 22) + Lua FakeHitResultEvent " +
            "to bypass Input System bridging limitations. " +
            "Params: x (float 0-1), y (float 0-1). " +
            "Returns: success, hitObject, hitPath, hitPoint, luaFired.")]
        public static string ClickBuilding(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("x", out var xVal) || !args.TryGetValue("y", out var yVal))
                return ErrorJson("castle.click_building requires 'x' and 'y' parameters (normalized 0-1)");

            var nx = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
            var ny = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
            var screenPos = new Vector2(nx * Screen.width, ny * Screen.height);

            // ── Find active camera ──
            Camera cam = Camera.main;
            if (cam == null)
            {
                var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                foreach (var c in cams)
                {
                    if (c != null && c.gameObject.activeInHierarchy && c.enabled)
                    {
                        cam = c;
                        break;
                    }
                }
            }
            if (cam == null)
                return ErrorJson("No active camera found in scene");

            // ── Physics raycast (same layer mask as CastleInputManager:InvokeOnClick) ──
            var ray = cam.ScreenPointToRay(screenPos);
            var hitResults = new RaycastHit[16];
            int hitCount = Physics.RaycastNonAlloc(ray, hitResults, 800f, LayerMask_Building);

            if (hitCount == 0)
            {
                // Try broader search: any collider (the building might use different layers)
                var broadHits = new RaycastHit[16];
                int broadCount = Physics.RaycastNonAlloc(ray, broadHits, 800f, ~0);
                if (broadCount == 0)
                    return ErrorJson("No building hit at position (no 3D collider found)");

                // Return info about what WAS hit
                var broadInfo = new List<string>();
                for (int i = 0; i < Math.Min(broadCount, 5); i++)
                {
                    var h = broadHits[i];
                    broadInfo.Add(JsonHelper.BuildJsonObject(
                        ("name", JsonHelper.EscapeString(h.transform?.gameObject?.name ?? "?")),
                        ("layer", h.transform?.gameObject.layer.ToString() ?? "?"),
                        ("layerName", JsonHelper.EscapeString(LayerMask.LayerToName(h.transform?.gameObject.layer ?? 0))),
                        ("distance", h.distance.ToString(CultureInfo.InvariantCulture))
                    ));
                }
                return JsonHelper.BuildJsonObject(
                    ("success", "false"),
                    ("error", JsonHelper.EscapeString("No building hit on layers 20|22. Use 'input.click_screen?layer=X' for debug.")),
                    ("nearbyHits", JsonHelper.BuildJsonArray(broadInfo.ToArray()))
                );
            }

            // ── Sort: layer 22 (HUD) first, then by distance ──
            Array.Sort(hitResults, 0, hitCount, Comparer<RaycastHit>.Create((a, b) =>
            {
                bool isHudA = a.transform?.gameObject.layer == Layer_TopMost3DTransparent;
                bool isHudB = b.transform?.gameObject.layer == Layer_TopMost3DTransparent;
                if (isHudA == isHudB)
                    return a.distance.CompareTo(b.distance);
                return isHudA ? -1 : 1;
            }));

            var hit = hitResults[0];
            var hitGO = hit.transform?.gameObject;
            if (hitGO == null)
                return ErrorJson("Raycast hit but GameObject is null");

            // ── Fire FakeHitResultEvent via Lua ──
            bool luaFired = FireFakeHitResult(hitGO, hit.point);

            // Prevent rapid double-dispatch on same object (same frame)
            int currentFrame = Time.frameCount;
            if (luaFired && currentFrame == _lastDispatchFrame && hitGO.GetInstanceID().ToString() == _lastDispatchObject)
                luaFired = false; // already dispatched this frame

            if (luaFired)
            {
                _lastDispatchFrame = currentFrame;
                _lastDispatchObject = hitGO.GetInstanceID().ToString();
            }

            // ── Build response ──
            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("screenPos", JsonHelper.FloatArrayJson(new[] { screenPos.x, screenPos.y })),
                ("normalizedPos", JsonHelper.FloatArrayJson(new[] { nx, ny })),
                ("hitObject", JsonHelper.EscapeString(hitGO.name)),
                ("hitPath", JsonHelper.EscapeString(GetObjectPath(hitGO))),
                ("hitInstanceId", hitGO.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("hitLayer", hitGO.layer.ToString()),
                ("hitPoint", JsonHelper.FloatArrayJson(new[] { hit.point.x, hit.point.y, hit.point.z })),
                ("hitDistance", hit.distance.ToString("F2", CultureInfo.InvariantCulture)),
                ("luaFired", luaFired ? "true" : "false"),
                ("lastError", JsonHelper.EscapeString(_lastError)),
                ("message", luaFired
                    ? JsonHelper.EscapeString("Building click dispatched via FakeHitResultEvent")
                    : JsonHelper.EscapeString("Raycast hit but Lua dispatch failed"))
            );
        }

        /// <summary>
        /// Fire a FakeHitResultEvent on the Lua EventHandler via xLua.
        /// Uses reflection to find LuaManager.Instance.Env.DoString.
        /// </summary>
        private static bool FireFakeHitResult(GameObject go, Vector3 worldPoint)
        {
            try
            {
                _lastError = "";
                // ── Resolve LuaManager.Env ──
                if (_luaEnv == null)
                {
                    // Search all loaded assemblies for Wod.LuaManager
                    Type luaMgrType = null;
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            luaMgrType = asm.GetType("Wod.LuaManager");
                            if (luaMgrType != null) break;
                        }
                        catch { }
                    }

                    if (luaMgrType == null)
                    {
                        _lastError = "Wod.LuaManager type not found in any assembly";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }

                    var instanceProp = luaMgrType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    if (instanceProp == null)
                    {
                        _lastError = "LuaManager.Instance property not found";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }

                    var instance = instanceProp.GetValue(null);
                    if (instance == null)
                    {
                        _lastError = "LuaManager.Instance is null (Lua not initialized?)";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }

                    var envProp = luaMgrType.GetProperty("Env",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (envProp == null)
                    {
                        _lastError = "LuaManager.Env property not found";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }

                    _luaEnv = envProp.GetValue(instance);
                    if (_luaEnv == null)
                    {
                        _lastError = "LuaManager.Env is null";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }

                    // Cache DoString method — find by name + compatible params
                    _doStringMethod = null;
                    foreach (var m in _luaEnv.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "DoString") continue;
                        var ps = m.GetParameters();
                        if (ps.Length < 2) continue;
                        if (ps[0].ParameterType == typeof(string) &&
                            ps[1].ParameterType == typeof(string))
                        {
                            _doStringMethod = m;
                            break;
                        }
                    }
                    if (_doStringMethod == null)
                    {
                        _lastError = "LuaEnv.DoString(string,string,...) not found";
                        Debug.LogWarning("[BuildingHandler] " + _lastError);
                        return false;
                    }
                }

                // ── Build Lua script ──
                int instanceId = go.GetInstanceID();
                string posX = worldPoint.x.ToString("R", CultureInfo.InvariantCulture);
                string posY = worldPoint.y.ToString("R", CultureInfo.InvariantCulture);
                string posZ = worldPoint.z.ToString("R", CultureInfo.InvariantCulture);
                string objName = go.name;

                string script = $@"
local success = false
-- Strategy 1: find by InstanceID via Resources.InstanceIDToObject
local obj = CS.UnityEngine.Resources.InstanceIDToObject({instanceId})
if obj then
    local go = obj.gameObject
    if go then
        local FakeHitResultEvent = require('Event.FakeHitResultEvent')
        local evt = FakeHitResultEvent()
        evt.transform = go.transform
        evt.point = CS.UnityEngine.Vector3({posX}, {posY}, {posZ})
        FMainGame.Instance().EventHandler:raiseHandlerEvent(FakeHitResultEvent, evt)
        success = true
    end
end
if not success then
    -- Strategy 2: find by name
    local go = CS.UnityEngine.GameObject.Find('{objName}')
    if go then
        local FakeHitResultEvent = require('Event.FakeHitResultEvent')
        local evt = FakeHitResultEvent()
        evt.transform = go.transform
        evt.point = CS.UnityEngine.Vector3({posX}, {posY}, {posZ})
        FMainGame.Instance().EventHandler:raiseHandlerEvent(FakeHitResultEvent, evt)
        success = true
    end
end
if not success then
    -- Strategy 3: exhaustive search by instanceId
    local allGOs = CS.UnityEngine.GameObject.FindObjectsOfType(typeof(CS.UnityEngine.GameObject))
    for i = 0, allGOs.Length - 1 do
        if allGOs[i]:GetInstanceID() == {instanceId} then
            require('Event.FakeHitResultEvent')
            local evt = FakeHitResultEvent()
            evt.transform = allGOs[i].transform
            evt.point = CS.UnityEngine.Vector3({posX}, {posY}, {posZ})
            FMainGame.Instance().EventHandler:raiseHandlerEvent(FakeHitResultEvent, evt)
            success = true
            break
        end
    end
end
return success
";

                // ── Execute (pass null for optional LuaTable env param) ──
                var paramCount = _doStringMethod.GetParameters().Length;
                var invokeArgs = paramCount <= 2
                    ? new object[] { script, "BuildingHandler.ClickBuilding" }
                    : new object[] { script, "BuildingHandler.ClickBuilding", null };
                _doStringMethod.Invoke(_luaEnv, invokeArgs);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"Dispatch exception: {ex.GetType().Name}: {ex.Message}";
                Debug.LogError($"[BuildingHandler] Failed to fire FakeHitResultEvent: {ex}");
                return false;
            }
        }
    }
}
