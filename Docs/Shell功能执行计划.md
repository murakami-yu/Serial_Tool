# Shell 功能执行计划：终端仿真 / SSH / 多会话（Xshell/MobaXterm 方向）

> 批次代号：**V1.4**（终端/Shell 功能线，与 V1.3「发送历史/热插拔」并行不冲突）
> 日期：2026-09-18 ｜ 状态：**M0/M1/M2 已实现（构建 0 错误 / 98 单测全绿 / 启动冒烟 ×2；M1 回环与 M2 真机验收待人工确认），M3 待启动**
> 关联：[Serial_Tool_Design.md](Serial_Tool_Design.md) ｜ [README 路线图](../README.md) ｜ [功能增强执行计划](功能增强执行计划.md)

## 0. 完成度跟踪（每完成一个阶段更新此表）

| 里程碑 | flag（验收即达成） | 状态 | 完成日期 |
| --- | --- | --- | --- |
| M0 地基 | 计划文档落定 + 依赖选型验证通过（XTerm.NET 2.0.2 API 冒烟） | ✅ 完成 | 2026-09-18 |
| M1 串口终端 | 连上串口设备 → 终端窗渲染 VT 输出（颜色/清屏/光标/滚回）→ 键入即时回显交互 | ✅ 完成（代码+构建+冒烟；TCP 回环/真机项待人工确认） | 2026-09-18 |
| M2 SSH 会话 | 连真实 Linux 主机交互 shell（密码/私钥、host key 首次确认、resize 通知） | ✅ 代码完成（构建+98 单测+冒烟；真机验收待人工） | 2026-09-18 |
| M3 多会话 | 并发 N 个终端会话 + 标签页切换 + 保存的主机快速连接 | ⬜ 未开始 | — |
| M4 扩展（可选） | Telnet / 本地终端 ConPTY / SFTP，按需逐项启动 | ⬜ 未开始 | — |

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
- **窗口生命周期**：照抄 ChartWindow 模式（勾选=新建 owned 窗、取消勾选/X=销毁、位置尺寸记忆、主窗 Closing 前先真关）。
- **持久化套路**：`Config/ui_settings.json` 加键（ShowTerminalPanel/终端字体字号），`Config/known_hosts.json`（M2），照抄现有 `Load*/Save*` 模式。
- **测试边界**：纯逻辑（已知主机指纹解析、字节度量）进 xUnit；渲染/交互走构建 + 人工冒烟清单。

## 3. M1：WPF 终端控件 + 串口 VT 模式 ✅

**实现记录（2026-09-18）**：新增 `Controls/TerminalView.cs`（自绘渲染控件，~600 行）+ `TerminalWindow.xaml(.cs)`（独立窗，图表窗同款销毁/重建模式 + 滚回滚动条 + 未连接遮罩）；`MainViewModel` 增 `RawRxTap`（读取线程旁路）/`SendTerminalBytes`（终端发送链路）/`ShowTerminalPanel`（持久化）；工具条新增「终端」勾选项。构建 0 错误、92 单测全绿、配置开启终端窗启动 8 秒冒烟无崩溃。
已知取舍：双击选词暂缺（`Control.OnMouseDoubleClick` 在 `FrameworkElement` 不可用，待补 `ClickCount` 方案）；256 色中 16-255 按标称值渲染（OSC 改写调色板仅对 0-15 生效）；粘贴走 `Terminal.Paste`（括号粘贴模式）。

### 3.1 目标行为

- 工具条新增「终端」勾选项（与时序图并列），勾选打开终端窗口（独立窗，默认贴主窗右侧）。
- 串口/TCP 连接后，终端窗把 RX 字节流按 VT100/xterm 解释渲染：颜色、清屏、光标移动、`htop`/`vim` 备屏切换。
- 键盘输入即时编码发往对端（回显由对端负责）；中文输入法可输入（经 `GenerateCharInput`）。
- 滚回：鼠标滚轮 + 滚动条，滚到底自动跟随；备屏（vim 等）禁滚。
- 复制粘贴：拖选 + 右键菜单（复制/粘贴/清屏/回到底部）；`Ctrl+Shift+C/V`。
- 未连接时显示遮罩「未连接」；连接断开终端保留内容（只读）。

### 3.2 设计要点

**TerminalView**（`src/SerialTool.App/Controls/TerminalView.cs`，自定义 FrameworkElement）：

- 度量：字体 `Global Monospace`（WPF 复合字体，CJK 等宽回退且宽度=2 格），`FormattedText("M")` 取格宽，行高 = 字体行高；`Resize` 按视口尺寸反推行列（下限 80×24 起步按内容）。
- 渲染（`OnRender`）：背景 → 逐行把同属性连续格合并为 run（一条 `FormattedText`）→ 光标（块/闪 530ms）→ 选区高亮。只画视口行（`Lines[YDisp + row]`），`Width==0` 续格跳过。
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

## 5. M3：多会话管理 ⬜

### 5.1 目标行为

- 终端窗改标签页容器（一个终端窗内 N 个 tab，每 tab 独立连接）；或保留多窗（按实现成本定，倾向 tab）。
- 会话面板：保存的主机列表（名称/地址/认证方式）→ 双击快速连接；支持串口会话与 SSH 会话并列。
- 标签标题 = 会话名/host；关闭标签=断开该连接。

### 5.2 设计要点

- 现有 VM 是单 `_active` 连接模型：M3 把「连接实例」抽象为 `SessionViewModel`（backend + terminal + 状态），主界面保持单活动连接不变（调试主链路不动），多连接只存在于终端窗内。
- 保存会话进 `Config/sessions.json`（密码绝不落盘）。

### 5.3 验收标准（flag M3）

- [ ] 同时保持 ≥3 个 SSH 会话独立收发。
- [ ] 保存主机 → 重启程序 → 双击一键重连。
- [ ] 关标签/关窗正确断开对应连接（无句柄泄漏，串口占用释放）。

## 6. M4：可选扩展（按需逐项启动）⬜

| 项 | 说明 | 前置 |
| --- | --- | --- |
| Telnet | TcpBackend + IAC 协商（极小），终端窗选协议 | M3 |
| 本地终端 | ConPTY P/Invoke 承载 PowerShell/cmd，同一 TerminalView | M1 控件稳定 |
| SFTP | SSH.NET SftpClient + 简易双栏文件面板（MobaXterm 式） | M2 |
| 端口转发 | SSH.NET 转发列表 UI | M2 |

## 7. 风险与对策

| 风险 | 对策 |
| --- | --- |
| XTerm.NET 年轻库（单一维护者） | 纯托管 MIT；若上游停更可整源 vendor 进仓库（GitHub 当前网络不通，暂走 NuGet 二进制依赖，不阻塞） |
| WPF 自绘性能（输出高峰） | 只画视口 + 同属性 run 合并 + 16ms 合帧；若仍不足再加 per-line 缓存（计划预留的优化旋钮） |
| CJK 宽字符度量偏差 | 用 WPF 复合字体 `Global Monospace`（等宽回退按 2 格设计）；冒烟含中文 vim |
| SSH.NET 不支持 .ppk | UI 明确提示转换（Xshell 用户迁移注意） |
| 每键回显延迟 | 键入直发（不经 50ms FlushRx）；终端泵 16ms；实测超阈值再降 |

## 8. 完成度更新约定

每达成一个里程碑：更新本文档 §0 跟踪表与状态行 → README 路线图行同步 → 相关验收项打勾（硬件/真机项注明「待人工确认」）→ 按需提升版本号。
