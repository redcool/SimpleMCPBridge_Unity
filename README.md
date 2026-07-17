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
│   │   ├── Handlers/       # 工具处理器（9 个 Handler，57 个工具）
│   │   ├── Tools/          # 工具辅助类
│   │   └── Models/
│   ├── Editor/
│   │   └── MCPBridgeWindow.cs       # Tools > SimpleMCPBridge 窗口
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
| 格式 | `{"encrypted":"<base64(IV+ciphertext)>"}` |
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

## 可用工具（共 57 个）

### 场景工具（SceneHandler，22 工具）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `scene.get_hierarchy` | All | — | 场景层级树（含 path、instanceId、组件名、位置） |
| `scene.get_objects` | All | `nameContains` | 按名称筛选对象列表 |
| `scene.get_objects_by_type` | All | `typeName`, `nameContains`, `layer`, `layerName`, `isIncludeInvisible` | 按组件类型查找对象 |
| `scene.get_objects_by_tag` | All | `tag`, `nameContains`, `layer`, `layerName` | 按标签查找对象 |
| `scene.get_objects_by_path` | All | `path`, `nameContains`, `layer`, `layerName` | 按 Transform 路径查找 |
| `scene.create_object` | All | `name`, `position`, `rotation`, `scale`, `parentId`/`parentPath` | 创建 GameObject |
| `scene.delete_object` | All | `instanceId`/`path` | 删除对象 |
| `scene.set_transform` | All | `instanceId`/`path`, `position`, `rotation`, `scale`, `space`(world/local) | 设置位置/旋转/缩放 |
| `scene.set_component_property` | All | `instanceId`/`path`, `componentType`, `propertyName`, `value` | 修改组件字段/属性 |
| `scene.get_components` | All | `instanceId`/`path` | 获取 GameObject 所有组件列表 |
| `scene.get_component_properties` | All | `instanceId`/`path`, `componentType` | 获取组件所有可序列化属性 |
| `scene.set_active` | All | `instanceId`/`path`, `active` | 启用/禁用 GameObject |
| `scene.duplicate_object` | All | `instanceId`/`path` | 复制 GameObject |
| `scene.rename` | All | `instanceId`/`path`, `name` | 重命名 |
| `scene.set_parent` | All | `instanceId`/`path`, `parentId`/`parentPath` | 设置父级（留空则解除到根） |
| `scene.add_component` | All | `instanceId`/`path`, `componentType` | 添加组件 |
| `scene.remove_component` | All | `instanceId`/`path`, `componentType` | 移除组件 |
| `scene.enter_play_mode` | Editor | — | 进入 Play Mode |
| `scene.exit_play_mode` | Editor | — | 退出 Play Mode |
| `scene.pause_play_mode` | Editor | `paused` | 暂停/继续 |
| `scene.get_play_mode` | Editor | — | 获取播放模式状态 |
| `scene.save_current` | Editor | `savePath` | 保存当前场景 |
| `scene.instantiate_prefab` | Editor | `assetPath`, `position` | 实例化预制体 |
| `scene.set_material` ⚠ | Editor | `instanceId`/`path`, `color`, `texturePath` | 修改运行时材质。资产级改材质建议直接改 .meta GUID |

### 编辑器工具（EditorHandler，7 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `editor.request_compile` | — | 触发 Unity 脚本重新编译（minimize→restore 确保可靠触发） |
| `editor.open_window` | `menuPath` | 按菜单路径打开窗口，如 `"Window/General/Console"` |
| `editor.window_focus` | `action` | 控制 Editor 窗口：`minimize`/`restore`/`focus`/`maximize`/`get_state` |
| `editor.eval` | `code` | **编译并执行 C# 代码**（Mono.CSharp in-memory，无 domain reload，变量跨调用保持） |
| `editor.get_console` | `count` | 获取最近 N 条控制台日志 |
| `editor.get_preferences` | `keys` | 读取 Editor/Project 设置 |
| `editor.get_project_tree` | `path`, `maxDepth` | 获取 Assets 目录树 |
| `editor.undo` | — | 撤销 |
| `editor.redo` | — | 重做 |

> **editor.eval** 预导入命名空间：`System`、`System.Linq`、`System.Collections.Generic`、`UnityEngine`、`UnityEditor`、`UnityEngine.UI`、`UnityEngine.EventSystems`。`UnityEngine.Object` 别名为 `UnityObject`。

### Scene View 工具（SceneViewHandler，2 工具，仅 Editor）

| 工具 | 参数 | 说明 |
|------|------|------|
| `scene_view.get_camera` | — | 获取 SceneView 相机状态（位置/旋转/FOV/pivot） |
| `scene_view.set_camera` | `position`, `rotation`, `pivot`, `size`, `isOrthographic` | 设置 SceneView 相机 |

### 录制工具（RecordingHandler，4 工具，需 InstantReplay）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `recording.start` | Android/iOS/Standalone | `width`, `height`, `fps`, `enableAudio`, `quality` | 开始录屏。默认 1280×720@30fps |
| `recording.stop` | Android/iOS/Standalone | — | 停止录制，异步编码 MP4。返回 `status:"encoding"` |
| `recording.status` | Android/iOS/Standalone | — | 查询状态：`idle`/`recording`/`encoding`/`completed`/`error` |
| `recording.reset` | Android/iOS/Standalone | — | 强制重置录制系统（编码卡死时恢复） |

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

### 输入模拟工具（InputHandler，7 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.click_screen` | `x`, `y` (normalized 0-1) | EventSystem 点击（Raycast → PointerDown → Up → Click） |
| `input.mouse_click` | `x`, `y` (normalized), `button`(0/1/2) | Input System 鼠标点击 |
| `input.mouse_move` | `dx`, `dy` (pixel delta) | Input System 鼠标移动（视角旋转/瞄准） |
| `input.key_press` | `key`, `action`(tap/hold/release) | Input System 键盘。`action=release` + `key=*` = 释放全部 |
| `input.touch` | `action`(tap/start/move/end), `x`, `y`, `fingerId` | Input System 触屏 |
| `input.swipe` | `startX/Y`, `endX/Y`, `duration`, `steps`, `fingerId` | 平滑滑动手势（异步多帧） |
| `input.gamepad` | `action`(button/axis/set/reset/state) | 虚拟手柄控制 |

**gamepad 按键名**：`south/a`、`east/b`、`north/x`、`west/y`、`leftShoulder/lb`、`rightShoulder/rb`、`leftStick/rightStick`、`start`、`select/back`、`dpadUp/down/left/right`

**gamepad 轴名**：`leftStickX/Y`、`rightStickX/Y`、`leftTrigger`、`rightTrigger`

### UI 分析工具（2 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `ui.get_texts` | — | 读取屏幕所有 UI 文本（Text + TMP，无 OCR，零 Token 成本） |
| `ui.find` | `type`, `contains`, `interactable` | 查找可交互 UI 元素，返回归一化屏幕坐标和中心点 |

### 统一输入工具（1 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `input.action` | `keys[]`, `mouse{}`, `axes{}` | 单次调用组合输入（键盘+鼠标+滚轮+轴） |

### 游戏状态 / 等待工具（3 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `game.get_state` | `includeUI`, `playerPath` | 综合感知快照：场景/时间/UI/玩家位置 |
| `game.wait` | `type`(seconds/sceneLoaded/uiAppears/uiDisappears/property), `value`, `timeout` | 异步等待。返回 `id`，用 `game.wait_check` 轮询 |
| `game.wait_check` | `id` | 轮询等待状态 |

### 资源工具（AssetHandler，3 工具）

| 工具 | 平台 | 参数 | 说明 |
|------|------|------|------|
| `asset.refresh` | All | — | 刷新资产数据库（新脚本导入会触发 domain reload） |
| `asset.find_assets` | Editor | `nameContains`, `typeFilter` | 按名称/类型搜索资源 |
| `asset.find_references` | All | `assetPath` | 查找引用指定资源的所有资源（GUID 扫描） |

### 物理 / 相机工具（2 工具）

| 工具 | 参数 | 说明 |
|------|------|------|
| `physics.raycast` | `origin[3]`, `direction[3]`, `maxDistance`, `layerMask` | 射线检测 |
| `camera.screenshot` | `savePath`, `cameraName`, `width`, `height` | 截取主相机画面为 PNG |

### Server 内置工具

由 SimpleMcpServer 直接提供，不在 Bridge 注册：

| 工具 | 参数 | 说明 |
|------|------|------|
| `bridge.list` | — | 列出所有已连接 Bridge（ID/IP/工具数） |
| `bridge.call` | `target`(bridgeId), `method`, `params` | 定向调用指定 Bridge 上的工具 |

## Multi-Bridge 路由

多个 Unity Bridge 可同时连接：

- **路由规则**：`last-registration-wins` — 后连接的 bridge 覆盖同名工具
- Editor bridge（57 tools）与 Android bridge（部分工具）共存
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
             Platform = MCPToolPlatforms.Android | MCPToolPlatforms.Editor)]
    public static string MyTool(string paramsJson)
    {
        var args = ParseJsonObject(paramsJson);
        var name = GetString(args, "name", "default");
        return "{\"success\":true}";
    }
}
```

2. **自动注册** — `MessageRouter` 构造时扫描程序集，自动发现带 `[MCPTool]` 的方法。

### 规则

- 方法签名必须为 `public static string MethodName(string paramsJson)`
- 类需要 **public 无参构造函数**（static class 自动跳过）
- `[MCPToolClass]` 标记类可加速发现
- `Platform` 可选，控制哪些构建目标注册该工具
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

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [SimpleMCPBridge](https://github.com/redcool/SimpleMCPBridge_Unity) | 本仓库 — Unity 桥接包 |
| [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) | MCP Server — Node.js/TypeScript |
| [InstantReplay](https://github.com/CyberAgentGameEntertainment/InstantReplay) | CyberAgent InstantReplay — OS 原生硬编码录屏 |
