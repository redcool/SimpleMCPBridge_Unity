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
        /// <summary>按组件类型获取场景内所有对象（支持 nameContains/layer/layerName 过滤）。</summary>
        public const string GET_OBJECTS_BY_TYPE = "scene.get_objects_by_type";
        /// <summary>按标签查找对象（支持 nameContains/layer/layerName 过滤）。</summary>
        public const string GET_OBJECTS_BY_TAG = "scene.get_objects_by_tag";
        /// <summary>按 Transform 路径查找对象（支持 nameContains/layer/layerName 过滤）。</summary>
        public const string GET_OBJECTS_BY_PATH = "scene.get_objects_by_path";
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
        /// <summary>调用组件上的公共实例方法（反射，按名称传参）。</summary>
        public const string CALL_COMPONENT_METHOD = "scene.call_component_method";
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
        /// <summary>移除组件。</summary>
        public const string REMOVE_COMPONENT = "scene.remove_component";
        /// <summary>保存当前场景（Editor only）。</summary>
        public const string SAVE_CURRENT_SCENE = "scene.save_current";
        /// <summary>加载场景（Editor 打开或运行时加载）。</summary>
        public const string SCENE_LOAD_SCENE = "scene.load_scene";
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
        /// <summary>把 GameObject 保存为预制体资产（Editor only）。</summary>
        public const string SCENE_SAVE_PREFAB = "scene.save_prefab";
        /// <summary>按名称和类型搜索项目资源（AssetHandler）。</summary>
        public const string FIND_ASSETS = "asset.find_assets";
        /// <summary>查找引用了指定资源的所有资源（反向依赖查询）。</summary>
        public const string FIND_REFERENCES = "asset.find_references";
        /// <summary>创建资源（文件夹/材质，Editor only）。</summary>
        public const string ASSET_CREATE = "asset.create";
        /// <summary>删除资源（默认先检查引用，Editor only）。</summary>
        public const string ASSET_DELETE = "asset.delete";
        /// <summary>重命名资源（Editor only）。</summary>
        public const string ASSET_RENAME = "asset.rename";
        /// <summary>移动资源（Editor only）。</summary>
        public const string ASSET_MOVE = "asset.move";
        /// <summary>修改材质颜色或贴图。</summary>
        public const string SET_MATERIAL = "scene.set_material";

        // ── Shader 热替换工具 ──
        /// <summary>从 AssetBundle 热替换 Shader（运行时，全局或按 path）。异步：用 shader.hot_replace_status 轮询。</summary>
        public const string SHADER_HOT_REPLACE = "shader.hot_replace";
        /// <summary>轮询 shader.hot_replace 的下载/替换进度与结果。</summary>
        public const string SHADER_HOT_REPLACE_STATUS = "shader.hot_replace_status";
        /// <summary>构建 shader AssetBundle 并上传到 server，返回手机可达的 abUrl（Editor only）。</summary>
        public const string BUILD_BUNDLE = "asset.build_bundle";

        // ── 通用 AssetBundle 热部署工具 ──
        /// <summary>通用 AssetBundle 热部署：下载 AB，按资产类型(Shader/Material/Texture/AudioClip/Mesh/ScriptableObject/GameObject)自动分发。异步，用 assetbundle.hot_replace_status 轮询。</summary>
        public const string ASSETBUNDLE_HOT_REPLACE = "assetbundle.hot_replace";
        /// <summary>轮询 assetbundle.hot_replace 进度与每类计数。</summary>
        public const string ASSETBUNDLE_HOT_REPLACE_STATUS = "assetbundle.hot_replace_status";
        /// <summary>卸载所有已部署 AssetBundle（会断开实例化对象引用，用于迭代间隙重置）。</summary>
        public const string ASSETBUNDLE_UNLOAD_ALL = "assetbundle.unload_all";
        /// <summary>回滚指定 assetbundle.hot_replace 操作（需要原调用时带上 saveBackup:true）。</summary>
        public const string ASSETBUNDLE_ROLLBACK = "assetbundle.rollback";

        // ── 编辑器工具（Editor only） ──
        /// <summary>触发 Unity 脚本重新编译。</summary>
        public const string REQUEST_COMPILE = "editor.request_compile";
        /// <summary>通过菜单路径打开 Unity Editor 窗口。</summary>
        public const string OPEN_WINDOW = "editor.open_window";
        /// <summary>在 Editor 进程内编译执行 C# 代码片段 (in-memory, 即时)。</summary>
        public const string EVAL = "editor.eval";
        /// <summary>获取 Editor 控制台日志 (最近 N 条)。</summary>
        public const string GET_CONSOLE = "editor.get_console";
        /// <summary>撤销上一次操作。</summary>
        public const string UNDO = "editor.undo";
        /// <summary>重做上一次撤销。</summary>
        public const string REDO = "editor.redo";
        /// <summary>读取 Editor/Project 常用设置项。</summary>
        public const string GET_PREFERENCES = "editor.get_preferences";

        // ── 编辑器窗口工具（Editor only） ──
        /// <summary>最小化、恢复或聚焦 Unity Editor 窗口。</summary>
        public const string EDITOR_WINDOW_FOCUS = "editor.window_focus";

        // ── 项目浏览器工具 ──
        /// <summary>获取 Assets 目录树 (路径/类型/大小)。</summary>
        public const string GET_PROJECT_TREE = "editor.get_project_tree";

        // ── Scene 视图工具 ──
        /// <summary>获取 Scene 视图相机状态。</summary>
        public const string SCENE_VIEW_GET_CAMERA = "scene_view.get_camera";
        /// <summary>设置 Scene 视图相机位置/旋转/FOV。</summary>
        public const string SCENE_VIEW_SET_CAMERA = "scene_view.set_camera";

        // ── 输入模拟工具 ──
        /// <summary>在屏幕指定位置模拟点击（通过 EventSystem 完整事件管线）。</summary>
        public const string CLICK_SCREEN = "input.click_screen";
        /// <summary>在屏幕指定位置模拟城堡建筑点击（通过 Physics Raycast + Lua FakeHitResultEvent）。</summary>
        public const string CLICK_BUILDING = "castle.click_building";
        /// <summary>在屏幕指定位置模拟鼠标点击（通过 Input System 低层级事件队列）。</summary>
        public const string MOUSE_CLICK = "input.mouse_click";
        /// <summary>模拟鼠标移动增量（用于视角旋转/瞄准）。</summary>
        public const string MOUSE_MOVE = "input.mouse_move";
        /// <summary>模拟键盘按键（通过 Input System 低层级事件队列到物理键盘）。</summary>
        public const string KEY_PRESS = "input.key_press";
        /// <summary>触屏模拟：单击 / 开始触摸 / 移动 / 结束触摸（通过 Input System 虚拟触屏）。</summary>
        public const string TOUCH = "input.touch";
        /// <summary>滑动手势：起点→终点，异步多帧平滑执行。</summary>
        public const string SWIPE = "input.swipe";
        /// <summary>手柄输入：按钮 / 摇杆 / 扳机（通过 Input System 虚拟手柄）。</summary>
        public const string GAMEPAD = "input.gamepad";

        // ── 物理工具 ──
        /// <summary>从指定位置发射射线检测碰撞。</summary>
        public const string PHYSICS_RAYCAST = "physics.raycast";
        /// <summary>沿方向横扫一个盒体并返回首次碰撞。</summary>
        public const string PHYSICS_BOX_CAST = "physics.box_cast";
        /// <summary>沿方向横扫一个球体并返回首次碰撞。</summary>
        public const string PHYSICS_SPHERE_CAST = "physics.sphere_cast";
        /// <summary>获取指定球体范围内所有碰撞体。</summary>
        public const string PHYSICS_OVERLAP_SPHERE = "physics.overlap_sphere";
        /// <summary>获取指定盒体范围内所有碰撞体。</summary>
        public const string PHYSICS_OVERLAP_BOX = "physics.overlap_box";

        // ── 相机工具 ──
        /// <summary>截取主相机画面并保存为 PNG。</summary>
        public const string CAMERA_SCREENSHOT = "camera.screenshot";

        // ── 录制工具 ──
        /// <summary>开始录制游戏画面。</summary>
        public const string START_RECORDING = "recording.start";
        /// <summary>停止录制并导出 MP4 文件。</summary>
        public const string STOP_RECORDING = "recording.stop";
        /// <summary>获取当前录制状态。</summary>
        public const string GET_RECORDING_STATUS = "recording.status";

        // ── UI 分析工具（uGUI）──
        /// <summary>获取场景中所有 uGUI 文本内容及屏幕坐标。</summary>
        public const string UI_GET_TEXTS = "ui.get_texts";
        /// <summary>查找场景中可交互的 uGUI 元素（Button/Toggle/Slider/Dropdown/InputField）。</summary>
        public const string UI_FIND = "ui.find";

        // ── NGUI 分析工具（需安装 com.tasharen.ngui 才注册，独立于 ui.*）──
        /// <summary>获取场景中所有 NGUI UILabel 文本内容及屏幕坐标。</summary>
        public const string NGUI_GET_TEXTS = "ngui.get_texts";
        /// <summary>查找场景中可交互的 NGUI 元素（UIButton/UIToggle/UISlider/UIInput）。</summary>
        public const string NGUI_FIND = "ngui.find";
        /// <summary>查找场景中所有 NGUI UIWidget（UITexture/UISprite/UILabel 等）及其屏幕坐标。</summary>
        public const string NGUI_FIND_WIDGETS = "ngui.find_widgets";

        // ── UI Toolkit 分析工具（UnityEngine.UIElements 内置模块，始终可用，独立于 ui.*/ngui.*）──
        /// <summary>获取场景中所有 UI Toolkit (UIDocument) 面板信息。</summary>
        public const string UITK_GET_PANELS = "uitk.get_panels";
        /// <summary>读取所有 UI Toolkit 文本内容（Label/TextElement/TextField，内存读取无 OCR）。</summary>
        public const string UITK_GET_TEXTS = "uitk.get_texts";
        /// <summary>查找可交互的 UI Toolkit 元素（Button/Toggle/Slider/SliderInt/DropdownField/TextField/ScrollView）。</summary>
        public const string UITK_FIND = "uitk.find";
        /// <summary>导出 UI Toolkit 视觉树元素列表（名称/类型/路径/类名/可见性/值）。</summary>
        public const string UITK_GET_ELEMENTS = "uitk.get_elements";
        /// <summary>点击 UI Toolkit 元素（按路径或归一化坐标）。</summary>
        public const string UITK_CLICK = "uitk.click";
        /// <summary>设置 UI Toolkit 元素值（Toggle/Slider/SliderInt/DropdownField/TextField）。</summary>
        public const string UITK_SET_VALUE = "uitk.set_value";
        /// <summary>动态创建 UI Toolkit 元素（运行时，不持久）。</summary>
        public const string UITK_CREATE_ELEMENT = "uitk.create_element";
        /// <summary>移除 UI Toolkit 元素（运行时，不持久）。</summary>
        public const string UITK_REMOVE_ELEMENT = "uitk.remove_element";

        // ── PlayerPrefs 工具 ──
        /// <summary>获取所有已知 PlayerPrefs 键值对。</summary>
        public const string ALL_PLAYERPREFS_GET = "playerprefs.get_all";
        /// <summary>获取单个 PlayerPrefs 值。</summary>
        public const string PLAYERPREFS_GET = "playerprefs.get";
        /// <summary>设置 PlayerPrefs 值。</summary>
        public const string PLAYERPREFS_SET = "playerprefs.set";
        /// <summary>删除 PlayerPrefs 键。</summary>
        public const string PLAYERPREFS_DELETE = "playerprefs.delete";

        // ── 统一输入工具 ──
        /// <summary>统一输入：键盘、鼠标、滚轮、轴（Input System + Legacy Input）。</summary>
        public const string INPUT_ACTION = "input.action";

        // ── 游戏状态与时序工具 ──
        /// <summary>获取游戏综合状态（场景/时间/UI/玩家）。</summary>
        public const string GAME_GET_STATE = "game.get_state";
        /// <summary>开始一个等待操作（秒/场景加载/UI出现等）。</summary>
        public const string GAME_WAIT = "game.wait";
        /// <summary>轮询等待操作是否完成。</summary>
        public const string GAME_WAIT_CHECK = "game.wait_check";

        // ── 连续感知工具 ──
        /// <summary>注册要持续监测的信号（属性/UI文本/Transform变化）。</summary>
        public const string GAME_WATCH = "game.watch";
        /// <summary>获取自上次查询以来所有信号的变化（增量）。</summary>
        public const string GAME_GET_DELTA = "game.get_delta";

        // ── 动作序列工具 ──
        /// <summary>执行一系列预设步骤（键盘/鼠标/手柄/点击/等待的组合序列）。</summary>
        public const string GAME_DO_SEQUENCE = "game.do_sequence";
        /// <summary>查询序列执行状态。</summary>
        public const string GAME_SEQUENCE_STATUS = "game.sequence_status";

        // ── 空间感知工具 ──
        /// <summary>获取参考点周围指定半径内的 GameObject 空间信息。</summary>
        public const string GAME_GET_SPATIAL = "game.get_spatial";

        // ── 批量分发工具 ──
        /// <summary>在一帧内执行多个工具调用，将 N+1 次网络往返压缩为 1 次。</summary>
        public const string GAME_BATCH = "game.batch";

        // ── 导航网格工具 ──
        /// <summary>在 NavMesh 上计算两点之间的路径。</summary>
        public const string NAV_QUERY_PATH = "nav.query_path";
        /// <summary>将世界坐标吸附到最近的 NavMesh 位置。</summary>
        public const string NAV_SAMPLE_POSITION = "nav.sample_position";
        /// <summary>检查场景中是否存在 NavMesh。</summary>
        public const string NAV_HAS_NAVMESH = "nav.has_navmesh";

        // ── 音频工具 ──
        /// <summary>获取场景中所有正在播放的 AudioSource。</summary>
        public const string AUDIO_GET_SOURCES = "audio.get_sources";

        // ── 粒子工具 ──
        /// <summary>列出场景中所有 ParticleSystem 及实时状态。</summary>
        public const string PARTICLE_GET_SYSTEMS = "particle.get_systems";
        /// <summary>获取单个 ParticleSystem 的详细状态与配置。</summary>
        public const string PARTICLE_GET_STATE = "particle.get_state";
        /// <summary>创建 GameObject + ParticleSystem（Edit Mode 可撤销）。</summary>
        public const string PARTICLE_CREATE = "particle.create";
        /// <summary>播放粒子系统（可选 restart）。</summary>
        public const string PARTICLE_PLAY = "particle.play";
        /// <summary>暂停粒子系统。</summary>
        public const string PARTICLE_PAUSE = "particle.pause";
        /// <summary>停止粒子系统（可选清空粒子）。</summary>
        public const string PARTICLE_STOP = "particle.stop";
        /// <summary>清空粒子系统所有粒子。</summary>
        public const string PARTICLE_CLEAR = "particle.clear";
        /// <summary>一次性发射粒子（EmitParams，无需播放）。</summary>
        public const string PARTICLE_EMIT = "particle.emit";
        /// <summary>模拟粒子系统到指定时间（确定性预览）。</summary>
        public const string PARTICLE_SIMULATE = "particle.simulate";
        /// <summary>设置粒子系统模块属性（typed 代码路径，非反射）。</summary>
        public const string PARTICLE_SET = "particle.set";

        // ── UI 操作工具 ──
        /// <summary>设置输入框文本。</summary>
        public const string UI_SET_INPUT_FIELD_TEXT = "ui.set_input_field_text";
        /// <summary>设置 Toggle 开关状态。</summary>
        public const string UI_SET_TOGGLE = "ui.set_toggle";
        /// <summary>设置 Slider 滑块值。</summary>
        public const string UI_SET_SLIDER = "ui.set_slider";
        /// <summary>选择 Dropdown 下拉选项。</summary>
        public const string UI_SELECT_DROPDOWN_OPTION = "ui.select_dropdown_option";
        /// <summary>拖拽 UI 元素从起点到终点。</summary>
        public const string UI_DRAG = "ui.drag";
        /// <summary>获取 UI 元素的 Tooltip 文本。</summary>
        public const string UI_GET_TOOLTIP = "ui.get_tooltip";

        // ── 玩家与实体感知工具 ──
        /// <summary>一步获取玩家完整状态（位置/旋转/速度/动画/自定义组件属性）。</summary>
        public const string GAME_GET_PLAYER = "game.get_player";
        /// <summary>批量获取场景中带AI/Health/CharacterController的实体及其关键状态。</summary>
        public const string GAME_GET_ENTITIES = "game.get_entities";
        /// <summary>获取 Animator 当前状态（stateHash/normalizedTime/parameters）。</summary>
        public const string GAME_GET_ANIMATOR_STATE = "game.get_animator_state";
        /// <summary>查询当前所有输入状态（tracked keys/mouse position/gamepad）。</summary>
        public const string INPUT_GET_STATE = "input.get_state";
        /// <summary>设置 NavMeshAgent 目标点，自动寻路移动。</summary>
        public const string NAV_MOVE_TO = "nav.move_to";
        /// <summary>获取当前 Time.timeScale 和 fixedDeltaTime。</summary>
        public const string GAME_GET_TIME_SCALE = "game.get_time_scale";
        /// <summary>设置 Time.timeScale（0=暂停, 1=正常, 2=2倍速）。</summary>
        public const string GAME_SET_TIME_SCALE = "game.set_time_scale";

        public const string TOOLS_ENABLE = "tools.enable";
        public const string TOOLS_DISABLE = "tools.disable";
        public const string TOOLS_LIST_CATEGORIES = "tools.list_categories";
        public const string TOOLS_RESET = "tools.reset";
    }
}
