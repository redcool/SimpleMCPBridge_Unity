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
- `node tests\test-e2e.cjs` — E2E test (spawns server, waits for bridge, calls tools)
- `npm run dev` — watch mode via tsx (no build needed)
- `setup.bat` — first-time setup (node check + npm install + build)

**Unity Bridge:**
- 拖 `Prefabs/MCPBridge.prefab` 进场景（或给任意 GameObject 添加 `MCPBridge` 组件）
- 选中后 Inspector 内联显示状态/Bridge ID/错误面板 + IP/Port 字段 + Connect 按钮
- `[ExecuteAlways]` 三态可用（Edit/Play/Player）；isAutoReconnect 断线自动重连
  （含进出 Play Mode、域重载、场景切换）

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
- 兜底: 包内 `Resources/bridge-config.json` 内嵌默认值
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

## editor.eval 安全说明

`editor.eval` 通过 Mono.CSharp 动态编译并在内存中执行任意 C# 代码 —— 等同于完全的 Unity/机器控制（读写任意文件、删除资产、网络访问、启动进程等）。这是 MCP 工具集最强的"逃生舱":当某个场景操作没有专用工具覆盖时,AI 可用 eval 即时补救。

**默认 ON,以用户方便为先**。开发调试、快速原型、补救缺口工具时即时可用,不必先翻配置。若你的环境不可信（共享机器/公网暴露的服务器),关闭它。

**双重 gate（任一关闭即不可用）**:
- **Server 侧 `config.json` → `evalEnabled`**（默认 `true`）:`false` 时 `tools/list` 不暴露 `editor.eval` 给 agent,agent 看不到也就调不到。
- **Bridge 侧 `EditorPrefs SimpleMCPBridge_EvalEnabled`**（默认 `true`）:执行前再检查一次;可用 MCPBridge Inspector 的 toggle 切换。

**风险面**:任何能调用 `/rpc` 的 AI 都能执行任意代码。本工具**不做代码内容过滤**（任意代码无法穷举拦截,黑名单无意义）。安全靠网络层 gate —— `allowedIps` 白名单默认仅本机（`127.0.0.1`/`::1`）。云部署（`ip:0.0.0.0`）前务必扩白名单到可信 IP 段,或直接 `evalEnabled:false`。

**与 `scene.call_component_method` 权限的区别**:后者有方法名黑/白名单（可枚举拦截);eval 是任意代码,只能靠网络层 gate,无方法级过滤。

**关闭方法**（任一即可）:
- `SimpleMcpServer/config.json` 设 `"evalEnabled": false`（重启 server 生效,对所有 agent 隐藏）
- Unity Editor: MCPBridge Inspector 的 eval toggle（立即生效,单机）
- 代码:`EditorPrefs.SetBool("SimpleMCPBridge_EvalEnabled", false)`

## Tools (127)

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
| `scene.instantiate_prefab` | Instantiate a prefab from project Assets (PrefabUtility.InstantiatePrefab — 保留 prefab 关联,实例改动传导 prefab;材质资产化依赖此关联推断同级目录) | Editor |
| `scene.save_current` | Save current scene | Editor |
| `scene.load_scene` | Load a scene (Editor: open asset; Play/built: SceneManager, single/additive) — ⚠ after load ALL instanceIds go stale, re-fetch hierarchy | All |
| `scene.save_prefab` | Save a GameObject (instanceId/path) as a prefab asset (overwrites existing) | Editor |
| `scene.enter_play_mode` | Enter Play Mode | Editor |
| `scene.exit_play_mode` | Exit Play Mode | Editor |
| `scene.pause_play_mode` | Pause/resume Play Mode | Editor |
| `scene.get_play_mode` | Get current play mode state | Editor |
| `physics.raycast` | Cast a ray and return first hit info | All |
| `physics.box_cast` | Sweep a box along direction, return first hit | All |
| `physics.sphere_cast` | Sweep a sphere along direction, return first hit | All |
| `physics.overlap_sphere` | All colliders within a sphere (name, instanceId, path, position, tag, layer) | All |
| `physics.overlap_box` | All colliders within a box (optional rotation) | All |
| `camera.screenshot` | Capture main camera view and save as PNG (savePath 可选,省略自动生成时间戳文件名; root: Editor→Project/VideoRecord, Runtime→tempCache/VideoRecord) | All |
| `audio.get_sources` | List all playing AudioSources (clipName, volume, isPlaying, time, loop, spatialBlend, position, distanceFromListener, path, instanceId) | All |
| `particle.get_systems` | List all ParticleSystems + live state (isPlaying/isEmitting/particleCount/time/enabledModules/renderer) | All |
| `particle.get_state` | Detailed state + config of one ParticleSystem (main/emission/shape values) | All |
| `particle.create` | Create GameObject + ParticleSystem with optional initial config + material (builtin name or Assets/... path, Editor 下内置名自动落地为特效资产同目录的磁盘 .mat) + shader (可选,缺省 URP Simple Lit) (Edit Mode, undoable) | All |
| `particle.set` | Set module property via typed code path (main/emission/shape/.../renderer, incl. material + shader 换材质 shader; curve/gradient/burst syntax; Edit+Play, undoable) | All |
| `particle.simulate` | Deterministic preview to time t (works in Edit Mode) | All |
| `particle.play` | Play (optional restart) | All(Play Mode) |
| `particle.pause` | Pause (particles freeze) | All(Play Mode) |
| `particle.stop` | Stop (optional clear) | All(Play Mode) |
| `particle.clear` | Clear all particles without stopping | All(Play Mode) |
| `particle.emit` | One-shot burst via EmitParams (position/velocity/color/size, works without playing) | All(Play Mode) |
| `nav.query_path` | Find path between two points on NavMesh (reachable, status, waypoints, distance) | All |
| `nav.sample_position` | Snap world position to nearest NavMesh point | All |
| `nav.has_navmesh` | Check if NavMesh exists (hasNavMesh, vertexCount, triangleCount) | All |
| `nav.move_to` | Set NavMeshAgent destination, auto pathfinding movement | All |
| `asset.refresh` | Refresh Unity asset database | All |
| `asset.find_assets` | Search Assets/ by name and/or type (AssetDatabase.FindAssets) | Editor |
| `asset.find_references` | Find all assets referencing a given asset | All |
| `asset.create` | Create asset: type `folder`/`material` (+ optional color) | Editor |
| `asset.delete` | Delete asset (reference pre-check; `force=true` skips) | Editor |
| `asset.rename` | Rename asset (`newName` without extension) | Editor |
| `asset.move` | Move asset to `newPath` | Editor |
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
| `input.gamepad` | Gamepad control: button tap/press/release, axis, batch set, reset, state query, rumble 震动 | All |
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
| `uitk.create_element` | Create runtime UI Toolkit element (Button/Label/Slider/Toggle) in a panel — NOT persisted (panel refresh/UXML re-apply destroys it) | All |
| `uitk.remove_element` | Remove UI Toolkit element from panel — NOT persisted (refresh restores) | All |
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
| `playerprefs.get_all` | Get all session-known PlayerPrefs keys with type-sniffed values (Unity has no key enumeration API — only keys seen via set/get) | All |
| `playerprefs.get` | Get single PlayerPrefs value (optional `keyType`: int/float/string, auto-detected) | All |
| `playerprefs.set` | Set PlayerPrefs value (optional `valueType`, auto-detected from JSON type; `save`=true default) | All |
| `playerprefs.delete` | Delete a PlayerPrefs key | All |
| `castle.click_building` ⚠ | Click a 3D castle building at normalized screen position (project-specific tool) | All |
| `recording.start` | Start recording via InstantReplay — OS-native MP4 (MediaCodec / VideoToolbox / Media Foundation), Play Mode only; Platform = Android/iOS/Standalone/Editor (RequirePlayMode, 仅 Play Mode 注册) | All(Play Mode) |
| `recording.stop` | Stop recording and finalize MP4 (async, poll status) | All(Play Mode) |
| `recording.status` | Get current recording/export state | All(Play Mode) |
| `recording.reset` | Force-reset recording system (recover from stuck state) | All(Play Mode) |
| `shader.hot_replace` | Runtime hot-swap a Shader from an AB (WebClient download), global or per-path with instance materials | PlayMode |
| `shader.hot_replace_status` | Poll shader.hot_replace progress | PlayMode |
| `asset.build_bundle` | Build an AssetBundle from project assets (Editor only, uses BuildPipeline) | Editor |
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

**⚠️ Bridge ID 每次重连都会变化**（每次连接生成新 GUID，非持久）。断线
重连（如 app 崩溃重启、域重载、进出 Play Mode、重新安装包）后，旧 ID
立即失效，`bridge.call` 用旧 ID 会报 `Bridge '<id>' not found`。调用前
必须先重新获取：

- `GET /health` → 每个 bridge 的 `id`（当前真实 ID）
- 或 `bridge.list` → 各 bridge ID/IP

正确调用模式：**先查 /health 拿最新 ID，再发 `bridge.call`**，不要缓存
或手写 ID。AGENTS.md Known Issues #6 的多 bridge 路由也依赖此行为。

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
| `Runtime/Handlers/AssetHandler.cs` | Asset tools (find, refresh, create/delete/rename/move, build_bundle) |
| `Runtime/Handlers/PlayerPrefsHandler.cs` | PlayerPrefs tools (get_all/get/set/delete — Unity 无 key 枚举 API，会话级 key 注册表) |
| `Runtime/Handlers/BuildingHandler.cs` | 项目特定 castle.click_building（Physics.Raycast + Lua FakeHitResultEvent） |
| `Plugins/` | NuGet DLL 依赖（InstantReplay/UniEnc 需要，已含在仓库内）|
| `Resources/bridge-config.json` | 包内兜底配置（Editor 首次自动拷贝到项目 `Assets/SimpleMCPBridge-config/`） |

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
| `rumble` | `lowFreq`, `highFreq`, `duration` | 触发震动（虚拟设备仅写状态，不物理震动） |

- 震动感知不在此工具——AI 侧用 `game.watch` 监听游戏暴露的震动相关属性
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
- 录屏工具 (`recording.*`) 注册平台为 `Android | iOS | Standalone | Editor` 且 `RequirePlayMode = true`
  （仅 Play Mode 注册）——InstantReplay 包本身支持
  **Android/iOS/macOS/Windows/Linux(需 ffmpeg)/Web(需 WebCodecs)**，Windows/macOS 走 OS 原生编码
  （Media Foundation / Video Toolbox），无需外部工具；`RecordingTools.BuildOutputPath` 也含 `UNITY_EDITOR` 分支
  （Editor 输出到项目 `VideoRecord/`）。Editor 注册是 2026-08 起的默认（曾为路由决策排除，避免 Editor bridge
  与 Android bridge 抢 recording.* 路由，已实测 Windows Editor 录制通过），multi-bridge 下仍遵循
  last-registration-wins + `bridge.call` 显式指定
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

### 2. `BuildJsonObject` 字符串值必须预引号 + 控制字符转义

`JsonHelper.BuildJsonObject(("key", "value"))` 生成 `"key":value`（裸词，无效 JSON）。字符串值必须通过 `JsonHelper.EscapeString("value")` 包装: `("key", JsonHelper.EscapeString("value"))` → `"key":"value"`。数值和 bool 不需要包装。

**同类陷阱 —— 控制字符未转义**：`EscapeString` 原先只处理 `" \` `\n` `\r` `\t`，漏了 JSON 规范要求转义的 U+0000–U+001F 控制字符（及 `\b` `\f`）。工具返回含控制字符的字符串（如 `Vector3.ToString()` 的 `(0, 0, 0)`、二进制数据、带格式符的文本）会产出无效 JSON → 服务器日志 `Invalid JSON from bridge` → 客户端表现 30s 超时（像 handler 挂起，实际是响应被丢弃）。`EscapeString` 已补全 `\b` `\f` 及 `\uXXXX` 兜底；非字符串值（float/int 的 `ToString("G")`）不受影响。诊断方法：server.log 搜 `Invalid JSON`，用 `node -e "JSON.parse(...)"` 定位裸词位置。

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

### 10. Android 触摸工具崩溃 — URP DebugUpdater + EnhancedTouch SIGSEGV（已修复）

**现象**: Android 上 `input.touch`（start→move→end 序列的 end/move 阶段）或 `input.swipe` 后紧跟 `input.mouse_click` 会让 **App 进程崩溃 → bridge 断开**（server.log: `Bridge disconnected` + `Rejected 1 pending tool call`，重连空窗 >3s = 进程已死需手动重启）。`input.key_press` / `input.click_screen`（EventSystem 路径）/ `uitk.*` / scene/physics 工具不受影响。仅 Development build / Editor 复现。

**根因**: URP `DebugUpdater.Update()` 每帧调用 `DebugManager.GetActionToggleDebugMenuWithTouch()` → 读 `Touch.activeTouches`（3 指 debug 菜单手势检测）。唯一 gate 是 `if (!EnhancedTouchSupport.enabled) return false;`，而 `DebugUpdater.EnableRuntime()` 在 `RuntimeInitializeLoadType.AfterSceneLoad` 调用了 `EnhancedTouchSupport.Enable()`，gate 恒开。虚拟 Touchscreen（`InputSystem.AddDevice<Touchscreen>("VirtualAgentTouch")` + `QueueStateEvent`）的合成事件流破坏 EnhancedTouch 的历史不变量（`Debug.Assert(currentTouchState != null, "Must have current touch record at this point")`，InputSystem 1.7.0 Touch.cs L783），Development build 下 assert 不中断 → null 解引用 → **SIGSEGV（fault addr 0x20）** → 进程死亡 → bridge 断开。URP `DebugUpdater` 本体是 `#if DEVELOPMENT_BUILD || UNITY_EDITOR`，Release build 永不创建。

**修法**（已固化在 `Runtime/UrpDebugGuard.cs`）: `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 将 `DebugManager.instance.enableRuntimeUI` 设为 `false` —— DebugUpdater 在 `AfterSceneLoad` 创建时检查 `enableRuntimeUI`，提前关掉后 DebugUpdater 永不被创建，`Touch.activeTouches` 不再被每帧轮询。仅用公共 API，不动 TouchDeviceTools/InputHandler/MouseDeviceTools 注入逻辑（崩溃在 URP 侧，不在注入侧）。

**影响**: URP runtime debug UI（3 指手势）一并被禁用 —— 对自动化测试场景是预期行为。

**参考**: InputSystem Touch.cs assert commit `33e45e5` / case `1230756`。

### 11. `scene.set_component_property` 对 il2cpp 裁剪的 setter 报 not found（非桥 bug，工程侧需引用 setter）

**现象**: Android（il2cpp 构建）上 `scene.set_component_property` 对 `Rigidbody.mass`/`drag` 等属性报
`Property/field 'mass' not found on Rigidbody`，但 `scene.get_component_properties` 能列出该属性并读到值。
同一调用在 **Editor bridge 上成功**（同源码、同场景）。

**根因**: il2cpp 发包时的 Managed Stripping（代码裁剪）会删除**工程从未引用过的 setter**。属性对象仍在
（getter 被保留，所以 get 侧枚举能看到值），但 setter 被 linker 删除 → 反射 `CanWrite=false` →
`GetProperty(name)` 命中但写入被拒 → 落入 `m_` 字段兜底也失败 → 报 not found。这是**预期行为，非桥 bug**，
任何反射方式都无法写入被裁剪的 setter。诊断特征：同一 Android 包上 `get_component_properties` 的
`propertyCount` 远小于 Editor（实测 Rigidbody 9 vs 51），即大量未引用成员已被裁剪。

**验证实验**（已确认根因）: 在测试工程任意代码（如 `PlayerMove.Start()`）显式调用一次
`rb.mass = 5f` → linker 检测到使用即保留 setter → 重新发包后：
- Start 中 mass 初值生效（=5）
- `scene.set_component_property` 写 mass **恢复成功**（set 7 → get 7）
- 未引用的对照属性（`drag`）**仍然 not found** —— 证明裁剪假设成立

**解决**: 工程侧三选一 ——
1. **引用一次 setter**（最简）: 工程代码里调一次 `rb.mass = rb.mass`（或设实际值），linker 即保留
2. **降低 stripping**: Player Settings → Other Settings → Managed Stripping Level → Low/Disabled（影响包大小）
3. **link.xml 保留**: 工程 Assets 加 link.xml 保留对应类型（如 `UnityEngine.CoreModule` 的 `UnityEngine.Rigidbody`）

**排查指引**: 遇到 `set_component_property` 对某属性报 not found 但 get 能读到 → 先对照 Editor bridge
同调用是否成功 + 对比两端的 `propertyCount` → 若 Editor 成功且 Android 裁剪严重，即本问题，按上述工程侧
方案解决；若 Editor 也失败，才是桥代码问题（可查 SceneHandler.cs `SetComponentProperty` 的 field/property/
枚举回退/`m_` 兜底四段查找链）。

**注**: `SceneHandler.SetComponentProperty` 已含 il2cpp 防御性枚举回退分支（`GetProperties()` + 
`OrdinalIgnoreCase` 匹配 + `CanWrite` + 非索引器），用于"属性存在且可写但 `GetProperty(name)` 按名查找
失败"的反射差异场景；setter 被裁剪（`CanWrite=false`）的场景该分支同样无法命中，需工程侧保留 setter。

## Particle 特效创作 (particle.*)

使用模型：**Editor 创作（资产）+ Runtime 播放**。AI 在 Editor 里创建/编辑特效并保存 prefab，
运行时只做播放控制；一般不在 runtime 创建特效。

### 工具分层

- **创作/预览**（全平台，Edit Mode 可撤销）: `particle.create` / `particle.set` / `particle.simulate` / `particle.get_systems` / `particle.get_state`
- **运行时播放**（仅 Play Mode 注册）: `particle.play` / `particle.pause` / `particle.stop` / `particle.clear` / `particle.emit`
- **材质**: `particle.create` 的 `material` 参数（内置名 `Default-Particle`/`Sprites-Default`/`Default-Material` 或 `Assets/...` 路径）或
  `particle.set` 的 `renderer.material` —— 创建后必须赋材质粒子才可见（URP 下默认材质可能不渲染）
- **shader 参数**: `particle.create`/`particle.set` 新增 `shader` 参数（shader 名 `Shader.Find` 或 `Assets/...` 路径），
  创建材质时指定 shader；**缺省 = `Universal Render Pipeline/Particles/Simple Lit`**（非 URP 回退 Legacy Particles Alpha Blended）
- **换材质 shader**: `particle.set` renderer 模块新增 `shader` 属性（如 `{"module":"renderer","property":"shader","value":"Universal Render Pipeline/Particles/Unlit"}`）——
  只改材质 shader 字段不动引用；Editor 下材质是资产时 SetDirty + SaveAssets **持久化到磁盘资产**（runtime 材质实例直接改）
- **材质资产化（Editor 创作路径,重要）**: 创作阶段（Editor 非 Play）**绝不使用材质实例**——
  - `Assets/...` 路径 → 直接加载该资产引用
  - 内置名 → 在特效资产同目录创建/复用 `<对象名>_<材质名>.mat` **磁盘资产**并写入磁盘，特效引用此资产
    （同一路径已存在则复用，不重复创建）
  - 目录解析优先级: ① `materialDir` 显式参数（`particle.create`/`particle.set` 新增）→ ② 对象是 prefab 实例时其
    源 prefab 所在目录（配合 `scene.instantiate_prefab` 用 `PrefabUtility.InstantiatePrefab` 保留关联）→ ③ 兜底 `Assets/`
  - 创建/修改特效 prefab 或 GameObject 时都用资产引用，保存的 prefab 持有材质资产引用（跨目录引用合法）
  - **唯独 runtime 用材质实例**: Player 或 Editor Play Mode（播放态特效本来就是临时的）→ URP Simple Lit shader
    创建实例或内置资源
  - 内置名在 URP 工程下优先用 `Universal Render Pipeline/Particles/Simple Lit` shader（Shader.Find），非 URP 回退内置资源
- 材质颜色/贴图: 用 `scene.set_material`（ParticleSystemRenderer 是 Renderer）
- 保存: `scene.save_prefab`（assetPath 参数）

### 关键实现约束

- **typed 代码路径，零反射**: 模块 struct 是值类型句柄（内部持 ParticleSystem 引用，setter 立即生效无需写回）；
  反射对嵌套模块 struct 结构性不可用（get-only 访问器 + ToString 无数据），且 Android il2cpp 会裁剪模块属性
  （同 Known Issue #11），typed 引用天然免疫。改模块属性必须先存局部变量（CS1612: 不能 `ps.emission.rateOverTime = x`）
- **BuildJsonObject 裸词陷阱（本工具集踩过）**: 枚举 `ToString()`（simulationSpace/renderMode/scalingMode/
  stopAction/shapeType）与颜色 hex 都必须 `JsonHelper.EscapeString()` 包装，否则服务器解析响应失败 → 调用超时
- **`particle.set` 值语法**:
  - MinMaxCurve 属性: 单数 = 常量；`[min,max]` = 双常量随机；`[[t,v],...]` = 动画曲线（如 `[[0,1],[1,0]]` 随时间缩小）
  - 颜色属性: 单色（`#RRGGBB`/`[r,g,b,a]`）；`[c1,c2]` = 双色渐变（colorOverLifetime 下是**时间渐变**，startColor 下是随机）；
    `[[t,c],...]` = 完整渐变 keys（如 `[[0,"#FF7F00"],[1,"#00000000"]]` 橙→透明渐隐）
  - `emission.burst`: `[count, time]` 单 burst；`[[c,t],...]` 多 burst（替换现有）
  - `main.startSize3D` 开启时 `startSize` 只影响 X（用 startSizeX/Y/Z）

### 特效配方（描述词 → 完整可执行序列，AI 生成特效的"手艺"）

> 用法：用户说"做个火焰特效" → 按下方序列执行（`<id>` 用 `particle.create` 返回的 instanceId 替换）。
> 每个配方 = 1 次 `particle.create`（含材质）+ 若干 `particle.set`。Edit 预览用 `particle.simulate`，
> 效果确认后 `scene.save_prefab` 存资产。

**火焰（Fire）** — 锥形喷发 + 橙→透明渐隐 + 微扰
```
particle.create {"name":"FX_Fire","material":"Default-Particle","shapeType":"Cone","rateOverTime":45,"startSpeed":4,"startLifetime":1.2,"startSize":0.5,"startColor":"#FF7F00","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":["#FF7F00","#00000000"]}
particle.set {"instanceId":"<id>","module":"noise","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"noise","property":"strength","value":0.2}
```

**爆炸（Explosion）** — 一次性爆发 + 亮黄白 + 拖尾 + 渐小
```
particle.create {"name":"FX_Explosion","material":"Default-Particle","shapeType":"Sphere","startSpeed":12,"startLifetime":0.7,"startSize":0.45,"startColor":"#FFF4B8","loop":false,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"emission","property":"burst","value":[60,0]}
particle.set {"instanceId":"<id>","module":"sizeOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"sizeOverLifetime","property":"size","value":[[0,1],[1,0.3]]}
particle.set {"instanceId":"<id>","module":"trails","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"trails","property":"lifetime","value":0.3}
```

**火花/喷溅（Sparks）** — 小锥形高速度 + 重力下落 + 黄色
```
particle.create {"name":"FX_Sparks","material":"Default-Particle","shapeType":"Cone","rateOverTime":80,"startSpeed":9,"startLifetime":0.5,"startSize":0.1,"startColor":"#FFD700","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"main","property":"gravityModifier","value":1.5}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":["#FFD700","#FF4500"]}
```

**烟雾（Smoke）** — 大粒子低速 + 灰→透明 + 强噪声 + 上升
```
particle.create {"name":"FX_Smoke","material":"Default-Particle","shapeType":"Sphere","rateOverTime":20,"startSpeed":0.8,"startLifetime":3,"startSize":2,"startColor":"#808080","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"main","property":"gravityModifier","value":-0.3}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":["#808080","#00000000"]}
particle.set {"instanceId":"<id>","module":"noise","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"noise","property":"strength","value":0.5}
```

**魔法/能量（Magic）** — 蓝紫渐变 + 脉冲爆发 + 拖尾
```
particle.create {"name":"FX_Magic","material":"Default-Particle","shapeType":"Sphere","rateOverTime":30,"startSpeed":3,"startLifetime":1.2,"startSize":0.5,"startColor":"#7B68EE","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"emission","property":"burst","value":[[20,0],[20,0.5],[20,1]]}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":[[0,"#7B68EE"],[0.7,"#00CED1"],[1,"#00000000"]]}
particle.set {"instanceId":"<id>","module":"trails","property":"enabled","value":true}
```

**雨/雪（Rain/Snow）** — Box 大范围 + 高密度 + 下落
```
particle.create {"name":"FX_Rain","material":"Default-Particle","shapeType":"Box","rateOverTime":300,"startSpeed":12,"startLifetime":2,"startSize":0.05,"startColor":"#B0C4DE","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"shape","property":"boxThickness","value":[20,0.1,20]}
particle.set {"instanceId":"<id>","module":"main","property":"gravityModifier","value":2}
```
（雪: startSpeed 1.5、startSize 0.3、gravityModifier 0.2、rateOverTime 150、颜色 #FFFFFF）

**光点/星星（Sparkle）** — 小粒子高密度 + 白金色 + 短命
```
particle.create {"name":"FX_Sparkle","material":"Default-Particle","shapeType":"Sphere","rateOverTime":80,"startSpeed":0.5,"startLifetime":1.5,"startSize":0.05,"startColor":"#FFFFFF","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":["#FFFFFF","#FFD700"]}
```

**传送门/漩涡（Portal）** — Donut 环 + 旋转 + 紫青渐变
```
particle.create {"name":"FX_Portal","material":"Default-Particle","shapeType":"Donut","rateOverTime":60,"startSpeed":1,"startLifetime":2,"startSize":0.4,"startColor":"#8A2BE2","loop":true,"playOnAwake":true}
particle.set {"instanceId":"<id>","module":"rotationOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"rotationOverLifetime","property":"z","value":[[0,0],[1,360]]}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"colorOverLifetime","property":"color","value":[[0,"#8A2BE2"],[1,"#00FFFF"]]}
particle.set {"instanceId":"<id>","module":"noise","property":"enabled","value":true}
particle.set {"instanceId":"<id>","module":"noise","property":"strength","value":0.3}
```

**验证闭环**: `particle.create` → `particle.set`（Edit 预览 `particle.simulate`）→ 进 Play 实测 →
`camera.screenshot` 看效果 → 迭代 → `scene.save_prefab`。
