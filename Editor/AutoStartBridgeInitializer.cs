#if UNITY_EDITOR
using SimpleMCPBridge.Runtime;
using UnityEditor;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Creates an AutoStartBridge GameObject in the current scene.
    /// Use Tools > SimpleMCPBridge > Create AutoStartBridge when you need
    /// a scene object that auto-connects to the MCP server on startup.
    /// </summary>
    public static class AutoStartBridgeInitializer
    {
        [MenuItem("PowerUtilities/SimpleMCPBridge/Create AutoStartBridge")]
        private static void CreateAutoStartBridge()
        {
            if (Object.FindObjectOfType<AutoStartBridge>() != null)
            {
                DebugUtils.Log("[AutoStartBridge] Already exists in scene — skipping");
                return;
            }

            var go = new GameObject("[AutoStartBridge]");
            go.hideFlags = HideFlags.None;
            go.AddComponent<AutoStartBridge>();
            DebugUtils.Log("[AutoStartBridge] Created AutoStartBridge GameObject");
        }
    }
}
#endif
