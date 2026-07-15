using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles physics-related MCP operations (raycast, overlap, etc.).
    /// Works in both Editor and Runtime (no UnityEditor dependency).
    /// </summary>
    [MCPToolClass]
    public class PhysicsHandler
    {
        [MCPTool(MCPMethodConst.PHYSICS_RAYCAST, "Cast a ray from origin in direction and return the first hit. " +
            "Params: origin (float[3]), direction (float[3]), maxDistance (float, optional), layerMask (int, optional). " +
            "Returns hit point, normal, distance, collider info (gameObject path, instanceId).")]
        public static string Raycast(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var originArr = GetOptionalFloatArray(args, "origin");
            if (originArr == null || originArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'origin' (float[3])");

            var dirArr = GetOptionalFloatArray(args, "direction");
            if (dirArr == null || dirArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'direction' (float[3])");

            var origin = new Vector3(originArr[0], originArr[1], originArr[2]);
            var direction = new Vector3(dirArr[0], dirArr[1], dirArr[2]).normalized;

            var maxDistance = float.MaxValue;
            if (args.TryGetValue("maxDistance", out var maxDistObj) && maxDistObj != null)
                maxDistance = Convert.ToSingle(maxDistObj, CultureInfo.InvariantCulture);

            var layerMask = (int)GetOptionalInt(args, "layerMask").GetValueOrDefault(Physics.DefaultRaycastLayers);

            if (Physics.Raycast(origin, direction, out var hit, maxDistance, layerMask))
            {
                var go = hit.collider.gameObject;
                return JsonHelper.BuildJsonObject(
                    ("hit", "true"),
                    ("point", JsonHelper.FloatArrayJson(new[] { hit.point.x, hit.point.y, hit.point.z })),
                    ("normal", JsonHelper.FloatArrayJson(new[] { hit.normal.x, hit.normal.y, hit.normal.z })),
                    ("distance", hit.distance.ToString("F4", CultureInfo.InvariantCulture)),
                    ("colliderType", JsonHelper.EscapeString(hit.collider.GetType().Name)),
                    ("gameObjectName", JsonHelper.EscapeString(go.name)),
                    ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go)))
                );
            }

            return JsonHelper.BuildJsonObject(
                ("hit", "false")
            );
        }
    }
}

