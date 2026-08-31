using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // SceneHandler 对象操作组（partial）：create / delete / set_transform / set_active / duplicate / rename / set_parent
    public partial class SceneHandler
    {
[MCPTool(MCPMethodConst.CREATE_OBJECT, "Create a new GameObject with optional name, position, rotation, scale, and parent (by instanceId or path)")]
        [MCPParam("name", Type = "string", Description = "New object name (default 'New GameObject')")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("parentId", Type = "integer", Description = "InstanceId of parent object")]
        [MCPParam("parentPath", Type = "string", Description = "Transform path of parent object")]
        public static string CreateObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var name = GetString(args, "name", "New GameObject");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var go = new GameObject(name);
            SceneObjectTools.UndoRegisterCreated(go, $"Create {name}");

            var parent = ResolveParentTarget(args);
            if (parent != null)
                go.transform.SetParent(parent.transform);

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            var ref_ = UnityObjectRef.FromGameObject(go);
            return JsonUtility.ToJson(ref_);
        }

        [MCPTool(MCPMethodConst.DELETE_OBJECT, "Destroy a GameObject by instanceId or path")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        public static string DeleteObject(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            SceneObjectTools.UndoDestroyObject(go);
            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        [MCPTool(MCPMethodConst.SET_TRANSFORM, "Set position, rotation, and/or scale of a GameObject by instanceId or path. " +
            "Optional 'space' parameter: 'world' (default, transform.position) or 'local' (transform.localPosition).")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("space", Type = "string", Description = "Coordinate space: world or local", EnumValues = new[] { "world", "local" })]
        public static string SetTransform(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");
            var space = GetString(args, "space", "world").ToLowerInvariant();
            var isLocal = space == "local";

            SceneObjectTools.UndoRecord(go.transform, "Set Transform");
            if (posArr != null && posArr.Length >= 3)
            {
                var pos = new Vector3(posArr[0], posArr[1], posArr[2]);
                if (isLocal) go.transform.localPosition = pos;
                else go.transform.position = pos;
            }
            if (rotArr != null && rotArr.Length >= 3)
            {
                var rot = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
                if (isLocal) go.transform.localRotation = rot;
                else go.transform.rotation = rot;
            }
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            return JsonHelper.BuildJsonObject(("success", "true"));
        }

        
    }
}
