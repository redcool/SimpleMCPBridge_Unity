using System;
using UnityEngine;

namespace SimpleMCPBridge.Runtime.Models
{
    /// <summary>
    /// Serializable reference to a Unity GameObject.
    /// </summary>
    [Serializable]
    public class UnityObjectRef
    {
        public int instanceId;
        public string name;
        public string type;

        public static UnityObjectRef FromGameObject(GameObject go)
        {
            return new UnityObjectRef
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                type = "GameObject"
            };
        }
    }
}
// mcp-revision: 181632
