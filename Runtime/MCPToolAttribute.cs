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
        /// <summary>
        /// Optional explicit category for this tool (e.g. "Scene", "Input", "Asset").
        /// When null/empty, the category is auto-derived from the tool name prefix
        /// (the part before the first '.'), e.g. "scene.get_hierarchy" → "Scene".
        /// Used to group tools and to let agents enable/disable whole categories.
        /// </summary>
        public string Category { get; set; } = null;

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

    /// <summary>
    /// Declares one parameter of an [MCPTool] method as JSON Schema metadata.
    /// Attach one attribute per documented parameter (AllowMultiple). Emitted as the
    /// tool's "inputSchema" in register_tools so agents see real parameter schemas
    /// instead of only free-text descriptions.
    ///
    /// Usage:
    /// [MCPTool("scene.set_transform", "...")]
    /// [MCPParam("instanceId", Type = "integer", Required = true, Description = "Target object")]
    /// [MCPParam("position", Type = "array", Description = "[x,y,z]")]
    /// [MCPParam("space", Type = "string", EnumValues = new[] { "Self", "World" })]
    /// public static string SetTransform(string paramsJson) { … }
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MCPParamAttribute : Attribute
    {
        public string Name { get; }
        /// <summary>JSON Schema type: "string" | "number" | "integer" | "boolean" | "array" | "object".</summary>
        public string Type { get; set; }        // defaults to "string" when left empty at emission
        /// <summary>Whether the parameter is required (emitted in the schema "required" array).</summary>
        public bool Required { get; set; }      // default false
        /// <summary>Human-readable description (emitted as the property "description").</summary>
        public string Description { get; set; } // default ""
        /// <summary>Optional allowed values, emitted as the property JSON "enum" array.</summary>
        public string[] EnumValues { get; set; } // optional, emitted as JSON "enum" array

        public MCPParamAttribute(string name)
        {
            Name = name;
        }
    }
}
// mcp-revision: 181632
