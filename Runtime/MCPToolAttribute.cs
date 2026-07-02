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
}
// mcp-revision: 181632
