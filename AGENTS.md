# 项目记忆（2026-10-11；中文为主，技术标识保留原文）

## 路径
- H=`D:\CODE\HoyoDLSS5`；A=`D:\CODE\FsrBridgeDepthAddon`；B=`D:\CODE\genshin_fsr_brigde\Dx11FsrBridge`；O=`H\OptiScaler-MFG-Ada\OptiScaler`。
- 测试便携版 T=`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full`；会话日志=`T\log\sessions`。只认最新 session；构建/部署不等于实测。

## 游戏隔离约定
- 原神使用 B 的前置滤镜/桥链路；崩铁走 DX11-only 单 runtime，保留原生/ReShade DEPTH，不加载 B 的深度；绝区零走原生 DX12/Streamline。
- 不强退游戏、不改 ReShade 核心、不清 NR 质量/倍率/分辨率；部署前备份并校验 SHA256。

## 崩铁现状（以 2026-10-11 最新记录为准）
- Home 鼠标修复已在 session `20261011-033145_StarRail_79168` 验证：DX11 runtime 唯一、输入 HWND visible=1、mouse 坐标回到视口范围，Home 可点击。
- DX11 编辑控件目前在 addon settings，不是 ReShade Home 的普通效果列表；若要 Home 内独立区块，属于单独 UI 需求。
- StarRail addon 为 controller-only，FSR depth lookup/upload disabled；不恢复 Genshin Bridge 深度。NR/DLSS 有 depth surface 不代表 ReShade DEPTH 已可读。
- StarRail session `20261011-043159_StarRail_67688` 已编译并运行 `DisplayDepth.fx`，用户确认仍无深度；NR 私有 depth surface 存在但不等于 ReShade DEPTH。崩铁专属 preset 保持用于诊断，`DepthCopyBeforeClears=2` 无效。StarRail-only DSV bind/clear 审计源码已构建、隔离测试通过，并已只部署到 hkrpg_bilibili 缓存（SHA256 EE84F5D2A07C42CAC47A164A6BACDA64F3529D3F5182F0A110B709727AF5D628），待新 session 实测。
- OptiScaler 坑：启动时 `SyncIntoProfile` 后 `Activate(profile)` 会用 `profiles\hkrpg_bilibili.ini` 覆盖主 INI。DX11-only flag 必须在激活 profile 后重设并只回写崩铁 profile；检查本次 session 配置快照，不能只看磁盘主 INI。
- 运动图上下方向尚无足够证据；Bridge 的 `Ffx12MotionFlipY` 只适用于加载 Bridge 的游戏，不能拿来修崩铁当前 DX11 source-only 路线。

## PowerShell 输出
- 当前用户 PS7 profile 含 `cgrep` / `ctail` / `chead`，默认限量输出；路径 `D:\DATA\Documents\PowerShell\profile.ps1`。新终端用 `Get-Command cgrep` 验证已加载；查日志优先用这些函数，避免无过滤全量输出。

## 启动器 UI 维护（2026-10-25）
- UI 修改完成后自主启动 test，实际进入修改页面、等待异步列表加载并反复切页，检查最新日志；构建通过和部署哈希不代表 UI 通过。测试脚本见 `artifacts\Test-NrCardsUi-20261025.ps1`。
- WinUI 的 ThemeResource 可能到 DataTemplate 实例化时才解析；不得引用未经确认存在的样式。本次 `SubtleButtonStyle` 缺失导致 NR 卡片加载崩溃，修复使用默认 Button 模板和显式透明背景。
- NR 卡片图标按钮为 28×28 DIP、14 DIP 图标；与无文字开关同一行。全局开关独立控制选择器可用版本，不互斥、不改已有游戏选择。
- `OptiScaler-MFG-Ada` 已废弃并归档，不再作为构建源；归档与恢复记录位于 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup\HoyoDLSS5-20261025-093337`。
- 本次细节和运行证据索引见 `docs\NR-RUNTIME-SELECTION-20261025.md`。

## 打包与空间管理（2026-10-25）
- 完整包使用 `package-full.ps1`，不能用普通便携包代替。每次打包先删除上一轮打包暂存目录和旧便携 ZIP；成功后删除本轮解包暂存，只保留最终包及小型校验记录。
- 删除失败时移动到 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup`，记录原路径、去向和失败原因；同盘移动不算释放磁盘空间。删除与移动前核对绝对路径范围，不跟随重解析点，不混删源码、`.git`、SDK、研究源码或 test 安装。
- Full 流程调用 `package.ps1 -SkipZip`，不重复生成普通便携 ZIP。复用已安装的 .NET 10 SDK，不为每次打包复制 SDK。当前脚本自动清理范围和构建方法见 `docs\FULL-PACKAGE-BUILD.md`。
- 核验 ZIP 必要条目、逐文件内容哈希及最终 SHA256 后才能报告成功；完整包不包含用户数据库、游戏配置、下载缓存、日志及 test 插件。默认不启动启动器或游戏测试。

## 记忆维护
- 记忆以中文为主、代码/日志标识照原样保留；仅存可复用且已验证的信息。整理前备份到 `H\docs\memory-backups\YYYYMMDD-HHmmss\` 并写 README。详细过程放交接文档，不堆进本摘要。
- 本地记忆：`H\docs\MEMORY-starrail-ui-depth-20261011.md`；详细交接：`H\docs\HANDOFF-starrail-dx11-filter-depth-mv-20261011.md`。
