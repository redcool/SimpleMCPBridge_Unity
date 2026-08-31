using System;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using SceneManagement = UnityEngine.SceneManagement;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // SceneHandler 查询组（partial）：scene.get_hierarchy / get_objects / get_objects_by_*
    public partial class SceneHandler
    {
[MCPTool(MCPMethodConst.GET_HIERARCHY, "Get the full scene hierarchy as a tree of objects with position, components, children, and transform path")]
        public static string GetHierarchy(string paramsJson)
        {
            var rootObjects = SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var entries = new List<string>();
            foreach (var root in rootObjects)
            {
                entries.Add(SceneObjectTools.BuildTreeEntry(root, root.name));
            }
            return JsonHelper.BuildJsonArray(entries.ToArray());
        }



        [MCPTool(MCPMethodConst.GET_OBJECTS, "Find GameObjects in the scene by optional name filter — returns instanceId + path for each. " +
            "If nameContains contains '/', it is treated as a transform path (e.g. 'Canvas/Button').")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter, or transform path with '/'")]
        public static string GetObjects(string paramsJson)
        {
            var filter = ParseJsonObject(paramsJson);
            filter.TryGetValue("nameContains", out var nameFilterObj);
            var nameFilter = nameFilterObj as string;

            // If nameContains contains '/', treat it as a transform path
            if (!string.IsNullOrEmpty(nameFilter) && nameFilter.Contains("/"))
            {
                var go = FindObjectByPath(nameFilter);
                var pathRefs = new List<UnityObjectRef>();
                if (go != null)
                    pathRefs.Add(UnityObjectRef.FromGameObject(go));
                return BuildObjectRefArrayJson(pathRefs);
            }

            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            var refs = new List<UnityObjectRef>();

            foreach (var go in allObjects)
            {
                if (!string.IsNullOrEmpty(nameFilter) &&
                    !go.name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_TYPE, "Get all GameObjects in the scene that have a specific component type. " +
            "Supports optional filters: nameContains, layer (int), layerName (string), isIncludeInvisible (bool, default true). " +
            "Parameter 'typeName' accepts short type name (e.g. 'Button', 'Image', 'Renderer', 'Collider', 'Selectable').")]
        [MCPParam("typeName", Type = "string", Required = true, Description = "Component type short name, e.g. 'Button'")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        [MCPParam("isIncludeInvisible", Type = "boolean", Description = "Include inactive objects (default true)")]
        public static string GetObjectsByType(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("typeName", out var typeNameObj) || typeNameObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'typeName'")));

            var typeName = typeNameObj as string ?? "";
            var targetType = ResolveComponentType(typeName);
            if (targetType == null)
                return JsonHelper.BuildJsonObject(
                    ("error", JsonHelper.EscapeString($"Unknown component type: '{typeName}'")),
                    ("hint", JsonHelper.EscapeString("Try 'Button', 'Image', 'Renderer', 'Collider', 'Selectable', 'Rigidbody', etc.")));

            var includeInvisible = true;
            if (args.TryGetValue("isIncludeInvisible", out var includeObj) && includeObj is bool b)
                includeInvisible = b;

            // Use SceneObjectTools.FindObjectsByType for efficient root-based scan
            var scene = SceneManagement.SceneManager.GetActiveScene();
            var goList = scene.FindObjectsByType(targetType, includeInvisible, null);
            var refs = new List<UnityObjectRef>(goList.Count);

            foreach (var go in goList)
            {
                if (FilterGameObject(go, args))
                    refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_TAG, "Get all GameObjects in the scene with a specific tag. " +
            "Supports optional filters: nameContains, layer (int), layerName (string).")]
        [MCPParam("tag", Type = "string", Required = true, Description = "Unity tag to filter by")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        public static string GetObjectsByTag(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("tag", out var tagObj) || tagObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'tag'")));

            var tag = tagObj as string ?? "";
            var allObjects = GameObject.FindGameObjectsWithTag(tag);
            var refs = new List<UnityObjectRef>();

            foreach (var go in allObjects)
            {
                if (FilterGameObject(go, args))
                    refs.Add(UnityObjectRef.FromGameObject(go));
            }

            return BuildObjectRefArrayJson(refs);
        }

        [MCPTool(MCPMethodConst.GET_OBJECTS_BY_PATH, "Get a GameObject by its Transform path (e.g. 'Canvas/Panel/Button'). " +
            "Supports optional filters: nameContains, layer (int), layerName (string).")]
        [MCPParam("path", Type = "string", Required = true, Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("nameContains", Type = "string", Description = "Name substring filter")]
        [MCPParam("layer", Type = "integer", Description = "Layer index (0-31) to filter by")]
        [MCPParam("layerName", Type = "string", Description = "Layer name to filter by")]
        public static string GetObjectsByPath(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            if (!args.TryGetValue("path", out var pathObj) || pathObj == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString("Missing required parameter 'path'")));

            var path = pathObj as string ?? "";
            var go = FindObjectByPath(path);
            if (go == null)
                return JsonHelper.BuildJsonObject(("error", JsonHelper.EscapeString($"No GameObject found at path: '{path}'")));

            if (!FilterGameObject(go, args))
                return BuildObjectRefArrayJson(new List<UnityObjectRef>());

            var refs = new List<UnityObjectRef> { UnityObjectRef.FromGameObject(go) };
            return BuildObjectRefArrayJson(refs);
        }

        
    }
}
