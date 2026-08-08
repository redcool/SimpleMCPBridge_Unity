# SESSION_MEMORY — 会话记忆

> 用途:记录最近工作状态(上次做到哪、下一步做什么、关键决策、最近踩坑)。
> 与 AGENTS.md(静态知识库)互补:本文件是**动态便签**,定期把已验证的结论**迁移**到 AGENTS.md。
> 每次会话结束更新一次,不要每次工具调用都改。

## 当前目标

- **UPM 包化已完成迁移**:包位于 `H:\ai_works\SimpleMCPBridge`(独立 git 仓库),项目 `Packages/manifest.json` 用 `file:../../SimpleMCPBridge` 引用
- 连接架构咨询已闭环:确认**保持短连接现状**(agent 侧走 `/rpc` HTTP 短连接,不切 SSE/WS 长连接)
- ✅ 本轮（2026-08）已完成:黑名单/白名单调用权限 + CRUD 补齐 + 服务端/桥侧安全与性能修复 + 全部活体验证 + 文档补写（AGENTS/README/Server README/本文件）——待用户提交
- ✅ 本轮（2026-08）Android 活体测试轮已完成:URP DebugUpdater 崩溃修复(UrpDebugGuard) + Android 91 工具全量实测(A/B/C 类全过) + il2cpp setter 裁剪根因确认(Known Issue #11) + Bridge ID 每连接变化文档化——待用户提交

## 完成项 (Completed)

- [x] 2026-08 **URP DebugUpdater 崩溃修复（UrpDebugGuard, Known Issue #10）** — 新文件 `Runtime/UrpDebugGuard.cs`: `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 设 `DebugManager.instance.enableRuntimeUI=false` → DebugUpdater(AfterSceneLoad 创建)永不被创建 → EnhancedTouch 历史不变量不再被虚拟 Touchscreen 合成事件破坏 → 消除 SIGSEGV(fault addr 0x20);仅公共 API,不动注入逻辑;asmdef 加 `Unity.RenderPipelines.Core.Runtime` 软引用 + `RENDER_PIPELINES_CORE_ON` versionDefine
  - 验证(Android 真机新包): input.touch start/move/move/end + input.swipe + input.mouse_click 全跑,bridge 存活 1500s+ 零崩溃(logcat 无 SIGSEGV/无 "Must have current touch record")
- [x] 2026-08 **Android 91 工具全量活体测试（A/B/C 类, 10.0.46.187）** — A 类 input.* + game.* 全过(input.action WASD 驱动 Player、game.set_time_scale/wait/do_sequence/sequence_status/watch+get_delta/batch 2:1 RTT/spatial/entities/animator_state);B 类 scene.* + physics.* 全过(get_objects/by_type/by_path/by_tag、create/delete/duplicate/rename/set_active/set_parent/add/remove_component/set_component_property(Transform OK)/set_transform/call_component_method/load_scene(instanceId 失效警告符合预期) + box/sphere_cast/overlap_sphere/overlap_box);C 类 recording.reset/status + camera.screenshot(savePath 必需,落盘 428KB)
  - 参数坑(实测确认): call_component_method 用 `args` 命名映射且**必须给全重载参数**(AddForce 需 force+mode); box_cast/overlap_box 参数名是 `halfExtents` 不是 size; rename 用 `name`; camera.screenshot 需 `savePath`; game.set_time_scale 用 `value`
- [x] 2026-08 **il2cpp setter 裁剪根因确认（Known Issue #11）** — `set_component_property` 对 Rigidbody.mass/drag 报 not found 但 get 能读:il2cpp Managed Stripping 裁剪工程未引用的 setter → `CanWrite=false`(propertyCount Android 9 vs Editor 51)。验证实验:PlayerMove.Start 显式 `rb.mass=5f` → 新包 mass 初值 5 且 set 成功(drag 仍失败);验证后 PlayerMove.cs 已还原
  - SceneHandler.SetComponentProperty 已加 il2cpp 防御性枚举回退分支(GetProperties + OrdinalIgnoreCase + CanWrite + 非索引器)——对"属性可写但按名查找失败"有效;对 setter 被裁剪(CanWrite=false)无效属预期,需工程侧保留 setter
- [x] 2026-08 **Bridge ID 每连接变化文档化** — AGENTS.md `## BridgeId per Connection` 下补说明:ID 每重连新生成(非持久),旧 ID 立即失效报 `Bridge '<id>' not found`;调用前先 GET /health 或 bridge.list 取最新 ID,勿缓存
- [x] 2026-08 **scene.call_component_method 黑名单/白名单（文件化）** — BridgeConfig.ConfigData 加 `methodBlocklist`/`methodAllowlist`(SanitizeList + 静态缓存);SceneHandler 四步门:代码默认黑名单(6 项,destroy/destroyimmediate/destroyobject/quit/quitimmediate/disconnect,不可移除)→ 配置追加黑名单 → 命名空间守卫(SimpleMCPBridge)→ 白名单模式(空=关闭,非空=只放行命中项,支持 `MethodName`/`TypeName.MethodName`,OrdinalIgnoreCase);`EnsureMethodAccessCache` 懒缓存重启生效;Resources/bridge-config.json 加两个空数组
  - 验证(活体):配置追加 `Camera.ResetAspect` → blocked ✅;白名单模式放行 ✅;代码默认 Destroy 仍 blocked ✅;测试项目配置已还原
- [x] 2026-08 **CRUD 补齐（12 工具,ora-1 P0/P1 全覆盖）** — P0 `scene.load_scene`(Editor 打开资产/Play 与 built 用 SceneManager,single/additive,异步;⚠ 加载后 instanceIds 全失效需重取层级)、`scene.save_prefab`(PrefabUtility.SaveAsPrefabAsset,覆盖式);新 PlayerPrefsHandler(`playerprefs.get_all/get/set/delete` — Unity 无 key 枚举 API,会话级 key 注册表,set/get 时登记);AssetHandler 4 新工具 `asset.create/delete/rename/move`(delete 带 find_references 引用预检 + force 门);UIToolkitHandler 2 新工具 `uitk.create_element/remove_element`(运行时元素,**不持久**,面板刷新即毁/即恢复)
  - 缓存上限:`_typeCache` 256、`s_instanceIdCache` 512(clear-on-overflow,防无界增长)
- [x] 2026-08 **服务端安全/加固（fix-2,ora-2 F1/F3/非致命项）** — `allowedIps` 白名单(默认 `["127.0.0.1","::1"]`,`isIpAllowed` 含 `::ffff:` 归一化)**只 gate `/rpc` `/sse` `/mcp`**,WS 与 `/ab` 不受限(桥走 10.0.46.244 连接不受影响);config.json 缺失→首次启动自动 copy template;maxPayload 4MB;30s ping/pong `isAlive` → 超时 `ws.terminate()`;日志脱敏(工具名+参数长度,错误路径保留详情);`npx tsc --noEmit` + build 零错
  - 用户拍板:**F2 `/ab localpath` 任意文件读取保留不修**(只读、用户认可风险);F3 config.json 本地 gitignored 不入库,template 已提交,真实 key 从 git 历史轮换(改环境变量 `LLM_API_KEY`)
- [x] 2026-08 **桥侧性能/可靠性（fix-3）** — DrainQueue 每帧 12 条预算(BridgeClient:214-229,Disconnect 仍全清防卡死);日志脱敏 DescribeMessage(类型+长度,错误路径保留);game.watch `_watchPropertyCache`(path|component|property → Component/MemberInfo,销毁重解析,上限 MaxWatchEntries*2);树截断 SceneObjectTools 深度24/总节点1500、uitk 1000、项目树 2000,均带 `"truncated": true`
- [x] 2026-08 **gamepad rumble（触发）** — input.gamepad action=rumble(lowFreq/highFreq/duration) + 每帧 TickRumble 自动归零 + ResetAll 清震动;感知走 game.watch 既有通道不建新工具;传感器模拟(加速度计/陀螺仪)评估后暂缓(无目标游戏需求+真机虚拟传感器未验证)
- [x] 2026-08 **修复 scene.load_scene 真 bug** — 首次报 `Scene 'Assets/Scenes/X.unity' not found in project`:ResolveScenePath 曾把完整路径当名字塞 FindAssets;改为路径入参先 `AssetDatabase.LoadAssetAtPath<SceneAsset>` 直接校验、FindAssets 只用文件名,修复后活体通过
- [x] 2026-08 **文档补齐（本轮）** — AGENTS.md 工具表 101→117(加 12 新工具 + castle.click_building 补漏 + `assetbundle.build_bundle`→`asset.build_bundle` 改名修正,Directory Refs 加 PlayerPrefsHandler/BuildingHandler);桥 README 计数 101+→116(场景 26→28、资源 3→7、UITK 6→8、新增 PlayerPrefs 段 4、城堡段 1);Server README 241→286(可用工具动态注册说明、配置节重写 allowedIps/encryption/evalEnabled/llm、技术说明 +5 条、故障排查 +403 条)
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

- (无重大进行项——本轮修复全部完成并验证，文档已补写;Android 活体测试轮也全部完成并验证)

## 下一步 (Next Move)

- **待办: 两个仓库提交由用户执行**(桥 `H:\ai_works\SimpleMCPBridge` + 服务端 `H:\ai_works\SimpleMcpServer` 均故意丢脏树)
  - 本轮(Android 测试轮)桥侧未提交改动:`Runtime/UrpDebugGuard.cs`(新文件,URP 崩溃防护)、`Runtime/Handlers/SceneHandler.cs`(SetComponentProperty il2cpp 枚举回退分支)、`SimpleMCPBridge.asmdef`(Core.Runtime 软引用 + versionDefine)、`AGENTS.md`(Known Issue #10/#11 + BridgeId 变化说明)
  - 测试工程 `TestAIMcpPrj` PlayerMove.cs 验证代码已还原(无残留改动)
- **提醒: 轮换 git 历史残留的 LLM API key**——真实 key 曾提交到 SimpleMcpServer git 历史(如 98d601e),config.json 已被 .gitignore 排除;改用环境变量 `LLM_API_KEY` 覆盖(index.ts 已支持)后轮换
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
8. **call_component_method 权限合并语义(用户拍板)**:代码默认黑名单(6 项)不可移除 + 配置追加黑名单双重拦截始终优先,白名单只是最后一道放行门槛;命名空间守卫(SimpleMCPBridge)留代码不可配置 —— 宁可保守不可绕过
9. **allowedIps 只 gate HTTP 端点**:白名单默认 `["127.0.0.1","::1"]` 仅本机,只拦 `/rpc` `/sse` `/mcp`,**不 gate** WS 与 `/ab` —— 桥走局域网 WS 连接不受影响(否则 Editor/Android 双 bridge 局域网调用会全断)
10. **F2 `/ab localpath` 任意文件读取保留不修(用户拍板)**:只读不改写,风险已告知并接受;文档未隐藏该端点
11. **F3 config.json 本地不入库**:template 提交、config 缺失时自动复制;真实 key 不进 git,用环境变量覆盖 —— 防御「提交真实凭据」类事故再发
12. **grill-me / grill-with-docs 两个 skill 的取舍(评估结论)**:grill-me(纯访谈 prompt,一次一问+推荐答案,可查代码)保留备用 —— 新设计/新计划压力测试时说「grill me」即用;grill-with-docs(术语管理 CONTEXT.md + ADR 归档 + 代码交叉验证)**本项目不用** —— 其 ADR 产物已被本文件「关键决策记录」节覆盖、术语已被 AGENTS.md 覆盖,且会新建 CONTEXT.md/docs/adr 体系与现有双文档(AGENTS.md + 本文件)重叠冲突;若某天要正式引入 CONTEXT/ADR 体系再重划文档边界

## 最近踩坑 (Recents Pitfalls)

> 已固化到 AGENTS.md Known Issues 的:MPEG4Writer 音频轨道(1)、BuildJsonObject 字符串预引号(2)、InputSystem.Update 阻塞(3)、服务器需可见 cmd 窗口(5)、多 Bridge 路由 Editor(6)、同内容 AB 只能加载一次(7)、uitk.click 隐藏 gate(8)、编译错误静默阻断 Play Mode(9)、URP DebugUpdater+EnhancedTouch SIGSEGV(10)、il2cpp setter 裁剪(11)

- **Bridge ID 每次重连都会变**:ID 每连接新生成(非持久),重连后旧 ID 立即失效报 `Bridge '<id>' not found`。调用前先 `GET /health` 或 `bridge.list` 取最新 ID,勿缓存/勿手写(本会话因过期 ID 多次踩坑)
- **il2cpp setter 裁剪使 set_component_property 报 not found(非桥 bug)**:Android 上对工程未引用的属性(如 Rigidbody.mass/drag)报 not found,但 get 侧能读到值;Editor 同调用成功。诊断:对比两端 propertyCount(Android 9 vs Editor 51)。解决:工程侧引用一次 setter(`rb.mass = rb.mass`)/降低 stripping/link.xml 保留
- **call_component_method 重载参数必须给全**:`args` 命名映射 + 完整重载参数(如 AddForce 需 `force`+`mode`,缺 mode 报 "Missing required argument")。box_cast/overlap_box 参数名是 `halfExtents`(不是 size);camera.screenshot 需 `savePath`;game.set_time_scale 用 `value`

- NGUI 符号定义 ≠ 类型可解析:versionDefines 加 `NGUI_ON` 只是定义符号,还必须 asmdef `references` 含 `"NGUI"` 才能解析类型,否则 CS0246
- 长连接隐患:Unity 重开 → opencode MCP 断连 → 必须重开 opencode → 会话记忆丢失(已用本文件规避)
- `scene.*` 组件操作参数名是 `componentType` 不是 `component`(例外:game.wait 用 `component`),已固化 AGENTS.md
- **编译错误会静默阻断 Play Mode**:`scene.enter_play_mode` 返回 `success:false` + 连续轮询 `get_play_mode` 全是 `edit`,第一反应是查编译——editor.get_console 的最近 50 条里可能全是 Log(编译错误在更早位置),要直接搜 `error CS` 或查 `Editor.log`。本会话 CS1061 卡了 Play Mode 一整天
- **`IPanel.GetTopElementUnderPointer` 在本 Unity 2022.3 patch 不存在**(CS1061):`IPanel` 接口没有该方法;公共命中测试用 `panel.Pick(position)`(已用于 uitk.click 的 gate 检查)
- **uitk.click 合成点击的隐藏 gate**:Clickable 的 `clicked` 只在 ProcessUpEvent 里经 `ContainsPointer(pointerId)` 触发,该缓存仅在 ①事件 `triggeredByOS=true`(只有 `MouseDownEvent/MouseUpEvent.GetPooled(Event)` 工厂会设置;PointerDown/Up 的 GetPooled 重载不会)且 ②坐标落在 `panel.visualTree.layout`(panel 空间)内才写入。两条任一不满足 → 静默 no-op 不触发回调。修法:用 `GetPooled(Event)` + `el.worldBound.center`(panel 空间)+ 布局内 clamp + `panel.Pick` 前置检查
- **scene.load_scene 首次报 "not found in project"**:ResolveScenePath 曾把完整路径当名字塞进 FindAssets 名称过滤器 → 路径入参应先 `LoadAssetAtPath<SceneAsset>` 直接校验,FindAssets 只用文件名
- **服务端 HTTP 403 排查**:先看 config.json 的 `allowedIps` 是否含来源 IP(默认仅 127.0.0.1/::1);测试调用从局域网 IP 打 `/rpc` 会 403 属预期,走 127.0.0.1 或加白名单
- **DrainQueue 需帧预算**:桥队列在慢工具(树截断/大响应)时可能积压,Disconnect 必须全清(否则卡死),常规 Drain 按帧预算(12 条)防一帧卡爆

## 相关文件索引

| 路径 | 说明 |
|------|------|
| `H:\ai_works\SimpleMcpServer\src\index.ts` | WS 服务器(679)、/rpc(1250)、/sse+/mcp(979-1005)、来源标注(268-294)、last-wins(754-765)、failover(879-917)、allowedIps gate(118-182)、template 自动复制(150-161)、maxPayload 4MB(713)、pong 跟踪(719-736) |
| `H:\ai_works\SimpleMcpServer\config.json.template` | 完整配置模板(allowedIps 默认本机 + llm 段 + evalEnabled) |
| `Runtime/BridgeClient.cs` | NetWebSocketClient(135)、BridgeId=GUID(43)、DrainQueue 帧预算(214-229) |
| `Runtime/UrpDebugGuard.cs` | (新,2026-08)URP DebugUpdater 崩溃防护 —— BeforeSceneLoad 关 enableRuntimeUI,Known Issue #10 |
| `Runtime/Config/BridgeConfig.cs` | ConfigData.methodBlocklist/methodAllowlist + SanitizeList + 静态缓存 |
| `Runtime/Handlers/SceneHandler.cs` | LoadScene(1004)/ResolveScenePath(已修 bug)、SavePrefab(1345)、方法四重门(EnsureMethodAccessCache)、SetComponentProperty il2cpp 枚举回退分支(381-396) |
| `Runtime/Handlers/PlayerPrefsHandler.cs` | playerprefs.* 4 工具(会话级 key 注册表) |
| `Runtime/Handlers/AssetHandler.cs` | asset CRUD + build_bundle(Editor,#if UNITY_EDITOR) |
| `Runtime/Handlers/UIToolkitHandler.cs` | uitk.create_element(320)/remove_element(400) |
| `Runtime/Handlers/GameHandler.cs` | TickWatch(每10帧缓存)、get_delta 读缓存清空、_watchPropertyCache |
| `Runtime/MCPBridge.cs` | Update/InstanceUpdate 挂接 TickWatch |
| `SimpleMCPBridge.asmdef` | references 含 NGUI+Unity.TextMeshPro;versionDefines NGUI_ON+TEXT_MESH_PRO_ON |
| `C:\Users\Admin\.config\opencode\opencode.json` | unityMCP=8082/mcp(**另一个 MCP,勿动**) |
