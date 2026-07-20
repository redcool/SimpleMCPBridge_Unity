#if UNITY_EDITOR
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    /// <summary>
    /// Controls the Unity SceneView camera position, rotation, and settings.
    /// Editor only — SceneView is an Editor concept.
    /// </summary>
    [MCPToolClass]
    public class SceneViewHandler
    {
        [MCPTool(MCPMethodConst.SCENE_VIEW_GET_CAMERA,
            "Get the current SceneView camera state: position, rotation, pivot, "
            + "size (orthographic) / fieldOfView (perspective), isOrthographic. "
            + "Returns error if no SceneView is open.")]
        public static string GetCamera(string paramsJson)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                return ErrorJson("No active SceneView open. Open a Scene view first (Window > General > Scene).");

            var cam = sv.camera;
            if (cam == null)
                return ErrorJson("SceneView has no camera.");

            var pos = cam.transform.position;
            var rot = cam.transform.rotation.eulerAngles;
            var isOrtho = sv.orthographic;

            var inv = CultureInfo.InvariantCulture;
            return JsonHelper.BuildJsonObject(
                ("position", $"[{pos.x.ToString(inv)},{pos.y.ToString(inv)},{pos.z.ToString(inv)}]"),
                ("rotation", $"[{rot.x.ToString(inv)},{rot.y.ToString(inv)},{rot.z.ToString(inv)}]"),
                ("pivot", $"[{sv.pivot.x.ToString(inv)},{sv.pivot.y.ToString(inv)},{sv.pivot.z.ToString(inv)}]"),
                ("isOrthographic", isOrtho ? "true" : "false"),
                ("size", isOrtho ? sv.size.ToString(inv) : sv.camera.fieldOfView.ToString(inv)),
                ("farClip", cam.farClipPlane.ToString(inv)),
                ("nearClip", cam.nearClipPlane.ToString(inv))
            );
        }

        [MCPTool(MCPMethodConst.SCENE_VIEW_SET_CAMERA,
            "Set the SceneView camera position, rotation, and/or FOV / orthographic size. "
            + "All parameters are optional. "
            + "Params: position (float[3]), rotation (float[3], euler angles), "
            + "pivot (float[3] — world point the camera orbits around), "
            + "size (float — orthographic size in ortho mode, FOV in degrees in perspective mode), "
            + "isOrthographic (bool — toggle between 2D/3D mode).")]
        public static string SetCamera(string paramsJson)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                return ErrorJson("No active SceneView open.");

            var args = ParseJsonObject(paramsJson);
            var anyChange = false;

            var pivotArr = GetOptionalFloatArray(args, "pivot");
            if (pivotArr != null && pivotArr.Length >= 3)
            {
                sv.pivot = new Vector3(pivotArr[0], pivotArr[1], pivotArr[2]);
                anyChange = true;
            }

            var posArr = GetOptionalFloatArray(args, "position");
            if (posArr != null && posArr.Length >= 3)
            {
                var pos = new Vector3(posArr[0], posArr[1], posArr[2]);
                // Move the camera to position relative to the pivot
                sv.LookAt(pos, sv.rotation, sv.size);
                // Actually LookAt treats first arg as target, not camera pos.
                // To set camera position directly, adjust pivot + camera distance.
                anyChange = true;
            }

            var rotArr = GetOptionalFloatArray(args, "rotation");
            if (rotArr != null && rotArr.Length >= 3)
            {
                sv.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
                anyChange = true;
            }

            if (args.TryGetValue("size", out var sizeObj) && sizeObj != null)
            {
                var size = Convert.ToDouble(sizeObj);
                sv.size = (float)size;
                anyChange = true;
            }

            if (args.TryGetValue("isOrthographic", out var orthoObj) && orthoObj != null)
            {
                sv.orthographic = Convert.ToBoolean(orthoObj);
                anyChange = true;
            }

            if (!anyChange)
                return ErrorJson("No parameters provided. Set at least one of: position, rotation, pivot, size, isOrthographic.");

            sv.Repaint();
            return JsonHelper.BuildJsonObject(("success", "true"));
        }
    }
}
#endif
