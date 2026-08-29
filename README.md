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
| 波形 | ScottPlot 5（SignalPlot 百万点实时） |
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
├── tests/SerialTool.Core.Tests/ # xUnit（41 用例）
├── scripts/publish.ps1          # 自包含单文件发布
├── Docs/                        # 设计文档 + 技术调查
└── legacy/                      # v0.2 Go B/S 方案归档
```

## 开发环境

- Windows 10/11
- Visual Studio 2026 Community（含 .NET 桌面开发工作负载）或 `dotnet` SDK 10
- 运行调试：VS 2026 打开 `SerialTool.slnx`，或 `dotnet run --project src/SerialTool.App`
- 单测：`dotnet test tests/SerialTool.Core.Tests`
- 发布：`./scripts/publish.ps1` → `dist/` 单 exe

## 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| V1.0 | WPF 骨架 + 串口收发控制台（SerialPortStream，含设备名识别）+ TCP 连接 + 会话日志 + 多帧定时发送（含帧备注） | ✅ 完成 |
| V1.1 | 帧解析引擎 + CRC 校验库 + JSON 协议模板（含编辑器）+ 帧结构化显示 | ✅ 完成（v2：字段链 + 长度域任意位置 + 校验字节序 + 帧尾扫描 + 多模板并行仲裁） |
| V1.2 | ScottPlot 时序图 | 规划 |
| V1.3 | 发送历史 / 热插拔 / 多端口标签 / 流控 | 规划 |
| V2 | I2C 后端（FT232H 官方包）+ 事务解析 | 规划 |
| V3 | CAN 后端（PCAN/slcan）+ DBC 解析 + 信号曲线 | 规划 |

## 方案变更历史

| 版本 | 方案 | 状态 |
| --- | --- | --- |
| v0.2 | Go 单文件后端 + 浏览器 UI（B/S，跨 Win/Mac，规避 Mac 签名） | **作废**（macOS 需求取消），代码已归档至 `legacy/` |
| v0.3 | C# / .NET 10 + WPF（Windows-only，自包含单 exe） | **现行** |
