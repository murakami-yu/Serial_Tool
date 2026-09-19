# Serial Tool — Windows 通用串口调试工具

Windows 专用通用串口调试工具：UART / RS232 / RS485 收发起步，模块化扩展 I2C、CAN，
协议级帧解析 + 可选波形显示。**免安装分发**（自包含单 exe，零运行时依赖，双击即用）。

> 状态：方案 v0.3 ｜ V1.0 骨架完成（2026-08-29，构建+单测 12/12+冒烟通过）
> 决策依据与调查记录：[Docs/串口工具技术栈调查文档.md](Docs/串口工具技术栈调查文档.md) ｜ 设计详情：[Docs/Serial_Tool_Design.md](Docs/Serial_Tool_Design.md)

## 技术栈（v0.3 定稿）

| 层 | 组件 |
| --- | --- |
| 语言/运行时 | C# / **.NET 10**（LTS），WPF |
| MVVM | CommunityToolkit.MVVM |
| 停靠布局 | Dirkster.AvalonDock（VS 式多面板） |
| 波形 | ScottPlot.WPF 5（逻辑分析仪式逐位方波：按波特率重建 UART 位流，RX/TX 双通道） |
| 串口 | RJCP.SerialPortStream（NuGet 3.0.5） |
| I2C（V2） | FTDI.FTD2XX_NET（厂商官方包） |
| CAN（V3） | Peak.PCANBasic.NET（官方）或 CANable slcan |
| 单测 | xUnit |

## 架构

```
WPF UI 层（MVVM：收发控制台 / 帧解析视图 / 波形面板，AvalonDock 停靠）
   ↓
Core 解析引擎（纯 C# 类库：帧状态机 / CRC 校验库 / JSON 协议模板 / DBC）
   ↓
Backends 硬件层（IBusBackend 接口插拔）
   SerialBackend(V1) → I2cBackend(V2) → CanBackend(V3)
   ↓
分发：dotnet publish 自包含单 exe（~70MB，免安装）
```

## 目录结构（规划）

```
SerialTool/
├── SerialTool.slnx              # 解决方案（.NET 10 XML 格式）
├── src/
│   ├── SerialTool.App/          # WPF 主程序（MVVM 收发控制台 + 会话日志 + 多帧定时发送）
│   ├── SerialTool.Core/         # 解析引擎（Hex + Checksum + Framing 帧解析）
│   └── SerialTool.Backends/     # 硬件后端（IBusBackend + SerialBackend + TcpBackend）
├── tests/SerialTool.Core.Tests/ # xUnit（92 用例）
├── scripts/publish.ps1          # 自包含单文件发布（版本目录制，不覆盖历史版本）
├── Docs/                        # 设计文档 + 技术调查
└── legacy/                      # v0.2 Go B/S 方案归档
```

## 开发环境

- Windows 10/11
- Visual Studio 2026 Community（含 .NET 桌面开发工作负载）或 `dotnet` SDK 10
- 运行调试：VS 2026 打开 `SerialTool.slnx`，或 `dotnet run --project src/SerialTool.App`
- 单测：`dotnet test tests/SerialTool.Core.Tests`
- 发布：`./scripts/publish.ps1` → `dist/v<版本号>/` 单 exe（版本取自 csproj `<Version>`；同版本重发自动加时间戳后缀，不覆盖历史版本；`dist/LATEST.txt` 指向最新版本目录）

## 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| V1.0 | WPF 骨架 + 串口收发控制台（SerialPortStream，含设备名识别）+ TCP 连接 + 会话日志 + 多帧定时发送（含帧备注） | ✅ 完成 |
| V1.1 | 帧解析引擎 + CRC 校验库 + JSON 协议模板（含编辑器）+ 帧结构化显示 | ✅ 完成（v2：字段链 + 长度域任意位置 + 校验字节序 + 帧尾扫描 + 多模板并行仲裁） |
| V1.2 | ScottPlot 时序图（逻辑分析仪式逐位方波）+ GBK/UTF-8 文本解码 | ✅ 完成 |
| V1.2.x | 功能增强：状态栏速率/时长统计 + 接收过滤 ｜ RTS/DTR 控制 + CTS/DSR 指示灯 ｜ 帧字段实时曲线 ｜ 自动应答器（[执行计划](Docs/功能增强执行计划.md)）｜ 布局独立性（工具条换行 / 各框 ClipToBounds / 等宽输入 28px 基线 / MinWidth 1290） | ✅ 完成（2026-09-01，92 单测通过 + 启动冒烟；硬件行为项待接设备人工验证） |
| V1.2.x | 图表独立窗口：时序图/字段曲线迁出主窗（默认关闭·按需勾选）｜ 销毁/重建代替隐藏（位置尺寸记忆）｜ 最小尺寸按内容完整显示实测标定（744×485，画布 200 可读性地板）｜ 页签行平级「？」使用说明弹层 | ✅ 完成（2026-09-04，含坐标轴消失/窗口关闭重入两轮根因修复与排障记录） |
| V1.2.x | 接收区 TX/RX 行颜色自定义：接收区标题栏色块入口 + 16 色调色板 + 恢复默认；接收框 TextBox→RichTextBox 逐行着色；默认发送蓝/接收深灰，持久化记忆 | ✅ 完成（2026-09-05，TCP 回环实测双色/换色重绘/持久化/>800K 截断保色） |
| V1.2.x | 波特率扩展：23 档常用预设（300–2000000，含 128000/256000/500000/1M/1.5M/2M 等非标准档）+ 下拉末尾「自定义…」选项弹对话框输入任意波特率（正整数校验、自定义值自动插入下拉、ui_settings.json 持久化）；下拉圆角风格统一（悬浮圆角弹层 + 圆角高亮块） | ✅ 完成（2026-09-12） |
| V1.2.x | 发送区改版 + 主发送区定时发送：控制单行横贯（多帧面板 / HEX 显示 / HEX 发送 / 定时发送+周期ms / RTS/DTR/CTS/DSR 仅串口显示随组收起，整行无右侧空白）；「HEX 显示」自顶部接收显示条迁入发送区；发送/清空按钮竖排输入框右侧并贴上下边填满（清空=清空发送输入框，新增）。定时发送勾选即按当前输入立即发首帧、之后按周期自动重发（默认 1000ms 最小 50，复用多帧循环同一 50ms 调度节拍，空内容/HEX 非法跳过本轮下周期重试），开关与周期持久化。串口配置改整行拉通表单（串口下拉填满+刷新 / 连接方式填满 / 四参数一行贴内容宽 / 连接按钮整行收底，面板贴合最宽行、各行右缘对齐无横向空白），面板收窄发送区加宽，底部条行高 215 且下限钉死（内容余量 18 DIP，防显示缩放/字体度量差异削掉「连接」按钮底缘；2026-09-19 起随连接方式动态：SSH 五行内容抬至 264、窗口最小高联动 710），全局下拉列表高度封顶 340 出滚动条 | ✅ 完成（2026-09-17） |
| V1.2.x | 各界面背景色自定义：主窗状态栏「外观…」集中设置——主窗口（SSH/Telnet 等对话框跟随）/终端窗口/波形窗口/模板编辑器各自独立 + 终端内容底色（引擎默认背景，ANSI 调色板不动，已开标签实时换底）；Appearance 单例 + x:Static 绑定统一全窗口路径，改色即时生效、ui_settings.json 持久化、坏值回退默认；复用 ColorChipButton 调色板 | ✅ 完成（2026-09-19，截图实测五路改色/重启持久化/坏值回退/终端实时换底） |
| V1.2.x | 控件级配色自定义：外观设置新增 8 项控件色——面板/显示框底色、按钮底色、边框色（自动派生控件边框加深 + 分隔线调亮 + 滚动条滑块）、正文/次要文字、强调色（聚焦边/勾选勾底）、悬停高亮、选中/按下；显示框/勾选框/下拉框/按钮（点击框）全覆盖，改色即时级联、ui_settings.json 持久化、坏值回退默认、每项弹层自带恢复默认。机制 = 画刷**整实例替换** + 引用方全量 **DynamicResource**（11 个 XAML 181 处；样式/模板密封会冻结 StaticResource 到的画刷实例，「共享实例突变 Color」方案抛异常被吞静默失效，本轮废弃） | ✅ 完成（2026-09-20，截图实测五路改色/下拉弹层/重启持久化/恢复默认/坏值回退，107 单测全绿） |
| V1.3 | 发送历史 / 热插拔 / 多端口标签 / 流控 | 规划 |
| V1.4 | 终端/Shell：XTerm.NET 终端仿真（WPF 自绘，零 WebView）—— M1 串口/TCP VT 终端模式 ✅ → M2 SSH.NET 远程会话（密码/私钥、host key TOFU、resize 跟随）✅ → M3 多会话标签+会话管理器 ✅ → M4 Telnet（IAC 协商）+ 本地终端（ConPTY 纯 P/Invoke）✅；SFTP/端口转发按需待启（[执行计划](Docs/Shell功能执行计划.md)） | ✅ 主体完成（2026-09-19：107 单测全绿 + ConPTY 实测回显/resize；回环/SSH/多会话/真机项待人工验收） |
| V2 | I2C 后端（FT232H 官方包）+ 事务解析 | 规划 |
| V3 | CAN 后端（PCAN/slcan）+ DBC 解析 + 信号曲线 | 规划 |

## 方案变更历史

| 版本 | 方案 | 状态 |
| --- | --- | --- |
| v0.2 | Go 单文件后端 + 浏览器 UI（B/S，跨 Win/Mac，规避 Mac 签名） | **作废**（macOS 需求取消），代码已归档至 `legacy/` |
| v0.3 | C# / .NET 10 + WPF（Windows-only，自包含单 exe） | **现行** |
