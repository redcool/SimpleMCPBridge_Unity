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

## Tools (54)

| Tool | What it does | Platform |
|------|-------------|----------|
| `scene.get_hierarchy` | Scene tree (root→children, with components + positions) | All |
| `scene.get_objects` | Filtered list by nameContains | All |
| `scene.get_objects_by_type` | Objects with a component type | All |
| `scene.get_objects_by_tag` | Objects with a tag | All |
| `scene.get_objects_by_path` | Object at Transform path | All |
| `scene.create_object` | New GameObject with options | All |
| `scene.delete_object` | Destroy by instanceId or path | All |
| `scene.duplicate_object` | Duplicate a GameObject | All |
| `scene.rename` | Rename a GameObject | All |
| `scene.set_active` | Enable/disable a GameObject | All |
| `scene.set_transform` | Set position/rotation/scale | All |
| `scene.set_parent` | Set parent (omit parentId or 0 to unparent to root) | All |
| `scene.set_component_property` | Set field/property on a component | All |
| `scene.set_material` ⚠ | Set material color/texture on Renderer | All |
| `scene.get_components` | List all components on a GameObject | All |
| `scene.get_component_properties` | Get all serializable properties + current values | All |
| `scene.add_component` | Add component by type name | All |
| `scene.remove_component` | Remove a component from a GameObject | All |
| `scene.instantiate_prefab` | Instantiate a prefab from project Assets | Editor |
| `scene.save_current` | Save current scene | Editor |
| `scene.enter_play_mode` | Enter Play Mode | Editor |
| `scene.exit_play_mode` | Exit Play Mode | Editor |
| `scene.pause_play_mode` | Pause/resume Play Mode | Editor |
| `scene.get_play_mode` | Get current play mode state | Editor |
| `physics.raycast` | Cast a ray and return first hit info | All |
| `camera.screenshot` | Capture main camera view and save as PNG | Editor |
| `asset.refresh` | Refresh Unity asset database | All |
| `asset.find_assets` | Search Assets/ by name and/or type (AssetDatabase.FindAssets) | Editor |
| `asset.find_references` | Find all assets referencing a given asset | All |
| `scene_view.get_camera` | Get SceneView camera state (pos/rot/FOV/pivot) | Editor |
| `scene_view.set_camera` | Set SceneView camera (position/rotation/size/ortho) | Editor |
| `editor.request_compile` | Trigger Unity script recompilation | Editor |
| `editor.open_window` | Open a Unity Editor window by menu path | Editor |
| `editor.window_focus` | Minimize/restore/focus Unity Editor window | Editor |
| `editor.eval` | **Compile & execute C# code in-memory (instant)** | Editor |
| `editor.get_console` | Get recent Editor console log entries | Editor |
| `editor.undo` | Undo last operation | Editor |
| `editor.redo` | Redo last undone operation | Editor |
| `editor.get_preferences` | Read Editor/Project settings | Editor |
| `editor.get_project_tree` | Get Assets directory tree (folders + files + sizes) | Editor |
| `input.click_screen` | Simulate click at normalized screen position (EventSystem) | All |
| `input.mouse_click` | Simulate mouse click at normalized screen position (Input System) | All |
| `input.mouse_move` | Move mouse by pixel delta (camera look/aim) (Input System) | All |
| `input.key_press` | Simulate keyboard key — tap/hold/release (Input System) | All |
| `input.action` | Unified input: keys + mouse + axes + scroll in one call | All |
| `ui.get_texts` | Read on-screen UI text from memory (no OCR) — Text + TMP | All |
| `ui.find` | Find interactive UI elements with screen positions + state | All |
| `game.get_state` | Composite scene/time/UI/player perception snapshot | All |
| `game.wait` | Async wait: seconds, scene load, UI appear/disappear, component property | All |
| `game.wait_check` | Poll game.wait completion status | All |
| `recording.start` | Start recording via InstantReplay (Android only, Play Mode) | Android |
| `recording.stop` | Stop recording and finalize MP4 (async, poll status) | Android |
| `recording.status` | Get current recording/export state | Android |
| `recording.reset` | Force-reset recording system (recover from stuck state) | Android |

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

## Compilation Trigger

Single call to trigger Unity script recompilation:

```
editor.request_compile
```

That's it. Internally it: minimize → wait 300ms → restore → force refresh +
request compilation. No need for manual `window_focus` calls.

Typical timing: DLL rebuilt in ~3s, bridge reconnected in ~6s after the call.

If `SimpleMCPBridge.dll` was deleted and not recreated, Unity has a
compilation error — check `editor.get_console` for details.

## Directory Refs (from Assets/SimpleMCPBridge)

| Path | Description |
|------|-------------|
| `Runtime/MCPBridge.cs` | Bridge MonoBehaviour, connect/retry/disconnect, queue drain |
| `Runtime/WebSocketClient.cs` | Raw TCP WS client, frame read/write, HTTP upgrade |
| `Runtime/MessageRouter.cs` | Routes tool calls to handlers |
| `Runtime/MCPToolRegistry.cs` | Scans for [MCPTool] methods |
| `Runtime/Handlers/GameHandler.cs` | High-level game tools: ui.*, input.action, game.* |
| `Runtime/Tools/UIAnalysisTools.cs` | Canvas UI scanning (Text + TMP + interactive elements) |
| `Runtime/Tools/InputActionTools.cs` | Virtual Gamepad + combined input (keys/mouse/axes) |
| `Runtime/Handlers/SceneHandler.cs` | Scene inspection + manipulation tools |
| `Runtime/Handlers/RecordingHandler.cs` | Gameplay recording tools (CyberAgent InstantReplay) |
| `Runtime/Handlers/EditorHandler.cs` | Editor window control + eval + console + project tree + prefs |
| `Runtime/Handlers/SceneViewHandler.cs` | SceneView camera control |
| `Runtime/Handlers/PhysicsHandler.cs` | Physics raycast |
| `Runtime/Handlers/CameraHandler.cs` | Main camera screenshot |
| `Runtime/MCPToolAttribute.cs` | MCPTool + MCPToolClass attrs + MCPToolPlatforms enum |
| `Runtime/MCPToolRegistry.cs` | Auto-discovery + registration + platform filter |
| `Editor/MCPBridgeWindow.cs` | Tools > SimpleMCPBridge window |
| `Editor/AutoStartBridge.cs` | Auto-connect on domain reload |
| `bridge-config.json` | Bridge IP/port |
