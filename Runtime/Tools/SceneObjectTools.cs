using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}