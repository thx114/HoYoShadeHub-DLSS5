# Rocket 本地版原神防报错调查（2026-10-08）

## 结论

检查对象为 `D:\APPS\Rocket 管理器最新版`。本地程序的显示版本数值为 9.22；界面格式串包含“拖入修正版 20261005”，PE 编译时间戳为 2026-10-06 12:52:55 UTC（北京时间 20:52:55）。这些是本地文件信息，未确认它是否也是线上最新发行版。

界面将“原神防报错”说明为“解决添加 Mod 后游戏内弹出错误代码的问题”，同时明确显示需要购买服务。

已定位网络过滤主体：代码位于 Rocket.exe 中，依赖 WinDivert.dll / WinDivert64.sys。它观察 FLOW 事件来维护连接与 PID 的对应关系，在 NETWORK 层解析 IPv4 TCP / UDP 包，按端口及目标 PID 判断是否丢弃，并有暂停放行、停止与统计处理。

**过滤规则已完整还原**（详见 [`_research/rocket-error-prevention/FILTER-RULES.md`](../_research/rocket-error-prevention/FILTER-RULES.md)）。NETWORK 层表达式模板为 `(outbound or inbound) and (tcp.DstPort == P or tcp.SrcPort == P or udp.DstPort == P or udp.SrcPort == P ...)`；正常调用传入端口 **8999**，端口参数为 0 时回退为 **{8999, 80}**。规则中没有任何 IP / 域名 / 报文内容条件，只有端口与「包所属进程 PID 是否等于目标 PID」。命中的 TCP 包不转发原包，而是把包改写为「来自服务器、发往游戏」的 **TCP RST** 注入回去；命中的 UDP 包直接丢弃。全程序只有 2 个 WinDivertOpen 调用点（FLOW 层 `ip`、NETWORK 层上述表达式）。

单独复制 WinDivert 文件无法包含 Rocket 的过滤控制与进程关联逻辑。ys.dll 日志另有管理器授权与等待非系统 d3d11.dll 的流程；目前证据不足以认定该 DLL 与网络过滤的依赖关系，不能当作可单独移植的防报错 DLL。

## 检查范围

静态检查程序导入表、字符串、x64 指令及本地既有日志；没有运行 Rocket，没有操作驱动服务，没有修改其授权状态，也没有对游戏联网行为做试验。因此尚不能证明过滤实际解决了当前游戏版本中的弹窗，也没有验证对登录及正常游戏通信的影响。

## 文件证据

- `Rocket.exe`：16,080,384 字节；无 FileVersion / ProductVersion 资源可读。
- `WinDivert.dll`：47,616 字节。
- `WinDivert64.sys`：94,144 字节。
- `backing/Config/YS/ys.dll`：209,408 字节。
- `backing/Config/YS/ys.log`：2026-10-08 14:52:36 起记录等待管理器授权；14:52:37 记录授权接受，随后等待注入的非系统 d3d11.dll。
- `backing/config.ini` 中已有原神过检测判断 / 原神过滤授权设置。配置中的 true 仅代表保存的状态，不能用于确认当前服务是否有效。

SHA-256 等元数据保存在 `_research/rocket-error-prevention/file-metadata.json`；相关函数反汇编保存在同目录，均来自当前本地样本。

## 定位点

所有 RVA 均以该样本 ImageBase 0x140000000 为基准。

| RVA / 文件偏移 | 证据 | 含义及限制 |
| --- | --- | --- |
| 文件偏移 0xDACC48 | 原神防报错标题 | 对应用户描述的功能 |
| 文件偏移 0xDACC58 | 添加 Mod 后弹出错误代码的说明 | 界面宣称的用途，尚未实测 |
| 文件偏移 0xDACCA0 | 购买服务说明 | 有独立授权条件 |
| RVA 0x1AD170 | WinDivertOpen 的 layer 参数为 2；读取 FLOW 事件 | 连接到 PID 的映射 |
| RVA 0x1AD4D0 | 拼接 inbound / outbound 及 TCP / UDP 源目标端口条件 | NETWORK 过滤表达式构建 |
| RVA 0x1AD920 | NETWORK 捕获、包解析、PID 比较、丢弃或 WinDivertSend 放行 | 核心网络循环；端口复检 0x1ADC77 起，PID 判定 0x1ADCF6 起 |
| RVA 0x1ACC50 | 交换 IP/TCP 地址端口、清 FIN/SYN/ACK/PSH/URG 并置 RST、重算校验和后回注 | 伪 RST 注入 |
| RVA 0x1ACFB0 | 按 (协议, 源地址, 目的地址, 源端口, 目的端口) 双向查表返回 PID | FLOW 表查询 |
| RVA 0x1ADE70 | 游戏.开始过滤 | 前台到管理员后台的开始入口 |
| RVA 0x1AE290 / 0x1AE470 | 游戏.过滤开关 | 暂停 / 恢复入口 |
| RVA 0x1AE650 | 游戏.停止过滤 | 停止入口 |
| RVA 0x2F4EA0 内 0x2F51C6 起 | 开始过滤的后台分发，默认端口 0x2327 | 属于内部协议，未验证为公开可调用接口 |
| RVA 0x2EB949 | 版本显示使用的 double 约为 9.220000267 | 以两位小数显示为 9.22 |

`backing/config.json` 的 3.6.1 版本号属于 Mod 修复工具的配置，应与 Rocket 程序显示版本分别理解。

## 当前 HoYoShadeHub 的接入位置

当前仓库有 UI 启动路径和 GameLaunchPipeline 启动路径。若需求是接入本项目，需让两条路径共享会话管理：在游戏网络连接建立前准备 FLOW 观察，在取得本次游戏 PID 后绑定过滤，游戏退出或启动失败时释放过滤句柄。

WinDivert 官方文档说明 NETWORK 层无法直接取得 PID；FLOW 层可以取得 PID，但只能观察句柄打开之后发生的连接事件。这是启动顺序需要专门处理的理由。官方文档还说明 WinDivertOpen 需要管理员权限。

接入可选择：

1. 联动用户已授权的 Rocket，由 Rocket 管理其服务状态。需要确认公开联动入口或支持的启动流程，当前发现的内部命令不等于公开接口。
2. 开发本项目自己的网络过滤组件。可以实现相同种类的端口 / PID 过滤能力，但要另行验证规则与当前游戏弹窗的关系，不能仅凭静态分析声称已经复现防报错效果。

目前没有更改 Hub 的启动代码；目标产品和授权联动方式仍需用户明确。

## 官方技术参考

WinDivert 2.2 Documentation： https://reqrypt.org/windivert-doc.html ，参考 3 Installing、5.1 WINDIVERT_LAYER、5.3 WINDIVERT_ADDRESS。
