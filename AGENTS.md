# AGENTS.md — SimpleMCPBridge

## PowerUtilities 约束

**PowerUtilities 包是外部依赖，不是本仓库的一部分。**  
如果要修改 PowerUtilities 包的代码，必须先告知用户详情并取得同意后才能改。

## Architecture

Two separate repos that work together:

| Component | Repo | Tech |
|-----------|------|------|
| MCP Server | `SimpleMcpServer` (独立 clone) | Node.js 22+, TypeScript, ws |
| Unity Bridge | 本仓库 (`SimpleMCPBridge`) | C#, Unity 2022.3 |

Data flow: Agent (stdio) → MCP Server → WebSocket → Unity Bridge → Unity API.

## Key Commands

**Server (run from `H:\ai_works\SimpleMcpServer/`):**
- `start.bat` — build + start
- `start-quick.bat` — skip build, start (if dist/ is up to date)
- `start-test.bat` — E2E test (spawns server, waits for bridge, calls tools)
- `npm run dev` — watch mode via tsx (no build needed)
- `setup.bat` — first-time setup (node check + npm install + build)

**Unity Bridge:**
- Open via `Tools > SimpleMCPBridge`
- IP/Port fields, Connect button, GUID display, error panel
- `AutoStartBridge.cs` ([InitializeOnLoad]) auto-connects on domain reload

## Port Conflicts (Common)

The `ws` library's WebSocketServer sometimes leaves the port bound after the
process exits. Before restarting, kill stale processes:

```powershell
Get-Process -Name "node" | Stop-Process -Force
```

If Unity has multiple windows open, more than one bridge might connect —
server keeps only the most recent one.

## Config

- **Server:** `SimpleMcpServer/config.json` — `{ "ip": "127.0.0.1", "port": 45678 }`
- **Bridge:** `Assets/SimpleMCPBridge/bridge-config.json` — `{ "serverIp": "...", "serverPort": 45678 }`
- Both use the same format. Cloud deployment: server `ip: "0.0.0.0"`.

## Logs

- **Bridge debug:** `Logs/mcp_bridge_debug.log` (relative to Unity project root)
- **Server stderr:** `SimpleMcpServer/server.err`
- **Unity Editor log:** `$env:LOCALAPPDATA\Unity\Editor\Editor.log`

## 6 Tools

| Tool | What it does |
|------|-------------|
| `scene.get_hierarchy` | Scene tree (root→children, with components + positions) |
| `scene.get_objects` | Filtered list by nameContains |
| `scene.create_object` | New GameObject with optional position/rotation/scale/parentId |
| `scene.delete_object` | Destroy by instanceId |
| `scene.set_transform` | Set position/rotation/scale by instanceId |
| `scene.set_component_property` | Set field/property on a component (supports Vector3, Color, enum, etc.) |

Tools are discovered at runtime via `[MCPTool]` attribute on methods with
signature `(string paramsJson) -> string`. Register new tools in
`MessageRouter.cs` constructor: `_registry.Register(new MyHandler());`.

## MCP SDK v1.x Limitation

`registerTool()` throws after `server.connect()`. Don't use it — use
`server.setRequestHandler(ListToolsRequestSchema, ...)` and
`server.setRequestHandler(CallToolRequestSchema, ...)` with a manual
`registeredTools` array that gets updated when the bridge sends
`register_tools`. See `src/index.ts` for the pattern.

## Known Bug

**BridgeId is lost on retry reconnect.** In `MCPBridge.cs` line 179, the
retry loop sends `register_tools` without `bridgeId`:
```csharp
var registerMsg = $"{{\"type\":\"register_tools\",\"tools\":{toolsJson}}}";
```
Compare to the initial connection (line 91) which includes
`,\"bridgeId\":\"{BridgeId}\"`. Server shows `(unknown)` for bridge ID on
retry connections. Fix: add `bridgeId` to the retry path's register message.

## BridgeId per Window Open

`MCPBridgeWindow.OnEnable()` generates a new GUID. This GUID is set on the
bridge via `_bridge.BridgeId = _bridgeId`. Each window open → new ID. Both
the server and the window UI display it for multi-bridge tracking.

## WebSocket Impl (Unity Side)

- Zero external dependencies (raw `System.Net.Sockets` + `System.Security.Cryptography`)
- RFC 6455: masked frames client→server, unmasked server→client
- HTTP upgrade path must be `GET / HTTP/1.1` (not path-prefixed)

## Main-Thread Safety

Bridge receives messages on a background thread, queues them in a
`ConcurrentQueue<Action>`, and drains on the Unity main thread:
- **Edit Mode:** `EditorApplication.update` event
- **Play Mode:** `MonoBehaviour.Update()`
- `MCPBridgeWindow.OnEditorUpdate()` ticks `_bridge.DrainQueue()`

## Testing

```powershell
# Run from SimpleMcpServer\:
node tests\test-e2e.cjs
```

Test spawns the server, waits for bridge to connect via retry loop, calls
`tools/list`, then calls `scene.get_hierarchy`. E2E triggers a new server
instance — the bridge must reconnect to the new one.

## Directory Refs (from SimpleMcpServer)

| Path | Description |
|------|-------------|
| `src/index.ts` | Entrypoint: WS server + MCP handlers |
| `src/types.ts` | Shared type defs |
| `tests/test-e2e.cjs` | E2E test |
| `dist/` | Build output (gitignored) |
| `config.json` | Server ip/port |

## Directory Refs (from Assets/SimpleMCPBridge)

| Path | Description |
|------|-------------|
| `Runtime/MCPBridge.cs` | Bridge MonoBehaviour, connect/retry/disconnect, queue drain |
| `Runtime/WebSocketClient.cs` | Raw TCP WS client, frame read/write, HTTP upgrade |
| `Runtime/MessageRouter.cs` | Routes tool calls to handlers |
| `Runtime/MCPToolRegistry.cs` | Scans for [MCPTool] methods |
| `Runtime/Handlers/SceneHandler.cs` | The 6 tool implementations |
| `Editor/MCPBridgeWindow.cs` | Tools > SimpleMCPBridge window |
| `Editor/AutoStartBridge.cs` | Auto-connect on domain reload |
| `bridge-config.json` | Bridge IP/port |
