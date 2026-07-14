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

## Tools (26)

| Tool | What it does |
|------|-------------|
| `scene.get_hierarchy` | Scene tree (root→children, with components + positions) |
| `scene.get_objects` | Filtered list by nameContains |
| `scene.get_objects_by_type` | Objects with a component type (+ optional nameContains/layer/layerName/isIncludeInvisible) |
| `scene.get_objects_by_tag` | Objects with a tag (+ optional nameContains/layer/layerName) |
| `scene.get_objects_by_path` | Object at Transform path (+ optional nameContains/layer/layerName) |
| `scene.create_object` | New GameObject with optional position/rotation/scale/parentId |
| `scene.delete_object` | Destroy by instanceId |
| `scene.set_transform` | Set position/rotation/scale by instanceId |
| `scene.set_component_property` | Set field/property on a component (supports Vector3, Color, enum, etc.) |
| `scene.get_components` | List all components on a GameObject (type name, fullType, enabled) |
| `scene.get_component_properties` | Get all serializable properties + current values of a component |
| `scene.set_active` | Enable/disable a GameObject |
| `scene.duplicate_object` | Duplicate a GameObject |
| `scene.rename` | Rename a GameObject |
| `scene.set_parent` | Set parent (omit parentId or 0 to unparent to root) |
| `scene.add_component` | Add component by type name (e.g. Rigidbody) |
| `scene.instantiate_prefab` | Instantiate a prefab from project Assets by asset path (Editor only) |
| `scene.find_assets` 🚫 | **DO NOT USE.** Use VS Code global search `*.meta` instead — much faster |
| `scene.set_material` ⚠ | Set material color/texture on Renderer. For asset-level material edits, prefer editing `.meta` GUIDs via filesystem |
| `scene.enter_play_mode` | Enter Play Mode (Editor only) |
| `scene.exit_play_mode` | Exit Play Mode (Editor only) |
| `recording.start` | Start recording via InstantReplay (OS-native encoding, no external binaries) (Play Mode only). Params: width/height/fps/enableAudio/quality |
| `recording.stop` | Stop recording and finalize MP4 (async, poll status for completion via recording.status) |
| `recording.status` | Get current recording/export state — returns isRecording/elapsedSeconds/completed filePath |
| `scene.pause_play_mode` | Pause/resume Play Mode — `paused: true/false` (Editor only) |
| `editor.request_compile` | Trigger Unity script recompilation (after editing C# via filesystem) (Editor only) |

Tools are auto-discovered via `AutoRegisterAll()` — just create a class with
`[MCPTool]` methods and it's picked up automatically.

## Protocol: Tool Registration (request_tools)

Registration is server-driven, not bridge-push:

```
Server → Bridge: {"type":"request_tools"}
Bridge → Server: {"type":"register_tools","tools":[...],"bridgeId":"..."}
```

- Server sends `request_tools` right after WebSocket handshake
- Bridge responds with `register_tools` in `HandleMessage()`
- If no response within 8s, server re-requests (up to 3 attempts)
- Both initial connect and retry reconnect use the same path
- The `handleMessage` method in `MCPBridge.cs` checks for `request_tools`
  before routing to JSON-RPC dispatch

This avoids the race condition where bridge connects but tools aren't registered.

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
| `Runtime/Handlers/SceneHandler.cs` | Scene inspection + manipulation tools |
| `Runtime/Handlers/RecordingHandler.cs` | Gameplay recording tools (CyberAgent InstantReplay) |
| `Editor/MCPBridgeWindow.cs` | Tools > SimpleMCPBridge window |
| `Editor/AutoStartBridge.cs` | Auto-connect on domain reload |
| `bridge-config.json` | Bridge IP/port |
