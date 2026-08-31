using SimpleMCPBridge;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Models;
using System;
using UnityEngine;
using Object = UnityEngine.Object;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // SceneHandler 资产组（partial）：instantiate_prefab / save_prefab / set_material（Editor only）
    public partial class SceneHandler
    {
#if UNITY_EDITOR
        // ── Asset tools (Editor only) ──

        [MCPTool(MCPMethodConst.INSTANTIATE_PREFAB, "Instantiate a prefab from project Assets into the scene by asset path")]
        [MCPParam("assetPath", Type = "string", Required = true, Description = "Prefab asset path, e.g. 'Assets/Prefabs/X.prefab'")]
        [MCPParam("position", Type = "array", Description = "[x,y,z] world position")]
        [MCPParam("rotation", Type = "array", Description = "[x,y,z] Euler rotation degrees")]
        [MCPParam("scale", Type = "array", Description = "[x,y,z] local scale")]
        [MCPParam("parentId", Type = "integer", Description = "InstanceId of parent object")]
        [MCPParam("parentPath", Type = "string", Description = "Transform path of parent object")]
        public static string InstantiatePrefab(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var assetPath = GetRequiredString(args, "assetPath");
            var posArr = GetOptionalFloatArray(args, "position");
            var rotArr = GetOptionalFloatArray(args, "rotation");
            var scaleArr = GetOptionalFloatArray(args, "scale");

            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                return ErrorJson($"Prefab not found at path: {assetPath}");

            var go = UnityEditor.PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (go == null)
                return ErrorJson("Failed to instantiate prefab (PrefabUtility.InstantiatePrefab returned null)");
            SceneObjectTools.UndoRegisterCreated(go, $"Instantiate {prefab.name}");

            if (posArr != null && posArr.Length >= 3)
                go.transform.position = new Vector3(posArr[0], posArr[1], posArr[2]);
            if (rotArr != null && rotArr.Length >= 3)
                go.transform.rotation = Quaternion.Euler(rotArr[0], rotArr[1], rotArr[2]);
            if (scaleArr != null && scaleArr.Length >= 3)
                go.transform.localScale = new Vector3(scaleArr[0], scaleArr[1], scaleArr[2]);

            var parent = ResolveParentTarget(args);
            if (parent != null) go.transform.SetParent(parent.transform);

            return JsonUtility.ToJson(UnityObjectRef.FromGameObject(go));
        }

        [MCPTool(MCPMethodConst.SCENE_SAVE_PREFAB, "Save a GameObject (by instanceId or path) as a prefab asset. " +
            "Overwrites the prefab at assetPath if it exists. " +
            "NOTE: PrefabUtility/AssetDatabase operations are NOT Undo-trackable.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("assetPath", Type = "string", Required = true, Description = "Prefab asset path, e.g. 'Assets/Prefabs/X.prefab'")]
        public static string SavePrefab(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null)
                return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var assetPath = GetRequiredString(args, "assetPath");
            UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, assetPath, out var success);
            return JsonHelper.BuildJsonObject(
                ("success", success ? "true" : "false"),
                ("assetPath", JsonHelper.EscapeString(assetPath))
            );
        }

        [MCPTool(MCPMethodConst.SET_MATERIAL, "⚠ Set material color and/or main texture on a Renderer (by instanceId or path). For asset-level material edits, prefer editing .meta GUIDs via filesystem — faster.")]
        [MCPParam("instanceId", Type = "integer", Description = "Object instanceId (fast lookup)")]
        [MCPParam("path", Type = "string", Description = "Transform path, e.g. 'Canvas/Panel/Button'")]
        [MCPParam("materialIndex", Type = "integer", Description = "Material index on renderer (default 0)")]
        [MCPParam("color", Type = "array", Description = "[r,g,b] or [r,g,b,a] color")]
        [MCPParam("texturePath", Type = "string", Description = "Texture asset path for main texture")]
        public static string SetMaterial(string paramsJson)
        {
            var args = ParseJsonObject(paramsJson);
            var go = ResolveTarget(args);
            if (go == null) return ErrorJson("Object not found. Provide 'instanceId' or 'path'.");

            var materialIndex = (int)GetOptionalInt(args, "materialIndex").GetValueOrDefault(0);
            var colorArr = GetOptionalFloatArray(args, "color");
            var texturePath = GetString(args, "texturePath");

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return ErrorJson("No Renderer found on object");

            if (materialIndex < 0 || materialIndex >= renderer.sharedMaterials.Length)
                return ErrorJson($"Material index {materialIndex} out of range (0-{renderer.sharedMaterials.Length - 1})");

            var mat = renderer.sharedMaterials[materialIndex];
            SceneObjectTools.UndoRecord(mat, "Set Material");

            if (colorArr != null && colorArr.Length >= 3)
            {
                var c = colorArr.Length >= 4
                    ? new Color(colorArr[0], colorArr[1], colorArr[2], colorArr[3])
                    : new Color(colorArr[0], colorArr[1], colorArr[2], 1f);
                mat.color = c;
            }

            if (!string.IsNullOrEmpty(texturePath))
            {
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (tex == null) return ErrorJson($"Texture not found at path: {texturePath}");
                mat.mainTexture = tex;
            }

            return JsonHelper.BuildJsonObject(("success", "true"));
        }
#endif
    }
}
