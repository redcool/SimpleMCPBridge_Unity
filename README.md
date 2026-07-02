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
├── SimpleMCPBridge/      ← 本仓库
│   ├── Runtime/           # 运行时桥接代码
│   ├── Editor/            # Editor Window + 自动启动
│   ├── bridge-config.json # IP/Port 配置
│   └── AGENTS.md          # AI 开发指引
├── ...
```

> 注意：SimpleMCPBridge 是自己独立的 git 仓库，不是 Unity 项目的子模块。

## 使用方法

1. 用 Unity 打开项目
2. 菜单栏 → **Tools → SimpleMCPBridge**
3. 填写 Server IP/Port（默认 `127.0.0.1:45678`）
4. 点击 **Connect to Server**
5. 在另一侧启动 `SimpleMcpServer`

或让 Unity 启动时自动连接（已内置 `AutoStartBridge.cs` 通过 `bridge-config.json` 自动连接）。

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

## 相关仓库

| 仓库 | 说明 |
|------|------|
| [SimpleMCPBridge](https://github.com/redcool/SimpleMCPBridge_Unity) | 本仓库 — Unity 桥接包 |
| [SimpleMcpServer](https://github.com/redcool/SimpleMCPServer) | MCP Server — Node.js/TypeScript，处理 MCP 协议并转发请求到 Unity |

两个仓库都需要 clone。分开管理避免耦合。
