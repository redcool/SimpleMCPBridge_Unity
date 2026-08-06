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
        [MCPParam("origin", Type = "array", Required = true, Description = "[x,y,z] ray origin")]
        [MCPParam("direction", Type = "array", Required = true, Description = "[x,y,z] ray direction")]
        [MCPParam("maxDistance", Type = "number", Description = "Max ray distance (default max)")]
        [MCPParam("layerMask", Type = "integer", Description = "Layer mask filter (default all)")]
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

        // ══════════════════════════════════════════════════════════════
        //  physics.box_cast
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PHYSICS_BOX_CAST,
            "Sweep a box along a direction and return the first hit. " +
            "Params: origin (float[3], required), halfExtents (float[3], required), " +
            "direction (float[3], required), rotation (float[4], optional — quaternion [x,y,z,w]), " +
            "maxDistance (float, optional, default 100), layerMask (int, optional, default -1). " +
            "Returns hit point, normal, distance, collider info (gameObject path, instanceId).")]
        [MCPParam("origin", Type = "array", Required = true, Description = "[x,y,z] box origin")]
        [MCPParam("halfExtents", Type = "array", Required = true, Description = "[x,y,z] half extents")]
        [MCPParam("direction", Type = "array", Required = true, Description = "[x,y,z] sweep direction")]
        [MCPParam("rotation", Type = "array", Description = "Quaternion [x,y,z,w] rotation")]
        [MCPParam("maxDistance", Type = "number", Description = "Max sweep distance (default 100)")]
        [MCPParam("layerMask", Type = "integer", Description = "Layer mask filter (default -1)")]
        public static string BoxCast(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var originArr = GetOptionalFloatArray(args, "origin");
            if (originArr == null || originArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'origin' (float[3])");

            var halfExtArr = GetOptionalFloatArray(args, "halfExtents");
            if (halfExtArr == null || halfExtArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'halfExtents' (float[3])");

            var dirArr = GetOptionalFloatArray(args, "direction");
            if (dirArr == null || dirArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'direction' (float[3])");

            var origin = new Vector3(originArr[0], originArr[1], originArr[2]);
            var halfExtents = new Vector3(halfExtArr[0], halfExtArr[1], halfExtArr[2]);
            var direction = new Vector3(dirArr[0], dirArr[1], dirArr[2]).normalized;

            var rotationArr = GetOptionalFloatArray(args, "rotation");
            var rotation = Quaternion.identity;
            if (rotationArr != null && rotationArr.Length >= 4)
                rotation = new Quaternion(rotationArr[0], rotationArr[1], rotationArr[2], rotationArr[3]);

            var maxDistance = 100f;
            if (args.TryGetValue("maxDistance", out var maxDistObj) && maxDistObj != null)
                maxDistance = Convert.ToSingle(maxDistObj, CultureInfo.InvariantCulture);

            var layerMask = (int)GetOptionalInt(args, "layerMask").GetValueOrDefault(-1);

            if (Physics.BoxCast(origin, halfExtents, direction, out var hit, rotation, maxDistance, layerMask, QueryTriggerInteraction.UseGlobal))
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

        // ══════════════════════════════════════════════════════════════
        //  physics.sphere_cast
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PHYSICS_SPHERE_CAST,
            "Sweep a sphere along a direction and return the first hit. " +
            "Params: origin (float[3], required), radius (float, required), " +
            "direction (float[3], required), " +
            "maxDistance (float, optional, default 100), layerMask (int, optional, default -1). " +
            "Returns hit point, normal, distance, collider info (gameObject path, instanceId).")]
        [MCPParam("origin", Type = "array", Required = true, Description = "[x,y,z] sphere origin")]
        [MCPParam("radius", Type = "number", Required = true, Description = "Sphere radius")]
        [MCPParam("direction", Type = "array", Required = true, Description = "[x,y,z] sweep direction")]
        [MCPParam("maxDistance", Type = "number", Description = "Max sweep distance (default 100)")]
        [MCPParam("layerMask", Type = "integer", Description = "Layer mask filter (default -1)")]
        public static string SphereCast(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var originArr = GetOptionalFloatArray(args, "origin");
            if (originArr == null || originArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'origin' (float[3])");

            if (!args.TryGetValue("radius", out var radiusObj) || radiusObj == null)
                return ErrorJson("Missing or invalid required parameter 'radius' (float)");

            var dirArr = GetOptionalFloatArray(args, "direction");
            if (dirArr == null || dirArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'direction' (float[3])");

            var origin = new Vector3(originArr[0], originArr[1], originArr[2]);
            var radius = Convert.ToSingle(radiusObj, CultureInfo.InvariantCulture);
            var direction = new Vector3(dirArr[0], dirArr[1], dirArr[2]).normalized;

            var maxDistance = 100f;
            if (args.TryGetValue("maxDistance", out var maxDistObj) && maxDistObj != null)
                maxDistance = Convert.ToSingle(maxDistObj, CultureInfo.InvariantCulture);

            var layerMask = (int)GetOptionalInt(args, "layerMask").GetValueOrDefault(-1);

            if (Physics.SphereCast(origin, radius, direction, out var hit, maxDistance, layerMask, QueryTriggerInteraction.UseGlobal))
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

        // ══════════════════════════════════════════════════════════════
        //  physics.overlap_sphere
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PHYSICS_OVERLAP_SPHERE,
            "Find all colliders within a sphere at a given point. " +
            "Params: center (float[3], required), radius (float, required), " +
            "layerMask (int, optional, default -1). " +
            "Returns: success, count, and array of collider info (name, instanceId, path, position, tag, layer).")]
        [MCPParam("center", Type = "array", Required = true, Description = "[x,y,z] sphere center")]
        [MCPParam("radius", Type = "number", Required = true, Description = "Sphere radius")]
        [MCPParam("layerMask", Type = "integer", Description = "Layer mask filter (default -1)")]
        public static string OverlapSphere(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var centerArr = GetOptionalFloatArray(args, "center");
            if (centerArr == null || centerArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'center' (float[3])");

            if (!args.TryGetValue("radius", out var radiusObj) || radiusObj == null)
                return ErrorJson("Missing or invalid required parameter 'radius' (float)");

            var center = new Vector3(centerArr[0], centerArr[1], centerArr[2]);
            var radius = Convert.ToSingle(radiusObj, CultureInfo.InvariantCulture);
            var layerMask = (int)GetOptionalInt(args, "layerMask").GetValueOrDefault(-1);

            var colliders = Physics.OverlapSphere(center, radius, layerMask, QueryTriggerInteraction.UseGlobal);

            var colliderJsons = new List<string>();
            foreach (var col in colliders)
            {
                var go = col.gameObject;
                colliderJsons.Add(JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(go.name)),
                    ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("position", JsonHelper.FloatArrayJson(new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z })),
                    ("tag", JsonHelper.EscapeString(go.tag)),
                    ("layer", go.layer.ToString(CultureInfo.InvariantCulture))
                ));
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("count", colliders.Length.ToString(CultureInfo.InvariantCulture)),
                ("colliders", JsonHelper.BuildJsonArray(colliderJsons.ToArray()))
            );
        }

        // ══════════════════════════════════════════════════════════════
        //  physics.overlap_box
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.PHYSICS_OVERLAP_BOX,
            "Find all colliders within a box at a given point. " +
            "Params: center (float[3], required), halfExtents (float[3], required), " +
            "rotation (float[4], optional — quaternion [x,y,z,w], default identity), " +
            "layerMask (int, optional, default -1). " +
            "Returns: success, count, and array of collider info (name, instanceId, path, position, tag, layer).")]
        [MCPParam("center", Type = "array", Required = true, Description = "[x,y,z] box center")]
        [MCPParam("halfExtents", Type = "array", Required = true, Description = "[x,y,z] half extents")]
        [MCPParam("rotation", Type = "array", Description = "Quaternion [x,y,z,w] rotation")]
        [MCPParam("layerMask", Type = "integer", Description = "Layer mask filter (default -1)")]
        public static string OverlapBox(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var centerArr = GetOptionalFloatArray(args, "center");
            if (centerArr == null || centerArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'center' (float[3])");

            var halfExtArr = GetOptionalFloatArray(args, "halfExtents");
            if (halfExtArr == null || halfExtArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'halfExtents' (float[3])");

            var center = new Vector3(centerArr[0], centerArr[1], centerArr[2]);
            var halfExtents = new Vector3(halfExtArr[0], halfExtArr[1], halfExtArr[2]);

            var rotationArr = GetOptionalFloatArray(args, "rotation");
            var rotation = Quaternion.identity;
            if (rotationArr != null && rotationArr.Length >= 4)
                rotation = new Quaternion(rotationArr[0], rotationArr[1], rotationArr[2], rotationArr[3]);

            var layerMask = (int)GetOptionalInt(args, "layerMask").GetValueOrDefault(-1);

            var colliders = Physics.OverlapBox(center, halfExtents, rotation, layerMask, QueryTriggerInteraction.UseGlobal);

            var colliderJsons = new List<string>();
            foreach (var col in colliders)
            {
                var go = col.gameObject;
                colliderJsons.Add(JsonHelper.BuildJsonObject(
                    ("name", JsonHelper.EscapeString(go.name)),
                    ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                    ("path", JsonHelper.EscapeString(GetObjectPath(go))),
                    ("position", JsonHelper.FloatArrayJson(new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z })),
                    ("tag", JsonHelper.EscapeString(go.tag)),
                    ("layer", go.layer.ToString(CultureInfo.InvariantCulture))
                ));
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("count", colliders.Length.ToString(CultureInfo.InvariantCulture)),
                ("colliders", JsonHelper.BuildJsonArray(colliderJsons.ToArray()))
            );
        }
    }
}

