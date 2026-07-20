using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Handles NavMesh-related MCP operations (pathfinding, sampling, query).
    /// Works in both Editor and Runtime (no UnityEditor dependency, no NavMeshAgent needed).
    /// </summary>
    [MCPToolClass]
    public class NavHandler
    {
        // ══════════════════════════════════════════════════════════════
        //  nav.has_navmesh
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NAV_HAS_NAVMESH,
            "Check if a NavMesh exists in the current scene. " +
            "No parameters. " +
            "Returns: success, hasNavMesh (bool), vertexCount, triangleCount.")]
        public static string HasNavMesh(string paramsJson)
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var hasNavMesh = triangulation.vertices.Length > 0;
            var vertexCount = triangulation.vertices.Length;
            var triangleCount = triangulation.indices.Length / 3;

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("hasNavMesh", hasNavMesh ? "true" : "false"),
                ("vertexCount", vertexCount.ToString(CultureInfo.InvariantCulture)),
                ("triangleCount", triangleCount.ToString(CultureInfo.InvariantCulture))
            );
        }

        // ══════════════════════════════════════════════════════════════
        //  nav.sample_position
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NAV_SAMPLE_POSITION,
            "Snap a world position to the nearest point on the NavMesh. " +
            "Params: position (float[3], required), " +
            "maxDistance (float, optional, default 2.0), areaMask (int, optional, default -1 = all areas). " +
            "Returns: success, hit (bool), position (snapped [x,y,z]), distance (float), areaIndex (int).")]
        public static string SamplePosition(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            var posArr = GetOptionalFloatArray(args, "position");
            if (posArr == null || posArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'position' (float[3])");

            var position = new Vector3(posArr[0], posArr[1], posArr[2]);

            var maxDistance = 2.0f;
            if (args.TryGetValue("maxDistance", out var maxDistObj) && maxDistObj != null)
                maxDistance = Convert.ToSingle(maxDistObj, CultureInfo.InvariantCulture);

            var areaMask = (int)GetOptionalInt(args, "areaMask").GetValueOrDefault(NavMesh.AllAreas);

            if (NavMesh.SamplePosition(position, out var hit, maxDistance, areaMask))
            {
                int areaIndex = 0;
                if (hit.mask > 0)
                    areaIndex = (int)(Mathf.Log(hit.mask) / Mathf.Log(2));

                return JsonHelper.BuildJsonObject(
                    ("success", "true"),
                    ("hit", "true"),
                    ("position", JsonHelper.FloatArrayJson(new[] { hit.position.x, hit.position.y, hit.position.z })),
                    ("distance", hit.distance.ToString("F4", CultureInfo.InvariantCulture)),
                    ("areaIndex", areaIndex.ToString(CultureInfo.InvariantCulture))
                );
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("hit", "false")
            );
        }

        // ══════════════════════════════════════════════════════════════
        //  nav.query_path
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NAV_QUERY_PATH,
            "Find a path between two points on the NavMesh. " +
            "Params: start (float[3], required), end (float[3], required), " +
            "areaMask (int, optional, default -1 = all areas), " +
            "snapDistance (float, optional, default 2.0). " +
            "Returns: success, reachable (bool), status (PathComplete/PathPartial/PathInvalid), " +
            "waypointCount, waypoints ([x,y,z],...), startSnapped, endSnapped, distance (total path length).")]
        public static string QueryPath(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            // Check NavMesh exists
            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices.Length == 0)
                return ErrorJson("No NavMesh baked in scene");

            var startArr = GetOptionalFloatArray(args, "start");
            if (startArr == null || startArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'start' (float[3])");

            var endArr = GetOptionalFloatArray(args, "end");
            if (endArr == null || endArr.Length < 3)
                return ErrorJson("Missing or invalid required parameter 'end' (float[3])");

            var startPos = new Vector3(startArr[0], startArr[1], startArr[2]);
            var endPos = new Vector3(endArr[0], endArr[1], endArr[2]);

            var areaMask = (int)GetOptionalInt(args, "areaMask").GetValueOrDefault(NavMesh.AllAreas);

            var snapDistance = 2.0f;
            if (args.TryGetValue("snapDistance", out var snapObj) && snapObj != null)
                snapDistance = Convert.ToSingle(snapObj, CultureInfo.InvariantCulture);

            // Snap start
            if (!NavMesh.SamplePosition(startPos, out var startHit, snapDistance, areaMask))
                return ErrorJson("Start position not on NavMesh");

            // Snap end
            if (!NavMesh.SamplePosition(endPos, out var endHit, snapDistance, areaMask))
                return ErrorJson("End position not on NavMesh");

            // Calculate path
            var path = new NavMeshPath();
            NavMesh.CalculatePath(startHit.position, endHit.position, areaMask, path);

            // Build waypoints array
            var waypointJsons = new List<string>();
            var corners = path.corners;
            foreach (var c in corners)
            {
                waypointJsons.Add(JsonHelper.FloatArrayJson(new[] { c.x, c.y, c.z }));
            }

            // Calculate total distance
            float totalDist = 0f;
            for (int i = 1; i < corners.Length; i++)
            {
                totalDist += Vector3.Distance(corners[i - 1], corners[i]);
            }

            // Determine status string
            string statusStr;
            bool reachable;
            switch (path.status)
            {
                case NavMeshPathStatus.PathComplete:
                    statusStr = "PathComplete";
                    reachable = true;
                    break;
                case NavMeshPathStatus.PathPartial:
                    statusStr = "PathPartial";
                    reachable = true;
                    break;
                default:
                    statusStr = "PathInvalid";
                    reachable = false;
                    break;
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("reachable", reachable ? "true" : "false"),
                ("status", JsonHelper.EscapeString(statusStr)),
                ("waypointCount", corners.Length.ToString(CultureInfo.InvariantCulture)),
                ("waypoints", JsonHelper.BuildJsonArray(waypointJsons.ToArray())),
                ("startSnapped", JsonHelper.FloatArrayJson(new[] { startHit.position.x, startHit.position.y, startHit.position.z })),
                ("endSnapped", JsonHelper.FloatArrayJson(new[] { endHit.position.x, endHit.position.y, endHit.position.z })),
                ("distance", totalDist.ToString("F4", CultureInfo.InvariantCulture))
            );
        }

        // ══════════════════════════════════════════════════════════════
        //  nav.move_to
        // ══════════════════════════════════════════════════════════════

        [MCPTool(MCPMethodConst.NAV_MOVE_TO,
            "Set NavMeshAgent destination — auto pathfind to target. " +
            "Params: position (JSON array [x,y,z], required) OR targetInstanceId (int) OR targetPath (string) — destination, " +
            "stopDistance (float, optional, default 1.0) — how close to stop, " +
            "agentInstanceId (int, optional) — which NavMeshAgent to move (default: player's). " +
            "Returns: pathStatus (Complete/Partial/Invalid), pathPending (bool), distance (float), corners (int).",
            Platform = MCPToolPlatforms.All)]
        public static string MoveTo(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);

            // ── Resolve target position ──
            Vector3 targetPosition;
            var posArr = GetOptionalFloatArray(args, "position");
            if (posArr != null && posArr.Length >= 3)
            {
                targetPosition = new Vector3(posArr[0], posArr[1], posArr[2]);
            }
            else
            {
                var targetId = GetOptionalInt(args, "targetInstanceId");
                if (targetId.HasValue)
                {
                    var targetObj = SceneHandler.FindObjectById(targetId.Value);
                    if (targetObj == null)
                        return ErrorJson("Target object not found for targetInstanceId");
                    targetPosition = targetObj.transform.position;
                }
                else
                {
                    var targetPath = GetString(args, "targetPath");
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        var targetObj = GameObject.Find(targetPath);
                        if (targetObj == null)
                            return ErrorJson("Target object not found for targetPath");
                        targetPosition = targetObj.transform.position;
                    }
                    else
                    {
                        return ErrorJson("Missing required parameter: one of 'position', 'targetInstanceId', or 'targetPath'");
                    }
                }
            }

            // ── Resolve stopDistance (optional, default 1.0) ──
            var stopDistance = 1.0f;
            if (args.TryGetValue("stopDistance", out var stopObj) && stopObj != null)
                stopDistance = Convert.ToSingle(stopObj, CultureInfo.InvariantCulture);

            // ── Resolve NavMeshAgent ──
            NavMeshAgent agent = null;
            var agentId = GetOptionalInt(args, "agentInstanceId");
            if (agentId.HasValue)
            {
                var agentObj = SceneHandler.FindObjectById(agentId.Value);
                if (agentObj != null)
                    agent = agentObj.GetComponent<NavMeshAgent>();
            }
            else
            {
                var playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null)
                    agent = playerObj.GetComponent<NavMeshAgent>();
            }

            if (agent == null)
                return ErrorJson("No NavMeshAgent found");

            // ── Set destination ──
            agent.stoppingDistance = stopDistance;
            agent.destination = targetPosition;

            // ── Build path status string ──
            string statusStr;
            switch (agent.path.status)
            {
                case NavMeshPathStatus.PathComplete:
                    statusStr = "PathComplete";
                    break;
                case NavMeshPathStatus.PathPartial:
                    statusStr = "PathPartial";
                    break;
                default:
                    statusStr = "PathInvalid";
                    break;
            }

            return JsonHelper.BuildJsonObject(
                ("success", "true"),
                ("pathStatus", JsonHelper.EscapeString(statusStr)),
                ("pathPending", agent.pathPending ? "true" : "false"),
                ("distance", agent.remainingDistance.ToString("F4", CultureInfo.InvariantCulture)),
                ("corners", agent.path.corners.Length.ToString(CultureInfo.InvariantCulture))
            );
        }
    }
}
