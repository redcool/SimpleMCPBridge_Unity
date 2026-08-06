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
- **Bridge (Editor):** 项目 `Assets/SimpleMCPBridge-config/bridge-config.json` — 首次自动从包内
  `Resources/bridge-config.json` 拷贝生成（不存在时创建，已存在则不覆盖）；用户可改，重启生效
- **Bridge (Player):** `Application.persistentDataPath/bridge-config.json` — 同上逻辑
- 兜底: 包内 `Runtime/Resources/bridge-config.json` 内嵌默认值
- Both use the same format. Cloud deployment: server `ip: "0.0.0.0"`.
- `scene.call_component_method` 权限字段（可选，需重启生效）：
  - `methodBlocklist`: 数组，追加拦截项（`"MethodName"` 或 `"TypeName.MethodName"`，大小写不敏感）；代码默认 6 项
    （`destroy`/`destroyimmediate`/`destroyobject`/`quit`/`quitimmediate`/`disconnect`）**始终生效，无法通过配置移除**
  - `methodAllowlist`: 空数组 = 关闭；非空 = 白名单模式，只放行命中的方法（同上两种格式）；白名单**不会覆盖**黑名单
    —— 调用权限合并语义：代码默认黑名单 + 配置追加黑名单 双重拦截始终优先，白名单只是最后一道放行门槛

## Logs

- **Bridge debug:** `Logs/mcp_bridge_debug.log` (relative to Unity project root)
- **Server stderr:** `SimpleMcpServer/server.err`
- **Unity Editor log:** `$env:LOCALAPPDATA\Unity\Editor\Editor.log`

## Tools (101)

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
| `ngui.get_texts` ⚠ | Read NGUI UILabel text from memory (no OCR) — requires NGUI package | All |
| `ngui.find` ⚠ | Find interactive NGUI elements (UIButton/UIToggle/UISlider/UIInput) + state | All |
| `ngui.find_widgets` ⚠ | Find all NGUI UIWidget (UITexture/UISprite/UILabel/...) + screen rect | All |
| `uitk.get_panels` | List all UI Toolkit (UIDocument) panels + sortingOrder/enabled/attached | All |
| `uitk.get_texts` | Read UITK text from memory (no OCR) — Label/TextElement/TextField | All |
| `uitk.find` | Find interactive UITK elements (Button/Toggle/Slider/DropdownField/TextField/ScrollView) + state | All |
| `uitk.get_elements` | Dump UITK visual tree (name/type/path/classes/rect/state) | All |
| `uitk.click` | Click UITK element by path or normalized coords (pooled PointerDown/Up dispatch) | All |
| `uitk.set_value` | Set Toggle/Slider/SliderInt/DropdownField/TextField value (optional silent) | All |
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
| `game.get_delta` | Read cached signal changes (bridge polls every ~167ms/10 frames; read-and-clear) | All |
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
| `tools.list_categories` | List all tool categories with tool counts + enabled state | All |
| `tools.enable` | Enable tool categories (`categories` string[] or `all`=true) — re-registers + pushes tool list | All |
| `tools.disable` | Disable tool categories (`categories` string[] or `all`=true) — removes tools from registration | All |
| `tools.reset` | Re-enable every category (full tool list restored) | All |

Tools are auto-discovered via `AutoRegisterAll()` — just create a class with
`[MCPTool]` methods and it's picked up automatically.

## Tool Categories & Dynamic Registration

- Every tool gets a category derived from its name prefix (`scene.get_hierarchy` → `Scene`,
  `assetbundle.hot_replace` → `AssetBundle`). Override via `[MCPTool(..., Category = "X")]`.
- Tool descriptions are prefixed with `[Category]` (e.g. `[Scene] Get the full scene hierarchy...`)
  so agents can scan/group tools quickly.
- `tools.enable` / `tools.disable` / `tools.reset` change which categories are registered
  and immediately push the new tool list to the server (via `ReRegisterTools`).
- **Default: all categories are enabled.** Conditional compilation (`#if NGUI_ON`, `#if INSTANT_REPLAY_ON`, ...)
  only decides which tools *exist* — installed packages' tools are registered out of the box, no `tools.enable`
  needed. `tools.disable` is purely an AI-side pruning mechanism to cut irrelevant tools and save tokens.
- Category state is **static** (in-memory only, no EditorPrefs/PlayerPrefs persistence) — it survives Play Mode
  transitions and router rebuilds, but **resets to all-enabled on domain reload** (script recompilation / Editor restart).
- `tools.disable all` keeps only the `Tools` category alive, so the control tools are always available.
- `tools.list_categories` shows all categories ever scanned (including currently-disabled ones)
  with `count` and `enabled` flags.

### uGUI / NGUI / UI Toolkit 是独立工具集

- `ui.*`（uGUI: Canvas/Text/TMP）→ 类别 `Ui`；`ngui.*`（NGUI: UILabel/UIButton）→ 类别 `Ngui`；
  `uitk.*`（UI Toolkit: UIDocument/VisualElement）→ 类别 `Uitk`。
- 三者**互不互斥**：可同时开启，也可 `tools.disable ["Ui"]` 只留 NGUI（或反之）。
- NGUI 工具用 `#if NGUI_ON` 条件编译 —— 项目未装 NGUI 时不注册 `ngui.*`，不影响编译。
- UITK 工具**无条件编译** —— `UnityEngine.UIElements` 是引擎内置模块（2022.3+ 必有），注册即用；
  面板未 attach（非运行态）时优雅返回空结果，无需 RequirePlayMode。
- **不设 `UITK_ON` 条件编译**（有意决策）：`com.unity.modules.uielements` 是内置模块非可选包，
  2022.3 必在（桌面平台 Unity 官方不支持移除模块），条件恒真、纯噪音；且编译期剔除会让
  工具整体消失，比「优雅返回空结果」更糟。客户工程 runtime 不用 UITK 时，**用 `tools.disable ["Uitk"]`
  裁剪**即可（临时、任务级、domain reload 自动恢复全开）——做好工具分类（类别 `Uitk`）就已足够。
- **UITK 交互事件依赖 EventSystem 桥接**：UITK 面板要接收指针事件（点击/触摸），场景里必须有
  `PanelEventHandler` + `PanelRaycaster`（通常挂在 UIDocument 的 PanelSettings 持有者 GameObject 上，
  如 `EventSystem/Default Panel Settings`）。`PanelRaycaster` 让面板进入 `EventSystem.RaycastAll` 命中范围，
  `PanelEventHandler` 实现 `IPointerUpHandler` 等接口把 ExecuteEvents 指针事件翻译成 UITK 事件 → Clickable → `clicked`。
  **没有桥接 → UITK 无事件**（`input.click_screen` 射不到面板；`uitk.click` 不受影响，它直接向 panel 注入事件）。

### TMP 条件编译（TEXT_MESH_PRO_ON）

`SimpleMCPBridge.asmdef` 的 versionDefines 含 `com.unity.textmeshpro` → `TEXT_MESH_PRO_ON`，
references 含软引用 `"Unity.TextMeshPro"`。TMP 存在时：
- `UIAnalysisTools.TMPTextType` / `GameHandler.GetTMPInputFieldType()` / `GetTMPDropdownType()`
  返回**编译期类型**（`typeof(TMPro.X)`），替代原先 `Type.GetType("TMPro.X, Unity.TextMeshPro")`
  字符串反射（程序集名写死、易碎）。
- 下游反射属性读取（`GetProperty("text")` 等）保持不变，只换类型解析入口。

TMP 缺失时（`#else`）这些入口返回 `null`，相关扫描/操作静默跳过——行为与原来一致。

### NGUI 安装

原版 `tasharen/ngui` **没有 package.json / asmdef**,不能直接用 versionDefines 检测。
**已提供 UPM 化 fork：`https://github.com/redcool/ngui-upm.git`**（已含 asmdef + package.json，推荐直接使用）。
两种安装方式，任选其一：

#### 方式 A：UPM 包（推荐，versionDefines 自动生效）

**直接引用 UPM 化 fork（最简）**：

```json
// Packages/manifest.json
"com.tasharen.ngui": "https://github.com/redcool/ngui-upm.git"
```

- 仓库已含 `package.json`（`com.tasharen.ngui` v3.12.0）与两个 asmdef（`NGUI.asmdef` 运行时 + `NGUI.Editor.asmdef` Editor）
- `NGUI_ON` 由 asmdef `versionDefines` **自动**定义，零手动步骤
- 本地调试可先 `git clone https://github.com/redcool/ngui-upm.git <目录>`，再改用 `"com.tasharen.ngui": "file:../../ngui-upm"`

**旧模板方式（针对未 UPM 化的 tasharen/ngui 仓库）**：原仓库含 `upm_template/` 目录，按 `readme.txt` 三步完成：

1. `git clone https://github.com/tasharen/ngui.git <某目录>`（如 `H:\ai_works\ngui`）
2. 从 `upm_template/` 拷贝并改名放置：
   - `NGUI.asmdef_temp` → `Assets/NGUI/Scripts/NGUI.asmdef`
   - `NGUI.Editor.asmdef_temp` → `Assets/NGUI/Scripts/Editor/NGUI.Editor.asmdef`
   - `package.json` → 仓库根
3. 排除示例：`Assets/NGUI/Examples` → 改名 `Examples~`（Unity 不导入）
4. 项目 `Packages/manifest.json` 加：`"com.tasharen.ngui": "file:../../ngui"`（相对路径按实际位置）

**模板内容说明**：
- `NGUI.asmdef_temp` — 运行时程序集，名 `NGUI`，Any Platform，无引用
- `NGUI.Editor.asmdef_temp` — Editor 程序集，`includePlatforms: ["Editor"]`，引用 `NGUI`
- `package.json` — name `com.tasharen.ngui` / version `3.12.0` / unity `2019.4`

`NGUI_ON` 由 asmdef `versionDefines` **自动**定义，零手动步骤。

#### 方式 B：源码直接放 Assets/（非 UPM）+ 手动定义 NGUI_ON

1. NGUI 源码拷入项目 `Assets/NGUI/`
2. 建与方式 A 相同的两个 asmdef（**必须建**，见下方警告）
3. 排除示例：`Assets/NGUI/Examples` → 改名 `Examples~`
4. **手动**加 `NGUI_ON` 符号：
   - `Project Settings → Player → Other Settings → Scripting Define Symbols` 加 `NGUI_ON`
   - 或 `SimpleMCPBridge.asmdef` 的 `defineConstraints` 加 `"NGUI_ON"`（仅本程序集编译条件）

> ⚠️ **警告**：`#if NGUI_ON` 打开但 NGUI 类型不可解析会报 CS0246。NGUI 源码**必须**放在有 `NGUI.asmdef` 的程序集里
> （asmdef 程序集无法引用裸放进 Assembly-CSharp 的 NGUI 代码），且 `SimpleMCPBridge.asmdef` 的
> `references` 必须含 `"NGUI"`。方式 B 只省掉「package.json + manifest 引用」两步，其余前置条件不变。

**SimpleMCPBridge.asmdef 的配套配置（两种方式都必须具备）**：
- `versionDefines`：`com.tasharen.ngui` → `NGUI_ON`（方式 A 自动触发；方式 B 不触发，需手动符号）
- `references`：加 `"NGUI"`（软引用 —— NGUI 缺失时仅 warning 不报错，装了才能解析类型）
- 代码：`NguiHandler.cs` / `NGUIAnalysisTools.cs` 全部内容在 `#if NGUI_ON` 内

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

- **Active transport:** `NetWebSocketClient` (wraps .NET's `ClientWebSocket`) — created in
  `BridgeClient.ConnectToServer()`.
- **Legacy:** `WebSocketClient` (raw `System.Net.Sockets` + `System.Security.Cryptography`,
  custom RFC 6455) is marked `[Obsolete]` and kept as reference only — never instantiated
  by the bridge. Do not use it in new code.
- Protocol notes (apply to both): RFC 6455 masked frames client→server, unmasked
  server→client; HTTP upgrade path must be `GET / HTTP/1.1` (not path-prefixed).

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
| `Runtime/WebSocketClient.cs` | Legacy custom RFC 6455 client — `[Obsolete]`, reference only (use NetWebSocketClient) |
| `Runtime/MessageRouter.cs` | Routes tool calls to handlers |
| `Runtime/MCPToolRegistry.cs` | Scans for [MCPTool] methods |
| `Runtime/Handlers/GameHandler.cs` | High-level game tools: ui.*, input.action, game.* |
| `Runtime/Handlers/NguiHandler.cs` | NGUI tools: ngui.get_texts/find (#if NGUI_ON — requires com.tasharen.ngui) |
| `Runtime/Handlers/UIToolkitHandler.cs` | UI Toolkit tools: uitk.* (UIDocument/VisualElement scan + pointer event injection) |
| `Runtime/Tools/UIAnalysisTools.cs` | Canvas UI scanning (Text + TMP + interactive elements) |
| `Runtime/Tools/NGUIAnalysisTools.cs` | NGUI scanning (UILabel text + UIButton/UIToggle/UISlider/UIInput) (#if NGUI_ON) |
| `Runtime/Tools/UIToolkitAnalysisTools.cs` | UITK scanning (Label/TextField text + interactive elements + visual tree dump) |
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
| `Runtime/Handlers/ToolsHandler.cs` | Dynamic tool registration control (tools.enable/disable/list_categories/reset) |
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

服务器支持多个 bridge 同时连接（如 Editor + Android）。三层机制：

1. **默认路由: last-registration-wins** — 后连接的 bridge 覆盖同名工具的前一个注册
2. **显式路由: `bridge.call`** — 调用时指定 `target` = bridgeId（`bridge.list` 查 ID/IP），
   绕过默认路由，确定性调用（参见 Known Issues #6）
3. **断开 failover** — 某 bridge 断开时，若其路由的工具被其他在线 bridge 也注册了，
   自动回退到那个 bridge（服务器 index.ts close 处理）；否则清除该工具路由

- Editor bridge (88 tools) 先连接 → Android bridge (35 tools) 后连接 → Android 覆盖重叠工具
- 两个 bridge 都有 `scene.set_transform` → 默认调用路由到 **Android**（后注册者）
- 录屏工具 (`recording.*`) 只在 Android bridge 注册（`Platform = Android | iOS | Standalone`）
- 验证路由目标: `scene.get_hierarchy` 返回 flat array `[...]` = Android; 返回 `{"value":[...],"Count":N}` = Editor
- BridgeId 是每次连接生成的 GUID（BridgeClient.cs），非持久；`bridge.list` 显示的 clientPort
  是随机客户端端口（每次连接都变），**不要用 ip+port 作稳定标识**——精确调用一律用 bridgeId
- `tools/list`（含 MCP ListTools）返回的每个 bridge 工具 description 带 `[bridge: ip:port (id前8位)]`
  前缀 = 该工具**当前路由目标**（last-registration-wins 结果）。AI 可直接从工具列表得知调用会打到谁；
  标注用的是 `toolToBridge` 实际路由（非注册来源），与真实调用行为一致

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

### 5. MCP Server 需要显示 cmd 窗口

MCP Server 必须在一个**可见的 cmd 窗口**中运行（用 `start.bat` 启动），不能以无窗口/后台方式启动。服务器日志实时输出到窗口，便于排查连接/工具调用/AB 传输问题。

### 6. 多 Bridge 时工具路由到 Editor 而非 Android（设计决策，非缺陷）

当 Editor 和 Android 同时连接且 Editor 处于 Play Mode 时，`last-registration-wins` 让 Editor 覆盖同名工具（`shader.hot_replace`、`assetbundle.hot_replace` 等）的路由。直接调用会路由到 **Editor** 而非 Android。

这是**有意的默认行为**（见「Multi-Bridge 路由」三层机制）：无指定目标时，服务器按注册顺序取最后一个；需要确定性目标时，**用 `bridge.call` 显式指定**：`target` = Android bridge id，`method` + `params`。`bridge.list` 查看各 bridge ID/IP（Android 通常 `10.0.x.x`）。

### 7. 同内容 AssetBundle 只能加载一次

Unity AB 去重机制: 相同内容的 AB 只能被 `LoadFromMemory` 加载一次。若 `shader.hot_replace` 已加载某 AB 且未卸载，后续 `assetbundle.hot_replace` 加载同内容 AB 会报 `The AssetBundle 'Memory' can't be loaded because another AssetBundle with the same files is already loaded`。

**解决**: 每次部署前先 `assetbundle.unload_all`，或使用不同内容的 AB。

### 8. `uitk.click` 合成点击的隐藏 gate（clicked 静默不触发）

**现象**: `uitk.click` 返回 success、无报错，但 Button 的 `clicked` 回调不触发。

**根因**: Clickable 的 `clicked` 只在 `ProcessUpEvent` 里经 `ContainsPointer(pointerId)` 触发，该缓存仅在**两条同时满足**时写入:
1. 事件 `triggeredByOS == true` —— 只有 `MouseDownEvent/MouseUpEvent.GetPooled(Event)` 工厂会设置；PointerDown/PointerUp 的 GetPooled 重载**不会**
2. 坐标落在 `panel.visualTree.layout`（**panel 空间**）内

任一不满足 → 静默 no-op，不报错。

**修法**（已固化在 `UIToolkitHandler.DispatchClick`）: `GetPooled(Event)` + 坐标取 `el.worldBound.center`（panel 空间，与 uitk.find 的 normalizedRect 同原点）+ 布局内 clamp + `panel.Pick(position)` 前置检查（命中失败返回显式错误）。`IPanel` 接口在 2022.3 **没有** `GetTopElementUnderPointer`（CS1061）——公共命中测试用 `panel.Pick(position)`。

### 9. 编译错误会静默阻断 `scene.enter_play_mode`

**现象**: `scene.enter_play_mode` 返回 `success:false`，连续轮询 `scene.get_play_mode` 全是 `edit`，表面无报错。

**根因**: Unity 拒绝在编译错误状态下进入 Play Mode。`editor.get_console` 最近 50 条可能全是 Log（编译错误在更早位置），被误导为「无报错」。

**排查**: 直接搜 console 的 `error CS`（或查 `$env:LOCALAPPDATA\Unity\Editor\Editor.log`）确认编译状态，先修编译错误再进 Play Mode。
