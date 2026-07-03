using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Models
{
    /// <summary>
    /// Serializable reference to a Unity GameObject.
    /// Includes transform path for domain-reload-safe object resolution.
    /// </summary>
    [Serializable]
    public class UnityObjectRef
    {
        public int instanceId;
        public string name;
        public string type;
        /// <summary>Transform path (e.g. "Canvas/Panel/Button"). Survives domain reload.</summary>
        public string path;

        public static UnityObjectRef FromGameObject(GameObject go)
        {
            return new UnityObjectRef
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                type = "GameObject",
                path = GetObjectPath(go)
            };
        }

        /// <summary>
        /// Build the transform path from scene root to this GameObject.
        /// e.g. "Canvas/Panel/Button"
        /// </summary>
        private static string GetObjectPath(GameObject go)
        {
            var segments = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
// mcp-revision: 181633
