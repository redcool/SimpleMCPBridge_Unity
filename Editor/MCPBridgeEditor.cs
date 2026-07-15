#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SimpleMCPBridge.Editor
{
    /// <summary>
    /// Custom Editor for MCPBridge (scene component) — shows connection status
    /// inline in the Inspector. Works in both Editor and Runtime.
    /// Provides Connect/Disconnect control, status display, and config fields.
    /// </summary>
    [CustomEditor(typeof(Runtime.MCPBridge))]
    [CanEditMultipleObjects]
    public class MCPBridgeEditor : UnityEditor.Editor
    {
        private SerializedProperty _serverIpProp;
        private SerializedProperty _serverPortProp;
        private SerializedProperty _dontDestroyOnLoadProp;
        private SerializedProperty _isAutoReconnectProp;
        private SerializedProperty _mcpLogoProp;

        private void OnEnable()
        {
            _serverIpProp = serializedObject.FindProperty("_serverIp");
            _serverPortProp = serializedObject.FindProperty("_serverPort");
            _dontDestroyOnLoadProp = serializedObject.FindProperty("dontDestroyOnLoad");
            _isAutoReconnectProp = serializedObject.FindProperty("isAutoReconnect");
            _mcpLogoProp = serializedObject.FindProperty("mcpLogo");
        }

        public override void OnInspectorGUI()
        {
            var bridge = (Runtime.MCPBridge)target;

            serializedObject.Update();

            // ── Status header ──
            DrawStatusHeader(bridge);

            EditorGUILayout.Space(4);

            // ── Connect / Disconnect button ──
            DrawConnectButton(bridge);

            EditorGUILayout.Space(8);

            // ── Config section ──
            DrawConfigSection();

            serializedObject.ApplyModifiedProperties();

            // Repaint while connected to show live status
            if (bridge.IsConnected)
                Repaint();
        }

        private void DrawStatusHeader(Runtime.MCPBridge bridge)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (bridge.IsConnected)
            {
                EditorGUILayout.LabelField("●  Connected", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"ws://{bridge.ServerIp}:{bridge.ServerPort}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Bridge ID: {bridge.BridgeId}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("○  Disconnected", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Server: {bridge.ServerIp}:{bridge.ServerPort}", EditorStyles.miniLabel);
            }

            // Show last error if any
            if (!bridge.IsConnected && !string.IsNullOrEmpty(bridge.LastError))
            {
                EditorGUILayout.Space(4);
                GUI.color = new Color(1f, 0.6f, 0.6f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Last error:", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(bridge.LastError, EditorStyles.miniLabel, GUILayout.Height(30));
                EditorGUILayout.EndVertical();
                GUI.color = Color.white;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawConnectButton(Runtime.MCPBridge bridge)
        {
            if (bridge.IsConnected)
            {
                GUI.color = new Color(1f, 0.5f, 0.5f);
                if (GUILayout.Button("■  Disconnect", GUILayout.Height(30)))
                    bridge.Disconnect();
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(0.5f, 1f, 0.5f);
                if (GUILayout.Button("▶  Connect", GUILayout.Height(30)))
                    bridge.Connect();
                GUI.color = Color.white;
            }
        }

        private void DrawConfigSection()
        {
            EditorGUILayout.LabelField("Server Configuration", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_serverIpProp, new GUIContent("Server IP"));
            EditorGUILayout.PropertyField(_serverPortProp, new GUIContent("Server Port"));
            EditorGUILayout.PropertyField(_isAutoReconnectProp, new GUIContent("Auto Reconnect"));
            EditorGUILayout.PropertyField(_dontDestroyOnLoadProp, new GUIContent("Dont Destroy On Load"));
            EditorGUILayout.PropertyField(_mcpLogoProp, new GUIContent("MCP Logo Texture"));
        }
    }
}
#endif
