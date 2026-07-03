# SimpleMCPBridge — Unity 侧桥接包

让 AI 代理通过 MCP 协议在 Unity Editor 中操作场景。  
**必须配合 [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) 使用。**

## 架构

```
AI Agent (Claude Code / Cursor)
    │  MCP (stdio)
    ▼
SimpleMcpServer (Node.js/TypeScript)   ← 另一个仓库，需单独 clone
    │  WebSocket
    ▼
SimpleMCPBridge (C#)                   ← 本仓库，clone 到 Unity Assets/ 下
    │
    ▼
Unity Editor / Runtime
```

## 安装

```bash
# 在 Unity 项目的 Assets/ 目录下克隆
cd YourUnityProject/Assets/
git clone https://github.com/redcool/SimpleMCPBridge_Unity.git
```

安装后目录结构：
```
Assets/
├── SimpleMCPBridge/        ← 本仓库
│   ├── Runtime/             # 运行时桥接代码
│   │   ├── MCPBridge.cs     # 核心桥接类（plain C#，非 MonoBehaviour）
│   │   ├── WebSocketClient.cs
│   │   ├── Handlers/        # 工具处理器
│   │   │   ├── HandlerUtils.cs   # 静态工具类（JSON 解析、错误响应）
│   │   │   ├── SceneHandler.cs   # 场景工具（23 个 scene.* 工具）
│   │   │   └── AssetHandler.cs   # 资源工具（asset.find_assets 等）
│   │   └── Models/
│   ├── Editor/              # Editor Window + 自动启动
│   ├── bridge-config.json   # IP/Port 配置
│   └── AGENTS.md            # AI 开发指引
├── ...
```

> 注意：SimpleMCPBridge 是自己独立的 git 仓库，不是 Unity 项目的子模块。

## 使用方法

1. 用 Unity 打开项目
2. 菜单栏 → **PowerUtilities → SimpleMCPBridge**
3. 填写 Server IP/Port（默认 `127.0.0.1:45678`）
4. 点击 **Connect to Server**
5. 在另一侧启动 `SimpleMcpServer`

Bridge 生命周期独立于窗口：关闭窗口后 bridge 继续运行，进出 Play Mode 自动重连。

## 前置条件

- **Unity 2022.3+**（URP）
- **[SimpleMcpServer](https://github.com/redcool/SimpleMCPServer)** — 需先 clone 并启动

## 配置

编辑 `bridge-config.json`：

```json
{
    "serverIp": "127.0.0.1",
    "serverPort": 45678
}
```

## 对象寻址

所有场景工具同时支持两种方式定位 GameObject：

| 参数 | 说明 |
|------|------|
| `instanceId` | Unity 实例 ID，精确唯一，但 domain reload 后失效 |
| `path` | Transform 路径（如 `"Canvas/Panel/Button"`），跨 domain reload 有效，人类可读 |

解析优先级：`instanceId` > `path`。两个都传时先试 instanceId，找不到再 fallback 路径。

`get_hierarchy` 和 `get_objects` 的返回值同时包含 `instanceId` 和 `path`，AI agent 可自由选择。

## 可用工具（共 23 个）

### 场景工具（SceneHandler）

| 工具 | 说明 |
|------|------|
| `scene.get_hierarchy` | 获取场景层级树（含 path、instanceId、组件名、位置） |
| `scene.get_objects` | 查找对象，可选按 `nameContains` 过滤，返回 path + instanceId |
| `scene.create_object` | 创建 GameObject，支持 name / parentPath / position / rotation / scale |
| `scene.delete_object` | 按 instanceId 或 path 删除对象 |
| `scene.set_transform` | 按 instanceId 或 path 设置 position / rotation / scale |
| `scene.set_component_property` | 修改组件字段/属性，支持 Vector3、Color、Enum 等类型 |
| `scene.get_components` | 按 instanceId 或 path 获取 GameObject 所有组件列表 |
| `scene.get_component_properties` | 获取组件所有可序列化属性名和当前值 |
| `scene.set_active` | 按 instanceId 或 path 启用/禁用 GameObject |
| `scene.duplicate_object` | 按 instanceId 或 path 复制 GameObject |
| `scene.rename` | 按 instanceId 或 path 重命名 GameObject |
| `scene.set_parent` | 按 instanceId/path 设置父级，`parentPath`/`parentId` 指定父对象（留空则解除到根） |
| `scene.add_component` | 按类型名添加组件（如 Rigidbody） |
| `scene.instantiate_prefab` | 按 assetPath 实例化预制体到场景，支持 transform 和 parent（仅 Editor） |
| `scene.set_material` ⚠ | 修改运行时材质颜色/纹理。资产级材质修改建议直接改 `.meta` GUID |
| `scene.enter_play_mode` | 进入播放模式（仅 Editor） |
| `scene.exit_play_mode` | 退出播放模式（仅 Editor） |
| `scene.pause_play_mode` | 暂停/继续播放模式 — 传 `paused: true/false`（仅 Editor） |
| `scene.get_play_mode` | 获取当前播放模式状态 — 返回 isPlaying/isPaused/mode（仅 Editor） |

### 资源工具（AssetHandler，仅 Editor）

| 工具 | 说明 |
|------|------|
| `asset.find_assets` | 按名称和/或类型搜索项目 Assets。参数：`nameContains`（可选）、`typeFilter`（可选，如 `"Prefab"`、`"Material"`）。返回 `{path, name, type, guid}` 列表 |
| `asset.find_references` | 查找引用了指定资源的所有资源（反向依赖）。参数：`assetPath`（必填）。扫描文件内容中的 GUID，返回引用者列表。可靠但较慢 |

### 编辑器工具

| 工具 | 说明 |
|------|------|
| `editor.request_compile` | 触发 Unity 脚本重新编译（外部修改 C# 后用）（仅 Editor） |
| `editor.open_window` | 按菜单路径打开 Unity Editor 窗口，如 `"Window/General/Console"`（仅 Editor） |

## 自定义工具

1. 创建一个新类（例如 `MyTools.cs`）：

```csharp
using SimpleMCPBridge.Runtime;
using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;

public class MyTools
{
    [MCPTool("my_tool_name", "工具描述，AI 能看到")]
    public string MyTool(string paramsJson)
    {
        // 用 HandlerUtils.ParseJsonObject 解析参数
        var args = ParseJsonObject(paramsJson);
        var name = GetString(args, "name", "default");
        // 返回 JSON 字符串
        return "{\"success\":true}";
    }
}
```

2. **注册是自动的** — `MessageRouter` 构造时扫描所有程序集，
   自动发现带 `[MCPTool]` 方法的类并注册。
   不需要手动在 `MessageRouter.cs` 里加代码。
   只要类有无参构造函数且方法签名正确，就会被自动注册。

### 规则

- 方法签名必须为 `(string paramsJson) -> string`
- 用 `[MCPTool("tool.name", "description")]` 标记
- 描述用中文或英文均可，AI 会读取
- 类需要有 **public 无参构造函数**，否则跳过
- abstract / interface / static 类自动跳过
- 修改完 C# 代码后需要等待 Unity 编译完成
- 重启 Bridge 后新工具自动注册到 Server（或编译后自动注册）

### HandlerUtils 静态工具类

所有 handler 共用的 JSON 解析和响应构建方法，在 `HandlerUtils.cs` 中：

| 方法 | 说明 |
|------|------|
| `ParseJsonObject(json)` | 解析 JSON 字符串为 `Dictionary<string, object>` |
| `GetString(dict, key, default)` | 从字典取字符串，带默认值 |
| `GetRequiredString(dict, key)` | 取必填字符串，缺失抛异常 |
| `GetRequiredInt(dict, key)` | 取必填 int |
| `GetOptionalInt(dict, key)` | 取可选 int，返回 `int?` |
| `GetRequiredBool(dict, key)` | 取必填 bool |
| `GetOptionalFloatArray(dict, key)` | 取可选 `float[]`（如 position） |
| `ErrorJson(message)` | 构建错误响应 JSON |

用法：`using static SimpleMCPBridge.Runtime.Handlers.HandlerUtils;`

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [SimpleMCPBridge](https://github.com/redcool/SimpleMCPBridge_Unity) | 本仓库 — Unity 桥接包 |
| [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) | MCP Server — Node.js/TypeScript，处理 MCP 协议并转发请求到 Unity |

两个仓库都需要 clone。分开管理避免耦合。
