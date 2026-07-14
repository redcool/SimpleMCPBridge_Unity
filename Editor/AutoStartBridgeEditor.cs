#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Custom Editor for AutoStartBridge.
    /// Shows a yellow info box when MCPBridgeWindow is active, explaining the takeover:
    /// - In Editor, MCPBridgeWindow manages the bridge (StaticUpdate covers Edit + Play Mode)
    /// - AutoStartBridge is disabled by MCPBridgeWindow and intended for standalone builds
    /// - If re-enabled manually while MCPBridgeWindow is active, Update() self-guards as no-op
    /// </summary>
    [CustomEditor(typeof(Runtime.AutoStartBridge))]
    [CanEditMultipleObjects]
    public class AutoStartBridgeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (MCPBridgeWindow.IsBridgeActive)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "MCPBridgeWindow 正在运行桥接管理，此组件已被禁用。\n" +
                    "Editor 中由 BridgeWindow 接管，AutoStartBridge 专为" +
                    "独立构建(Runtime)使用。\n" +
                    "如需释放接管请在 PowerUtilities/SimpleMCPBridge/MCPBridgeWindow 中断开连接。",
                    MessageType.Warning
                );
            }
        }
    }
}
#endif
