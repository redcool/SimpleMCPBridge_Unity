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

**工具类别机制**：95+ 个工具按类别组织（`[Scene]`/`[Ui]`/`[Ngui]`/`[Input]`/`[Game]`…），
并支持运行时**按类别动态开关**（`tools.enable` / `tools.disable`）——只注册当前
任务需要的工具集，减少 token 消耗。详见下文「工具类别与动态注册」章节。

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
│   │   ├── WebSocketClient.cs       # 零依赖 RFC 6455 WebSocket（[Obsolete]，参考用）
│   │   ├── NetWebSocketClient.cs    # 活动传输：.NET ClientWebSocket 封装
│   │   ├── WebSocketInterfaces.cs   # WebSocket 接口抽象
│   │   ├── Config/
│   │   │   └── BridgeConfig.cs      # 配置加载（Editor/Player）
│   │   ├── Handlers/       # 工具处理器（17+ Handler，95+ 个工具）
│   │   │   └── NguiHandler.cs   # NGUI 工具（#if NGUI_ON 条件编译，未装 NGUI 不注册）
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

`scene.call_component_method` 权限字段（可选，需重启生效）：
- `methodBlocklist` — 追加拦截项（`"MethodName"` 或 `"TypeName.MethodName"`，大小写不敏感）；代码默认 6 项
  （`destroy`/`destroyimmediate`/`destroyobject`/`quit`/`quitimmediate`/`disconnect`）始终生效，无法通过配置移除
- `methodAllowlist` — 空 = 关闭；非空 = 白名单模式，只放行命中的方法；白名单**不会覆盖**黑名单
  （代码默认 + 配置追加的双重拦截始终优先）

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

## 可用工具（共 127 个）

### 场景工具（SceneHandler，18 All + 10 Editor = 28 工具）

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
| `scene.load_scene` | All | `sceneName`(必填), `mode`(single\|additive), `async` | 加载场景。Editor 非 Play Mode 时打开场景资产；其余经 SceneManager（Play/built 均可用）。⚠ 加载后所有 instanceId 失效，需重新获取层级 |
| `scene.save_prefab` | Editor | `instanceId`/`path`, `assetPath`(必填) | 将 GameObject 保存为预制体资产（已存在则覆盖）。PrefabUtility 操作不可撤销 |

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
| `recording.start` | All(Play Mode) | `width`(1280), `height`(720), `fps`(30), `enableAudio`(false), `quality`(50,1-100) | 开始通过 CyberAgent InstantReplay 录屏（OS 原生编码）。仅在 Play Mode 下可用。返回 success、outputPath、width、height、fps |
| `recording.stop` | All(Play Mode) | — | 停止录制并开始 MP4 编码。立即返回 `status:"encoding"`。轮询 recording.status 等待完成 |
| `recording.status` | All(Play Mode) | — | 查询录制/编码状态。状态流转：`idle` → `recording` → `encoding` → `completed`/`error`。完成后返回 filePath |
| `recording.reset` | All(Play Mode) | — | 强制重置录制系统。编码超时或卡死时用于恢复。清理所有会话状态 |

> **平台支持**: `recording.*` 基于 CyberAgent InstantReplay，该包官方支持 **Android / iOS / macOS / Windows /
> Linux（需 ffmpeg）/ Web（需 WebCodecs）**，全部走 OS 原生编码（MediaCodec / VideoToolbox / Media Foundation），
> 无需外部工具。桥注册平台为 `Android | iOS | Standalone | Editor` 且 `RequirePlayMode = true`（仅 Play Mode 注册）。
> Editor（Windows/macOS）侧录制——例如 AI 迭代粒子特效时的反馈回路——**已默认启用**（2026-08 起，Windows Editor
> 录制已实测通过）；multi-bridge 下如 Editor 与 Android 同时在线，遵循 last-registration-wins，需要确定性目标时用
> `bridge.call` 显式指定。Editor 下输出到项目 `VideoRecord/`（与 camera.screenshot 同根目录），设备构建输出到
> `temporaryCachePath/VideoRecord/`。

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

### 城堡点击工具（BuildingHandler，1 工具，项目特定）

| 工具 | 参数 | 说明 |
|------|------|------|
| `castle.click_building` ⚠ | `x`(必填,0-1), `y`(必填,0-1) | 在归一化屏幕坐标点击 3D 城堡建筑（Physics.Raycast 层 20+22 + Lua FakeHitResultEvent）。**项目特定工具**——通用工程无此需求时可 `tools.disable` 忽略 |

### 输入模拟工具（InputHandler，7 工具，需 Input System 包）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.mouse_click` | `x`(必填,0-1), `y`(必填,0-1), `button`(0左/1右/2中) | 在归一化屏幕坐标模拟鼠标点击。使用 Input System 虚拟鼠标（MouseDeviceTools）。适用于接收 Input System 事件的物体 |
| `input.mouse_move` | `dx`(必填,pixel), `dy`(必填,pixel) | 按像素增量移动鼠标（用于视角旋转/瞄准）。正 dx=右移，正 dy=上移。典型值：(50,0)=右转，(0,-30)=上仰。保持当前按钮状态，不中断按住。使用 InputSystem.QueueStateEvent 在物理鼠标设备上 |
| `input.key_press` | `key`(必填), `action`(tap\|hold\|release) | 模拟键盘按键。action 说明：`tap`=按下立即释放（跳跃/射击），`hold`=持续按住（WASD 移动），`release`=释放按键（key=`*` 或省略时释放全部）。使用 InputSystem.QueueStateEvent 在物理键盘设备上 |
| `input.touch` | `action`(必填,tap\|start\|move\|end), `x`(0-1), `y`(0-1), `fingerId`(0) | 触屏模拟。action: `tap`=立即触+放，`start`=开始触摸，`move`=移动到新位置，`end`=抬起。滑动手势：连续调用 start → move × N → end |
| `input.swipe` | `startX`/`startY`(必填,0-1), `endX`/`endY`(必填,0-1), `duration`(0.3s), `steps`(15), `fingerId`(0) | 从一个点到另一个点平滑滑动手势。异步多帧执行，立即返回估算时长。使用 game.wait 等待完成 |
| `input.gamepad` | `action`(必填) | 虚拟手柄控制。action 操作：`button`→需 `button`(名称)+ `press`(tap/press/release)；`axis`→需 `axis`(名称)+ `value`(-1..1)；`set`→批量，需 `buttons[]` + `axes{}`；`reset`→全部归零；`state`→查询当前状态；`rumble`→震动(`lowFreq`/`highFreq` 0-1, `duration` 秒,<=0 停止) |
| `input.get_state` | — | 查询所有当前输入状态。返回：trackedKeys（当前按下的键列表）、mousePosition（归一化鼠标位置）、mouseDelta、mouseScroll、gamepadState（连接检测+所有按钮/轴值） |

**gamepad 按键名**：`south/a`、`east/b`、`north/x`、`west/y`、`leftShoulder/lb`、`rightShoulder/rb`、`leftStick`、`rightStick`、`start`、`select/back`、`dpadUp/down/left/right`

**gamepad 轴名**：`leftStickX/Y`、`rightStickX/Y`、`leftTrigger`、`rightTrigger`

### UI 分析 / 操作工具（GameHandler，8 工具）— uGUI

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

### NGUI 分析工具（NguiHandler，3 工具）— 独立工具集 ⚠ 需安装 NGUI

> 针对使用 **NGUI**（第三方老牌 UI 插件）的旧项目。与上方 uGUI 工具**互相独立、互不互斥**：
> 可同时开启，也可 `tools.disable ["Ui"]` 只留 NGUI（或反之，类别 `Ngui`/`Ui`）。
> 点击模拟直接复用 `input.click_screen`（NGUI 的 UICamera 响应屏幕坐标）。

**安装 NGUI（两种方式任选）**：`tasharen/ngui` 仓库无 package.json，不能直接用 versionDefines 检测。

**方式 A：包化成 UPM 包（推荐）**——`NGUI_ON` 由 versionDefines 自动定义：

1. `git clone https://github.com/tasharen/ngui.git <某目录>`
2. 仓库根补 `package.json`：`{"name": "com.tasharen.ngui", "version": "3.12.0", ...}`
3. 建 asmdef：`Scripts/NGUI.asmdef`（运行时，程序集名 `NGUI`）+ `Scripts/Editor/NGUI.Editor.asmdef`（`includePlatforms: ["Editor"]`，引用 `NGUI`）
4. 排除示例：`Assets/NGUI/Examples` → 改名 `Examples~`
5. 项目 `Packages/manifest.json` 加 `"com.tasharen.ngui": "file:../../ngui"`

**方式 B：源码直接放 Assets/ + 手动符号**——仅省去「package.json + manifest 引用」：

1. NGUI 源码拷入 `Assets/NGUI/`，**必须**建与方式 A 相同的两个 asmdef（asmdef 程序集无法引用裸放进 Assembly-CSharp 的 NGUI 代码）
2. 排除示例：`Assets/NGUI/Examples` → 改名 `Examples~`
3. **手动**加 `NGUI_ON` 符号：`Project Settings → Player → Scripting Define Symbols` 加 `NGUI_ON`（或 `SimpleMCPBridge.asmdef` 的 `defineConstraints` 加 `"NGUI_ON"`）

> ⚠️ **警告**：`#if NGUI_ON` 打开但 NGUI 类型不可解析会报 CS0246——NGUI 源码必须放在有 `NGUI.asmdef` 的程序集里，
> 且 `SimpleMCPBridge.asmdef` 的 `references` 含 `"NGUI"`（软引用）。两种方式此前提相同。

SimpleMCPBridge 侧已配好：asmdef `versionDefines`（`com.tasharen.ngui` → `NGUI_ON`）+ `references` 软引用 `"NGUI"`。未装 NGUI 时工具不注册、仅一条 warning，不影响编译。

| 工具 | 参数 | 说明 |
|------|------|------|
| `ngui.get_texts` | `contains`(可选过滤) | 从内存读取所有 NGUI UILabel 文本（无 OCR）。每个元素返回：文本内容、类型、transform path、归一化屏幕矩形 [xMin,yMin,xMax,yMax]、中心点、字号、对齐、颜色。同时返回屏幕尺寸 |
| `ngui.find` | `type`(UIButton/UIToggle/UISlider/UIInput), `contains`(文本包含), `interactable`(bool) | 查找可交互 NGUI 元素及屏幕位置和状态。每个元素返回：type、path、label/text、interactable、归一化屏幕矩形、中心点。用 center 配合 input.click_screen 点击 |
| `ngui.find_widgets` | `type`(widget 类型名如 UITexture/UISprite/UILabel), `contains`(名称或文本包含) | 查找所有 NGUI UIWidget（UITexture/UISprite/UILabel 等）及屏幕位置。每个元素返回：type、name、path、instanceId、归一化屏幕矩形、中心点；UILabel 额外含 text/fontSize/alignment/color。定位纯显示元素（如背景图）用此工具 |

### UI Toolkit 分析工具（UIToolkitHandler，8 工具）— 独立工具集

> 针对使用 **UI Toolkit**（`UnityEngine.UIElements`，UXML/USS/UIDocument）构建运行时 UI 的项目。
> 与 uGUI（类别 `Ui`）和 NGUI（类别 `Ngui`）**互相独立、互不互斥**，类别 `Uitk`。
> UI Toolkit 是引擎内置模块，**无条件编译**——无需装包、无需 versionDefines，注册即用；
> 面板未 attach（非运行态）时优雅返回空结果，无需 Play Mode。
> **不设 `UITK_ON` 条件编译**（有意决策）：`com.unity.modules.uielements` 是内置模块非可选包，2022.3 必在；
> 客户工程 runtime 不用 UITK 时用 `tools.disable ["Uitk"]` 裁剪（临时、任务级，domain reload 自动恢复全开）。
> **UITK 交互事件依赖 EventSystem 桥接**：面板要接收指针事件，场景必须有 `PanelEventHandler` + `PanelRaycaster`
> （通常挂在 UIDocument 的 PanelSettings 持有者上，如 `EventSystem/Default Panel Settings`）——没有桥接则 UITK 无事件。
> 注意：UITK 元素是 VisualElement（**非 GameObject，无 instanceId**）——用 `panelIndex`/`panelPath` + 元素 `path` 寻址
> （`uitk.get_elements` 返回的 fullPath）。`input.click_screen`（EventSystem）能否驱动 UITK **取决于场景是否桥接**：
> 面板 GameObject 挂了 `PanelEventHandler` + `PanelRaycaster` 时（UIDocument + EventSystem 的标准集成），click_screen
> 可经 EventSystem 射中面板并触发 Clickable 的 `clicked`（已实测验证）；未桥接时 EventSystem 射不到 UITK 面板。
> 确定性点击仍用 `uitk.click`（向 panel 注入 pooled PointerDown/Up，Clickable 自动生成 ClickEvent）。

| 工具 | 参数 | 说明 |
|------|------|------|
| `uitk.get_panels` | — | 列出所有 UIDocument 面板：name、gameObjectPath、enabled、activeInHierarchy、attached、sortingOrder、panelSettingsSortingOrder、屏幕尺寸。用 gameObjectPath 作为后续调用的 panelPath |
| `uitk.get_texts` | `contains`(可选过滤) | 从内存读取所有 UITK 文本（无 OCR）。扫描 Label/TextElement/TextField。每个元素返回：文本、类型、name、fullPath、归一化屏幕矩形 [xMin,yMin,xMax,yMax]、字号、颜色（Button 排除，由 uitk.find 覆盖） |
| `uitk.find` | `type`(Button/Toggle/...), `contains` | 查找可交互 UITK 元素（Button/Toggle/Slider/SliderInt/DropdownField/TextField/ScrollView）及屏幕位置和状态。每个元素返回：typeName、name、fullPath、interactable、text/value、choices 数、归一化屏幕矩形、中心点 |
| `uitk.get_elements` | `panelIndex`/`panelPath`(可选，默认全部) | 导出 UITK 视觉树。每个元素返回：name、typeName、fullPath、classes、enabled、visible、text/value、归一化矩形。单面板超 500 元素截断（truncated:true） |
| `uitk.click` | `panelIndex`/`panelPath` + `path`（元素模式）或 `x`,`y` 归一化坐标（坐标模式） | 点击 UITK 元素。元素模式：在 `el.worldBound.center` 派发 pooled MouseDown+MouseUp（`GetPooled(Event)`，Clickable 生成 ClickEvent）；坐标模式：归一化坐标映射到 `root.worldBound`（与 uitk.find/get_elements 的 normalizedRect 同原点，左上角）+ `panel.Pick` 命中检查。需面板已 attach 且已布局（运行态） |
| `uitk.set_value` | `panelIndex`/`panelPath` + `path` + `value`, `silent`(可选) | 设置元素值。Toggle=bool、Slider=float、SliderInt=int、DropdownField=索引或选项文本、TextField=string。默认 `value=X` 触发 ChangeEvent 生效；`silent=true` 用 SetValueWithoutNotify 静默 |
| `uitk.create_element` | `panelIndex`/`panelPath` + `type`(Button/Label/Slider/Toggle), `parent`(可选), `name`(可选), `text`(可选) | 运行时创建 UITK 元素并加入面板视觉树。⚠ **不持久**——面板刷新/UXML 重新应用即销毁。返回新元素路径 |
| `uitk.remove_element` | `panelIndex`/`panelPath` + `path`(必填) | 从面板视觉树移除元素。⚠ **不持久**——面板刷新即恢复。返回被移除路径及原父路径 |

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
| `game.get_delta` | — | 获取自上次调用以来所有 watch 信号的变化。**桥侧每 ~167ms（10帧）持续检测**并缓存变化，本调用纯读缓存（读后清空）——两次调用之间的短事件也不会漏。只返回有变化的值（含新旧值）。无变化时返回空 |
| `game.get_entities` | `typeFilter`(可选, AI/Health/CharacterController), `maxResults`(默认20) | 批量获取场景中带指定组件的实体及其关键状态。每个实体返回：name、instanceId、position、rotation、velocity（如有）、组件摘要值 |
| `game.get_player` | `playerPath`(可选), `includeComponents`(默认false) | 一步获取玩家完整状态。返回：position、rotation、velocity、动画状态（如有 Animator）、所有挂载组件的关键属性。不传 playerPath 时自动查找 tagged Player |
| `game.do_sequence` | `steps`[](必填), `timeout`(默认30s) | 在 Unity 侧一次性执行一组动作序列。返回 sequence ID 立即返回。支持 step type：`wait`(等待)、`key`(键盘)、`mouse_click`(鼠标点击)、`mouse_move`(鼠标移动)、`gamepad`(手柄)、`click_screen`(UI 点击)。key/mouse/gamepad 需 Input System，click_screen 不需要 |
| `game.get_animator_state` | `instanceId`/`path`(必填) | 获取 Animator 当前状态。返回：stateHash、tagHash、normalizedTime、speed、parameters（所有参数名+类型+值） |
| `game.sequence_status` | `id`(必填) | 轮询 game.do_sequence 的执行状态。返回 status(`running`/`completed`/`error`)、step(当前步)、total(总步数)、log(最近日志) |
| `game.set_time_scale` | `timeScale`(必填,float) | 设置 Time.timeScale。0=暂停，0.5=半速，1=正常，2=2 倍速。返回设置后的 timeScale 和 fixedDeltaTime |
| `game.wait` | `type`(必填), `value`, `timeout`(默认30s) | 启动异步等待操作。立即返回 wait ID，用 game.wait_check 轮询完成状态 |
| `game.wait_check` | `id`(必填) | 轮询 game.wait 的完成状态。返回 status：`completed`/`waiting`/`timeout`/`error` |
| `game.batch` | `calls`[](必填) | 在单个 Unity 帧内批量执行多个工具调用。calls 为 `{name, arguments}` 数组，最多 50 个。返回 `{count, results: [{name, result}]}`。将 N+1 次 round trip 降为 1 次 |

### 资源工具（AssetHandler，8 工具）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `asset.refresh` | All | — | 刷新 Unity 资产数据库以导入新文件或检测变更。如果导入了新脚本，将触发 domain reload 且 MCP 连接会断开。客户端应轮询 /health 直到 bridgeConnected=true 确认完成 |
| `asset.find_assets` | Editor | `nameContains`(可选), `typeFilter`(可选) | 按名称和/或类型搜索 Assets。typeFilter：Unity 资源类型名如 'Prefab'、'Material'、'Texture'、'Scene'。返回 `{filter, count, assets: [{path, name, type, guid}]}` |
| `asset.find_references` | All | `assetPath`(必填) | 查找引用指定资源的所有资源。通过扫描文件内容中的目标 GUID 实现。可靠但较慢——扫描 Assets/ 下所有文本资源文件。返回引用者数组及 GUID |
| `asset.create` | Editor | `type`(folder\|material), `name`, `path`, `color`[3或4](可选) | 创建资源。folder：新建文件夹；material：新建材质（可选颜色）。AssetDatabase 操作不可撤销 |
| `asset.delete` | Editor | `assetPath`(必填), `force`(bool) | 删除资源。默认先做 find_references GUID 引用预检——有引用时返回错误（列出引用数+首个引用者），`force=true` 跳过预检 |
| `asset.rename` | Editor | `assetPath`(必填), `newName`(必填,不含扩展名) | 重命名资源，返回新路径。AssetDatabase 操作不可撤销 |
| `asset.move` | Editor | `assetPath`(必填), `newPath`(必填) | 移动（或改父级）资源到新路径。返回旧+新路径 |
| `asset.build_bundle` | Editor | `shaderPaths`[](必填), `fileName`(可选), `buildTarget`(可选) | 构建 Shader AssetBundle 并上传到服务器，返回手机可达的 abUrl 供 shader.hot_replace 使用 |

### PlayerPrefs 工具（PlayerPrefsHandler，4 工具）

> Unity 没有 PlayerPrefs key 枚举 API——`playerprefs.get_all` 只能列出**本会话**经 set/get 见过的 key（其他代码写入的持久 key 无法枚举，用 `playerprefs.get` 精确读取）。

| 工具 | 参数 | 说明 |
|------|------|------|
| `playerprefs.get_all` | — | 获取本会话已知的所有 PlayerPrefs。每条含 `key`、`type`（int/float/string 类型嗅探）、`value` |
| `playerprefs.get` | `key`(必填), `keyType`(int/float/string, 可选) | 读取单个 key。keyType 省略时按 sentinel 默认值嗅探类型（int→float→string 顺序） |
| `playerprefs.set` | `key`(必填), `value`(必填), `valueType`(可选), `save`(默认true) | 设置 key。valueType 省略时按 JSON 值类型推断（int/float/string）；save=true 时 PlayerPrefs.Save() |
| `playerprefs.delete` | `key`(必填) | 删除单个 key 并保存 |

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
| `camera.screenshot` | `savePath`(可选), `cameraName`(默认Main Camera), `width`, `height` | 截取指定相机画面并保存为 PNG。savePath 是相对路径（相对 `VideoRecord/` 根目录）或绝对路径；**省略时自动生成时间戳文件名**（如 `screenshot_20260814_125044.png`）。路径根目录：Editor → `<项目根>/VideoRecord/`，Runtime → `<temporaryCachePath>/VideoRecord/`。自动创建目录。返回绝对文件路径 |

### 音频工具（AudioHandler，1 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `audio.get_sources` | `maxResults`(默认50,上限200) | 返回所有正在播放的 AudioSource 信息。每个结果包含：clipName、volume、isPlaying、time、loop、spatialBlend、position、distanceFromListener、path、instanceId |

### 粒子特效工具（ParticleHandler，10 工具）

创作/预览（Edit Mode 可撤销）+ 运行时播放（Play Mode 才注册）。使用模型：**Editor 创作（资产）+ Runtime 播放**。模块属性走 typed 代码路径（零反射，规避 CS1612 与 il2cpp 裁剪），改模块属性须先存局部变量。

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `particle.get_systems` | All | `maxResults`(默认50) | 列出场景所有 ParticleSystem + 实时状态（isPlaying/isEmitting/particleCount/time/enabledModules/renderer） |
| `particle.get_state` | All | `instanceId`(必填) | 单个系统的详细状态 + 关键模块配置（main/emission/shape 等值） |
| `particle.create` | All | `name`, `position`[3], `parentId`/`parentPath`, `loop`, `playOnAwake`, `startSpeed`, `startSize`, `startLifetime`, `startColor`, `rateOverTime`, `shapeType`, `gravityModifier`, `material`, `materialDir`, `shader` | 创建 GameObject + ParticleSystem（可带初始配置）。`material`：内置名（Default-Particle/Sprites-Default/Default-Material）或 `Assets/...` 路径，赋给 ParticleSystemRenderer（**创建后必须赋材质粒子才可见**）。**材质资产化**：Editor 创作路径下内置名自动落地为磁盘资产 `<对象名>_<材质名>.mat`（目录解析：`materialDir` 显式 → prefab 实例源 prefab 同目录 → 兜底 `Assets/`），特效引用资产不用实例；runtime（Player/Play Mode）才用材质实例。`shader`：创建材质时指定 shader（名或 `Assets/...` 路径），缺省 URP Particles/Simple Lit。Edit Mode 可撤销 |
| `particle.set` | All | `instanceId`(必填), `property`(必填), `value`, `materialDir`(renderer.material 时), `shader`(renderer.material/shader 时) | 按 typed 代码路径设置模块属性：`main.*`/`emission.*`/`shape.*`/`velocityOverLifetime.*`/`forceOverLifetime.*`/`colorOverLifetime.*`/`sizeOverLifetime.*`/`rotationOverLifetime.*`/`noise.*`/`trails.*`/`textureSheetAnimation.*`/`lights.*`/`collision.*`/`renderer.*`（含 `renderer.material`，Editor 下同样材质资产化；`renderer.shader` 换材质 shader，Editor 下持久化到材质资产）。MinMaxCurve：单数=常量，`[min,max]`=随机，`[[t,v],...]`=曲线；颜色：单色，`[c1,c2]`=双色渐变（colorOverLifetime 时间渐变/startColor 随机），`[[t,c],...]`=完整渐变；`emission.burst`：`[count,time]` 或 `[[c,t],...]` |
| `particle.simulate` | All | `instanceId`(必填), `time`(必填), `restart`(默认true), `withChildren`(默认true) | 确定性预览到 t 时刻（Edit Mode 可用，不实际播放） |
| `particle.play` | All (Play Mode) | `instanceId`, `restart`(默认true), `withChildren` | 播放（可强制重新开始） |
| `particle.pause` | All (Play Mode) | `instanceId`, `withChildren` | 暂停（粒子冻结） |
| `particle.stop` | All (Play Mode) | `instanceId`, `clear`(默认true), `withChildren` | 停止（可选清空粒子） |
| `particle.clear` | All (Play Mode) | `instanceId`, `withChildren` | 清空所有粒子但不停止系统 |
| `particle.emit` | All (Play Mode) | `instanceId`, `count`(默认1), `position`[3], `velocity`[3], `startLifetime`, `startSize`, `startColor` | 通过 EmitParams 一次性爆发（无需播放中）。粒子参数可选，缺省用系统默认 |

材质颜色/贴图用 `scene.set_material`（ParticleSystemRenderer 是 Renderer）；保存用 `scene.save_prefab`（assetPath 参数）。**素材创作流程**：先 `scene.instantiate_prefab`（PrefabUtility.InstantiatePrefab，保留 prefab 关联）把特效 prefab 放进场景 → `particle.set` 赋内置材质自动落在 prefab 同目录 → 或 `particle.create/set` 显式传 `materialDir`。配方参考：火焰/爆炸/火花/烟雾/魔法/雨雪/光点/传送门 → 见 AGENTS.md「Particle 特效创作」配方表。

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

### 工具类别与动态注册（ToolsHandler，4 工具）

每个工具的**描述都以 `[类别]` 开头**（如 `[Scene] 获取完整的场景层级树...`），
类别由工具名前缀自动派生（`scene.xxx` → `Scene`，`assetbundle.xxx` → `AssetBundle`），
方便人眼和 AI 快速扫描、分组。

工具按类别动态注册/注销，避免无关工具干扰调用方：

| 工具 | 参数 | 说明 |
|------|------|------|
| `tools.list_categories` | — | 列出所有类别：工具数 + 启用状态（含已禁用的类别） |
| `tools.enable` | `categories`[](必填) 或 `all`(bool) | 启用指定类别，立即重新注册并推送新工具列表 |
| `tools.disable` | `categories`[](必填) 或 `all`(bool) | 禁用指定类别，从注册表移除对应工具 |
| `tools.reset` | — | 恢复全部类别（完整工具列表还原） |

**示例**：本次任务只用场景操作，把无关的 AssetBundle/Shader/Recording 关掉：

```json
{"name": "tools.disable", "arguments": {"categories": ["AssetBundle", "Shader", "Recording"]}}
```

之后 `tools.list_categories` 会显示这三类 `"enabled": false`，它们也不会出现在
`tools/list` 结果里。任务结束后 `tools.reset` 一键还原。

**要点**：

- **默认全部启用**——条件编译只决定工具"是否存在"（装包即编译注册，如 NGUI 装上后 `ngui.*` 自动可用），
  `tools.enable` 无需 AI 主动调用；`tools.disable` 是纯 AI 侧裁剪机制，用于砍掉无关工具省 token
- 类别状态是 **static** 的（纯内存，无 EditorPrefs/PlayerPrefs 持久化）：跨 Play Mode 切换、router 重建保留；
  **脚本重编译 / Editor 重启（domain reload）后重置为全部启用**
- `tools.disable all` 会保留 `Tools` 类别本身（4 个控制工具），保证永远能恢复
- `mcp.list_tools` / `tools/list` 返回的就是当前启用的工具子集
- 新增工具类时无需手动登记类别——注册器按名称前缀自动归类

## Multi-Bridge 路由

多个 Unity Bridge 可同时连接。三层机制：

1. **默认路由**：`last-registration-wins` — 后连接的 bridge 覆盖同名工具
2. **显式路由**：`bridge.call` 指定 `target` = bridgeId，绕过默认路由，确定性调用
3. **断开 failover**：某 bridge 断开时，其路由的工具若被其他在线 bridge 注册，自动回退；否则清除

- Editor bridge（92+ tools）与 Android bridge（部分工具）共存
- **Bridge 断线**：该 bridge 的工具从路由表移除；有其他 bridge 注册同工具时自动回退
- 无 bridge 时待处理调用进入重试队列（30s 宽限期）
- 使用 `bridge.list` 查看所有已连接 bridge（ID、IP、工具列表）
- ⚠️ BridgeId 每次连接重新生成（GUID），`clientPort` 是随机客户端端口——**不要用 ip+port 作稳定标识**，精确调用一律用 bridgeId
- `tools/list`（含 MCP ListTools）返回的每个 bridge 工具 description 带 `[bridge: ip:port (id前8位)]` 前缀 =
  该工具**当前路由目标**（last-registration-wins 结果）。AI 可直接从工具列表得知调用会打到哪个 bridge

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

**类别自动派生**：工具描述会带上 `[类别] ` 前缀，类别默认取自工具名前缀
（`my_tool_name` → `MyTool`，`assetbundle.xxx` → `AssetBundle`）。如需自定义类别：

```csharp
[MCPTool("my_tool_name", "工具描述", Category = "CustomGroup")]
```

归类后即可用 `tools.enable` / `tools.disable` 按类别动态开关。

2. **自动注册** — `MessageRouter` 构造时扫描程序集，自动发现带 `[MCPTool]` 的方法。

### 规则

- 方法签名必须为 `public static string MethodName(string paramsJson)`
- 类需要 **public 无参构造函数**（static class 自动跳过）
- `[MCPToolClass]` 标记类可加速发现
- `Platform` 可选，控制哪些构建目标注册该工具
- `RequirePlayMode` 可选，为 `true` 时工具仅在 Play Mode 时注册
- `Category` 可选，覆盖自动派生的类别名（默认取工具名前缀）
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

### 1. Editor Eval 开关与安全说明

`editor.eval` 通过 Mono.CSharp 动态编译并在内存中执行任意 C# 代码 —— 等同于完全的 Unity/机器控制（读写任意文件、删除资产、网络访问、启动进程等）。这是工具集最强的"逃生舱":当某个场景操作没有专用工具覆盖时,AI 可用 eval 即时补救。

**默认 ON,以用户方便为先**。开发调试、快速原型、补救缺口工具时即时可用,不必先翻配置。若环境不可信（共享机器/公网暴露的服务器),关闭它。

**双重 gate（任一关闭即不可用）**:
- **Server 侧 `config.json` → `evalEnabled`**（默认 `true`）:`false` 时 `tools/list` 不暴露 `editor.eval` 给 agent,agent 看不到也就调不到。
- **Bridge 侧 `EditorPrefs SimpleMCPBridge_EvalEnabled`**（默认 `true`）:执行前再检查一次;MCPBridge Inspector 显示为 **"Editor Eval (global)"** toggle,关闭后调用返回错误。

**风险面**:任何能调用 `/rpc` 的 AI 都能执行任意代码。本工具**不做代码内容过滤**（任意代码无法穷举拦截,黑名单无意义）。安全靠网络层 gate —— `allowedIps` 白名单默认仅本机（`127.0.0.1`/`::1`）。云部署（`ip:0.0.0.0`）前务必扩白名单到可信 IP 段,或直接 `evalEnabled:false`。

**关闭方法**（任一即可）:
- `SimpleMcpServer/config.json` 设 `"evalEnabled": false`（重启 server,对所有 agent 隐藏）
- Unity Editor: MCPBridge Inspector 的 eval toggle（立即生效,单机）
- 代码:`EditorPrefs.SetBool("SimpleMCPBridge_EvalEnabled", false)`

详见 AGENTS.md「editor.eval 安全说明」。

### 2. 加密格式变更

加密载荷格式从 `{"encrypted":"<base64>"}` JSON 包装改为 `#ENC#<base64>` 前缀格式。**Server 端也必须使用 `#ENC#` 前缀**，两端需同步更新。

### 3. 代码质量加固

多线程安全、资源泄漏修复、性能优化等多项改进。

## 已知问题

### 1. MPEG4Writer "Stop() called but track is not started"（Android 录屏）

**根因**：InstantReplay 始终创建音频轨道，即使 `enableAudio=false`。音频轨道从未收到帧。
**影响**：无害。MP4 视频轨道完整，播放正常。

### 2. `BuildJsonObject` 字符串值必须预引号 + 控制字符转义

`BuildJsonObject(("key", "value"))` 生成 `"key":value`（裸词，无效 JSON）。字符串值必须通过 `EscapeString("value")` 包装: `("key", JsonHelper.EscapeString("value"))` → `"key":"value"`。数值和 bool 不需要包装。

**同类陷阱 —— 控制字符未转义**：`EscapeString` 原先只处理 `" \` `\n` `\r` `\t`，漏了 JSON 规范要求转义的 U+0000–U+001F 控制字符（及 `\b` `\f`）。工具返回含控制字符的字符串（如 `Vector3.ToString()` 的 `(0, 0, 0)`、二进制数据、带格式符的文本）会产出无效 JSON → 服务器日志 `Invalid JSON from bridge` → 客户端表现 30s 超时（像 handler 挂起,实际是响应被丢弃）。`EscapeString` 已补全 `\b` `\f` 及 `\uXXXX` 兜底；非字符串值（float/int 的 `ToString("G")`）不受影响。诊断：server.log 搜 `Invalid JSON`,用 `node -e "JSON.parse(...)"` 定位裸词位置。

### 3. 端口占用

`ws` 库的 WebSocketServer 在进程退出后有时端口保持绑定：

```powershell
Get-Process -Name "node" | Stop-Process -Force
```

### 4. 多窗口 Unity 多 bridge

开启多个 Unity 进程（如多个 Editor 窗口）时各自建立独立 WebSocket 连接，每个进程一个 bridgeId。

### 6. `RequirePlayMode` 注册时机

`MCPToolRegistry` 在 `BridgeClient` 构造时扫描注册工具。由于 `BridgeClient` 是单例，工具注册只发生一次。进出 Play Mode 时如果 bridge 未断开，工具列表不会动态更新。但在标准工作流中（domain reload → bridge 重连），每次进入/退出 Play Mode 都会重新注册。

### 7. MCP Server 需要显示 cmd 窗口

MCP Server（`SimpleMcpServer`）必须在一个**可见的 cmd 窗口**中运行，不能以无窗口/后台方式启动：

- 用 `start.bat` 启动（会打开 cmd 窗口并显示日志）
- 不要用 `Start-Process -NoNewWindow` 或后台服务方式启动
- 原因：服务器日志（连接、工具调用、AB 传输）实时输出到窗口，便于排查问题；且服务器进程需要保持前台运行

### 8. 多 Bridge 时工具路由到 Editor 而非 Android（设计决策，非缺陷）

当 Editor 和 Android 两个 bridge 同时连接，且 Editor 处于 Play Mode 时，`last-registration-wins` 规则会让 Editor 覆盖同名工具（如 `shader.hot_replace`、`assetbundle.hot_replace`）的路由。此时直接调用这些工具会路由到 **Editor** 而非 Android。

这是**有意的默认行为**（见「Multi-Bridge 路由」三层机制）：无指定目标时，服务器按注册顺序取最后一个。需要确定性目标时，用 `bridge.call` 显式指定：

```json
{
  "name": "bridge.call",
  "arguments": {
    "target": "<android_bridge_id>",
    "method": "assetbundle.hot_replace",
    "params": { "abUrl": "http://<server_ip>:45678/ab/<bundle>" }
  }
}
```

`bridge.list` 可查看各 bridge 的 ID 和 IP（Android 通常为 `10.0.x.x`）。

### 9. 同内容 AssetBundle 只能加载一次

Unity 的 AB 去重机制：**相同内容的 AssetBundle 只能被 `LoadFromMemory` 加载一次**。若 `shader.hot_replace` 已加载某 AB 且未卸载，后续 `assetbundle.hot_replace` 加载同内容 AB 会报：

```
The AssetBundle 'Memory' can't be loaded because another AssetBundle with the same files is already loaded.
```

**解决**：每次部署前先调用 `assetbundle.unload_all` 卸载所有已加载 bundle，或使用不同内容的 AB（不同文件名/内容）。

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [SimpleMCPBridge](https://github.com/redcool/SimpleMCPBridge_Unity) | 本仓库 — Unity 桥接包 |
| [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) | MCP Server — Node.js/TypeScript |
| [InstantReplay](https://github.com/CyberAgentGameEntertainment/InstantReplay) | CyberAgent InstantReplay — OS 原生硬编码录屏 |
