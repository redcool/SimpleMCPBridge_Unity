using System;
using UnityEngine;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Target platform flags for [MCPTool] platform filtering.
    /// Combine with bitwise OR: MCPToolPlatforms.Editor | MCPToolPlatforms.Standalone.
    /// Default (All = 0) means no filter — register on every platform.
    /// </summary>
    [Flags]
    public enum MCPToolPlatforms
    {
        All        = 0,
        Editor     = 1 << 0,
        Standalone = 1 << 1,
        Android    = 1 << 2,
        iOS        = 1 << 3,
        WebGL      = 1 << 4,
    }

    /// <summary>
    /// Marks a method as an MCP tool that can be called by AI agents.
    /// The scanner discovers these at startup and registers them for dispatch.
    ///
    /// Optional Platform filter: set Platform to limit which build targets
    /// register this tool. When omitted (All), the tool is registered everywhere.
    ///
    /// Usage:
    /// [MCPTool("recording.start", "…", Platform = MCPToolPlatforms.Android)]
    /// public static string StartRecording(string paramsJson) { … }
    ///
    /// This lets the method live outside a #if !UNITY_EDITOR guard so VS
    /// can analyze it, while still skipping registration on excluded platforms.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class MCPToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public MCPToolPlatforms Platform { get; set; } = MCPToolPlatforms.All;
        /// <summary>
        /// When true, this tool is only registered when the Application is playing
        /// (Play Mode in Editor, or running in a built player).
        /// Prevents edit-mode-only tools from being registered during editor usage.
        /// </summary>
        public bool RequirePlayMode { get; set; } = false;

        public MCPToolAttribute(string name, string description)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? "";
        }
    }

    /// <summary>
    /// Marks a class that contains [MCPTool] methods.
    /// Speeds up tool discovery: AutoRegisterAll checks this type-level attribute
    /// first, skipping full method reflection for unrelated types.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class MCPToolClassAttribute : Attribute
    {
    }
}
// mcp-revision: 181632
