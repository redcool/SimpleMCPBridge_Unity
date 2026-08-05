# SESSION_MEMORY — 会话记忆

> 用途:记录最近工作状态(上次做到哪、下一步做什么、关键决策、最近踩坑)。
> 与 AGENTS.md(静态知识库)互补:本文件是**动态便签**,定期把已验证的结论**迁移**到 AGENTS.md。
> 每次会话结束更新一次,不要每次工具调用都改。

## 当前目标

- **UPM 包化已完成迁移**:包位于 `H:\ai_works\SimpleMCPBridge`(独立 git 仓库),项目 `Packages/manifest.json` 用 `file:../../SimpleMCPBridge` 引用
- 连接架构咨询已闭环:确认**保持短连接现状**(agent 侧走 `/rpc` HTTP 短连接,不切 SSE/WS 长连接)

## 完成项 (Completed)

- [x] 2026-08 **UPM 包迁移(完成)** — 
  - `package.json` 创建(`com.simplemcpbridge` v1.0.0,unity 2022.3)
  - 目录复制到 `H:\ai_works\SimpleMCPBridge`(含 .git/.meta),manifest 加 `file:` 引用,源目录清空
  - Unity `Client.Resolve()` 触发包解析 → packages-lock.json 记录 `file:../../SimpleMCPBridge`
  - 验证:SimpleMCPBridge.dll 从新位置编译(17:10:39)、Editor bridge 重连(89 工具,新 bridgeId)、**0 Error**
  - 注意:迁移时源目录外壳被 Unity 锁定(内容已清空),Unity 重启后自动消失,无影响
  - **UPM 包也支持直接拷贝到 Assets/ 使用**——Runtime/Editor/asmdef/Resources 结构兼容,package.json 不生效但不影响使用
- [x] 2026-08 **BridgeConfig 重构(UPM 友好)** — `Runtime/Config/BridgeConfig.cs` 重写:
  - Editor → 项目 `Assets/SimpleMCPBridge-config/bridge-config.json`(可写,首次自动从 Resources 拷贝,已存在不覆盖)
  - Player → `persistentDataPath/bridge-config.json`(原逻辑保留)
  - 兜底 Resources 内嵌默认值;`EnsureConfigOnDevice` 创建时 `AssetDatabase.Refresh()`(非 Play 时)
  - 修掉旧 bug:旧「content changed 覆盖」逻辑会在用户改配置后覆盖回默认值
  - 验证:编译 0 Error 0 Warning(editor.get_console 确认);配置自动生成到新路径且带 Editor IP(10.0.46.244)
  - 已删除包根遗留 `bridge-config.json`(运行时不再读)
- [x] 2026-08 **移除 PowerUtilities 死引用** — 全仓 grep 仅 2 处注释提及(零代码使用),asmdef `references` 移除 `"PowerUtilities"`(SimpleMCPBridge.asmdef:5),实现真正独立;编译通过证明零依赖
- [x] 2026-08 **UPM 化调研结论** — 单 asmdef + `#if UNITY_EDITOR`(46 处)合法(UPM 不强制 Editor/Runtime 拆分 asmdef);「只加 package.json」不够,必须通过 manifest.json `file:` 引用才生效
- [x] 2026-08 **AGENTS.md NGUI 安装指向 UPM fork** — 方式 A 首选改为直接引用 fork 库 `https://github.com/redcool/ngui-upm.git`(已含 asmdef + package.json),manifest 加 git URL 或本地 clone 后 file: 引用;旧模板方式(upm_template/ 三步拷贝)降级为「针对未 UPM 化 tasharen/ngui 仓库」的备选;SimpleMCPBridge 只负责 NGUI_ON 检测入口(versionDefines + references + #if NGUI_ON 代码),不管 NGUI 包本体
- [x] 2026-08 **NGUI 工具集集成** — `ngui.get_texts` / `ngui.find` / `ngui.find_widgets`(Ngui 类别 count=3)
  - 根因:asmdef `references` 缺 `"NGUI"`(versionDefines 只定义符号不加引用)→ 已修复
  - 验证:工具注册正常、扫描 UITexture+UILabel、滤镜工作;0 Error
  - 文档:AGENTS.md 已含 NGUI 安装两种方式(A: UPM 包化; B: 源码 + 手动 NGUI_ON)
- [x] 2026-08 **TMP 条件编译** — asmdef 加 `"Unity.TextMeshPro"` 软引用,`TMPTextType`/`GetTMPInputFieldType`/`GetTMPDropdownType` 改编译期 `typeof(TMPro.X)`(删字符串反射)
  - 验证:`ui.get_texts` 读 3 个 TMP 文本通过
- [x] 2026-08 **Multi-Bridge 路由核实 + 文档** — last-registration-wins(index.ts:763-765)、断开 failover(893-908)、bridgeId=GUID 每连接重生(BridgeClient.cs:43)、clientPort 随机
- [x] 2026-08 **工具来源标注** — `getMergedTools()` 给每个工具 description 加 `[bridge: ip:port (id前8位)]` 前缀(取 toolToBridge 实际路由);MCP tools/list 与 /rpc tools/list 共用
  - 验证:双 bridge(Editor + Android 10.0.46.187)标注正确
- [x] 2026-08 **game.get_delta 持续监测升级** — `GameHandler.TickWatch()` 每 10 帧(~167ms)检测 `_watchConfigs` → 变化入 `_watchChanges` 缓存;`get_delta` 读缓存 + 读后清空;`MCPBridge.Update`/`InstanceUpdate` 挂接 TickWatch
  - 验证:改值等 2s 不轮询 → delta 捕捉到;读后二次为空;回环变化(改回原值)也捕捉
- [x] 2026-08 **连接架构咨询(用户问题)** — ① bridge=ClientWebSocket→node ws 服务器(NetWebSocketClient, BridgeClient.cs:135; WebSocketServer, index.ts:679);工具调用是 server→bridge 经 WS 长连接 JSON-RPC(callBridge→ws.send, index.ts:448),agent 经 /rpc HTTP 短连接(index.ts:1250) ② bridge 侧 RPC 在长连接上,agent 侧是短连接 ③ 长连接需 opencode MCP 配置指向 `/sse`+`/mcp`(SSEServerTransport, index.ts:979-1005, 当前无 server→client 主动推送)
- [x] 2026-08 **uitk.\* 工具集实现** — `uitk.get_panels/get_texts/find/get_elements/click/set_value`(类别 `Uitk`,6 工具)
  - 新文件: `Runtime/Tools/UIToolkitAnalysisTools.cs`(纯工具类)+ `Runtime/Handlers/UIToolkitHandler.cs`([MCPToolClass]);MCPMethodConst.cs 加 6 常量
  - **无 asmdef 改动**(UnityEngine.UIElements 引擎内置,无条件编译);类别自动推导 `Uitk`,无需 Category 覆写
  - 关键实现: click = pooled MouseDown/MouseUp + SendEvent(不手动派发 ClickEvent/Capture,Clickable 自动生成);坐标模式 = 归一化坐标映射到 root.worldBound(与 get_elements/find 的 normalizedRect 同原点);坐标归一化除以 root.worldBound(缩放无关)
- [x] 2026-08 **uitk.\* 工具 Unity 实测通过(Play Mode + UIDocument)** —
  - 测试场景: `H:\ai_works\TestAIMcpPrj\Assets\Tests\TestUIDoc.uxml`(Button TestButton + Label)+ `TestUIDoc.cs`(Start 里 `bt.clicked += Debug.Log`,仅在 Play Mode 注册)
  - `uitk.click` 元素模式(path)→ `clicked` 回调触发 ✅;重复点击各触发恰好一次 ✅;坐标模式(x/y 归一化中心)→ 触发 ✅;**无双触发**
  - 修复 2 个 bug: ① `editor.eval` 的 Mono.CSharp refContainer 补 `UnityEngine.*` 模块程序集(否则 UIElements 类型无法解析,语句块静默编译失败)② `ResolveElement` 支持带 PanelSettings 前缀的全路径(多起点 + 跳过同名段)
  - Edit Mode 下面板即 attached 可读可 set_value(无布局无 rect);交互(click/坐标)需 Play Mode
- [x] 2026-08 **四种 click 路径全实测(Play Mode 驱动 UITK TestButton)** —
  - `uitk.click`(原生 SendEvent)→ ✅;`input.mouse_click`(Win32 分支: WarpCursorPosition+SendInput 真实 OS 级)→ ✅
  - `input.touch` tap(InputSystem 虚拟 Touchscreen, QueueStateEvent)→ ✅;`input.click_screen`(EventSystem ExecuteHierarchy)→ ✅
  - 每条路径 clicked 回调各恰好触发一次,console 栈完整(TestUIDoc lambda 为栈顶)
  - **坐标翻转**: uitk.find 的 center 是左上原点(UI Toolkit 空间),input.mouse_click/touch 用左下原点(Screen 空间)→ y 需 `1 - y_uitk`
  - **意外发现**: click_screen 在本场景能驱动 UITK —— 因为 `EventSystem/Default Panel Settings` 挂了 PanelEventHandler+PanelRaycaster 桥接组件(RaycastAll 命中 + ExecuteEvents 指针事件翻译成 UITK 事件)。README 原「click_screen 不驱动 UITK」表述已修正为「取决于场景是否桥接」
- [x] 2026-08 **UITK 事件桥接 + 无 UITK_ON 决策(用户拍板)** —
  - UITK 交互事件**依赖 EventSystem 桥接**(PanelEventHandler + PanelRaycaster),无桥接 → UITK 无事件(input.click_screen 射不到,uitk.click 不受影响)
  - **不加 `UITK_ON` 条件编译**: uielements 是内置模块非可选包(2022.3 必有,桌面平台官方不支持移除),条件恒真;客户 runtime 不用 UITK 时用 `tools.disable ["Uitk"]` 裁剪即可,做好分类(类别 `Uitk`)就够
  - 已同步: AGENTS.md(uGUI/NGUI/UITK 独立工具集段落)、README.md(UITK 工具块)、本文件

## 进行中 (Active)

- (无重大进行项)

## 下一步 (Next Move)

- **待办: 无**(uitk 6 工具已全部实测验证)
- **可选优化**:
  - package.json 后续补充 `dependencies` 声明(如 com.unity.inputsystem 1.14.2 / com.unity.textmeshpro / com.tasharen.ngui),让 Unity 自动解析
  - CHANGELOG.md / LICENSE 文件补充(发布到团队前的规范)
  - 如需跨项目分发,仓库推送到远程(git remote)
- 可选:agent 侧长连接需先给服务器 SSE 加 server→client 推送事件流,再改 opencode 配置(当前 `unityMCP` 指向 `http://127.0.0.1:8082/mcp` 是**另一个 MCP**,不是 SimpleMCPBridge,勿动)
- 定期把本文件已验证结论迁移到 AGENTS.md

## 关键决策记录 (Decisions)

1. **agent 侧维持短连接 `/rpc`**,不切长连接 —— 简单可靠、无状态、Unity 重启不影响 agent;长连接曾导致 Unity 重开后 opencode 断连需重开、记忆丢失
2. **opencode 配置 8082 是另一个 MCP**,与 SimpleMCPBridge 无关,禁止修改
3. **记忆用文件持久化**(本文件)—— 因长连接断连会丢 agent 会话记忆,改用磁盘文件接续
4. **Memory 文件与 AGENTS.md 分工**:本文件=动态进度快照;AGENTS.md=静态知识库(已验证结论迁移过去,避免 AGENTS.md 膨胀)
5. **UPM 包配置策略**:Editor 读项目 `Assets/SimpleMCPBridge-config/`(可写),Player 读 persistentDataPath(可写),Resources 只做默认值兜底 —— 因为 UPM 包内文件只读,不能作为用户配置唯一入口
6. **单 asmdef + #if UNITY_EDITOR 保持不拆**:UPM 允许;8 处 UnityEditor 引用全部条件编译保护,已验证
7. **类别状态不加 EditorPrefs 持久化**:裁剪是任务级临时状态(非用户偏好);EditorPrefs 全局会跨项目污染、陈旧状态破坏「装包即全开」契约;domain reload 重置回全开是安全默认。若未来出现「长任务重编译致 token 回升」痛点,再上「项目级 EditorPrefs + persisted 标注」方案(行为已写入 AGENTS.md/README.md)

## 最近踩坑 (Recents Pitfalls)

> 已固化到 AGENTS.md Known Issues 的:MPEG4Writer 音频轨道(1)、BuildJsonObject 字符串预引号(2)、InputSystem.Update 阻塞(3)、服务器需可见 cmd 窗口(5)、多 Bridge 路由 Editor(6)、同内容 AB 只能加载一次(7)

- NGUI 符号定义 ≠ 类型可解析:versionDefines 加 `NGUI_ON` 只是定义符号,还必须 asmdef `references` 含 `"NGUI"` 才能解析类型,否则 CS0246
- 长连接隐患:Unity 重开 → opencode MCP 断连 → 必须重开 opencode → 会话记忆丢失(已用本文件规避)
- `scene.*` 组件操作参数名是 `componentType` 不是 `component`(例外:game.wait 用 `component`),已固化 AGENTS.md
- **编译错误会静默阻断 Play Mode**:`scene.enter_play_mode` 返回 `success:false` + 连续轮询 `get_play_mode` 全是 `edit`,第一反应是查编译——editor.get_console 的最近 50 条里可能全是 Log(编译错误在更早位置),要直接搜 `error CS` 或查 `Editor.log`。本会话 CS1061 卡了 Play Mode 一整天
- **`IPanel.GetTopElementUnderPointer` 在本 Unity 2022.3 patch 不存在**(CS1061):`IPanel` 接口没有该方法;公共命中测试用 `panel.Pick(position)`(已用于 uitk.click 的 gate 检查)
- **uitk.click 合成点击的隐藏 gate**:Clickable 的 `clicked` 只在 ProcessUpEvent 里经 `ContainsPointer(pointerId)` 触发,该缓存仅在 ①事件 `triggeredByOS=true`(只有 `MouseDownEvent/MouseUpEvent.GetPooled(Event)` 工厂会设置;PointerDown/Up 的 GetPooled 重载不会)且 ②坐标落在 `panel.visualTree.layout`(panel 空间)内才写入。两条任一不满足 → 静默 no-op 不触发回调。修法:用 `GetPooled(Event)` + `el.worldBound.center`(panel 空间)+ 布局内 clamp + `panel.Pick` 前置检查

## 相关文件索引

| 路径 | 说明 |
|------|------|
| `H:\ai_works\SimpleMcpServer\src\index.ts` | WS 服务器(679)、/rpc(1250)、/sse+/mcp(979-1005)、来源标注(268-294)、last-wins(754-765)、failover(879-917) |
| `Runtime/BridgeClient.cs` | NetWebSocketClient(135)、BridgeId=GUID(43) |
| `Runtime/Handlers/GameHandler.cs` | TickWatch(每10帧缓存)、get_delta 读缓存清空 |
| `Runtime/MCPBridge.cs` | Update/InstanceUpdate 挂接 TickWatch |
| `SimpleMCPBridge.asmdef` | references 含 NGUI+Unity.TextMeshPro;versionDefines NGUI_ON+TEXT_MESH_PRO_ON |
| `C:\Users\Admin\.config\opencode\opencode.json` | unityMCP=8082/mcp(**另一个 MCP,勿动**) |
