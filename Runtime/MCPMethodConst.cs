namespace SimpleMCPBridge.Runtime
{
    /// <summary>
    /// MCP 方法/工具名称常量。
    /// 所有 tool name 集中管理，避免各处散落 magic string。
    /// </summary>
    public static class MCPMethodConst
    {
        // ── 内部方法（不由 [MCPTool] 注册，由 MessageRouter 直接处理） ──
        /// <summary>列出所有已注册工具。</summary>
        public const string LIST_TOOLS = "mcp.list_tools";

        // ── 场景工具 ──
        /// <summary>获取场景层级树。</summary>
        public const string GET_HIERARCHY = "scene.get_hierarchy";
        /// <summary>获取 GameObject 的所有组件列表。</summary>
        public const string GET_COMPONENTS = "scene.get_components";
        /// <summary>按名称过滤查找对象。</summary>
        public const string GET_OBJECTS = "scene.get_objects";
        /// <summary>创建 GameObject。</summary>
        public const string CREATE_OBJECT = "scene.create_object";
        /// <summary>按 instanceId 删除对象。</summary>
        public const string DELETE_OBJECT = "scene.delete_object";
        /// <summary>修改位置/旋转/缩放。</summary>
        public const string SET_TRANSFORM = "scene.set_transform";
        /// <summary>修改组件属性。</summary>
        public const string SET_COMPONENT_PROPERTY = "scene.set_component_property";
        /// <summary>获取组件所有可序列化属性名+值。</summary>
        public const string GET_COMPONENT_PROPERTIES = "scene.get_component_properties";
        /// <summary>启用/禁用 GameObject。</summary>
        public const string SET_ACTIVE = "scene.set_active";
        /// <summary>复制 GameObject。</summary>
        public const string DUPLICATE_OBJECT = "scene.duplicate_object";
        /// <summary>重命名 GameObject。</summary>
        public const string RENAME = "scene.rename";
        /// <summary>设置父级。</summary>
        public const string SET_PARENT = "scene.set_parent";
        /// <summary>添加组件。</summary>
        public const string ADD_COMPONENT = "scene.add_component";
        /// <summary>进入播放模式（Editor only）。</summary>
        public const string ENTER_PLAY_MODE = "scene.enter_play_mode";
        /// <summary>退出播放模式（Editor only）。</summary>
        public const string EXIT_PLAY_MODE = "scene.exit_play_mode";
        /// <summary>暂停/继续播放模式（Editor only）。</summary>
        public const string PAUSE_PLAY_MODE = "scene.pause_play_mode";
        /// <summary>获取当前播放模式状态（Editor only）。</summary>
        public const string GET_PLAY_MODE = "scene.get_play_mode";

        // ── 资源工具 ──
        /// <summary>刷新资产数据库，导入新文件或检测文件变化。</summary>
        public const string REFRESH_ASSETS = "asset.refresh";
        /// <summary>从项目资源加载预制体并实例化到场景。</summary>
        public const string INSTANTIATE_PREFAB = "scene.instantiate_prefab";
        /// <summary>按名称和类型搜索项目资源（AssetHandler）。</summary>
        public const string FIND_ASSETS = "asset.find_assets";
        /// <summary>查找引用了指定资源的所有资源（反向依赖查询）。</summary>
        public const string FIND_REFERENCES = "asset.find_references";
        /// <summary>修改材质颜色或贴图。</summary>
        public const string SET_MATERIAL = "scene.set_material";

        // ── 编辑器工具（Editor only） ──
        /// <summary>触发 Unity 脚本重新编译。</summary>
        public const string REQUEST_COMPILE = "editor.request_compile";
        /// <summary>通过菜单路径打开 Unity Editor 窗口。</summary>
        public const string OPEN_WINDOW = "editor.open_window";

        // ── 输入模拟工具 ──
        /// <summary>在屏幕指定位置模拟点击（通过 EventSystem 完整事件管线）。</summary>
        public const string CLICK_SCREEN = "input.click_screen";
    }
}
