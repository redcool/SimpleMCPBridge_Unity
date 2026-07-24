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
- Auto-connects on domain reload via [InitializeOnLoad]

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

## Tools (88)

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
| `physics.box_cast` | Sweep a box along direction, return first hit | All |
| `physics.sphere_cast` | Sweep a sphere along direction, return first hit | All |
| `physics.overlap_sphere` | All colliders within a sphere (name, instanceId, path, position, tag, layer) | All |
| `physics.overlap_box` | All colliders within a box (optional rotation) | All |
| `camera.screenshot` | Capture main camera view and save as PNG (root: Editor→Project/VideoRecord, Runtime→tempCache/VideoRecord) | All |
| `audio.get_sources` | List all playing AudioSources (clipName, volume, isPlaying, time, loop, spatialBlend, position, distanceFromListener, path, instanceId) | All |
| `nav.query_path` | Find path between two points on NavMesh (reachable, status, waypoints, distance) | All |
| `nav.sample_position` | Snap world position to nearest NavMesh point | All |
| `nav.has_navmesh` | Check if NavMesh exists (hasNavMesh, vertexCount, triangleCount) | All |
| `nav.move_to` | Set NavMeshAgent destination, auto pathfinding movement | All |
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
| `input.click_screen` | Simulate click at normalized screen position (EventSystem, **no Input System needed**) | All |
| `input.mouse_click` | Simulate mouse click at normalized screen position (Input System) | All |
| `input.mouse_move` | Move mouse by pixel delta (camera look/aim) (Input System) | All |
| `input.key_press` | Simulate keyboard key — tap/hold/release (Input System) | All |
| `input.touch` | Touch simulation: tap, start, move, end (virtual Touchscreen, Input System) | All |
| `input.swipe` | Async smooth swipe/drag gesture from one point to another over time | All |
| `input.gamepad` | Gamepad control: button tap/press/release, axis, batch set, reset, state query | All |
| `input.get_state` | Query all current input states (tracked keys/mouse position/gamepad) | All |
| `input.action` | Unified input: keys + mouse + axes + scroll in one call | All |
| `ui.get_texts` | Read on-screen UI text from memory (no OCR) — Text + TMP | All |
| `ui.find` | Find interactive UI elements with screen positions + state | All |
| `ui.set_input_field_text` | Set InputField/TMP_InputField text directly | All |
| `ui.set_toggle` | Set Toggle on/off | All |
| `ui.set_slider` | Set Slider value (normalized 0-1 maps to minValue-maxValue) | All |
| `ui.select_dropdown_option` | Select Dropdown option by index or text | All |
| `ui.drag` | Simulate drag from one UI element to another via ExecuteEvents | All |
| `ui.get_tooltip` | Fire PointerEnter on target, scan visible text for tooltip | All |
| `game.get_state` | Composite scene/time/UI/player/camera perception snapshot (includes camera position, forward, fov, isOrthographic, nearClipPlane, farClipPlane) | All |
| `game.get_animator_state` | Get Animator current state (stateHash/normalizedTime/parameters) | All |
| `game.get_entities` | Batch get entities with AI/Health/CharacterController and their key states | All |
| `game.get_player` | One-step get player full state (position/rotation/velocity/animation/custom component properties) | All |
| `game.get_time_scale` | Get current Time.timeScale and fixedDeltaTime | All |
| `game.get_spatial` | Nearby 3D objects within radius from origin/player (name, pos, distance, direction, components) | All |
| `game.watch` | Register property signals for change monitoring (baseline at registration) | All |
| `game.get_delta` | Poll watched signals — returns only changed values since last call | All |
| `game.do_sequence` | Execute predefined action sequence (key/mouse/gamepad/click/wait) on Unity side | All |
| `game.sequence_status` | Poll sequence execution status | All |
| `game.set_time_scale` | Set Time.timeScale (0=pause, 1=normal, 2=2x speed) | All |
| `game.wait` | Async wait: seconds, scene load, UI appear/disappear, component property | All |
| `game.wait_check` | Poll game.wait completion status | All |
| `game.batch` | Execute multiple tool calls in one Unity frame (max 50, reduces N+1 round trips to 1) | All |
| `recording.start` | Start recording via InstantReplay (Android only, Play Mode) | Android |
| `recording.stop` | Stop recording and finalize MP4 (async, poll status) | Android |
| `recording.status` | Get current recording/export state | Android |
| `recording.reset` | Force-reset recording system (recover from stuck state) | Android |
| `shader.hot_replace` | Runtime hot-swap a Shader from an AB (WebClient download), global or per-path with instance materials | PlayMode |
| `shader.hot_replace_status` | Poll shader.hot_replace progress | PlayMode |
| `assetbundle.build_bundle` | Build an AssetBundle from project assets (Editor only, uses BuildPipeline) | Editor |
| `assetbundle.hot_replace` | Download AB and auto-deploy assets by type (Shader/Material/Texture/AudioClip/Mesh/ScriptableObject/Prefab). Async with polling. Supports saveBackup, dryRun, rollback | PlayMode |
| `assetbundle.hot_replace_status` | Poll hot_replace progress (per-type counts, instanceIds, errors) | PlayMode |
| `assetbundle.rollback` | Rollback a previous hot_replace (requires saveBackup:true) | PlayMode |
| `assetbundle.unload_all` | Unload ALL deployed AssetBundles (breaks references) | PlayMode |

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

## BridgeId per Connection

The bridge ID is generated in the `MCPBridge.BridgeId` property. Each
connection gets a unique ID. Both the server and the window UI display it
for multi-bridge tracking.

## WebSocket Impl (Unity Side)

- Zero external dependencies (raw `System.Net.Sockets` + `System.Security.Cryptography`)
- RFC 6455: masked frames client→server, unmasked server→client
- HTTP upgrade path must be `GET / HTTP/1.1` (not path-prefixed)

## Main-Thread Safety

Bridge receives messages on a background thread, queues them in a
`ConcurrentQueue<Action>`, and drains on the Unity main thread:
- **Edit Mode:** `EditorApplication.update` event
- **Play Mode:** `MonoBehaviour.Update()`
- `MCPBridgeEditor.OnEditorUpdate()` ticks `_bridge.DrainQueue()`

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
| `Runtime/Handlers/PhysicsHandler.cs` | Physics raycast + box/sphere cast + overlap queries |
| `Runtime/Handlers/CameraHandler.cs` | Main camera screenshot |
| `Runtime/Handlers/AudioHandler.cs` | Audio source inspection |
| `Runtime/Handlers/NavHandler.cs` | NavMesh pathfinding + sampling |
| `Runtime/Handlers/BatchHandler.cs` | Batch dispatch (game.batch) |
| `Runtime/MCPToolAttribute.cs` | MCPTool + MCPToolClass attrs + MCPToolPlatforms enum |
| `Runtime/MCPToolRegistry.cs` | Auto-discovery + registration + platform filter |
| `Editor/MCPBridgeEditor.cs` | Custom Editor for MCPBridge Inspector |
| `Runtime/Handlers/AssetBundleHotReplaceHandler.cs` | General AB hot-deploy (+ rollback) |
| `Runtime/Handlers/ShaderHotReplaceHandler.cs` | Shader-only hot-swap |
| `Runtime/Handlers/AssetHandler.cs` | Asset tools (find, refresh, build_bundle) |
| `Plugins/` | NuGet DLL 依赖（InstantReplay/UniEnc 需要，已含在仓库内）|
| `bridge-config.json` | Bridge IP/port |

## AssetBundle Lifecycle

The hot-replace system manages AssetBundles through a centralized lifecycle:

- `_loadedBundles` is a `Dictionary<string, AssetBundle>` keyed by `abUrl`
- `LoadBundle` unloads any old bundle with the same URL before loading a new one
- If loading fails (possible duplicate content), `UnloadAllLoadedBundles` is called as fallback
- Hot-replace tools require Play Mode — use `RequirePlayMode = true` on the attribute

## MCPToolAttribute — RequirePlayMode

The `MCPToolAttribute` supports a `RequirePlayMode` property:

```csharp
[MCPTool("assetbundle.hot_replace", "...", RequirePlayMode = true)]
```

When `RequirePlayMode = true`, the tool is only registered when the Unity application is playing (Play Mode in Editor, or any built player). This prevents tools that modify scene objects from being called in Edit Mode.

## Input Tool Parameter Reference

### `input.key_press` — 键盘模拟 (Input System)

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `key` | string | 是* | 键名: `w`, `space`, `enter`, `upArrow`, `leftShift`, `f1` 等 |
| `action` | string | 否 (默认 `tap`) | `tap` = 按下立即释放; `hold` = 持续按住; `release` = 释放 |

- `action="release"` + `key` 省略或 `"*"` → 释放所有按键
- **WASD 连续移动**: `hold` 按住 → 等待 → `release` 释放（`tap` 太快，PlayerMove 读不到）
- 底层: `InputSystem.QueueStateEvent(Keyboard.current, ...)` — 不调用 `InputSystem.Update()`
- 事件在 Unity 原生 Input System update 周期中被处理

### `input.gamepad` — 虚拟手柄 (Input System)

| `action` | 必填参数 | 说明 |
|----------|---------|------|
| `button` | `button`, `press`(tap/press/release) | 按钮操作 |
| `axis` | `axis`, `value`(-1..1) | 设置摇杆轴值 |
| `set` | `buttons`[], `axes`{} | 批量操作 |
| `reset` | — | 全部归零 |
| `state` | — | 查询当前状态 |

- Button names: `south`/`a`, `east`/`b`, `north`/`x`, `west`/`y`, `leftShoulder`/`lb` 等
- Axis names: `leftStickX`, `leftStickY`, `rightStickX`, `rightStickY`, `leftTrigger`, `rightTrigger`
- 底层: `InputSystem.QueueStateEvent(Gamepad.current, GamepadState{...})`

### `scene.*` 组件操作 — 参数名是 `componentType`

> ⚠️ **不是 `component`，是 `componentType`！**

| 工具 | 必填参数 |
|------|---------|
| `scene.get_component_properties` | `instanceId`\|`path` + `componentType` |
| `scene.set_component_property` | `instanceId`\|`path` + `componentType` + `propertyName` + `value` |
| `scene.add_component` | `instanceId`\|`path` + `componentType` |
| `scene.remove_component` | `instanceId`\|`path` + `componentType` |
| `scene.get_components` | `instanceId`\|`path`（不需要 `componentType`） |

例外: `game.wait` 的组件参数名是 `component`（不一致，但已固化）。

## Multi-Bridge 路由

服务器支持多个 bridge 同时连接（如 Editor + Android）。

- 路由规则: **last-registration-wins** — 后连接的 bridge 覆盖同名工具的前一个注册
- Editor bridge (88 tools) 先连接 → Android bridge (35 tools) 后连接 → Android 覆盖重叠工具
- 两个 bridge 都有 `scene.set_transform` → 调用路由到 **Android**（后注册者）
- 录屏工具 (`recording.*`) 只在 Android bridge 注册（`Platform = Android | iOS | Standalone`）
- 验证路由目标: `scene.get_hierarchy` 返回 flat array `[...]` = Android; 返回 `{"value":[...],"Count":N}` = Editor

## Android Runtime Testing

已在 Android 设备 (V2073A, OpenGL ES 3.0) 上验证通过的工具:

| 工具 | 验证内容 |
|------|---------|
| `input.key_press` | WASD hold/release → Player 走一圈 ✅ |
| `input.touch` | tap/start/move/end 全部 ✅ |
| `input.swipe` | 滑动手势 ✅ |
| `input.gamepad` | state/button/axis/set/reset 全部 ✅ |
| `input.click_screen` | 狂点中心 Button ×10 ✅ |
| `input.mouse_click` | 归一化坐标点击 ✅ |
| `scene.set_transform` | position + rotation（Cube 旋转 10×15°）✅ |
| `scene.get_component_properties` | `componentType` 参数 → Transform 37 属性 ✅ |
| `recording.start/stop/status` | MP4 录屏 → 编码 → 导出 ✅ |

### 自动化测试脚本

| 脚本 | 路径 | 内容 |
|------|------|------|
| Android 全流程 | `SimpleMcpServer/tests/auto-test-android.ps1` | 录屏→走原点→点按钮→旋转Cube→移动Sphere→停录屏 |
| Cube 旋转录屏 | 内联 PowerShell | 录屏→Cube 10×15°→停录屏（800ms 间隔）|

运行: `H:\ai_works\SimpleMcpServer\tests\auto-test-android.ps1`

## Known Issues

### 1. MPEG4Writer "Stop() called but track is not started" (Android 录屏)

**现象**: logcat 出现 `Error MPEG4Writer Stop() called but track is not started or stopped`

**根因**: InstantReplay 库的 `UnboundedRecordingSession` **永远创建音频轨道**（`EncodingSystem.CreateAudioEncoder()` + `MuxerAudioInput`），即使 `enableAudio=false`。音频轨道在 MP4 容器中被创建但从未收到编码帧，`CompleteAsync()` 停止一个未启动的轨道 → MPEG4Writer 报错。

**不能设 `AudioOptions = null`**: `UnboundedRecordingSession` 构造函数 line 125 直接访问 `options.AudioOptions.SampleRate` → NullReferenceException。

**影响**: 无害。MP4 视频轨道完整，播放正常，只是没有音频。

### 2. `BuildJsonObject` 字符串值必须预引号

`JsonHelper.BuildJsonObject(("key", "value"))` 生成 `"key":value`（裸词，无效 JSON）。字符串值必须通过 `JsonHelper.EscapeString("value")` 包装: `("key", JsonHelper.EscapeString("value"))` → `"key":"value"`。数值和 bool 不需要包装。

### 3. `InputSystem.Update()` 在 Play Mode 阻塞

输入工具（gamepad/touch/key_press）使用 `InputSystem.QueueStateEvent()` 但不调用 `InputSystem.Update()`。事件由 Unity 原生 player loop 处理。曾尝试在 `GamepadTools.ApplyState()` 中调用 `InputSystem.Update()` 导致 Play Mode 阻塞，已移除。

### 4. 服务器日志无工具名（已修复）

工具调用和响应日志现在包含工具名: `Calling tool 'input.gamepad' → bridge [xxx]` 和 `Received tool response tool='input.gamepad'`。
