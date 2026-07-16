using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleMCPBridge.Runtime;
using SimpleMCPBridge.Runtime.Handlers;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
namespace SimpleMCPBridge
{
    public static class SceneObjectTools
    {
        /// <summary>
        /// Find objects by type in scene.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="scene"></param>
        /// <param name="isIncludeInvisible"></param>
        /// <param name="resultList">when null, create new List<GameObject></param>
        /// <returns></returns>
        public static List<GameObject> FindObjectsByType<T>(this Scene scene, bool isIncludeInvisible, List<GameObject> resultList) where T : Component
        {
            var type = typeof(T);
            return FindObjectsByType(scene, type, isIncludeInvisible, resultList);
        }
        /// <summary>
        /// Find objects by type in scene.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="componentType"></param>
        /// <param name="isIncludeInvisible"></param>
        /// <param name="resultList">when null, create new List<GameObject></param>
        /// <returns></returns>
        public static List<GameObject> FindObjectsByType(this Scene scene, Type componentType, bool isIncludeInvisible, List<GameObject> resultList)
        {
            if (resultList == null)
                resultList = new List<GameObject>();

            var rootObjs = scene.GetRootGameObjects();
            foreach (var rootObj in rootObjs)
            {
                var comps = rootObj.GetComponentsInChildren(componentType, isIncludeInvisible);
                resultList.AddRange(comps.Select(c => c.gameObject));
            }
            return resultList;
        }
        /// <summary>
        /// Find object by path in scene. Path format: Cube/Child/Child2
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="objPath"></param>
        /// <returns></returns>
        public static Object FindObjectByPath(this Scene scene, string objPath)
        {
            var rootObjs = scene.GetRootGameObjects();
            return FindObjectByPath(rootObjs, objPath);
        }
        /// <summary>
        /// Find object by path in root objects. Path format: Cube/Child/Child2
        /// </summary>
        /// <param name="rootObjs"></param>
        /// <param name="objPath"></param>
        /// <returns></returns>
        public static Object FindObjectByPath(GameObject[] rootObjs, string objPath)
        {
            SplitObjPath(objPath, out var objName, out var remainPath);
            return rootObjs.Where(obj => obj.name == objName)
                .FirstOrDefault()?.transform.Find(remainPath);
        }
        /// <summary>
        /// Find object by path in a root object. Path format: Cube/Child/Child2
        /// </summary>
        /// <param name="rootObj"></param>
        /// <param name="objPath"></param>
        /// <returns></returns>
        public static Object FindObjectByPath(this GameObject rootObj, string objPath)
        {
            SplitObjPath(objPath, out var objName, out var remainPath);
            if (rootObj.name == objName)
                return rootObj.transform.Find(remainPath);
            return null;
        }
        /// <summary>
        /// Split object path into object name and remaining path.
        /// Cube/Child/Child2 => Cube, Child/Child2
        /// </summary>
        /// <param name="objPath"></param>
        /// <param name="objName"></param>
        /// <param name="remainObjPath"></param>
        public static void SplitObjPath(string objPath, out string objName, out string remainObjPath)
        {
            var index = objPath.IndexOf("/");
            objName = index > -1 ? objPath.Substring(0, index) : objPath;
            remainObjPath = index > -1 ? objPath.Substring(index + 1) : objPath;
        }

        public static string BuildTreeEntry(GameObject go, string path)
        {
            // Collect component names
            var components = go.GetComponents<Component>();
            var compNames = new List<string>();
            foreach (var c in components)
            {
                if (c != null)
                    compNames.Add(c.GetType().Name);
            }

            // Collect children
            var childJsons = new List<string>();
            foreach (Transform child in go.transform)
            {
                childJsons.Add(BuildTreeEntry(child.gameObject, path + "/" + child.name));
            }

            var pos = go.transform.position;
            return JsonHelper.BuildJsonObject(
                ("instanceId", go.GetInstanceID().ToString(CultureInfo.InvariantCulture)),
                ("path", JsonHelper.EscapeString(path)),
                ("name", JsonHelper.EscapeString(go.name)),
                ("active", JsonHelper.BoolJson(go.activeSelf)),
                ("position", JsonHelper.FloatArrayJson(new[] { pos.x, pos.y, pos.z })),
                ("components", JsonHelper.StringArrayJson(compNames.ToArray())),
                ("children", JsonHelper.BuildJsonArray(childJsons.ToArray()))
            );
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            if (rawValue == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            var rawStr = rawValue.ToString();

            if (targetType == typeof(float)) return Convert.ToSingle(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return Convert.ToInt64(rawValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return Convert.ToBoolean(rawValue);
            if (targetType == typeof(string)) return rawValue.ToString();
            if (targetType == typeof(Vector2))
            {
                var v2 = HandlerUtils.ToFloatArray(rawValue);
                if (v2 != null && v2.Length >= 2)
                    return new Vector2(v2[0], v2[1]);
                return Vector2.zero;
            }
            if (targetType == typeof(Vector3))
            {
                var v3 = HandlerUtils.ToFloatArray(rawValue);
                if (v3 != null && v3.Length >= 3)
                    return new Vector3(v3[0], v3[1], v3[2]);
                return Vector3.zero;
            }
            if (targetType == typeof(Vector4))
            {
                var v4 = HandlerUtils.ToFloatArray(rawValue);
                if (v4 != null && v4.Length >= 4)
                    return new Vector4(v4[0], v4[1], v4[2], v4[3]);
                return Vector4.zero;
            }
            if (targetType == typeof(Quaternion))
            {
                var vq = HandlerUtils.ToFloatArray(rawValue);
                if (vq != null && vq.Length >= 4)
                    return new Quaternion(vq[0], vq[1], vq[2], vq[3]);
                return Quaternion.identity;
            }
            if (targetType == typeof(Color))
            {
                var vc = HandlerUtils.ToFloatArray(rawValue);
                if (vc != null && vc.Length >= 4)
                    return new Color(vc[0], vc[1], vc[2], vc[3]);
                if (vc != null && vc.Length >= 3)
                    return new Color(vc[0], vc[1], vc[2], 1f);
                return Color.white;
            }
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, rawStr, ignoreCase: true);
            }

            // Fallback: try string conversion
            return Convert.ChangeType(rawValue, targetType, CultureInfo.InvariantCulture);
        }

        // ──────────────────────────────────────────────
        //  Undo helpers (no-op outside Editor)
        // ──────────────────────────────────────────────

        public static void UndoRecord(Object target, string label)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(target, $"[MCP] {label}");
#endif
        }

        public static void UndoRegisterCreated(GameObject go, string label)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, $"[MCP] {label}");
#endif
        }

        public static void UndoDestroyObject(GameObject go)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.DestroyObjectImmediate(go);
#else
            Object.DestroyImmediate(go);
#endif
        }

        public static Component UndoAddComponent(GameObject go, Type type)
        {
#if UNITY_EDITOR
            return UnityEditor.Undo.AddComponent(go, type);
#else
            return go.AddComponent(type);
#endif
        }
    }
}