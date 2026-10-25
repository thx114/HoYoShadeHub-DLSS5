# NR 版本库与云端 DLL 更新（2026-10-25）

## 页面与选择规则

- 左侧为不含 NR 的 DLL 紧凑清单；按组件列出当前文件，可在清单内切换已归档版本或下载安装所选版本。
- 右侧大区域为 NR 版本库，每版本独立卡片，提供下载 / 更新、检查更新、目录和全局可选开关；辅助操作放入“更多”。
- 全局可选开关不互斥，可以同时开放多个版本。它只过滤 OPT 和插件页的 NR 选择器，不切换共享运行时、不修改游戏选择、不删除归档。默认允许已下载版本被选择。
- 关闭正在被某游戏使用的版本，不会使该游戏的已有选择失效。选择器会说明原选择已关闭，要求明确选择其他项才可应用；取消保持原选择。
- 各游戏的 ReShade / OptiScaler NR 仍在对应卡片内单独选择；空选使用插件 / 构建默认 NR。
- NR 下载先进入版本库，不会因为下载而更改正在使用的版本。
- 左侧清单占页面约 40%，最小宽度 340 DIP；隐藏垂直滚动条，保留滚轮与触摸滚动，取消原有 18 DIP 滚动条预留。
- NR 标题和状态位于左侧；下载、检查更新、目录图标和全局开关位于右侧同一行。图标按钮 28×28 DIP，图标 14 DIP，保留悬停提示和无障碍名称。开关不显示“可选 / 不可选”文字。
- 已下载版本置顶；其后以“可下载”标题分区。未下载卡片灰色侧条，只显示下载按钮，隐藏检查更新、目录和启用开关。页面检查更新移到顶部“更多”左侧，移除 NR 标题下的说明行。
- 每游戏插件 NR 选择器位于卡片展开配置区；OPT NR 选择器放在卡片当前配置操作行末尾，宽 160 DIP。直接下拉选择即保存，不再经过对话框或“应用”。只展示全局开放且已归档的版本；旧选择被关闭或缺失时以禁用项保留显示，初始化与打开下拉不会重写选择。
- OPT 扩展配置隐藏 `DLSSG/SmoothMotion`、`Hooks/HookOriginalNvngxOnly`、`InitFlags/AutoExposure`，仅过滤 UI 项，不改写对应 INI 值。

## 存储与验证

NR 按 SHA256 进入内容库；归档、每游戏插件包与每游戏 / 每构建 OptiScaler 加载目录使用硬链接。同大小不视为同版本。硬链接失败时报告错误，不退回复制；替换采用新文件链接 / rename，不原地覆盖共享文件。

OptiScaler 配置保留在各游戏独立目录；换 NR 或回到构建默认不清除质量、倍率、分辨率等配置。

识别元数据位于主仓库 `catalog/dll-fingerprints.json`，包括 DLL SHA256、大小、PE 版本、架构、DLL 标志以及 ZIP URL / SHA256。云端清单支持独立更新；错误更新保留前一有效快照。首次离线使用随包 JSON，随后使用本地缓存；手动检查更新强制刷新。

## GitHub 发布

两个用户 ZIP 保持原始内容，发布到 `thx114/HoYoShadeHub-DLSSNR-Runtimes`：

- tag：`nr-genshin-v2-3000-20261025`
- `nvngx_dlssnr_GenshinV2-PresetA-3000.zip`：188461510 字节，SHA256 `25560769FA3100F71471CEFEE9B9C4ED8B3DF8DA5AD715D91CC94F22FCC08AD9`
- `nvngx_dlssnr_GenshinV2-3000.zip`：198233507 字节，SHA256 `E7CA8C7AA0BDA9B8912D72784AA86276A4FDD5ED44005C402C54F10AF09F0033`
- GitHub 返回的各资产 digest 与本地 ZIP 一致，Release 已发布。
- 主仓库云端目录提交：`f2a75c74404e878b1bd0a6031617175cda284a43`；API 回读字节与本地 JSON 一致。
- 发布记录、资产元数据和回读文件已随清理归档到 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup\HoyoDLSS5-20261025-093337\files\artifacts\nr-github-20261025`。发布时直连 raw / Release URL 的探测遇到 DNS / 连接失败，不能据此声称已完成公网下载实测；启动器保留既有代理与缓存逻辑。

## 两个 DLL

| 版本 | DLL 字节数 | PE 版本 | DLL SHA256 |
|---|---:|---|---|
| 原神V2 预设A_3000 | 266424944 | 310.9.16.0 | 2B385F0965AA9CD130A0C2A22B66FB59BEC88D8F8592CC37A6C25AD51E8CEF3D |
| 原神V2 3000 | 276162560 | 310.9.16.0 | E58C6FBC20D327A4CF7F8BF3C1181FD14F120F9EAE41F57F1708DAA5C867FB37 |

## 验证边界

NR 测试包含真实用户 ZIP、全局可选过滤、多版本同时开放、关闭全部选项、已有选择继续加载、硬链接文件 ID、每游戏隔离、配置保留、云端热更新、PE 不匹配和坏清单保留。

新增本地 HTTP 下载端到端测试验证 ZIP / DLL 内容、硬链接归档、下载不更改共享运行时、清理临时目录和错误 SHA256 拒绝。启动器回归 13 组与独立 OPT 启动准备 16 项通过。

构建和部署不等于真实游戏验证；没有启动游戏、强退进程或改 ReShade 核心。

## UI 崩溃修复与自主验证

- 2026-10-25 10:31:45 的 test 日志记录 `XamlParseException: Cannot find a Resource with the Name/Key SubtleButtonStyle`。该样式到 NR 卡片模板实例化才解析，因此编译和部署校验未捕获。
- 修复移除不存在的样式引用，保留默认按钮模板，显式使用透明背景和零边框。更新后主程序集 SHA256：`87DC6736998F8B6B1009C5EA79F0ED85B53D9B2FD835110CC99BE3C0D8F47909`。
- Release/x64 构建：308 个警告、0 个错误；test 部署 980 文件哈希匹配，替换 4 文件。回退备份：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-nr-20261025-103651`。
- 自主 UI 测试脚本：`artifacts\Test-NrCardsUi-20261025.ps1`。通过 Windows UI Automation 操作实际 test 窗口；验证 NR 版本库、卡片按钮、开关文字、反复切页和进程存活。只切换页面，不切换 NR 或游戏配置。
- 运行证据目录：`artifacts\nr-crash-ui-20261025`，包含控件树 `page.json`、截图 `page.png` 和最终结果 `receipt.json`。部署记录中的 `uiTested=false` 仅表示部署步骤，后续 UI 结果以该独立 receipt 为准。
- 10:37:38 最终 UI 验证通过：实际 test PID `102308`，在全局插件与 DLL 页面间往返 3 次，12 个 NR 目录按钮实例加载，开关文字已移除，等待后进程仍存活；该 PID 最新日志中无新增 `App Crash` / `XamlParseException` / 缺失资源错误。截图已人工查看，标题、紧凑图标及无文字开关在同一行。150% DPI 下 28 DIP 按钮对应 42 物理像素。
- 后续 UI 调整必须实际进入页面并等待模板加载，不再以构建或哈希校验代替运行验证。

## Mod 页面后续维护

角色分类、缩略图与拖放的当前实现和测试边界见 docs/MOD-CATEGORIES-DROP-20261025.md。

## 12:32 本地版本丢显与导入修复

- 现场内容库 4 份 NR，版本目录仅 2 份；遗漏的 B6DCBFA8C572B645 与 F95FEB54137EA119 已按完整 SHA256 校验并补为自定义版本硬链接，不替换已有版本、游戏选择或活动 DLL。两份内容未匹配当前指纹清单，不依据 PE 版本猜测名称。
- 新增 RecoverContentVersions：恢复孤立内容库索引，验证 SHA256 与 PE，跳过重解析点，已有名称冲突时使用内容标识，逐文件报告失败。DLL 页在等待云端更新前先恢复并显示本地版本。
- 导入按钮将文件窗口打开移入异常处理与忙状态保护，忙时明确提示；失败写日志。XamlRoot 的 AppWindowId 通过 Win32Interop.GetWindowFromWindowId 转成 HWND 后初始化文件选择器。
- nr-archive-download-tests：10 项通过，覆盖下载保持活动 DLL、硬链接、坏下载拒绝、索引丢失恢复、幂等及内容哈希不符。
- Release 构建 309 警告、0 错误。已部署 test，980 文件校验通过，6 文件替换；备份 _backup-nr-20261025-123224。
- 主 DLL SHA256：16D045AAFDC0A4E7C6C88219F6099E907ED810E093070B26AB66AD35E30CCBF0。
- 遵照用户“不用你测试启动器”，本轮未启动启动器或执行 UI 测试。文件选择器修复仅完成编译与代码校验，实际交互待用户验证。

## 15:05 默认 NR 误登记修正

- 更正上节对“两份遗漏版本”的判断：`B6DCBFA8C572B645...` 来自 `OptiScaler\official\DLSS F5`，`F95FEB54137EA119...` 来自 `OptiScaler\janblade-f5\v0.1.31`。它们是构建自带默认 NR，不是丢失的用户下载；此前按内容库枚举补为“自定义”是错误处理。
- 内容库继续负责 SHA256 去重和硬链接；新增 `cache\nr-version-index\<版本>.json`，仅在显式归档（下载、导入等）后记录版本与内容哈希。默认库的 Intern / 整理硬链接不产生这份记账。
- RecoverContentVersions 只按记账恢复原版本名，不再扫描所有内容生成卡片。相同版本更新只保留最新记账；删除版本时撤销记账，防止刷新复活。保留旧内容文件用于已有硬链接，不据此新增用户版本。
- 在用户退出 test 后完成部署，并将上述两条错误版本目录移到备份；两处 OptiScaler 默认 DLL 与内容库的 SHA256 均保持不变，默认链接保留。原有 `310.8.3`、`310.9.16-SF` 归档保留并补记账；游戏选择 `nr_version_reshade_hk4e_cn=310.9.16-SF` 未修改。现场执行两次恢复，两条错误版本均未重新出现。
- Release/x64：309 警告、0 错误。下载及恢复回归 24 项、NR 选择回归 20 项、启动器回归 13 组通过。测试覆盖默认更新不新增卡片、显式导入仍可恢复、删除不复活、同名更新不暴露旧内容及坏记账拒绝。
- test 部署 980 文件哈希匹配，4 文件替换；主 DLL SHA256：`0FA855EA31DEA8B61A1A7504E6EA97148EC36720183E66C4A98F96B8FB832FEF`。
- 程序备份：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-nr-20261025-150459`。错误登记备份：同根 `_backup-nr-default-index-20261025-150506`，含清理 receipt。
- 实现：`NrRuntimeLibrary.cs`、`DllVersionStore.cs`。一次性修复工具：`artifacts\NrDefaultProvenanceRepair`；部署脚本：`artifacts\Deploy-NrDefaultProvenanceTest-20261025.ps1`；过程记录：`artifacts\nr-default-provenance-deploy-20261025.log`；回归日志：`artifacts\nr-provenance-*-20261025.log`。
- 按用户要求未启动或操作启动器，未启动游戏；构建、文件部署及库恢复校验不代表实际 UI 或游戏验证。
