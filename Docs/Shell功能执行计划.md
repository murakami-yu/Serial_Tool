# Shell 功能执行计划：终端仿真 / SSH / 多会话（Xshell/MobaXterm 方向）

> 批次代号：**V1.4**（终端/Shell 功能线，与 V1.3「发送历史/热插拔」并行不冲突）
> 日期：2026-09-19 ｜ 状态：**计划主体完成：M0-M4（Telnet + 本地终端）全部实现（107 单测全绿；M1 回环/M2 SSH/M3 多会话/M4 真机验收待人工清单在 §3.3/§4.3/§5.3/§6.1）；SFTP 与端口转发按「可选按需」待需求确认后独立批次**
> 关联：[Serial_Tool_Design.md](Serial_Tool_Design.md) ｜ [README 路线图](../README.md) ｜ [功能增强执行计划](功能增强执行计划.md)

## 0. 完成度跟踪（每完成一个阶段更新此表）

| 里程碑 | flag（验收即达成） | 状态 | 完成日期 |
| --- | --- | --- | --- |
| M0 地基 | 计划文档落定 + 依赖选型验证通过（XTerm.NET 2.0.2 API 冒烟） | ✅ 完成 | 2026-09-18 |
| M1 串口终端 | 连上串口设备 → 终端窗渲染 VT 输出（颜色/清屏/光标/滚回）→ 键入即时回显交互 | ✅ 完成（代码+构建+冒烟；TCP 回环/真机项待人工确认） | 2026-09-18 |
| M2 SSH 会话 | 连真实 Linux 主机交互 shell（密码/私钥、host key 首次确认、resize 通知） | ✅ 代码完成（构建+98 单测+冒烟；真机验收待人工） | 2026-09-18 |
| M3 多会话 | 并发 N 个终端会话 + 标签页切换 + 保存的主机快速连接 | ✅ 代码完成（构建+98 单测+冒烟；多并发验收待人工） | 2026-09-19 |
| M4 扩展（可选） | Telnet / 本地终端 ConPTY / SFTP，按需逐项启动 | ✅ Telnet + 本地终端完成（107 单测+ConPTY 实测回显/resize/销毁）；SFTP/端口转发待需求确认 | 2026-09-19 |

## 1. 背景与选型（已定稿）

目标：为串口调试工具新增 Xshell/MobaXterm 式终端能力。约束：**不使用 WebView，打包 exe 保持零外部运行时依赖（自包含单文件）**。

| 层 | 选型 | 理由 | 外部依赖 |
| --- | --- | --- | --- |
| 终端仿真引擎 | **XTerm.NET 2.0.2**（NuGet，MIT，net10.0，零包依赖，纯托管单 DLL） | xterm.js 的 .NET 移植：VT100/ANSI 全量、256 色+RGB、CJK 宽字符/字素簇、主备缓冲+滚回、键盘/鼠标输入编码、Sixel/Kitty 图形；headless 设计自带渲染器 | 无（单文件发布内嵌） |
| 终端渲染 | **自写 WPF 控件 `TerminalView`** | 引擎无 UI 依赖，渲染层自己写（~千行）是本功能主要工作量；一次投入四处复用（串口/SSH/Telnet/本地终端） | 无 |
| SSH 协议 | **SSH.NET 2026.0.0**（NuGet，MIT，纯托管） | 密码/私钥认证、shell 通道+PTY resize、SFTP、端口转发；社区标准库 | 无（单文件发布内嵌） |
| 本地终端（M4） | ConPTY（Win10 1809+ 系统 API，P/Invoke） | OS 自带，无附带二进制 | 无（OS API） |

备选记录：XtermSharp（migueldeicaza）缺滚回/reflow，弃用；WebView2 + xterm.js 方案被「零外部依赖」约束否决。

## 2. 架构基线（所有阶段共同遵守）

```
                          ┌─ SerialBackend ──┐
IBusBackend._active ──────┼─ TcpBackend      │   现有：RX → ConcurrentQueue → FlushRx → RichTextBox
                          └─ SshBackend(M2新)┘   新增旁路：RX 原始字节 → TerminalView 字节泵 → XTerm.NET 引擎 → 自绘渲染
                                                     输入：KeyDown/IME → GenerateKeyInput/CharInput → 字节 → _active.Write
```

- **旁路不改主链**：终端是接收区的**附加视图**（同一字节流，两处消费）；`RawRxTap` 事件在读取线程抛出，TerminalView 用 `ConcurrentQueue` + UI 定时器（~16ms）泵给引擎，**引擎实例只在 UI 线程触碰**（线程模型干净，无锁）。
- **发送旁路**：终端键入走 `SendRawBytes()`（计数/波形与主发送一致，但不进接收区行缓冲、不写会话日志——远端回显已覆盖且避免每键一行刷屏）。
- **窗口生命周期**：照抄 ChartWindow 模式（打开=新建独立窗、X=销毁、位置尺寸记忆、主窗 Closing 前先真关）。两窗 2026-09-26 起不设 Owner（从属窗口被绑进主窗激活组，开窗/激活会强制带出主窗），主窗入口为按钮（见 §6 2026-09-26 记录）。
- **持久化套路**：`Config/ui_settings.json` 加键（ShowTerminalPanel/终端字体字号），`Config/known_hosts.json`（M2），照抄现有 `Load*/Save*` 模式。
- **测试边界**：纯逻辑（已知主机指纹解析、字节度量）进 xUnit；渲染/交互走构建 + 人工冒烟清单。

## 3. M1：WPF 终端控件 + 串口 VT 模式 ✅

**实现记录（2026-09-18）**：新增 `Controls/TerminalView.cs`（自绘渲染控件，~600 行）+ `TerminalWindow.xaml(.cs)`（独立窗，图表窗同款销毁/重建模式 + 滚回滚动条 + 未连接遮罩）；`MainViewModel` 增 `RawRxTap`（读取线程旁路）/`SendTerminalBytes`（终端发送链路）/`ShowTerminalPanel`（持久化）；工具条新增「终端」勾选项。构建 0 错误、92 单测全绿、配置开启终端窗启动 8 秒冒烟无崩溃。
已知取舍：双击选词暂缺（`Control.OnMouseDoubleClick` 在 `FrameworkElement` 不可用，待补 `ClickCount` 方案）；256 色中 16-255 按标称值渲染（OSC 改写调色板仅对 0-15 生效）；粘贴走 `Terminal.Paste`（括号粘贴模式）。

### 3.1 目标行为

- 工具条「终端」按钮（与时序图并列；2026-09-26 由勾选框改按钮）打开终端窗口（独立窗，默认贴主窗右侧；已开则带到前台，关闭走窗口 X）。
- 串口/TCP 连接后，终端窗把 RX 字节流按 VT100/xterm 解释渲染：颜色、清屏、光标移动、`htop`/`vim` 备屏切换。
- 键盘输入即时编码发往对端（回显由对端负责）；中文输入法可输入（经 `GenerateCharInput`），IME 候选框跟随终端光标（2026-09-25 修订，见 §6）。
- 滚回：鼠标滚轮 + 滚动条，滚到底自动跟随；备屏（vim 等）无滚回缓冲区——应用开鼠标跟踪则滚轮事件转发，未开则模拟 Up/Down 键发给应用（2026-09-25 修订，见 §6）；Shift+滚轮强制终端自处理（非备屏滚回 / 备屏模拟 PgUp/PgDn）。
- 复制粘贴：拖选 + 右键菜单（复制/粘贴/清屏/回到底部）；`Ctrl+Shift+C/V` 与 `Ctrl+V` 粘贴；`Ctrl+C` 遵循终端惯例——**有选区时复制（复制即清选区，对齐 Windows Terminal），无选区时发送 `^C`（\x03）中断对端**；应用开鼠标跟踪时 **Shift+左键强制文字选择、Shift+右键强制弹菜单**（2026-09-25 修订，见 §6）。
- 未连接时显示遮罩「未连接」；连接断开终端保留内容（只读）。

### 3.2 设计要点

**TerminalView**（`src/SerialTool.App/Controls/TerminalView.cs`，自定义 FrameworkElement）：

- 度量：字体 `Cascadia Mono, SimSun` 复合族（2026-09-19 用户对比截图后选定，演进见 §6：TNR 比例字体方案废弃）。格宽 = max(Cascadia Mono 数字 "0" 字宽≈8.2 DIP, SimSun 全角/2=7)——等宽字体单字符宽即格宽，中文两格 16.4 vs 字形 14 留白仅 2.4；行高 = max(两字体行高)×1.08；`Resize` 按视口尺寸反推行列（下限 80×24 起步按内容）。
- 渲染（`OnRender`）：背景 → 逐行同底色连续格合并为一个矩形；文字**逐格**绘制（为比例字体兼容性设计——字宽≠格宽时合并 run 会漂移；等宽字体下结果与合并等价），`FormattedText` 按 (文本,粗,斜,前景色) 缓存、DPI 变化清空；超格宽字符水平压缩兜底 → 光标（块/闪 530ms）→ 选区高亮。只画视口行（`Lines[YDisp + row]`），`Width==0` 续格跳过。
- 字节泵：`ConcurrentQueue<byte[]>`（任意线程投递）+ `DispatcherTimer 16ms` 出队 `terminal.Write` + 合帧失效（输出高峰每帧最多一次全视口重绘）。
- 输入：`KeyDown` → `GenerateKeyInput`（返回字节序列）→ `InputEmitted` 事件；`TextInput`（IME）→ `GenerateCharInput`；粘贴文本逐字节写入。
- 引擎回话（`DataReceived` 事件，如 DA 应答/DSR 光标报告）→ 同样走 `InputEmitted` 发对端。
- 滚回：自维护视口偏移（跟随底部= `Lines.Count - Rows`），滚轮步进 3 行；`BufferChanged` 备屏时钳到底。

**接线**（`MainViewModel` / `MainWindow`）：

- `[ObservableProperty] ShowTerminalPanel`（持久化）；`RawRxTap` 事件（`OnDataReceived` 内原样抛字节，读线程）；`SendRawBytes()`（复用现有 TX 计数与波形跳变，跳过 `_lines`/日志）。
- MainWindow 持 `_terminalWindow`，模式与图表窗完全一致（含 Closing 顺序坑）。

### 3.3 验收标准（flag M1 达成条件）

- [x] 构建 0 错误、既有 xUnit 全绿（92/92）。
- [x] 勾选「终端」开窗、取消/X 关窗、重开位置记忆（冒烟：带终端窗启动 8s 无崩溃；关闭重开与位置记忆逻辑与图表窗同模式）。
- [ ] TCP 回环（`nc`/自写回显服务）下发 ANSI 彩色序列：颜色/光标/清屏正确渲染 —— **待人工确认**。
- [ ] 键入字符 + Enter 即时回显（对端回显路径）—— **待人工确认**。
- [ ] 滚轮滚动滚回、滚到底恢复跟随；拖选复制、粘贴可用 —— **待人工确认**。
- [ ] 串口接真实设备（ESP32/Linux 板）验证登录提示符与交互 —— **待接设备人工确认**。

### 3.4 测试

- 渲染/交互为 App 层：构建通过 + 上节人工冒烟清单。
- 引擎行为由 XTerm.NET 上游测试覆盖（其 CI 含 wcwidth 表回放一致性测试）。

## 4. M2：SSH 远程会话 ✅（代码完成，真机验收待人工）

**实现记录（2026-09-18）**：`Backends/Ssh/SshBackend.cs`（SSH.NET 2026.0.0：xterm-256color PTY、密码/私钥（带口令）认证、10s 连接超时、指纹被拒→明确异常）+ `KnownHostsStore.cs`（TOFU 纯逻辑，6 单测）；`HostKeyConfirmWindow`（首次/指纹变更两态确认，同步模态）；连接方式下拉新增 SSH（主机/端口/用户名/认证方式/密码或私钥+口令，凭据不落盘，其余持久化）；`TerminalView.Resized → NotifyTerminalResized → ChangeWindowSize`（远端 `stty size` 跟随）。
**环境备忘**：本机 `dotnet restore` 对 nuget.org CDN 下载 SSH.NET nupkg 反复 TLS 失败（curl 正常）——已用 curl 将 SSH.NET 2026.0.0 + BouncyCastle.Cryptography 2.7.0 + Logging.Abstractions 8.0.3 手工铺进 `~/.nuget/packages`（含 .sha512/.nupkg.metadata），还原命中全局缓存零网络。其他机器首次还原若遇同问题照此处理。

### 4.1 目标行为

- 连接方式下拉新增「SSH」：主机/端口(22)/用户名/认证（密码 or 私钥文件+口令）。
- 首次连接弹 host key 指纹确认（TOFU：SHA256 Base64 展示，接受后写 `Config/known_hosts.json`；变更=红色警告拒绝直连）。
- 交互 shell：终端窗复用 M1 控件；窗口 resize → `channel.SendWindowChange`；`TitleChanged` → 窗口标题。
- 断线：终端保留现场 + 状态栏提示；错误进既有 `ErrorOccurred` 链路。

### 4.2 设计要点

- `SshBackend : IBusBackend`（Backends 项目）：`Connect` 内起读线程泵 shell 流 → `DataReceived`；`Write` → 流写；密钥用 `PrivateKeyFile`（OpenSSH/PEM，**不支持 .ppk**，UI 注明）。
- `Terminal` 构造参数 `Cols/Rows` 初值取 M1 控件度量；`Resized`/控件 resize → 后端回调转发。
- 认证凭据**不持久化**（仅记住主机/用户名/密钥路径）。
- 单测：known_hosts 加载/匹配/指纹解析（纯逻辑，tests 引用 Backends）。

### 4.3 验收标准（flag M2）

- [ ] 密码认证连真实 Linux 主机，bash/htop/vim 全部正常（备屏、256 色、鼠标）—— **待人工真机**。
- [ ] 私钥（OpenSSH ed25519）认证通过 —— **待人工真机**。
- [ ] 首连 TOFU 弹窗 → 接受后重连不再询问；篡改主机指纹 → 拒绝并提示 —— **待人工真机**（比对/持久化逻辑 6 单测覆盖）。
- [ ] 终端窗拖拽 resize → 远端 `stty size` 跟随 —— **待人工真机**。
- [ ] 断网/主机下线 → 明确断线提示，程序不崩 —— **待人工真机**（断线走既有 ErrorOccurred 链路）。

## 5. M3：多会话管理 ✅（代码完成，多并发验收待人工）

**实现记录（2026-09-19）**：终端窗重构为 TabControl——主连接固定第一标签（`TerminalSession.ForMain`，后端仍归 MainViewModel，字节走 RawRxTap 旁路；标题随连接方式/状态联动，如「主连接 · SSH root@192.168.1.10」）+ 独立会话标签（`TerminalSession.Ssh` 自持 SshBackend，接线 RX→View/输入→Write/resize→通道变更，关标签即断开）。工具条：「+ SSH」新建会话（`SshConnectDialog`：预填主面板参数/保存会话预填，凭据不落盘）、「已保存会话」下拉 + 连接/删除（`Config/terminal_sessions.json`，SavedSessionsStore 按名称去重）。host key TOFU 复用主连接同一入口（`vm.VerifyHostKey` 公开化，弹窗 Owner 仍为主窗）。主调试链路（单 _active）完全未动。
范围取舍：独立会话 v1 仅 SSH（Telnet/本地终端随 M4 加入）；多串口独立会话未做（主连接已覆盖单串口场景）。

### 5.1 目标行为

- 终端窗改标签页容器（一个终端窗内 N 个 tab，每 tab 独立连接）；或保留多窗（按实现成本定，倾向 tab）。
- 会话面板：保存的主机列表（名称/地址/认证方式）→ 双击快速连接；支持串口会话与 SSH 会话并列。
- 标签标题 = 会话名/host；关闭标签=断开该连接。

### 5.2 设计要点

- 现有 VM 是单 `_active` 连接模型：M3 把「连接实例」抽象为 `SessionViewModel`（backend + terminal + 状态），主界面保持单活动连接不变（调试主链路不动），多连接只存在于终端窗内。
- 保存会话进 `Config/sessions.json`（密码绝不落盘）。

### 5.3 验收标准（flag M3）

- [ ] 同时保持 ≥3 个 SSH 会话独立收发 —— **待人工真机**。
- [ ] 保存主机 → 重启程序 → 选中点「连接」一键重连（凭据补输）—— **待人工真机**。
- [ ] 关标签/关窗正确断开对应连接（无句柄泄漏）—— **待人工真机**（销毁路径：关标签/关窗/主窗退出三处均已清理）。

## 6. M4：可选扩展 ✅（Telnet + 本地终端完成；SFTP/端口转发待需求）

**实现记录（2026-09-19）**：
- **Telnet**：`Backends/Telnet/TelnetNegotiator.cs`（RFC 854 IAC 协商纯逻辑，9 单测：透传/IAC IAC 转义/WILL-DO 应答策略〔接受服务器 ECHO+SGA、其余拒绝〕/DO 全拒含 NAWS/子协商丢弃/单字节命令忽略/跨包分片状态机/输出转义/数据流混合）+ `TelnetBackend`（同构事件流，协商应答读线程直写）；终端窗「+ Telnet」对话框 + 会话标签；保存会话 Kind="telnet" 快速连接分支。
- **本地终端**：`App/Services/ConPtySession.cs`（ConPTY 纯 P/Invoke 零依赖，实现 IBusBackend；pwsh→powershell 探测兜底；EOF→等退→Terminate→CancelSynchronousIo 取消阻塞读→收 ConPTY 的关闭顺序〔2026-09-21 修订，见下条〕）。**实测通过**：PowerShell 启动横幅/命令回显（含 VT 着色）/resize/销毁全链路。
- **开箱即用（2026-09-19 用户反馈）**：终端窗打开时若主连接未建立，**自动开一个本地终端标签**并聚焦——打开终端即可直接输命令，无需先连串口/TCP/SSH 或找「+ 本地」按钮；用户主动关闭该标签后不重开。冒烟验证：默认配置（终端窗关）零子进程；终端窗开 + 未连接 → conhost+powershell 自动拉起。
- **ConPTY 踩坑记录（重要）**：① STARTUPINFOW 必须含全部 8 个 DWORD（易漏 dwXSize/dwYSize）——缺 2 个使 STARTUPINFOEXW=104≠112，CreateProcessW 报 **Win32 错误 87**；② 必须设 `STARTF_USESTDHANDLES`（对齐 Pty.Net 生产实现）——否则子进程**回落父控制台**而非挂接 ConPTY（症状：输出漏到宿主进程控制台、管道只有 16 字节初始化转义）。两处均已在代码注释中标注。
- **Win10 关终端窗死锁修复（2026-09-21，用户 Win10 复现机挂起 dump 实锤）**：根因 = **Win10 `ClosePseudoConsole` 在输出管道存在挂起同步 ReadFile 时永不返回**（微软已知缺陷，Win11 已修，故本机不复现）——`Teardown` 原在 UI 线程（TerminalWindow.OnClosing → Dispose 链）执行 → UI 永久卡死。修复两层：① `Close()/Dispose()` 改 `Task.Run(Teardown)` 卸载线程池（`Interlocked` 防 Close/Dispose 双重入队），UI 不再承担拆卸风险；② 拆卸顺序在 Terminate 后插入「`CancelSynchronousIo` 取消读线程阻塞读（读线程自记 `_readTid`，`OpenThread(THREAD_TERMINATE)` 取句柄）→ `Join(1000)`」再 `ClosePseudoConsole`。**dump 分析插曲**：原转储 flag（`MiniDumpNormal|IndirectlyReferencedMemory`）缺模块数据段，dotnet-dump/ClrMD 均找不到 CLR——托管栈靠手写 minidump 解析器（线程上下文 RIP/RSP + VA→文件偏移翻译 + 栈 qword 扫描命中模块区间）读出；CrashLogger 转储 flag 同轮补强为含数据段/句柄/线程信息（体积仍数 MB 级）。本机（Win11）实测开→关→重开→关两轮干净、无挂起报告、107 单测全绿；**真验证在 Win10 复现机**（看门狗应不再触发）。
- **终端配色现代化（2026-09-19 用户反馈「优化 shell 界面显示和文字配色」）**：XTerm.NET 默认纯黑底（#000000）+ 纯白字 + VGA 老调色板 → 改 **Campbell 调色板（Windows Terminal 默认 16 色）+ VS Code 式柔和深底**：背景 #1E1E1E、前景 #D4D4D4、光标 #AEAFAD、选区 #264F78，经 `TerminalOptions.Theme`（`ThemeOptions`）下发；宿主 Border 背景与未连接遮罩同步 #1E1E1E（`TerminalView.ThemeBackground` 常量单一来源）。**同轮修复潜藏渲染缺陷**：原 DrawRun 把 `GetFgColorMode()==0` 当「默认色」——实际 mode 0 = **256 色调色板索引**（256/257 才是默认标记，mode 1 = RGB 直出），导致全部索引色（含 30-37/90-97 SGR 标准色、38;5;n 256 色）被画成默认前景色、索引背景整片不画；新增 `ResolveAttrColor`（mode 分流 + 默认标记 + `PaletteColor` 查表）统一 DrawRun 与 Block 光标重画两路。验证：本地 PowerShell 标签灌入 16 色 Write-Host + 38;5;208 + 38;2 真彩 + 粗体/下划线混排，截屏逐色肉眼核对正确；107 单测全绿。
- **终端字体更换（2026-09-19，三轮演进）**：① 用户指定英文 **Times New Roman**、中文 GB2312（本机无 仿宋_GB2312/楷体_GB2312 命名字体 → 宋体 SimSun，GB2312 字符集标准字体）。**渲染架构随之调整**：TNR 是比例字体（i=3.9 / W=13.2 DIP @14pt），原「同属性连续格合并为一条 FormattedText」会让后续字符按自然字宽推进、漂出格网——改为**逐格定位绘制** + `FormattedText` 缓存（key=文本/粗/斜/前景色，DPI 变化清空），背景仍按同底色段合并。② 格宽初版取 max(TNR 粗体 W≈13.7, SimSun 全角/2) → 用户实测反馈「占位太宽」→ 降 9 DIP 基准 + 超格字符（M/W/m/w）水平压缩。③ 用户仍不满意，要求「参考 VS Code 终端」——出 TNR vs **Cascadia Mono**（VS Code 终端默认等宽）双方案对比截图，用户选定后者：**最终方案 = `Cascadia Mono, SimSun`**，格宽 = max(数字 0 字宽≈8.2, 全角/2=7)，字距天然均匀、零压缩、对齐精准；逐格绘制与压缩逻辑保留（等宽下不触发，留作防御）。教训：**终端网格场景优先等宽字体，比例字体的压缩/稀疏怎么调都有妥协**。验证：离屏截图逐项核对 + 真实应用窗口截图复核（长路径单行不折行、字符均匀）。
- **Ctrl+C 失效根因与修复（2026-09-19 用户反馈「ctrl+c 没效果」）**：XTerm.NET 的 `Selection.HasSelection` 在**零宽选区**（单击未拖动）后也为真，且 `GetSelectionText()` 对零宽返回 anchor 处 1 个字符（非空）——鼠标点终端聚焦一次，`Ctrl+C` 就被「有选区→复制」分支吞掉（还污染剪贴板 1 字符），`^C` 永远发不出去。修复三层：① 鼠标抬起时零宽选区即 `ClearSelection`；② `Ctrl+C` 复制分支加「选文非空」守卫；③ **`CopySelection` 复制后清除选区**（Windows Terminal/xterm.js 惯例——引擎不会因新输出/键入自动清选区，残留选区会让后续 Ctrl+C 永远进复制分支）。另实测澄清：`\x03` 写入 ConPTY 输入管在 cmd / powershell(PSReadLine) 下均能正常中断运行中命令（ping -t）与取消行编辑，后端链路无问题。验证：WPF harness 对 TerminalView 注入真实 Ctrl+C 键击（SendInput）——无选区发 03 / 有选区复制且选区即清 / 复制后再按发 03，全过。
- **终端交互体验修复（2026-09-25 用户反馈「选中不能 Ctrl+C/V、TUI 不能滑动」）**：三个根因——① 应用开鼠标跟踪（vim/htop）后鼠标事件**全量转发给应用**，本地选区逻辑不触发，选不上字自然无从复制；② `Ctrl+V` 从未绑定粘贴（原落到字符路径发 `\x16`）；③ 备屏+无鼠标跟踪时滚轮**直接丢弃**（代码 `return`），less/man/vim 关鼠标下滚轮等于废的。修复（全在 `TerminalView`，对齐 Windows Terminal/xterm 惯例）：**Shift+左键拖动强制文字选择**（`_forceSelecting` 标记绕过跟踪）、**Shift+右键强制弹上下文菜单**（跟踪时右键事件原本也被吃掉）；**Ctrl+V 粘贴**（原 `\x16` literal-next 极少使用，Ctrl+Shift+V 终端标准键保留）；备屏+无跟踪滚轮**模拟 Up/Down 键**（每格 3 行）；**Shift+滚轮**始终终端自处理——非备屏强制滚回滚 / 备屏模拟 PgUp/PgDn 整页翻动；同轮**补全鼠标转发缺口**：左键 Down/Up/Drag（Drag 按 ButtonEvent/AnyEvent 模式）、右键 Down/Up（原实现只转发滚轮）。滚轮多格滚动（|Delta|>120）按格数展开发送。验证：构建 0 错误；用户本地终端实测 vim/less 滚轮与 Shift 选择复制确认。
- **IME 候选框跟随光标（2026-09-25 用户反馈「输中文时预览行固定显示在显示器左上角」；2026-09-26 定稿）**：根因 = TerminalView 是自绘控件，走 WPF 默认 TSF 文本存储时输入法查不到光标矩形（自绘控件没有 TextView），候选框/预编辑串就丢屏幕 (0,0)。**两轮失败教训**：① IMM32 钩子路径（HwndSource 挂 `WM_IME_STARTCOMPOSITION` 等 + `ImmSetCompositionWindow`/`ImmSetCandidateWindow`）对纯 TSF 输入法（微信输入法）无效——它根本不经由 IMM 设位；② 按 LightTextEditorPlus 配方加 `InputMethod.SetIsInputMethodSuspended=true` 走 IMM/AIMM 桥接，反而把 WPF 的文本提交链路整个掐断（中文完全输不了）。**最终方案 = 内联预编辑隐藏 TextBox（xterm.js 同款架构 + Windows Terminal 观感，2026-09-26 三轮定稿）**：`TerminalView` 基类 `FrameworkElement`→`Grid`，内藏 TextBox（无边框无内边距、IsHitTestVisible=false、**背景 null + CaretBrush 透明**——空框完全不可见不挡终端光标）承接键盘焦点——WPF TextBox 自带完整 TSF/IMM 支持，输入法候选框由 WPF 锚定到框内光标；框体每帧叠到终端游标格上、**宽取到右缘**（OnRender 末尾 `UpdateImeBoxPosition`），组合期 IME 在框文档里画的**预编辑串（带下划线）即以终端同款字体/前景色内联显示在光标行**，候选条紧跟其下（对齐 Windows Terminal 观感）；组合期终端自己的光标让位（RenderCursor 跳过），前景色随主题（含 OSC 10 运行时改色）。**第三个坑（用户截图反馈「蓝色输入框」）**：WPF 组合期用 `SelectionBrush`（默认系统高亮蓝）画预编辑串背景——亮色应用里不显眼、深色终端上成蓝块；`SelectionBrush=Transparent` + `SelectionTextBrush` 每帧跟终端前景即还原 WT 式「下划线裸字」。**第四个坑（Claude Code 场景蓝块复发，纯 PS 不现——像素扫描 0/34200 蓝素实证修复）**：TUI 应用高频重绘让引擎光标抖动，框跟着挪（每帧 UpdateImeBoxPosition）→ 输入法认为组合锚点不稳，弃用内联渲染、改画**自己的蓝色悬浮面板**（微信输入法）；`UpdateImeBoxPosition` 开头 `if (_imeComposing) return;` **组合期冻结锚点**即恢复内联（组合开始时框已在游标处，冻结到结束）。输入分流：直打字符 `PreviewTextInput` 直发对端（handled 阻断插入，框内不存字）；IME 上屏文字 `TextChanged` 转发后清空，预编辑阶段（`_imeComposing`）不转发——组合状态由 `TextCompositionManager` 三个附加事件（handledEventsToo）+ `WM_IME_START/ENDCOMPOSITION` 窗口钩子双通道跟踪（消息只响应本框握焦点，多标签共享 HWND 无串扰）；特殊键/快捷键仍走原 `OnPreviewKeyDown`（Preview 隧道先于隐藏框，原逻辑零改动）；焦点语义不变（`Focus()` new 转给隐藏框，宿主 `FocusActiveView` 无感）。**第二个坑（用户实测「候选框位置对了但中文上不了屏」+ 诊断日志实锤）**：微信输入法上屏时序 = 最终文字先进框（TextChanged 触发时 `_imeComposing` 仍 true，冲刷被守卫拦下）→ 组合结束事件（PreviewTextInput Final / WM_IME_ENDCOMPOSITION）晚几毫秒才到——复位后没人补冲刷，文字滞留框内发不出去（纯 PS 提示符下偶发顺序相反才碰巧通过）。修复：Final/ENDCOMPOSITION 复位后 `ScheduleImeFlush()` 以 **Background 优先级推迟冲刷**——等 TSF 对文档的写入在本轮输入分派内落定再取框内最终文字，两种时序都正确且不会误发预编辑串（冲刷与 TextChanged 路径先到先发、幂等）。验证（SendInput 驱动真输入法端到端，截屏肉眼核对）：微信输入法拼音 "nihao" 组合——预编辑下划线串画在终端光标处 + 候选条紧跟输入行下方（窗口内）；空格上屏——「你好」正确落到 PowerShell 提示符（含时序修复后复验）；ASCII "test99" 直打链路回归通过。**第五个坑（2026-09-26 用户再报「还是有蓝色的输入框」——纯 PS 空闲态即现，与组合无关）**：这个常驻蓝框根因根本不在输入法——App.xaml 的全局隐式 `Style TargetType="TextBox"`（圆角模板 + `IsKeyboardFocusWithin` 触发器把模板 `Bd` Border 直改 AccentBrush 蓝色 1.5px，另带 MinHeight=28/Padding=6,4），`new TextBox()` 默认吃隐式样式，而触发器改的是**模板内 Border**，框上局部设 `BorderThickness=0`/`FocusVisualStyle=null` 全拦不住 → 隐藏框一有键盘焦点即显形成蓝色圆角框（空闲恒定、组合/取消/英文模式/失焦全不变——五阶段像素扫描 bbox 恒等于框边界，966 蓝素）。修复 = BuildImeBox 里 `Style = null` 剥离隐式样式（一行）；复检 966→54（余量为窗口左缘静态杂色，与框无关）+ 截图肉眼核对。**排障教训**：蓝框 bbox 精确贴合「自建控件的边界 + 焦点态恒定」时先查自家全局样式（隐式样式/模板触发器），别先赖输入法——本轮前四个坑全在防输入法，真凶是自家样式。
- **终端窗/图表窗独立化 + 主窗入口改按钮（2026-09-26 用户报「启动终端界面或波形界面会强制拉起主窗」）**：根因 = 两窗创建时设了 `Owner = 主窗`——Win32 从属窗口（owned window）被绑进主窗的激活组：① 从属窗不能脱离隐藏/最小化的主窗单独显示，`Show()` 从属窗会把主窗强制带出来（WPF 里甚至给未 Show 过的窗口设 Owner 直接抛异常，当年 XAML 初始化期崩溃即此语义）；② 从属窗永远浮于主窗之上（Z 序绑定）；③ 激活从属窗会连带激活主窗、主窗最小化/还原联动从属窗——三条合起来即「新窗不独立、强制拉起主窗」。修复：`ChartWindow`/`TerminalWindow` 不再设 Owner，成为与主窗平级的独立顶层窗口（任务栏独立条目、可压到主窗之下/拖到另一台显示器）。同轮用户要求把主窗顶部「波形」「终端」复选框改**按钮**：点按打开（已开则 `Activate()` 带到前台），关闭只走窗口 X；原先复选框的 Checked/Unchecked 事件链移除，显隐统一由 `ShowWavePanel`/`ShowTerminalPanel` 属性变化驱动（`MainWindow.OnVmPropertyChanged` 分发 → `ApplyWavePanelState`/`ApplyTerminalPanelState`；窗口 X 的延迟回写 false → 销毁分支不变），「上次退出时开着则随启动打开」的记忆行为保留。构建 0 错误、107 单测全绿。

### 6.1 验收状态

- [x] Telnet 协商器 9 单测全绿（107 总计）。
- [x] ConPTY 本地终端实测：启动/回显/resize/销毁（探针程序，PowerShell 5.1）。
- [ ] Telnet 连真实设备（嵌入式 telnetd）字符模式回显 —— **待人工真机**。
- [ ] 终端窗「+ 本地」交互体验（字体渲染/中文/复制粘贴）—— **待人工**。
- [ ] SFTP / 端口转发 —— **待需求确认后独立批次**（SFTP 为大 UI 件：双栏浏览 + 传输队列）。

## 7. 风险与对策

| 风险 | 对策 |
| --- | --- |
| XTerm.NET 年轻库（单一维护者） | 纯托管 MIT；若上游停更可整源 vendor 进仓库（GitHub 当前网络不通，暂走 NuGet 二进制依赖，不阻塞） |
| WPF 自绘性能（输出高峰） | 只画视口 + 文字逐格缓存（逐格绘制为兼容比例字体设计，见 §3.2）+ 16ms 合帧；若仍不足再加 per-line 缓存（计划预留的优化旋钮） |
| CJK 宽字符度量偏差 | 格宽 = max(Cascadia Mono 数字宽≈8.2, 全角/2=7)，CJK 固定占两格逐格绘制；超格字形水平压缩兜底；冒烟含中文 vim |
| SSH.NET 不支持 .ppk | UI 明确提示转换（Xshell 用户迁移注意） |
| 每键回显延迟 | 键入直发（不经 50ms FlushRx）；终端泵 16ms；实测超阈值再降 |

## 8. 完成度更新约定

每达成一个里程碑：更新本文档 §0 跟踪表与状态行 → README 路线图行同步 → 相关验收项打勾（硬件/真机项注明「待人工确认」）→ 按需提升版本号。
