# 更新日志

> 便携包版本号 = 发布用的号；括号里是对应开发实例 `app-<ver>`。
> 更细的「问题 → 根因 → 改法」见 [GAMES-AND-INJECT.md](./GAMES-AND-INJECT.md)。

## 1.2.1 · 修复 OptiScaler 帧生成初始化

- 修：启用 OptiScaler 帧生成时报 `Can't init DLSSG Output — Are you missing the streamline folder?`。根因是 ini 默认 `OptiDllPath=auto` 按**游戏 exe 目录**解析 `.\OptiScaler`，外部注入（DLL 在启动器数据目录）时路径指向游戏目录，StreamlineProxy 加载不到自己的 `sl.interposer.dll`。现在安装 / 补 DLL 时自动把 `OptiDllPath` 钉成数据目录的绝对路径。
- `nvngx_dlssg.dll` 改为放进 `OptiScaler\streamline\`（StreamlineProxy 从该文件夹逐个加载 sl.common / nvngx_dlssg），构建根同时保留一份。
- 游戏页插件配置新增 **HookStreamline** 开关：往 `[INSTALL]` 写 `HookStreamline=1`，让 DLSS 插件跳过 Streamline 的 Present 钩子（外部注入时 Streamline 已先挂好，避免双重 Present hook）。

## 1.2.0 · 卡片视觉 + 新来源 + 体验修复

- 全局插件页换回精致版卡片：统一描边 / 圆角、标题行点开、标签 chip、状态徽标（已装 / 有新版 / 缺必需 DLL 红 / 缺建议 DLL 黄）；展开后有版本下拉、安装 / 切换 / 删除 / 打开目录和主页链接。
- 新增插件 MFG Unlock（ImDreamt/MFGAdaUnlock-RenoDx，RTX 40 系多帧生成 3x/4x/6x 解锁）；新增模块 mfg-unlock（matiasLombo）。
- 新增 OptiScaler 来源：DLSS Unlocked（ShyVortex，只取 standalone zip）、DLSS Enabler（artur-graniszewski，setup exe）。
- 「可下载」插件 / 模块卡片新增版本下拉，可指定版本安装。
- DLSS5 兼容性检测：结果按红 → 黄 → 提示 → 绿排序；修读显卡型号时 `Properties` 子键 ACL 拒绝访问导致整条显卡检查失败。
- 修全局插件页无法滚动；修 OptiScaler「可下载」卡片比其它卡片高。
- Hub 更新日志与 release 链接改读本 fork 仓库。

## 1.1.1 · 版本下拉排序

- 插件 / OptiScaler 版本下拉按真实发布时间（`published_at`）新 → 旧排列，下拉显示 `tag · 发布日期`；修 atom 跨 entry 匹配导致的时间错位。

## 1.1.0（app-9.9.64）· 模型替换（XXMI）

- 新增「模型替换（XXMI）」页：XXMI 实例自动查找 + Mods 管理（启用 / 禁用 / 打开 / 删除 / 导入）。
- 启动选项新增「启用XXMI」：后台调用 `XXMI Launcher.exe "<游戏 exe>" -x ZZMI -n`，ReShade 仍由本启动器注入。
- 插件汉化（设置 → 实验性功能）：替换 addon 内英文界面文本，启动前自动重打。
- 更新检查改为一天一次；移除「显示主窗口」全局快捷键。

## 1.0.24（app-9.9.34）· 自动补 nvngx_dlssnr.dll

- 装完 OptiScaler 自动从插件目录复制 `nvngx_dlssnr.dll` 到构建目录；构建卡片标黄缺运行时并提供「放入」按钮。

## 1.0.23（app-9.9.33）· GitHub 更新渠道

- 设置 → 关于新增更新渠道：官方 / GitHub 本分支；支持一键更新与退回旧版本（版本列表标当前 / 新 / 旧，装前备份 `version.ini`）。

## 1.0.22（app-9.9.32）· 首个公开发布

- 远端目录指向本 fork；以 GitHub Release v1.0.22 发布。

## 1.0.8 – 1.0.21（早期版本）

- 1.0.8：注入提示合并为常驻一条，再次启动先停旧注入器。
- 1.0.9 – 1.0.10：新增「额外注入 DLL」与 DLSS Enabler 一键启用（后于 1.0.11 撤掉，保留通用额外注入）。
- 1.0.11：插件清理 / 版本下拉 / 更新徽标；NVIDIA 驱动版本提示。
- 1.0.12：驱动检测改读真实显卡驱动；DLL 变体记账；覆盖更新跳过首次引导。
- 1.0.13：便携包不再带 `config.ini`（覆盖不冲配置）；启动时自动找数据目录。
- 1.0.14：切数据目录连数据库一起切；「自动查找游戏」只增不清空。
- 1.0.15 – 1.0.16：驱动版本检测修正；插件页常驻显示驱动状态。
- 1.0.17：新增 OptiScaler 页签与「启动 OptiScaler」选项。
- 1.0.18：没勾 HoYoShade 时不再被注入。
- 1.0.19 – 1.0.20：新增 DLSS NR on AMD 来源；版本下拉自动获取、代理 DLL（version.dll 等）可识别注入。
- 1.0.21：插件 / OptiScaler 清单改为从 GitHub `catalog/` 远端拉取，每天最多一次，无需发版即可更新目录。
