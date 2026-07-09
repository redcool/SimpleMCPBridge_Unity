using System;

namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// Marks a method as an MCP tool that can be called by AI agents.
    /// The scanner discovers these at startup and registers them for dispatch.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class MCPToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

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
