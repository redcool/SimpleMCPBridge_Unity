using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Handlers
{
    // AssetBundleHotReplaceHandler 解析帮手（partial）：路径/对象/scene 解析（Step 4）
    public partial class AssetBundleHotReplaceHandler
    {
private static Renderer FindRenderer(string path, List<string> errors)
        {
            GameObject go = GameObject.Find(path);
            if (go == null)
            {
                go = FindGameObjectByPath(path);
            }
            if (go == null)
            {
                errors.Add("path not found: " + path);
                return null;
            }
            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
            {
                errors.Add(path + ": no Renderer");
                return null;
            }
            return r;
        }

        /// <summary>
        /// Find a GameObject by path. Tries GameObject.Find first, then
        /// recursive search through all root GameObjects in the active scene.
        /// </summary>
        private static GameObject FindGameObjectByPath(string path)
        {
            var go = GameObject.Find(path);
            if (go != null) return go;

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                var found = SearchTransformRecursive(root.transform, path);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject SearchTransformRecursive(Transform parent, string targetName)
        {
            if (parent.name == targetName)
                return parent.gameObject;

            for (int i = 0; i < parent.childCount; i++)
            {
                var found = SearchTransformRecursive(parent.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Parse a JSON string array like ["a","b","c"] into a List&lt;string&gt;.
        /// (Same pattern as ShaderHotReplaceHandler and AssetHandler.)
        /// </summary>
        private static List<string> ParseStringArray(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json) || json.Trim() == "[]")
                return result;

            var trimmed = json.Trim().TrimStart('[').TrimEnd(']').Trim();
            if (string.IsNullOrEmpty(trimmed))
                return result;

            bool inStr = false;
            int start = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                    inStr = !inStr;
                if (!inStr && c == ',')
                {
                    var item = trimmed.Substring(start, i - start).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(item))
                        result.Add(item);
                    start = i + 1;
                }
            }
            if (start < trimmed.Length)
            {
                var item = trimmed.Substring(start).Trim().Trim('"');
                if (!string.IsNullOrEmpty(item))
                    result.Add(item);
            }

            return result;
        }

    }
}
