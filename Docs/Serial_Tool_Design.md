# 通用串口工具设计

> 版本：v0.3 ｜ 日期：2026-08-29
> 平台：**仅 Windows 10/11**，免安装（自包含单 exe）
> 技术栈：C# / .NET 10 + WPF（调查依据见 [串口工具技术栈调查文档.md](串口工具技术栈调查文档.md) v0.3）
> 变更记录：v0.2 为 Go+B/S 跨平台方案（作废，macOS 需求取消）；v0.3 基于 Windows-only 复评重定

## 一、功能设计

### 1.1 功能总表

| 编号 | 功能名 | 功能详情 | 阶段 |
| ---- | ------ | -------- | ---- |
| 1 | 串口扫描与识别 | 枚举 COM 口并显示设备名（WMI 查 PnP 实体，如 "COM3 · USB-SERIAL CH340"）、手动刷新（后台线程防卡 UI）、占用状态提示、热插拔自动提示 | V1.0 ✅（热插拔 V1.3） |
| 2 | 串口参数配置 | 波特率（300–2000000 常用预设 + 下拉末尾「自定义…」选项弹对话框输入任意值，正整数校验；自定义值插入下拉并持久化）、数据位（7/8）、停止位（1/1.5/2）、校验位（无/偶/奇）、流控（无/RTS-CTS，V1.3 补） | V1 |
| 3 | 数据接收 | HEX/文本双模式；文本模式智能解码（UTF-8 优先，GBK 回退——中文设备数据直接显示汉字）、时间戳开关、跟随/悬停滚动控制、大缓冲环形截断、一键清空、**TX/RX 行颜色自定义**（接收区标题栏色块入口，取色弹层 = HSV 色图 + 16 预设色板 + HEX/RGB 输入 + 恢复默认；默认发送蓝/接收深灰；整行按方向着色；持久化） | V1 ✅（颜色 V1.2.x） |
| 4 | 数据发送 | HEX/ASCII 双模式、Enter 快捷发送；多帧发送（右侧面板）：帧列表编辑/增删、**每帧备注注释**、任意帧手动发送、每帧独立周期循环发送（最小 50ms）、JSON 持久化（Config/send_frames.json，含备注） | V1.0 ✅（发送历史 V1.3） |
| 5 | 会话日志 | 收发数据持续写入 txt（AutoFlush 防异常丢失）、自定义保存位置、一键定位文件 | V1.0 ✅（回放预留） |
| 6 | 协议帧解析 | **字段链模板**（帧头 → 命令/固定/长度/数据字段任意组合 → 校验 → 帧尾）：长度域可在帧中任意位置（Modbus 写帧 byteCount）、校验字节序可配（CRCh CRCl 高前 / Modbus 标准低前）、无长度域协议支持帧尾扫描定界（EM 协议）、**多模板并行仲裁**（同总线混合协议/读写结构并存，按优先级+校验裁决）；粘包/半包/假帧头/坏帧重同步；JSON 模板（v1 自动迁移）+ 编辑器；帧行显示来源模板名 | V1.1 ✅（彩色高亮随 V1.2 视图升级） |
| 7 | 校验算法库 | CRC8(SMBus)/CRC16-Modbus/CRC16-CCITT-FALSE/CRC32（查表法）+ XOR + 累加和；标准测试向量全覆盖；大小端（长度域/校验值）；BCD/位域提取归入 V3 DBC | V1.1 ✅ |
| 8 | 数据级时序图（L1 波形） | **逻辑分析仪式逐位方波**（ScottPlot.WPF，独立图表窗口 ChartWindow，主窗顶部「波形」复选框控制显隐；2026-09-02 由接收区下方内嵌迁入）：按字节+波特率重建 UART 位流（起始位0+8数据位LSB在前+停止位1），RX/TX 双通道电平带并行显示；跳变点展开成方波拐点渲染；跟随窗口 100ms（位级观察）/自由缩放平移/双击复原；跳变缓冲 6 万点上限；开关与跟随状态持久化。注：按字节重建的合成波形（串口无电平采样能力，真实电平需逻辑分析仪硬件，见 L3） | V1.2 ✅ |
| 9 | 协议序列图（L2） | I2C/CAN 事务级时序视图：START→ADDR→ACK→DATA→STOP | V2/V3 |
| 10 | 电平级波形（L3） | 真实总线电平采样（需逻辑分析仪硬件），独立可选模块，接口预留 | 预留 |
| 11 | I2C 总线支持 | 设备扫描（7 位地址）、寄存器读写、ACK/NACK 显示、事务解析 | V2 |
| 12 | CAN 总线支持 | 标准帧/扩展帧收发、DBC 文件解析、信号物理量曲线 | V3 |
| 13 | 多端口并行 | 多串口标签页独立收发、对比视图 | V1.3 |
| 14 | 免安装分发 | 自包含单 exe（~70MB 零依赖），zip 分发，双击即用 | V1 |
| 15 | TCP 连接 | 串口服务器/TCP 透传设备连接（USR-TCP232、ESP8266 透传、ser2net 等）：地址+端口、3s 连接超时、断线自动提示；收发/日志/多帧全功能复用 | V1.0 ✅ |
| 16 | 终端仿真（Shell） | **VT100/xterm 终端视图**（XTerm.NET 2.0.2 纯托管引擎 + WPF 自绘 TerminalView，零 WebView 不破坏单 exe 分发）：串口/TCP 字节流按终端渲染（颜色/清屏/光标/主备屏/滚回 5000 行/256 色+真彩/CJK 宽字符）、键盘即时编码发往对端（IME 可用、候选框跟随终端光标，2026-09-25）、滚轮+滚动条滚回（备屏无跟踪时滚轮模拟 Up/Down 键、Shift+滚轮强制滚回/翻页）、拖选复制/粘贴（Ctrl+Shift+C/V + Ctrl+V，有选区 Ctrl+C 复制无选区发 ^C）、鼠标事件转发（vim/htop；Shift+左键强制选择、Shift+右键强制弹菜单，2026-09-25）；**Campbell 调色板 + 柔和深底主题**（#1E1E1E 底 / #D4D4D4 字 / #264F78 选区，经 `TerminalOptions.Theme` 下发，2026-09-19）；独立终端窗多标签（主连接 + 独立会话，关标签即断开）；**M2 SSH 远程会话 ✅**（SSH.NET 2026.0.0：密码/私钥认证、host key TOFU 首连确认与变更警告 known_hosts.json、resize→远端 stty 跟随、凭据不落盘）；**M3 多会话 ✅**（会话管理器 terminal_sessions.json + 快速连接）；**M4 Telnet ✅**（RFC854 IAC 协商器，哑终端+服务器回显策略，9 单测）+ **本地终端 ✅**（ConPTY 纯 P/Invoke 零依赖，pwsh/powershell 承载，实测回显/resize）；SFTP/端口转发待需求（[Shell功能执行计划](Shell功能执行计划.md)） | V1.4 ✅ 主体完成（真机验收待人工） |

### 1.2 功能优先级

```
V1.0   串口收发 + 端口管理(含设备名) + TCP 连接 + 会话日志 + 多帧定时发送（WPF 骨架 + MVVM）✅
V1.1 ★ 协议帧解析引擎 + 校验算法库 ✅（41 单测全过：校验标准向量 + 解析全边界）
V1.2    数据级时序图（ScottPlot）
V1.3    发送历史 / 热插拔 / 多端口标签 / 流控（RTS-CTS）
V1.4 ★ 终端/Shell 功能线：M1 串口/TCP VT 终端模式 ✅ → M2 SSH 远程会话 → M3 多会话 → M4 Telnet/本地终端/SFTP
V2      I2C 后端（FTDI.FTD2XX_NET 官方包）+ 事务解析 + L2 序列图
V3      CAN 后端（Peak.PCANBasic.NET 或 slcan）+ DBC 解析 + 信号曲线
预留    电平级波形（L3，逻辑分析仪接入）
```

## 二、UI 设计

### 2.1 主窗口布局（V1.0 实际：左主列 + 右多帧面板，GridSplitter 可调）

```
┌────────────────────────────────────────────────────────────────┐
│ 串口[COM3▼][刷新] 波特率[115200▼] 数据位[8▼] 停止位[1▼] 校验[无▼] [打开]│
├────────────────────────────────────────┬─┬──────────────────────┤
│ 接收区                                  │▎│ 多帧发送（内容｜备注 同行）│
│ [☑时间戳][☑HEX显示][☑记录日志][日志位置]│▎│ # 内容     备注     HEX周期循环 发送 │
│ [打开目录][清空]                        │▎│ 1 AA 55 01 复位命令  ☑ 1000 ☐ [发送]│
│ ┌────────────────────────────────────┐│▎│ 2 read_reg 读寄存器  ☑  500 ☑ [发送]│
│ │[15:04:05.123] ← AA 55 01 00 0F    ││▎│ ...                   │
│ │[15:04:05.201] → AA 55 02 00 10    ││▎│                       │
│ └────────────────────────────────────┘│▎│ [添加][删除选中][清空] │
├────────────────────────────────────────┤▎│ [添加][删除选中][清空] │
│ 发送区 ☑多帧面板|☑HEX显示 ☑HEX发送|      │▎│                       │
│ ☑定时发送[1000ms]                        │▎│                       │
│ ☐RTS ☐DTR  ●CTS ●DSR                    │▎│                       │
│ [AA 55 01 00 0F............] [发送]     │▎│                       │
│                              [清空]     │▎│                       │
├────────────────────────────────────────┴─┴──────────────────────┤
│ 状态栏: 就绪                          RX: 1024  TX: 128         │
└────────────────────────────────────────────────────────────────┘
```

> V1.2/V1.3 引入 AvalonDock 后，接收区/波形区升级为停靠文档标签页，多帧面板可停靠/浮动。

### 2.2 布局要点

- **多帧面板可隐藏**：「发送区」第一排「多帧面板」开关（2026-09-01 迁入发送区，2026-09-17 与 HEX 显示/HEX 发送/定时发送同排），隐藏时右栏与分隔条完全收起、接收区占满全宽；显隐状态记忆（Config/ui_settings.json），拖拽宽度为会话级记忆（`_framesPanelWidth`，不落盘）
- **主发送区定时发送 + 发送区改版（2026-09-17）**：发送区控制分两排——第一排发送设置（多帧面板 | HEX 显示 ☑ HEX 发送 ☑ | 定时发送 ☑ + 周期 ms 输入框，默认 1000 最小 50），第二排引脚（RTS/DTR/CTS/DSR，仅串口模式显示，TCP 整排收起）；**HEX 显示复选框由顶部「帧解析 · 接收显示」条迁入第一排**（与 HEX 发送并排），顶部条保留时间戳/跟随最新/日志/过滤/波形；**发送/清空按钮竖排于输入框右侧**（清空=清空发送输入框 `ClearTxCommand`，新增），输入框填满剩余宽度高度、Enter 发送不变；底部条行高最终 215（MinHeight 同值钉死：串口配置四行内容 ~148 DIP + 标题/边距 ~31 → 余量 18 DIP；曾用 205 余量仅 8 DIP，用户反馈底部整行「连接」按钮仍被削数像素——显示缩放取整/字体度量差异即可吃掉 8 DIP 余量，实测加大后按钮下方间隙 15 phys→23 phys；**2026-09-19 起随连接方式动态：SSH 五行内容抬至 264 且窗口 MinHeight 联动 710，见本节「SSH 认证明细行可见性 + 底部条动态行高」条**）。全局 ComboBox 增加 MaxDropDownHeight=340（约 10 行）：底部条贴近屏幕下缘时下拉弹层不再伸出被裁，改出滚动条。**串口配置改整行拉通表单（2026-09-17 终版）**：四行——串口设备下拉弹性填满 + 刷新右置（TCP 为地址填满 + 端口右置）/ 连接方式下拉弹性填满 / 波特率·数据位·停止位·校验四参数一行排开（下拉各自贴内容宽，局部覆盖全局 ComboBox MinWidth 90→44，等宽列会让「8/1.5/无」下拉撑出假空白）/ 连接·断开主按钮整行宽收底；面板宽度贴合最宽行（串口下拉 MinWidth 220 / MaxWidth 380——取 300 会中途截停在刷新按钮前留缝，须 ≥ 面板宽-标签-刷新 ≈ 330），每行都满宽 → 无横向空白，行距均匀铺满底部条高度；面板 ~520→~444 DIP，发送区相应加宽。定时发送勾选即按当前输入**立即发首帧**、之后按周期自动重发，取消即停；复用多帧循环的同一 50ms 调度节拍（`_cyclicTimer`/`CyclicTick`），内容为空或 HEX 非法时跳过本轮、下周期自动重试（不打断节奏、循环发送不刷状态栏）。开关与周期持久化到 ui_settings.json（`TxCyclic`/`TxPeriodMs`）
- **预览带式分隔条拖动（2026-09-12）**：拖动中两面板列宽保持起始值完全静止，仅预览层跟手（置顶灰竖线 #ADADAD + 60% 半透明 #F0F0F0 预览带 = 按钮同款灰、落点后右面板区域 + 灰底灰边宽度标签），松手一次应用落点宽度（ESC/捕获丢失走 `DragCompleted.Canceled` 还原）。根因：本机虚拟显示驱动对「连续失效的重内容子树」呈现带 ±1~2px 偏移（跟手毛刺抖动），静止即零失效零抖动；列宽计算由应用接管（GridSplitter 内部增量在本机被 DPI 异常放大数百倍），按鼠标 DIP 位移推算、设备像素空间取整、左+分隔+右恒等于根宽
- **左右间隔 12（2026-09-12 统一）**：分隔条带宽 12（`SplitterWidth` 常量，参与列宽推算/钳制）+ 右面板左 Margin 0 = 多帧面板与接收框横向间隔 12 DIP，与底部「发送区↔串口配置」中列间隔 12 一致（原 8+10=18 偏宽，用户反馈后统一）
- **窗口缩放不切边（2026-09-12）**：绝对列宽之和会垫高根 Grid 最小宽，窗口缩小时 arrange 被 MinWidth 顶回、Grid 溢出窗口 = 右栏伸出右缘被切（HEAD 同机制的老问题）。`SizeChanged` 内用 `GetClientRect` 现取客户区设备宽算目标根宽（不读会被溢出污染的 `RootGrid.ActualWidth`），事件内一次写完右栏钳制 + 左列吸收余量，无中间态（本机 200~1700Hz 消息风暴下任何「先收后放」的中间态都会上屏闪烁）
- **AvalonDock 停靠体系**（IDE 式，V1.2/V1.3 随多面板引入）：接收区/波形区为文档标签页，端口配置/发送为工具窗格
- **解析高亮**：接收区支持"帧模式"——按模板着色帧头/命令/数据/校验域，校验失败红色标记（V1.1）
- **最小窗口宽 1290（2026-09-19 由 1220 上调）**：顶部功能条加入「终端」复选框后实测不换行阈值 ~1246（原 2026-09-01 阈值 ~1185 不含「终端」），+ 字体度量余量取 1290；窄于该值这些行折行，MinWidth 与默认宽一致
- **SSH 认证明细行可见性 + 底部条动态行高（2026-09-19，截屏实测修复）**：`IsSshPasswordAuth`/`IsSshKeyAuth` 曾漏 `IsSsh` 与门，「密码」行在串口/TCP 模式也常驻 → 面板四行变五行，把「连接」按钮挤出 215 底部条、底缘被削；修复 = VM 属性补 `IsSsh &&` + `OnConnTypeIndexChanged` 补发两属性通知。底部条行高随之改动态：串口/TCP 215（四行）⇄ SSH 264（五行内容 ~239 DIP + 余量 ~25——首版取 248 余量仅 ~9，用户实测按钮底缘仍被削，显示缩放取整/字体度量差异所致），`MainWindow.ApplyBottomBarHeight` 监听 `ConnTypeIndex` 切换时重置 MinHeight/Height（分隔条手动拖到 380 上限的能力不变）；**窗口 MinHeight 同步联动 660 ⇄ 710**——窗口低于「各行 MinHeight 之和」时 Grid 无视行 MinHeight 星比压缩，底条被压扁照样削按钮。同轮顺带修正：发送区按钮行 `Grid.Row="4"` 越界（仅 4 行定义 0~3，靠 WPF 钳到末行才没露馅）改为显式 3；终端窗工具条改「按钮全停靠 + 提示文本末位填充省略号」，`MinWidth` 420→600（原值下右簇把新建按钮挤出可视区）
- **图表面板独立窗口（2026-09-02）**：时序图 / 字段曲线整体迁出主窗为 `ChartWindow`（Owner=主窗、懒创建常驻、Hide/Show 切换）；主窗顶部「波形」复选框控制显隐，**默认关闭**（2026-09-03：启动不自动弹图表窗，用户按需勾选，勾选状态仍记忆），点图表窗 X ⇔ 取消勾选；初始位置贴主窗右侧、之后记住用户拖出的位置；隐藏期间渲染暂停（缓冲/采样继续累计），重开补一帧。主窗 Closing 时强制真关图表窗，否则其「X=取消勾选」语义会取消关闭导致进程不退
- **字段曲线页「？」使用说明（2026-09-03）**：「？」为 20px 圆形小按钮，位于页签条上、紧随「字段曲线」页签之后——是页签的**平级**元素而非包含在页签框内（自定义 TabControl 模板：`StackPanel{ TabPanel + Button }`；用户要求）。与标题像素级对齐；自定义按钮模板需显式清零全局按钮样式的 MinWidth/MinHeight/Padding（Setter 不走模板且 MinWidth 会顶翻局部 Width）；模板名称域内的按钮不能被外部 Popup 用 ElementName 绑定 PlacementTarget，由 Click 处理器以 sender 指定。点击弹出本页使用说明弹层（WPF `Popup` + 圆角卡片/阴影，同全局 ToolTip 风格放大版；`StaysOpen=False` 点窗口内别处即关）——内容：数据来源（仅解析成功帧采样）、取值规则（模板/命令前缀/偏移/宽度/字节序/缩放单位）、启停与自动保存、跟随/缩放平移、图例英文命名建议
- **图表窗销毁/重建 + 补帧（2026-09-03，终版）**：「波形」取消勾选即销毁图表窗、重新勾选新建（记忆位置尺寸）——不使用 Hide/重显（补帧不可靠）；空闲（无收发数据）时渲染事件不触发且 ScottPlot 首帧不主动重绘，`OnContentRendered/SizeChanged/Activated/SelectionChanged` 延迟补帧 + 1Hz 保底。**坐标轴消失事故的真正根因**：帮助弹层 Popup 曾放在字段曲线页 DockPanel 内、画布 Border 之后——DockPanel 最后一个子元素接管填充职责，0 尺寸的 Popup 把默认 Dock=Left 的画布挤成 0 宽（时序图页无弹层故正常）；Popup 已移至窗口根 Grid，画布 Border 保持 DockPanel 末位
- **图表窗最小尺寸实测标定（2026-09-03）**：`OnSourceInitialized` 时清掉最小约束 → `SizeToContent=WidthAndHeight` 逐页（时序图/字段曲线）`UpdateLayout` 量出**含标题栏/边框的完整显示窗口尺寸**，取两页最大值写回 `MinWidth/MinHeight`；两块画布 `MinHeight=200` 为图形可读性地板（2026-09-03 用户要求：配置表恒定 96px，画布不许被压到看不清——字段曲线页是高度瓶颈，最小高 = 表 96 + 画布地板 200 + 固定开销），本机实测最小 **744×485**（SetWindowPos 强缩 400×200 被钳制），窗口无法缩到任何元素被裁切或图形不可读；随字号/DPI/系统主题自动重标定，无需手维护魔法数
- **各框互不覆盖（2026-09-01 V1.2.x）**：左列容器、右列工具面板均 `ClipToBounds`——任何缩放下越界渲染在自己框内裁切，绝不覆盖相邻框架。图表迁独立窗口后左列仅剩接收区单框架（原「接收区/图表」比例行 2.4\* : 1.6\* + GridSplitter 方案随之退役）；窗口 `MinHeight=660` 保留（WPF Grid 在可用空间小于各行 MinHeight 之和时会**无视 MinHeight 按星比压缩**的教训沿用）
- **等宽输入 28px 基线（2026-09-01）**：`Mono` 样式只换字体族（Cascadia 行高偏大，靠收紧行距落回基线），表格/工具条中 Mono 框与普通框同高、字号全局统一
- **浅色主题（SSCOM 经典风格）**：白色背景 + 标准 Windows 控件观感（用户指定，2026-08-29 由深色改浅色）；**2026-09-19 起各窗口背景色可自定义**（外观设置集中管理，见下条「外观自定义」）
- **外观自定义（2026-09-19）**：主窗状态栏「外观…」弹外观设置窗——主窗口（SSH/Telnet 等对话框跟随）/ 终端窗口 / 波形窗口 / 模板编辑器 4 个窗口背景各自独立 + 终端内容底色（引擎默认背景，ANSI 调色板不动；已开标签经 `TerminalView.SetContentBackground` 走 `ColorPalette.SetBackground` 即 OSC 11 同路径实时换底，宿主边框与未连接遮罩同步）。**`Appearance` 单例（Services/）作全窗口统一绑定源**：各窗 DataContext 类型不一（TerminalWindow 无 DataContext、对话框各有宿主数据），`{Binding Source={x:Static s:Appearance.Instance}, Converter=HexToBrush}` 是唯一路径一致的方案；MainViewModel 桥接 ui_settings.json（加载写单例 + `IsValidHex` 校验坏值回退默认，订阅单例 PropertyChanged 变化即落盘）；终端主题由静态共享改 `BuildTheme()` 每实例新建（背景色取自单例，防后建实例改色牵连）
- **控件级配色自定义（2026-09-20）**：外观设置窗新增「控件颜色」区段 8 项——面板/显示框底色（输入框/下拉框/接收显示框/勾选框方框/分组卡片）、按钮底色、边框色（自动派生控件边框加深 0.1393 + 分隔线调亮 0.463 + 滚动条滑块/分隔条手柄）、正文文字、次要文字、强调色（聚焦边/勾选勾底/下拉展开边/主按钮）、悬停高亮、选中/按下；显示框/勾选框/下拉框/按钮四类全覆盖，每项弹层自带「恢复默认」，ui_settings.json 持久化（IsValidHex 坏值回退默认）。**机制 = 画刷整实例替换 + 引用方全量 DynamicResource**（11 个 XAML 共 181 处）：`App.ApplyControlColor` 订阅单例 PropertyChanged → `Resources[key] = new SolidColorBrush(color)`，DynamicResource 引用方（含样式/模板/触发器/弹层）自动级联。**废弃方案与根因**：原「StaticResource 共享画刷实例 + 突变 Color」在样式/模板密封时画刷被冻结，突变抛 `InvalidOperationException`——加载期被 `LoadUiSettings` 的 catch 静默吞掉（首项即中断后续加载）、点击期吞在绑定源回写路径（单例字段已更新但落盘订阅被跳过，JSON 只剩下一窗背景色触发的保存才把全部值写盘）——现象 = 改色完全无效且无报错；教训：运行期换主题在 WPF 只有 DynamicResource + 实例替换一条可靠路径。深色面板需自行搭配文字色（由用户选色负责）
- **外观预设主题组 + 自定义取色（2026-09-21）**：「主题预设」下拉一键整套应用（经典浅色/深色夜间/护眼绿/高对比，`AppearancePreset` record 打包 13 项含终端内容底色；`ApplyPreset` 逐属性写入走既有级联，预设选中态不落盘——`MatchPreset()` 按当前 13 值全等反推，任一单项微调破坏全等即自动跳「自定义」）；`ColorChipButton` 调色板弹层新增自定义色输入：HEX（RRGGBB 兼容 # 前缀）⇄ RGB（0-255）双向联动 + 实时预览块 + 应用按钮/Enter 两路生效（弹层保持打开可连续微调）；外观设置窗改两列排版（左窗口背景 5 项/右控件颜色 8 项，窗口高 1077→709）。**踩坑**：① 色值输入框必须 `InputMethod.IsInputMethodEnabled="False"`——中文输入法激活时字母键进拼音候选（实测 C 键变「成」），Enter 被 IME 吃掉；② TextBox 单行模式 KeyDown 收不到 Enter（被控件标记 handled）→ 用 PreviewKeyDown；③ 弹层含输入框须 `Focusable=True`（StaysOpen=False 不受影响）；④ ColorChipButton 标签文字原沿窗口默认黑色，深色主题黑上黑不可读——改 `DynamicResource TextBrush`
- **外观设置排版重排 + HSV 色图取色（2026-09-25）**：① 设置窗两 StackPanel 改单 Grid 双列（列间 14px 缝）——13 项色块按钮全部拉伸列宽（原宽度随文字长度参差不齐）、行高共享保证左右逐行对齐（按钮 `VerticalAlignment=Top` 防 Auto 行高波动被拉伸变形）；`ColorChipButton` 新增 `ShowHex` DP（默认关，主窗发送/接收色芯片在 GroupBox 标题行保持紧凑）在按钮右端直显 HEX 色值（等宽灰字，填充拉伸后右侧视觉重心）；色块 14×14→20×14 扁宽块（原正方块带边框酷似未勾选 CheckBox）；说明文字收进左列尾部三行空位（RowSpan），底边与右列末按钮基本齐平。② 取色弹层顶部新增 HSV 色图：S/V 平面 = 横轴白→当前色相纯色渐变叠纵轴透明→黑渐变（两 Rectangle 纯 XAML 叠加，无位图无新依赖）+ 白描边投影十字圈，其下虹彩色相条（7 停点渐变 + 竖标）；拖动走 `CaptureMouse`（按住拖出图外持续取色），**拖动中只刷预览与 HEX/RGB 输入框、松手才写回 Hex**（改色订阅链 = 全量重绘 + 落盘，拖动 60Hz 提交会刷爆）；弹层打开/手输 HEX/RGB 时十字圈与色相标按当前色跟随（Opened 时 `Dispatcher.BeginInvoke` 等布局完成再读 ActualWidth）；**灰阶色 S≈0 不带色相信息，沿用上次色相避免色图底色跳红**；`SyncMapFromColor` 不写输入框（RGB→HSV→RGB 往返有 ±1 舍入漂移，展示值以 Hex 真值为准）
- **统一控件风格体系**：TextBox / ComboBox / Button / CheckBox 全部圆角（4px 框 / 3px 勾选框）+ 蓝色高亮（悬停/聚焦/展开）；全局 28px 控件高度基线保证混排行垂直居中；下拉弹层空列表时叠「（空）」占位（2026-09-19：否则缩成几像素细缝像渲染残影）；接收/发送框顶部对齐 + 自动换行 + 纵向刷屏（2026-08-29 UI 打磨定稿）
- **状态可见**：标题栏连接状态 + 底部状态栏（RX/TX 计数、帧统计）

### 2.3 后续页面

| 视图 | 内容 | 阶段 |
| --- | --- | --- |
| 帧解析模板管理 | JSON 模板编辑器 + 校验测试台（输入 hex 即时预览解析结果） | V1.1 |
| I2C 面板 | 设备扫描结果、寄存器表格读写、事务序列图 | V2 |
| CAN 面板 | DBC 加载、报文列表、信号曲线（ScottPlot 多通道） | V3 |

## 三、工具实现计划

| 阶段 | 内容 | 关键交付物 | 状态 |
| ---- | ---- | ---------- | ---- |
| V1.0 | WPF 骨架：项目结构、MVVM 模式（CommunityToolkit）、串口后端（SerialPortStream + WMI 设备名）、TCP 后端、收发控制台 UI、会话日志、多帧发送（手动/每帧独立周期循环，JSON 持久化）、自包含发布脚本 | `SerialTool.slnx`、`Backends/SerialBackend.cs`、`Backends/TcpBackend.cs`、`SendFrameViewModel.cs`、发布脚本 | ✅ 完成（2026-08-29：构建零警告、单测 12/12、启动冒烟通过） |
| V1.1 | 帧解析引擎：缓冲扫描解析器（粘包/半包/重同步）、校验库（CRC 查表 + XOR/累加和）、JSON 模板 + 编辑器窗口、接收区帧结构化显示、帧✓/✗ 统计 | `Core/Checksum/Checksums.cs`、`Core/Framing/{FrameTemplate,FrameParser}.cs`、`TemplateEditorWindow` | ✅ 完成（2026-08-29：41/41 单测、构建零警告、冒烟通过） |
| V1.2 | 波形面板（ScottPlot.WPF 时序图：帧/块散点时间线 + 跟随/缩放平移） | `MainWindow` 波形面板 | ✅ 完成（2026-09-01：构建零错误、单测 48/48、冒烟通过） |
| V1.2.x | 功能增强四件套 + 布局独立性：状态栏速率/时长统计 + 接收过滤（视图层）；RTS/DTR 控制 + CTS/DSR 指示灯；帧字段实时曲线（图表第二页）；自动应答器（右列第二页）；工具条 WrapPanel 化 + 左列比例行/Splitter/ClipToBounds 各框独立 + 等宽输入 28px 基线统一 | `Core/RxFilter.cs`、`Core/Framing/{FieldPlot,AutoReply}.cs`、`ViewModels/{FieldPlotViewModel,AutoReplyViewModel}.cs` | ✅ 完成（2026-09-01：92/92 单测、启动冒烟；[执行计划](功能增强执行计划.md)） |
| V1.2.x | 图表面板迁独立窗口：时序图 / 字段曲线从主窗左列迁出为 `ChartWindow`（「波形」复选框显隐、X=取消勾选、位置与缩放状态保留、隐藏期渲染暂停重开补帧）；主窗左列简化为接收区单框架 | `ChartWindow.xaml(.cs)`（MainWindow 图表代码整体迁入） | ✅ 完成（2026-09-02：构建零错误、92/92 单测、启动冒烟；修复 XAML 初始化期设 Owner 崩溃） |
| V1.2.x | 接收区 TX/RX 行颜色自定义：接收框由 `TextBox` 换 `RichTextBox`（TextBox 无 Document/TextPointer API，不支持逐行着色——原已知局限随之解除），渲染管线改分段载荷 `RxSeg(Text, IsTx)` 按方向着 Paragraph；接收区标题栏右侧「发送色/接收色」色块按钮 + 16 色调色板弹层 + 恢复默认（自绘 `ColorChipButton`，无新依赖）；默认发送 #0078D7 / 接收 #1E1E1E，配置存 ui_settings.json；换色即全量重绘；截断改段落粒度删除保色 | `Controls/ColorChipButton.xaml(.cs)`、`Converters.cs`（HexToBrush）、MainWindow / MainViewModel 渲染管线 | ✅ 完成（2026-09-05：构建零错误、92/92 单测；TCP 回环实测双色/换色重绘/恢复默认/持久化/旧配置回退/>800K 截断保色） |
| V1.2.x | 图表窗默认关闭（用户按需勾选，勾选状态仍记忆）+ 最小尺寸实测标定（`SizeToContent` 逐页量出完整显示尺寸写回 Min，画布可读性地板 200px，随 DPI/字号自适应，SetWindowPos 强缩实测被钳制在 744×485） | VM `UiSettings` 默认值、`ChartWindow.CalibrateMinSize` | ✅ 完成（2026-09-03：92/92 单测、启动验证不弹窗、EnumWindows/钳制实测） |
| V1.2.x | 各界面背景色自定义：`Appearance` 单例（Services/）+ 8 窗口 XAML 统一 x:Static 绑定（4 主窗口各自独立 + 4 对话框跟随主窗）；终端内容底色 `BuildTheme()` 实例化注入 + 已开标签 `ColorPalette.SetBackground` 实时换底（宿主边框/未连接遮罩同步）；状态栏「外观…」入口 + `AppearanceWindow` 集中设置（复用 ColorChipButton）；ui_settings.json 持久化（IsValidHex 坏值回退默认） | `Services/Appearance.cs`、`AppearanceWindow.xaml(.cs)`、各窗口 Background 绑定、`TerminalView`/`TerminalWindow` | ✅ 完成（2026-09-19：构建零错误、107/107 单测；截图实测五路改色/重启持久化/坏值回退/终端已开标签实时换底） |
| V1.2.x | 控件级配色自定义：外观设置新增 8 项控件色（显示框/勾选框/下拉框/按钮全覆盖）；机制 = 画刷整实例替换 + 11 个 XAML 181 处引用全量 DynamicResource（废弃 StaticResource+突变方案——样式/模板密封冻结画刷实例，突变抛异常被吞静默失效） | `App.xaml(.cs)`（DynamicResource 样式体系 + SetBrush 实例替换）、`AppearanceWindow.xaml`（控件颜色区段）、`TerminalWindow.xaml.cs`（SetResourceReference 动态边框） | ✅ 完成（2026-09-20：构建零错误、107/107 单测；截图实测五路改色/下拉弹层级联/重启持久化/恢复默认复原派生/坏值回退） |
| V1.4.x | 外观预设主题组 + 自定义取色：4 套内置主题一键整套应用（13 项打包含终端内容底色）+ 单项微调自动跳「自定义」（选中态不落盘、13 值反推）；调色板弹层 HEX⇄RGB 双向联动输入（禁 IME + PreviewKeyDown 收 Enter）+ 应用保持弹层连续微调；外观设置窗两列排版（1077→709 高） | `Services/Appearance.cs`（AppearancePreset/ApplyPreset/MatchPreset）、`AppearanceWindow.xaml(.cs)`（预设区 + 选中态推导）、`Controls/ColorChipButton.xaml(.cs)`（自定义色输入区） | ✅ 完成（2026-09-21：构建零错误、107/107 单测；截图实测四预设套用/微调跳自定义/HEX 与 RGB 双向/Enter 与应用两路/重启持久化/高对比可读性） |
| V1.4.x | 崩溃/卡死诊断日志：`CrashLogger`（Services/）——全局异常三通道（DispatcherUnhandled/AppDomain Unhandled/UnobservedTask，致命去重）+ UI 挂起看门狗（2s 心跳、15s 阈值、恢复追记）→ `Logs/crash/*.log`（异常全文+环境）+ 同名 `.dmp`（dbghelp MiniDumpWriteDump 自转储，全线程栈）；exe 目录不可写回落 LocalAppData；session.log 启动/正常退出标记；隐藏自检键 Ctrl+Alt+F12 崩 / Ctrl+Alt+Shift+F12 挂起 | `Services/CrashLogger.cs`、`App.xaml.cs`（Install）、`MainWindow.xaml.cs`（自检键） | ✅ 完成（2026-09-21：实测挂起出 dump+恢复追记/崩溃单份报告带行号栈/正常退出无 crash 文件，107 单测全绿） |
| V1.4.x | 终端交互体验修复：Shift+左键强制文字选择 / Shift+右键强制弹菜单（绕过应用鼠标跟踪，TUI 下可选字复制）；Ctrl+V 粘贴（原仅 Ctrl+Shift+V）；备屏+无鼠标跟踪滚轮模拟 Up/Down 键（每格 3 行）；Shift+滚轮——非备屏强制滚回滚 / 备屏模拟 PgUp/PgDn；补全鼠标转发（左键 Down/Up/Drag、右键 Down/Up，原仅滚轮） | `Controls/TerminalView.cs`（OnPreviewKeyDown / OnMouseLeftButtonDown/Move/Up / OnMouseRightButtonDown/Up / OnMouseWheel） | ✅ 完成（2026-09-25：构建零错误；用户本地终端实测 vim/less 滚轮与 Shift 选择复制确认） |
| V1.4.x | 外观设置体验优化：设置窗单 Grid 双列等宽排版（13 按钮统一列宽逐行对齐 + 右端 HEX 直显 `ShowHex` + 色块 20×14 + 说明文字归位左列空位）；取色弹层升级 HSV 色图（S/V 双层渐变平面 + 虹彩色相条 + 十字圈拖动，纯 XAML 零新依赖；拖动刷预览松手写回；跟随当前色 / 手输 HEX-RGB 同步；灰阶沿用上次色相） | `AppearanceWindow.xaml`、`Controls/ColorChipButton.xaml(.cs)`（ShowHex DP / 色图模板 / HSV 换算与拖动取色） | ✅ 完成（2026-09-25：构建零错误、107/107 单测；截图实测等宽对齐/色图点击取色即提交（强调色 →#365F80 预设跳自定义）/十字圈与色相标跟随/恢复默认还原/HEX 右显） |
| V1.4.x | 终端 IME 候选框跟随光标：自绘控件走 WPF 默认 TSF 文本存储时输入法查不到光标矩形，候选框/预编辑串丢屏幕 (0,0)；IMM32 设位对纯 TSF 输入法（微信输入法）无效、IsInputMethodSuspended 反而掐断提交链路（两轮失败教训）——最终 **内联预编辑隐藏 TextBox**（xterm.js 架构 + Windows Terminal 观感）：`TerminalView` 基类改 `Grid` 内藏 TextBox 承接焦点（背景/光标全透明、空框不可见；WPF TextBox 自带完整 TSF 支持，候选框锚其光标），框体每帧叠到终端游标格、宽取右缘（**组合期冻结锚点**——TUI 高频重绘光标抖动会让输入法弃用内联改画自己的蓝色面板；**`Style=null` 剥离全局隐式 TextBox 样式**——App.xaml 全局样式的 `IsKeyboardFocusWithin` 触发器直改模板 Border 画蓝色 1.5px 圆角框，局部 `BorderThickness=0` 拦不住、空闲态即常驻显形）——组合期预编辑串以终端字体色**内联显示在光标行**（下划线、`SelectionBrush` 透明去掉 WPF 默认蓝色组合高亮）、候选条紧跟其下，终端光标让位；直打字符 PreviewTextInput 直发（框内不存字），IME 上屏 TextChanged 转发后清空（组合状态 TextCompositionManager 事件 + WM_IME 钩子双通道；微信输入法上屏时 TextChanged 先于 Final 事件——复位后 Background 优先级补冲刷 ScheduleImeFlush，两种时序都正确），特殊键走原 PreviewKeyDown 隧道零改动，`Focus()` 转隐藏框宿主无感 | `Controls/TerminalView.cs`（Grid 基类 / BuildImeBox / FlushImeBoxText / ScheduleImeFlush / UpdateImeBoxPosition / ImeWndProc） | ✅ 完成（2026-09-26：SendInput 驱动微信输入法端到端实测——预编辑+候选条跟随光标、「你好」上屏（含时序修复复验）、ASCII 回归通过） |
| V1.3 | 发送历史、热插拔（WM_DEVICECHANGE）、多端口标签、流控（RTS-CTS） | — | 规划 |
| V2 | I2C 后端（FTDI.FTD2XX_NET）、扫描/寄存器读写、L2 序列图 | `Backends/I2cBackend.cs` | 规划 |
| V3 | CAN 后端（PCAN 或 slcan）、DBC 解析、信号曲线 | `Backends/CanBackend.cs`、`Core/Dbc` | 规划 |
| 预留 | 电平波形（L3）：逻辑分析仪 SDK（Saleae API 等）独立 `Daq/` 模块 | — | 待硬件 |

### 3.1 解决方案结构（C#）

```
SerialTool/
├── SerialTool.slnx                # .NET 10 XML 解决方案格式
├── src/
│   ├── SerialTool.App/            # WPF 主程序（视图、视图模型；AvalonDock 随 V1.2/V1.3 多面板引入）
│   ├── SerialTool.Core/           # 解析引擎（纯 C# 类库）：Hex / Checksum(V1.1✅) / Framing(V1.1✅) / DBC(V3)
│   └── SerialTool.Backends/       # 硬件后端：IBusBackend 接口 + Serial/Tcp(V1) / I2c(V2) / Can(V3)
├── tests/
│   └── SerialTool.Core.Tests/     # xUnit 单测（Hex 12 用例；V1.1 加校验向量、粘包/半包）
├── scripts/
│   └── publish.ps1                # dotnet publish 自包含单文件（win-x64）
└── Docs/
```

### 3.2 统一后端接口（V1.0 已实现）

```csharp
// 总线无关核心接口
public interface IBusBackend : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    IReadOnlyList<DeviceInfo> Scan();
    event EventHandler<TimedData>? DataReceived;  // {Timestamp, Bytes} 事件流（读取线程触发）
    event EventHandler<string>? ErrorOccurred;    // 拔出/断线等异常中断
    void Write(ReadOnlySpan<byte> data);          // 写入（各总线通用，上移至基接口）
    void Close();
}

// 总线特有的打开参数放各自后端接口（避免参数异构塞基接口）
public interface ISerialBackend : IBusBackend { void Open(SerialPortConfig cfg); }
public interface ITcpBackend : IBusBackend    { void Open(TcpConfig cfg); }      // Host/Port
```

- UI 通过"当前活动连接 _active"统一操作串口/TCP，收发/日志/多帧零差异
- SerialBackend / TcpBackend（V1 ✅）→ I2cBackend（V2）→ CanBackend（V3）插拔扩展

### 3.3 关键技术要点

- **帧解析（v2 字段链）**：帧结构 = 帧头 + 字段链（cmd/fixed/length/data 任意组合，长度域任意位置、定长或帧尾扫描）+ 校验域（字节序可配）+ 帧尾；TryParse 纯函数化，多模板并行仲裁（校验通过的帧优先于错误帧，错误帧整体消费防级联误报）
- **CRC 查表法**：预生成查找表，高速率下性能无忧；标准测试向量验证；校验值线上字节序可配（高前/低前）
- **UI 线程模型**：串口数据经 `Channel<T>` 缓冲，定时批量（如 50ms）投递 UI，避免高波特率刷爆调度器
- **热插拔**：WPF 窗口过程捕获 `WM_DEVICECHANGE`，提示并自动刷新端口列表

## 四、测试

### 4.1 单元测试（xUnit，无硬件依赖）

| 模块 | 测试项 |
| ---- | ------ |
| Checksum | CRC8/16/32 标准测试向量、XOR、累加和；边界（空数据/单字节/长数据） |
| Framing | 完整帧、粘包（一读多帧）、半包（多读一帧）、错帧头、校验失败、长度域异常 |
| Templates | JSON 模板序列化/反序列化往返、非法模板报错 |
| DBC（V3） | 标准 DBC 解析、信号提取正确性 |

### 4.2 硬件测试

| 设备 | 用途 |
| ---- | ---- |
| USB-TTL ×2 | TX-RX 回环；双模块互发 |
| RS485 模块（MAX485） | A/B 差分半双工 |
| 被测嵌入式板卡 | 真实协议帧解析联调 |
| FT232H（V2 起） | I2C 扫描/读写（可先 Arduino 模拟从机） |
| CANable 或 PCAN（V3 起） | CAN 收发、DBC 联调 |

硬件测试项：波特率矩阵（300–2000000 预设 + 自定义值）、7/8 数据位、各校验组合、大报文（>4KB）、921600 持续收发丢包检查、热插拔恢复。

### 4.3 发布验收（每版）

1. `publish.ps1` 产出单 exe，**干净无 .NET 的系统**（虚拟机验证）双击可运行
2. 首次运行 SmartScreen 提示场景记录（预期：未签名蓝警告，选择"仍要运行"）
3. exe 所在目录无额外文件生成（或仅可选配置文件），zip 拷贝到任意目录可用

### 4.4 V1.0 手动测试清单

1. 启动 → 主窗口正常显示停靠布局
2. 扫描端口（无设备时空列表提示）；插入 USB-TTL → 刷新可见
3. 打开 COM3@115200 → 状态栏实时更新；参数错误（如占用端口）→ 状态栏报错
4. 回环 TX-RX：发送 HEX `AA 55 01 00 0F` → 接收区原样显示（RX 计数 +5）
5. ASCII 模式收发文本正确；HEX 模式非可见字符处理正确
6. 高波特率压测：921600 连续收发 1 分钟无 UI 卡顿、无丢字节
7. 关闭端口/直接拔出设备 → 无崩溃，状态正确复位
8. 发布产物在干净虚拟机运行通过
