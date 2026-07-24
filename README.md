# SimpleMCPBridge — Unity 侧桥接包

让 AI Agent（Claude、Cursor 等）通过 MCP 协议直接控制 Unity Editor / Runtime。  
**必须配合 [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) 使用。**

## 架构

```
AI Agent (Claude Code / Cursor)
    │  MCP SSE (GET /sse + POST /mcp)
    ▼
SimpleMcpServer (Node.js/TypeScript)   ← 另一个仓库，需单独 clone
    │  WebSocket (ws://)
    ▼
SimpleMCPBridge (C#)                   ← 本仓库，clone 到 Unity Assets/ 下
    │
    ▼
Unity Editor / Runtime
```

支持同时连接多个 Bridge（如 Editor + Android 设备），路由规则 **last-registration-wins**。

## 核心理念：从内存读取游戏状态，不靠截图

SimpleMCPBridge 的设计理念是 **structured memory query**（结构化内存查询），而非 screenshot-based perception（基于截图的视觉感知）。

**为什么这样设计？**

| | 截图+视觉分析 | 内存查询（本桥） |
|---|---|---|
| **成本** | 每帧截图+传输+LLM视觉分析 = 高token消耗 | 直接读组件属性 = 几十字节JSON |
| **精度** | OCR/视觉推断有误差（血量估计、文字识别） | 精确值（`hp=87`, `mana=45.3`） |
| **速度** | 截图+编码+网络+LLM处理 = 秒级 | 读属性+返回JSON = 毫秒级 |
| **可操作性** | 看到画面但不知道对象名/组件/instanceId | 直接拿到 `instanceId` 可立即操控 |

**具体做法：**
- 血条 → 读 `Slider.value`（精确浮点值），不分析像素颜色
- UI 文字 → 读 `Text.text` / `TMP_Text.text`（字符串），不 OCR
- 玩家状态 → 读 `Transform.position` + `Rigidbody.velocity` + `Animator` 参数，不截图推断
- 场景对象 → 遍历 `Transform` 层级 + `GetComponent`，不视觉识别
- 敌人感知 → `game.get_entities` 批量读 AI/Health 组件，不数像素

> `camera.screenshot` 仅作为**辅助手段**（调试、确认视觉布局），不是感知游戏状态的主路径。所有游戏状态都应从 Unity 对象的组件属性中直接读取。

## 前置要求

- **Node.js 22+** — 运行 MCP Server
- **Git** — 克隆仓库
- **Unity 2022.3+**

## 安装

### 1. Unity Bridge（放到 Unity 工程 Assets/ 目录）

```bash
cd YourUnityProject/Assets/
git clone https://github.com/redcool/SimpleMCPBridge_Unity.git SimpleMCPBridge
```

安装后目录结构：

```
Assets/
├── SimpleMCPBridge/        ← 本仓库（git clone）
│   ├── Runtime/             # 运行时桥接代码
│   │   ├── BridgeClient.cs          # 核心桥接逻辑（连接/消息/加密）
│   │   ├── MCPBridge.cs             # MonoBehaviour 组件（生命周期/重连）
│   │   ├── MessageRouter.cs         # JSON-RPC 消息路由
│   │   ├── MCPToolRegistry.cs       # [MCPTool] 自动发现/注册
│   │   ├── MCPToolAttribute.cs      # [MCPTool] + [MCPToolClass] 特性
│   │   ├── MCPMethodConst.cs        # 工具名常量
│   │   ├── EncryptionHelper.cs      # AES-256-CBC 加解密
│   │   ├── WebSocketClient.cs       # 零依赖 RFC 6455 WebSocket
│   │   ├── WebSocketInterfaces.cs   # WebSocket 接口抽象
│   │   ├── Config/
│   │   │   └── BridgeConfig.cs      # 配置加载（Editor/Player）
│   │   ├── Handlers/       # 工具处理器（15+ Handler，88+ 个工具）
│   │   ├── Tools/          # 工具辅助类
│   │   └── Models/
│   ├── Editor/
│   │   └── MCPBridgeEditor.cs       # Tools > SimpleMCPBridge 窗口
│   ├── Plugins/             # 依赖 DLL（已含仓库内，clone 即可用）
│   ├── bridge-config.json   # IP/Port/加密配置
│   └── SimpleMCPBridge.asmdef  # 程序集定义（含 versionDefines）
├── ...
```

> SimpleMCPBridge 是独立 git 仓库，不是 Unity 项目的子模块。
> Plugins DLL 已包含在仓库内，clone 后无需手动下载。

### 2. MCP Server（任意目录）

```bash
# 任意目录下克隆
cd D:/dev/  # 举例，随意放哪里
git clone https://github.com/redcool/SimpleMCPServer.git
cd SimpleMCPServer
# 首次需要安装依赖
npm install
npm run build
```

之后每次启动只需运行 `start.bat`（会自动 build + 启动）。

### 3. InstantReplay（可选，录屏用）

通过 Unity Package Manager 安装：

```
UPM Git URL:
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay#release
```

或者手动编辑 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "jp.co.cyberagent.instant-replay": "https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay#release"
  }
}
```

安装后 `SimpleMCPBridge.asmdef` 的 `versionDefines` 会自动检测到该包，定义 `INSTANT_REPLAY_ON` 宏。

## 使用方法

1. 启动 MCP Server（双击 `SimpleMcpServer/start.bat`）
2. 用 Unity 打开项目
3. 菜单栏 → **Tools → SimpleMCPBridge**
4. 填写 Server IP/Port（默认 `127.0.0.1:45678`）
5. 点击 **Connect to Server**

Bridge 生命周期独立于窗口：关闭窗口后 bridge 继续运行，进出 Play Mode 自动重连。

## 配置

编辑 `bridge-config.json`：

```json
{
    "serverIp": "127.0.0.1",
    "serverPort": 45678,
    "encryptionKey": ""
}
```

配置加载策略：
- **Editor** — 直接从项目文件读取
- **Player** — 首次从 `Resources` 拷贝到 `Application.persistentDataPath`

## Payload 加密（可选）

替代 TLS/wss 的 AES-256-CBC 载荷加密：

| 特性 | 说明 |
|------|------|
| 算法 | AES-256-CBC, PKCS7 padding |
| 密钥派生 | SHA-256(encryptionKey) |
| 格式 | `#ENC#<base64(IV+ciphertext)>`（前缀格式，不再使用旧版 `{"encrypted":"..."}` JSON 包装） |
| 空密钥 | 透传（不加密） |
| 密钥不匹配 | 返回 `{"type":"error","code":"decrypt_failed"}` |

启用：Server 和 Bridge 的配置中设置相同的 `encryptionKey`。

选择载荷加密而非 TLS/wss 的原因：Unity Editor（Mono）TLS 兼容性差，Android IL2CPP 自签名证书部署复杂。

## 对象寻址

所有场景工具同时支持两种方式定位 GameObject：

| 参数 | 说明 |
|------|------|
| `instanceId` | Unity 实例 ID，精确唯一，但 domain reload 后失效 |
| `path` | Transform 路径（如 `"Canvas/Panel/Button"`），跨 domain reload 有效 |

解析优先级：`instanceId` > `path`。两个都传时先试 instanceId，找不到再 fallback 路径。

## 可用工具（共 88+ 个）

### 场景工具（SceneHandler，17 All + 9 Editor = 26 工具）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `scene.get_hierarchy` | All | — | 获取完整的场景层级树（含 position、components、children 及 transform path） |
| `scene.get_objects` | All | `nameContains` | 按名称过滤查找对象。如果 nameContains 包含 `/`，则当作 Transform 路径处理（如 `"Canvas/Button"`） |
| `scene.get_objects_by_type` | All | `typeName`(必填), `nameContains`, `layer`, `layerName`, `isIncludeInvisible` | 查找含有指定组件类型的所有 GameObject。typeName 支持短名称如 'Button'、'Image'、'Renderer'、'Collider'、'Selectable' |
| `scene.get_objects_by_tag` | All | `tag`(必填), `nameContains`, `layer`, `layerName` | 按 Tag 查找所有 GameObject |
| `scene.get_objects_by_path` | All | `path`(必填), `nameContains`, `layer`, `layerName` | 按 Transform 路径（如 `"Canvas/Panel/Button"`）查找 GameObject |
| `scene.create_object` | All | `name`, `position`[3], `rotation`[3], `scale`[3], `parentId`/`parentPath` | 创建新的 GameObject（支持位置/旋转/缩放/父级） |
| `scene.delete_object` | All | `instanceId`/`path` | 按 instanceId 或 path 删除 GameObject |
| `scene.set_transform` | All | `instanceId`/`path`, `position`[3], `rotation`[3], `scale`[3], `space`(world\|local) | 设置位置/旋转/缩放。space 参数：`world`（默认，transform.position）或 `local`（transform.localPosition） |
| `scene.set_component_property` | All | `instanceId`/`path`, `componentType`, `propertyName`, `value` | 设置组件上的可序列化字段/属性值（自动按字段类型转换） |
| `scene.get_components` | All | `instanceId`/`path` | 获取 GameObject 所有组件列表（类型名、完整类型名、enabled 状态） |
| `scene.get_component_properties` | All | `instanceId`/`path`, `componentType` | 获取组件所有可序列化属性名和当前值 |
| `scene.set_active` | All | `instanceId`/`path`, `active`(bool) | 启用或禁用 GameObject |
| `scene.duplicate_object` | All | `instanceId`/`path` | 复制 GameObject，新对象命名为 `"原名 (Copy)"` |
| `scene.rename` | All | `instanceId`/`path`, `name` | 重命名 GameObject |
| `scene.set_parent` | All | `instanceId`/`path`, `parentId`/`parentPath` | 设置父级。parentId/parentPath 留空则解除父级到根 |
| `scene.add_component` | All | `instanceId`/`path`, `componentType` | 添加组件（如 Rigidbody、BoxCollider）。自动在所有程序集中查找类型 |
| `scene.remove_component` | All | `instanceId`/`path`, `componentType` | 移除组件 |
| `scene.enter_play_mode` | Editor | — | 进入 Play Mode |
| `scene.exit_play_mode` | Editor | — | 退出 Play Mode（使用 delayCall 确保 JSON-RPC 响应先发送） |
| `scene.pause_play_mode` | Editor | `paused`(bool) | 暂停或继续 Play Mode |
| `scene.get_play_mode` | Editor | — | 获取当前播放模式状态。返回：isPlaying、isPaused、mode（edit\|playing\|paused） |
| `scene.save_current` | Editor | `savePath`(可选) | 保存当前场景。savePath 不传则覆盖保存；未命名场景 savePath 为必填 |
| `scene.instantiate_prefab` | Editor | `assetPath`(必填), `position`[3], `rotation`[3], `scale`[3], `parentId`/`parentPath` | 从项目 Assets 路径实例化预制体到场景 |
| `scene.set_material` ⚠ | Editor | `instanceId`/`path`, `materialIndex`(默认0), `color`[3或4], `texturePath` | 修改 Renderer 材质的颜色或主纹理。资产级材质修改建议直接改 .meta GUID |

### 编辑器工具（EditorHandler，7 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `editor.request_compile` | — | 触发 Unity 脚本重新编译。通过最小化→恢复 Editor 窗口来触发 Unity 事件处理，确保编译可靠启动 |
| `editor.open_window` | `menuPath`(必填) | 按菜单路径打开 Unity Editor 窗口（如 `"Tools/SimpleMCPBridge"`、`"Window/General/Console"`）。菜单项不存在时返回错误 |
| `editor.window_focus` | `action`(必填) | 通过 Win32 API 控制 Unity Editor 窗口状态。action: `minimize`/`restore`/`focus`/`maximize`/`get_state`。`get_state` 返回 `{state: 'normal'\|'minimized'\|'maximized'\|'hidden'}` |
| `editor.eval` | `code`(必填) | **编译并执行 C# 代码**（Mono.CSharp in-memory，即时，无 domain reload）。变量跨调用保持。预导入：System、System.Linq、System.Collections.Generic、UnityEngine、UnityEditor、UnityEngine.UI、UnityEngine.EventSystems。UnityEngine.Object 别名为 UnityObject。示例：`'GameObject.Find("Main Camera").transform.position.ToString()'` |
| `editor.get_console` | `count`(默认50) | 获取最近的 Editor 控制台日志。返回 `{entries: [{message, stackTrace, type, time}], count: N}`。type 值：Log、Warning、Error、Exception、Assert |
| `editor.get_preferences` | `keys`[](可选) | 读取 Editor/Project 设置。无 keys 时返回常用默认集：productName、companyName、scriptingBackend、apiCompatibilityLevel、buildTarget、activeBuildTargetGroup、unityVersion 等。传入 keys 数组可查询自定义 EditorPrefs |
| `editor.get_project_tree` | `path`(默认Assets), `maxDepth`(默认5,最大10) | 获取 Assets 目录树。返回嵌套 JSON 数组 `{name, path, type(folder/file), size(bytes)}` |
| `editor.undo` | — | 撤销上一次操作 |
| `editor.redo` | — | 重做上一次撤销的操作 |

### Scene View 工具（SceneViewHandler，2 工具，仅 Editor）

| 工具 | 参数 | 说明 |
|------|------|------|
| `scene_view.get_camera` | — | 获取当前 SceneView 相机状态：position、rotation、pivot、size（正交）或 fieldOfView（透视）、isOrthographic、farClip、nearClip。无 SceneView 打开时返回错误 |
| `scene_view.set_camera` | `position`[3], `rotation`[3], `pivot`[3], `size`, `isOrthographic` | 设置 SceneView 相机。所有参数可选。pivot 是相机环绕的世界坐标点。size：正交模式为尺寸，透视模式为 FOV 度数 |

### 录制工具（RecordingHandler，4 工具，需 InstantReplay）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `recording.start` | Android/iOS/Standalone | `width`(1280), `height`(720), `fps`(30), `enableAudio`(false), `quality`(50,1-100) | 开始通过 CyberAgent InstantReplay 录屏（OS 原生编码）。仅在 Play Mode 下可用。返回 success、outputPath、width、height、fps |
| `recording.stop` | Android/iOS/Standalone | — | 停止录制并开始 MP4 编码。立即返回 `status:"encoding"`。轮询 recording.status 等待完成 |
| `recording.status` | Android/iOS/Standalone | — | 查询录制/编码状态。状态流转：`idle` → `recording` → `encoding` → `completed`/`error`。完成后返回 filePath |
| `recording.reset` | Android/iOS/Standalone | — | 强制重置录制系统。编码超时或卡死时用于恢复。清理所有会话状态 |

录制流程：

```
1. scene.enter_play_mode          # 进入播放模式
2. recording.start                # 开始录制（默认 1280x720@30fps）
3. scene.set_transform(...)       # 操作场景对象
4. recording.stop                 # 停止录制
5. recording.status → polling    # 轮询直到 state="completed"
6. scene.exit_play_mode           # 退出播放模式
```

Quality → Bitrate 映射：

| quality | bitrate | 级别 |
|---------|---------|------|
| 90-100 | 12 Mbps | 非常高 |
| 75-89  | 8 Mbps  | 高 |
| 50-74  | 4 Mbps  | 中等（默认）|
| 25-49  | 2 Mbps  | 低 |
| 1-24   | 1 Mbps  | 非常低 |

> **FreshFrameProvider**：每帧创建独立 RenderTexture，解决 InstantReplay 默认 ScreenshotFrameProvider 的纹理重用竞态问题。

### 屏幕点击工具（ScreenHandler，1 工具，无需额外依赖）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.click_screen` | `x`(必填,0-1), `y`(必填,0-1) | 在归一化屏幕坐标模拟用户点击。经过完整 EventSystem 事件管线：RaycastAll → PointerDown → PointerUp → PointerClick。返回 hit 列表和实际点击对象。需要场景中有活动的 EventSystem（Play Mode 或 Runtime）。**不依赖 Input System**，只使用 Unity 内置 UnityEngine.EventSystems |

### 输入模拟工具（InputHandler，7 工具，需 Input System 包）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.mouse_click` | `x`(必填,0-1), `y`(必填,0-1), `button`(0左/1右/2中) | 在归一化屏幕坐标模拟鼠标点击。使用 Input System 虚拟鼠标（MouseDeviceTools）。适用于接收 Input System 事件的物体 |
| `input.mouse_move` | `dx`(必填,pixel), `dy`(必填,pixel) | 按像素增量移动鼠标（用于视角旋转/瞄准）。正 dx=右移，正 dy=上移。典型值：(50,0)=右转，(0,-30)=上仰。保持当前按钮状态，不中断按住。使用 InputSystem.QueueStateEvent 在物理鼠标设备上 |
| `input.key_press` | `key`(必填), `action`(tap\|hold\|release) | 模拟键盘按键。action 说明：`tap`=按下立即释放（跳跃/射击），`hold`=持续按住（WASD 移动），`release`=释放按键（key=`*` 或省略时释放全部）。使用 InputSystem.QueueStateEvent 在物理键盘设备上 |
| `input.touch` | `action`(必填,tap\|start\|move\|end), `x`(0-1), `y`(0-1), `fingerId`(0) | 触屏模拟。action: `tap`=立即触+放，`start`=开始触摸，`move`=移动到新位置，`end`=抬起。滑动手势：连续调用 start → move × N → end |
| `input.swipe` | `startX`/`startY`(必填,0-1), `endX`/`endY`(必填,0-1), `duration`(0.3s), `steps`(15), `fingerId`(0) | 从一个点到另一个点平滑滑动手势。异步多帧执行，立即返回估算时长。使用 game.wait 等待完成 |
| `input.gamepad` | `action`(必填) | 虚拟手柄控制。action 操作：`button`→需 `button`(名称)+ `press`(tap/press/release)；`axis`→需 `axis`(名称)+ `value`(-1..1)；`set`→批量，需 `buttons[]` + `axes{}`；`reset`→全部归零；`state`→查询当前状态 |
| `input.get_state` | — | 查询所有当前输入状态。返回：trackedKeys（当前按下的键列表）、mousePosition（归一化鼠标位置）、mouseDelta、mouseScroll、gamepadState（连接检测+所有按钮/轴值） |

**gamepad 按键名**：`south/a`、`east/b`、`north/x`、`west/y`、`leftShoulder/lb`、`rightShoulder/rb`、`leftStick`、`rightStick`、`start`、`select/back`、`dpadUp/down/left/right`

**gamepad 轴名**：`leftStickX/Y`、`rightStickX/Y`、`leftTrigger`、`rightTrigger`

### UI 分析 / 操作工具（GameHandler，8 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `ui.get_texts` | `contains`(可选过滤) | 从内存读取所有屏幕 UI 文本（无 OCR，零 Token 成本）。扫描活动 Canvas 中的 Text 和 TextMeshProUGUI 组件。每个元素返回：文本内容、类型、transform path、归一化屏幕矩形 [xMin,yMin,xMax,yMax]、中心点、字号、颜色。同时返回屏幕尺寸作为坐标参考 |
| `ui.find` | `type`(Button/Toggle/Slider/…), `contains`(文本包含), `interactable`(bool) | 查找可交互 UI 元素（Button、Toggle、Slider、Dropdown、InputField）及屏幕位置和状态。每个元素返回：type、path、label/text、interactable、归一化屏幕矩形、中心点。用 center 位置配合 input.click_screen 点击 |
| `ui.set_input_field_text` | `path`/`instanceId`, `text` | 设置 InputField/TMP_InputField 文本内容 |
| `ui.set_toggle` | `path`/`instanceId`, `value`(bool) | 设置 Toggle 开关状态 |
| `ui.set_slider` | `path`/`instanceId`, `value`(float), `normalized`(默认true) | 设置 Slider 值。normalized=true 时 0-1 映射到 [minValue,maxValue] |
| `ui.select_dropdown_option` | `path`/`instanceId`, `option`(int) 或 `optionText`(string) | 按索引或文本选择 Dropdown 选项 |
| `ui.drag` | `fromPath`/`fromInstanceId`, `toPath`/`toInstanceId` | 通过 ExecuteEvents 模拟从一个 UI 元素拖拽到另一个 |
| `ui.get_tooltip` | `path`/`instanceId` 或 `x`,`y` | 触发 PointerEnter 并扫描新出现的可见文本，返回 Tooltip 内容 |

### 统一输入工具（GameHandler，1 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.action` | `keys`[], `mouse`{}, `axes`{} | 单次调用组合输入。`keys`= `{key, action}`数组（action: tap/hold/release）；`mouse`=可选 x/y(归一化)、dx/dy(像素增量)、scroll(浮动)、buttons(数组)；`axes`=Input System 动作名→值字典（如 `{"Horizontal":1.0,"Vertical":0.5,"Fire1":1.0}`）。轴通过 ApplyOverrideValue 设置（同时馈入 legacy Input.GetAxis）。返回执行的操作列表 |

### 游戏状态 / 等待 / 连续感知 / 动作序列 / 空间感知 / 批量调度工具（GameHandler，14 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `game.get_state` | `includeUI`(默认true), `playerPath`(可选) | 单次调用获取游戏综合快照。返回：活动场景名+buildIndex、时间(time/deltaTime/timeScale/frameCount)、屏幕尺寸、播放模式状态、所有 UI 文本、所有可交互 UI、可选玩家位置（通过 playerPath 指定）、活动 Input System 轴、**相机信息（position、forward、fov、isOrthographic、nearClipPlane、farClipPlane）**。设置 includeUI=false 跳过 UI 扫描加速响应。**核心感知工具——一次调用提供 AI 80% 决策所需信息** |
| `game.get_time_scale` | — | 获取当前时间缩放。返回：timeScale（Time.timeScale）、fixedDeltaTime（Time.fixedDeltaTime）、realtimeSinceStartup、frameCount |
| `game.get_spatial` | `origin`[3](可选), `playerPath`(可选), `radius`(默认10), `maxObjects`(默认20), `tag`, `layerName`, `typeFilter` | 获取参考点周围指定半径内的 3D 物体空间信息。返回物体名、instanceId、组件列表、世界坐标、距离、方向(归一化向量)、tag、layer。自动过滤空对象。支持按 tag/layer/组件类型过滤 |
| `game.watch` | `signals`[](必填) | 注册一组信号持续监测。每个 signal 含 id、type(`property`)、path、component、property。返回当前值作为基线。示例：`[{"id":"hp","type":"property","path":"Player","component":"Health","property":"currentHP"}]` |
| `game.get_delta` | — | 获取自上次调用以来所有 watch 信号的变化。只返回有变化的值（含新旧值）。无变化时返回空 |
| `game.get_entities` | `typeFilter`(可选, AI/Health/CharacterController), `maxResults`(默认20) | 批量获取场景中带指定组件的实体及其关键状态。每个实体返回：name、instanceId、position、rotation、velocity（如有）、组件摘要值 |
| `game.get_player` | `playerPath`(可选), `includeComponents`(默认false) | 一步获取玩家完整状态。返回：position、rotation、velocity、动画状态（如有 Animator）、所有挂载组件的关键属性。不传 playerPath 时自动查找 tagged Player |
| `game.do_sequence` | `steps`[](必填), `timeout`(默认30s) | 在 Unity 侧一次性执行一组动作序列。返回 sequence ID 立即返回。支持 step type：`wait`(等待)、`key`(键盘)、`mouse_click`(鼠标点击)、`mouse_move`(鼠标移动)、`gamepad`(手柄)、`click_screen`(UI 点击)。key/mouse/gamepad 需 Input System，click_screen 不需要 |
| `game.get_animator_state` | `instanceId`/`path`(必填) | 获取 Animator 当前状态。返回：stateHash、tagHash、normalizedTime、speed、parameters（所有参数名+类型+值） |
| `game.sequence_status` | `id`(必填) | 轮询 game.do_sequence 的执行状态。返回 status(`running`/`completed`/`error`)、step(当前步)、total(总步数)、log(最近日志) |
| `game.set_time_scale` | `timeScale`(必填,float) | 设置 Time.timeScale。0=暂停，0.5=半速，1=正常，2=2 倍速。返回设置后的 timeScale 和 fixedDeltaTime |
| `game.wait` | `type`(必填), `value`, `timeout`(默认30s) | 启动异步等待操作。立即返回 wait ID，用 game.wait_check 轮询完成状态 |
| `game.wait_check` | `id`(必填) | 轮询 game.wait 的完成状态。返回 status：`completed`/`waiting`/`timeout`/`error` |
| `game.batch` | `calls`[](必填) | 在单个 Unity 帧内批量执行多个工具调用。calls 为 `{name, arguments}` 数组，最多 50 个。返回 `{count, results: [{name, result}]}`。将 N+1 次 round trip 降为 1 次 |

### 资源工具（AssetHandler，3 工具）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `asset.refresh` | All | — | 刷新 Unity 资产数据库以导入新文件或检测变更。如果导入了新脚本，将触发 domain reload 且 MCP 连接会断开。客户端应轮询 /health 直到 bridgeConnected=true 确认完成 |
| `asset.find_assets` | Editor | `nameContains`(可选), `typeFilter`(可选) | 按名称和/或类型搜索 Assets。typeFilter：Unity 资源类型名如 'Prefab'、'Material'、'Texture'、'Scene'。返回 `{filter, count, assets: [{path, name, type, guid}]}` |
| `asset.find_references` | All | `assetPath`(必填) | 查找引用指定资源的所有资源。通过扫描文件内容中的目标 GUID 实现。可靠但较慢——扫描 Assets/ 下所有文本资源文件。返回引用者数组及 GUID |

### 物理工具（PhysicsHandler，5 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `physics.raycast` | `origin`[3](必填), `direction`[3](必填), `maxDistance`, `layerMask` | 从 origin 沿 direction 发射射线，返回第一个碰撞结果。返回：hit(bool)、point、normal、distance、colliderType、gameObjectName、instanceId、transform path |
| `physics.box_cast` | `origin`[3], `halfExtents`[3], `direction`[3], `maxDistance`(默认100), `layerMask`(默认-1) | 沿 direction 方向扫描盒体，返回第一个碰撞结果 |
| `physics.sphere_cast` | `origin`[3], `radius`, `direction`[3], `maxDistance`(默认100), `layerMask`(默认-1) | 沿 direction 方向扫描球体，返回第一个碰撞结果 |
| `physics.overlap_sphere` | `center`[3], `radius`, `layerMask`(默认-1) | 返回球体内所有碰撞体。每个结果包含：name、instanceId、path、position、tag、layer |
| `physics.overlap_box` | `center`[3], `halfExtents`[3], `rotation`[4](可选) | 返回盒体内所有碰撞体。rotation 为可选四元数 [x,y,z,w] |

### 相机工具（CameraHandler，1 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `camera.screenshot` | `savePath`(必填), `cameraName`(默认Main Camera), `width`, `height` | 截取指定相机画面并保存为 PNG。savePath 是相对路径（相对 `VideoRecord/` 根目录）或绝对路径。路径根目录：Editor → `<项目根>/VideoRecord/`，Runtime → `<temporaryCachePath>/VideoRecord/`。自动创建目录。返回绝对文件路径 |

### 音频工具（AudioHandler，1 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `audio.get_sources` | `maxResults`(默认50,上限200) | 返回所有正在播放的 AudioSource 信息。每个结果包含：clipName、volume、isPlaying、time、loop、spatialBlend、position、distanceFromListener、path、instanceId |

### 导航工具（NavHandler，4 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `nav.query_path` | `start`[3], `end`[3], `areaMask`(默认-1), `snapDistance`(默认2.0) | 在 NavMesh 上计算两点间路径。返回：reachable(bool)、status(PathComplete/PathPartial/PathInvalid)、waypoints[[x,y,z]...]、distance(float) |
| `nav.sample_position` | `position`[3], `maxDistance`(默认2.0), `areaMask`(默认-1) | 将世界坐标吸附到最近的 NavMesh 点 |
| `nav.has_navmesh` | — | 检查 NavMesh 是否存在。返回：hasNavMesh(bool)、vertexCount、triangleCount |
| `nav.move_to` | `instanceId`/`path`(必填), `destination`[3](必填), `speed`(可选), `stopDistance`(可选) | 设置 NavMeshAgent 目标点，自动寻路移动。返回 agent 状态（pathPending、remainingDistance、isStopped、velocity） |

### 资源热替换工具（AssetBundleHotReplaceHandler + ShaderHotReplaceHandler，6 工具）

这些工具使用 `RequirePlayMode = true`，仅当 Play Mode 激活时才注册。

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `shader.hot_replace` | PlayMode | `abUrl`(必填), `shaderName`(必填), `paths`[](可选), `oldShaderName`(可选), `materialIndex`(可选) | 从 AssetBundle 热替换 Shader。异步，返回 replaceId，轮询 shader.hot_replace_status |
| `shader.hot_replace_status` | PlayMode | `id`(必填) | 查询 shader.hot_replace 进度 |
| `assetbundle.hot_replace` | PlayMode | `abUrl`(必填), `types`[](可选), `paths`[](可选), `oldShaderName`(可选), `saveBackup`(可选), `dryRun`(可选) | 通用 AssetBundle 热部署。自动分发 Shader/Material/Texture/AudioClip/Mesh/ScriptableObject/Prefab。异步，轮询 status |
| `assetbundle.hot_replace_status` | PlayMode | `id`(必填) | 查询替换进度（各类型计数） |
| `assetbundle.rollback` | PlayMode | `id`(必填) | 回滚一次带 saveBackup 的替换操作 |
| `assetbundle.unload_all` | PlayMode | — | 卸载所有已加载的 AB（⚠ 会破坏引用，材质变粉） |

由 SimpleMcpServer 直接提供，不在 Bridge 注册：

| 工具 | 参数 | 说明 |
|------|------|------|
| `bridge.list` | — | 列出所有已连接 Bridge（ID/IP/工具数） |
| `bridge.call` | `target`(bridgeId), `method`, `params` | 定向调用指定 Bridge 上的工具 |

## Multi-Bridge 路由

多个 Unity Bridge 可同时连接：

- **路由规则**：`last-registration-wins` — 后连接的 bridge 覆盖同名工具
- Editor bridge（88+ tools）与 Android bridge（部分工具）共存
- **Bridge 断线**：该 bridge 的工具从路由表移除；有其他 bridge 注册同工具时自动回退
- 无 bridge 时待处理调用进入重试队列（30s 宽限期）
- 使用 `bridge.list` 查看所有已连接 bridge

## LLM 集成

Bridge 可通过 Server 调用外部 LLM：

```
Bridge → Server: {"type":"ai_request","prompt":"...","context":{...},"requestId":"..."}
Server → Bridge: {"type":"ai_response","text":"...","requestId":"..."}
```

- 支持任何 OpenAI 兼容 API（OpenAI、Agnes、Ollama 等）
- API Key 支持 `LLM_API_KEY` 环境变量
- Server `config.json` 中 `llm.enabled: false` 关闭

## 自定义工具

1. 创建新类：

```csharp
using SimpleMCPBridge.Runtime;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

public class MyTools
{
    [MCPTool("my_tool_name", "工具描述",
             Platform = MCPToolPlatforms.All,
             RequirePlayMode = true)]
    public static string MyTool(string paramsJson)
    {
        var args = ParseJsonObject(paramsJson);
        var name = GetString(args, "name", "default");
        return "{\"success\":true}";
    }
}
```

`RequirePlayMode = true` 时该工具仅当应用处于播放模式时注册，避免 Editor Edit Mode 下误调用。

2. **自动注册** — `MessageRouter` 构造时扫描程序集，自动发现带 `[MCPTool]` 的方法。

### 规则

- 方法签名必须为 `public static string MethodName(string paramsJson)`
- 类需要 **public 无参构造函数**（static class 自动跳过）
- `[MCPToolClass]` 标记类可加速发现
- `Platform` 可选，控制哪些构建目标注册该工具
- `RequirePlayMode` 可选，为 `true` 时工具仅在 Play Mode 时注册
- 修改 C# 后等待 Unity 编译完成

### HandlerUtils 静态工具类

| 方法 | 说明 |
|------|------|
| `ParseJsonObject(json)` | 解析 JSON 为 `Dictionary<string, object>` |
| `GetString(dict, key, default)` | 取字符串，带默认值 |
| `GetRequiredString(dict, key)` | 取必填字符串 |
| `GetRequiredInt(dict, key)` | 取必填 int |
| `GetOptionalInt(dict, key)` | 取可选 int，返回 `int?` |
| `GetRequiredBool(dict, key)` | 取必填 bool |
| `GetOptionalFloatArray(dict, key)` | 取可选 `float[]` |
| `ErrorJson(message)` | 构建错误响应 JSON |

用法：`using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;`

## 条件编译

录制工具依赖 **CyberAgent InstantReplay**（Unity Package `jp.co.cyberagent.instant-replay`）。

`SimpleMCPBridge.asmdef` 中 `versionDefines` 自动检测包是否存在，存在时定义 `INSTANT_REPLAY_ON`：

| 场景 | INSTANT_REPLAY_ON | FreshFrameProvider | RecordingHandler |
|------|-------------------|-------------------|-----------------|
| 包已安装 | ✅ 自动定义 | ✅ 编译 | ✅ 编译 |
| 包未安装 | ❌ 未定义 | ❌ 跳过 | ❌ 跳过 |

## 代码质量改进

### 1. Editor Eval 开关

`editor.eval` 现在可通过 EditorPrefs 全局开关控制（默认 ON）。在 MCPBridge Inspector 中显示为 **"Editor Eval (global)"** 切换按钮。关闭后 `editor.eval` 调用返回错误，防止意外执行 C# 代码。

### 2. 加密格式变更

加密载荷格式从 `{"encrypted":"<base64>"}` JSON 包装改为 `#ENC#<base64>` 前缀格式。**Server 端也必须使用 `#ENC#` 前缀**，两端需同步更新。

### 3. 代码质量加固

多线程安全、资源泄漏修复、性能优化等多项改进。

## 已知问题

### 1. MPEG4Writer "Stop() called but track is not started"（Android 录屏）

**根因**：InstantReplay 始终创建音频轨道，即使 `enableAudio=false`。音频轨道从未收到帧。
**影响**：无害。MP4 视频轨道完整，播放正常。

### 2. `BuildJsonObject` 字符串值必须预引号

`BuildJsonObject(("key", "value"))` 生成 `"key":value`（裸词）。字符串值需通过 `EscapeString("value")` 包装。

### 3. 端口占用

`ws` 库的 WebSocketServer 在进程退出后有时端口保持绑定：

```powershell
Get-Process -Name "node" | Stop-Process -Force
```

### 4. 多窗口 Unity 多 bridge

开启多个 Unity 进程（如多个 Editor 窗口）时各自建立独立 WebSocket 连接，每个进程一个 bridgeId。

### 6. `RequirePlayMode` 注册时机

`MCPToolRegistry` 在 `BridgeClient` 构造时扫描注册工具。由于 `BridgeClient` 是单例，工具注册只发生一次。进出 Play Mode 时如果 bridge 未断开，工具列表不会动态更新。但在标准工作流中（domain reload → bridge 重连），每次进入/退出 Play Mode 都会重新注册。

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [SimpleMCPBridge](https://github.com/redcool/SimpleMCPBridge_Unity) | 本仓库 — Unity 桥接包 |
| [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) | MCP Server — Node.js/TypeScript |
| [InstantReplay](https://github.com/CyberAgentGameEntertainment/InstantReplay) | CyberAgent InstantReplay — OS 原生硬编码录屏 |
