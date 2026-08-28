# Serial Tool — 通用串口调试工具

跨平台（Windows / macOS）通用串口调试工具：解析 UART / RS232 / RS485 / I2C / CAN，
**免安装、免签名**分发——单文件二进制 + 浏览器 UI。

## 快速开始

```bash
# 方式一：源码运行（需 Go 1.22+）
go run ./cmd/serial-tool

# 方式二：直接运行编译好的二进制
./serial-tool            # 或 serial-tool.exe（Windows）
```

启动后自动打开浏览器访问 http://127.0.0.1:8970 即可使用。
停止：Ctrl+C。无安装、无服务注册、无签名依赖。

## 架构

**Go 单文件后端 + 浏览器 UI（B/S）**：

```
浏览器 ──WebSocket──► 后端（127.0.0.1:8970）
                        │
                        ├── backend/serial   ← UART / RS232 / RS485（V1）
                        ├── backend/i2c      ← V2（规划）
                        ├── backend/can      ← V3（规划）
                        └── core/            ← 协议解析引擎（规划）
```

- 三类总线实现统一 `backend.Backend` 接口，输出 `{Ts, Bytes}` 事件流，插拔式扩展
- 前端资源 `go:embed` 进二进制，单文件分发
- 只监听 `127.0.0.1` + Host 校验，防外部访问（安全要点见调查文档 §7.4）

## 目录结构

```
├── cmd/serial-tool/     # 入口
├── internal/
│   ├── server/          # HTTP + WebSocket + 静态文件伺服
│   ├── backend/         # 统一后端接口 + serial / i2c / can 实现
│   └── core/            # 协议解析引擎（规划）
├── web/                 # 前端静态资源（embed）
├── scripts/             # 交叉编译脚本（win/mac × amd64/arm64）
└── docs/                # 设计文档、技术栈调查
```

## 构建与分发

```bash
./scripts/build.sh       # Windows: .\scripts\build.ps1
```

产出 `dist/` 下 4 个单二进制（win/mac × amd64/arm64），zip 打包即分发。

## 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| V1 | UART/RS232/RS485 收发 + 端口管理 | 骨架已完成 |
| V1.1 | 可配置帧解析（帧头/长度/校验）+ 协议模板 | 规划 |
| V1.2 | L1 数据级时序图（波形基础版） | 规划 |
| V2 | I2C 后端（FT232H/D2XX）+ 事务解析 | 规划 |
| V3 | CAN 后端（CANable SLCAN）+ DBC 解析 | 规划 |

详细决策记录见 [docs/串口工具技术栈调查文档.md](docs/串口工具技术栈调查文档.md)。
