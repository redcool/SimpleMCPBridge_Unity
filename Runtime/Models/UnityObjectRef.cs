using System;
using System.Collections.Generic;
using UnityEngine;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

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
    }
}
// mcp-revision: 181633
