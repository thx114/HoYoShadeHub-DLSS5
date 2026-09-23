# 更新日志

> 便携包版本号 = 发布用的号；括号里是对应开发实例 `app-<ver>`。
> 更细的「问题 → 根因 → 改法」见 [GAMES-AND-INJECT.md](./GAMES-AND-INJECT.md)。

## 1.3.4b4 · 帧率解锁数据跟随上游 GitHub 更新

- 帧率解锁开关放进启动页左下角「启动选项」列表，和「启用XXMI」等开关同一列表（原神才显示）。
- 游戏设置页的帧率解锁区块只保留目标帧率（60-1000），由启动选项里的开关控制是否生效，点页面底部「应用」保存。
- 帧率解锁的 shellcode 与扫描特征不再只靠内置固定数据：本地数据目录新增 `FpsUnlock`（`shellcode.bin` + `meta.json`），从上游 xiaonian233/genshin-fps-unlock 的 `unlockfps/main.cpp` 实时解析 `_shellcode_genshin_Const` 字节数组与 `PatternScan_Region` 特征（raw 地址失败自动回退 GitHub API）。
- 启动前按游戏版本（游戏目录 `config.ini` 的 `game_version`）同步：版本变动或本地无数据时自动拉取；版本不变时按 24 小时节流做后台静默检查。
- 设置页（高级设置）新增「帧率解锁数据」区块：显示本地数据更新时间，可手动「检查更新」；拉取到新数据后帧率解锁即按新数据工作。
- 首次运行且离线拉取失败时使用随程序分发的内置兜底数据（416 字节，与当前上游一致）。

## 1.3.4b3 · 启动选项内置帧率解锁（原神）

- 启动选项新增「帧率解锁」+ 目标帧率输入框（60-1000，默认 120），只对原神显示，按游戏记设置。
- C# 原生实现，启动器自身充当解锁器，不外挂 exe：游戏启动后扫描主模块 `.text` 段特征 `8B 0D ?? ?? ?? ?? EB ?? 33 C0` 定位帧率变量，写入 416 字节 shellcode 并启动同步线程；游戏内线程通过 `OpenProcess` 回启动器进程读目标帧数值，启动器后台循环每 2 秒校正一次（移植自 xiaonian233/genshin-fps-unlock，shellcode credit winTEuser）。
- 普通启动与注入模式（含无 shade 分支）都生效；游戏退出 / 重新启动 / 停注入器时释放解锁器并让游戏内同步线程自行退出。
- 游戏以管理员启动时，Hub 也必须用管理员启动，否则 OpenProcess 被拒。

## 1.3.4b2 · 新增 OptiScaler F5 源

- OptiScaler 可下载列表新增「OptiScaler F5 DLSSNR Multipass (janblade)」：仓库 `janblade/OptiScaler-F5-DLSSNR-Multipass`，F5 版 DLSSNR + multipass（vit-reuse、nvidia-residual、pre/post-SR 预设、RTX 40 MFG 测试构建）；只取 `OptiScaler-DLSSNR-F5-*.zip`。
- 内置来源表与远端 `catalog/optiscaler.json` 同步加该源。

## 1.3.4b1 · 内置 OptiScaler MFG Ada 源

- 源构建包为 `mfg-ada-0.1.2`：DLL/ini 在包根、streamline 在 `OptiScaler\streamline\`；出厂 ini 全部功能关闭（帧生成、Ada 解锁、NR 均关），装完游戏画面不受影响，进游戏按 Insert 在菜单里自行开启。（0.1.0 多嵌套一层导致路径重复、菜单不出现；0.1.1 布局正确但默认全开；两个旧 release 均已删除。）

- OptiScaler 可下载列表新增「OptiScaler MFG Ada（本 fork）」：仓库 `thx114/OptiScaler-MFG-Ada`，发布包含 Ada 门补丁 + NvAPI 双向架构伪装 + midpoint 修正的构建，包内两处 dlssg 均为 310.9.1。tag 过滤只取 `mfg-ada-*`，`runtime-*`（dlssg 单文件）自动跳过。
- 内置来源表与远端 `catalog/optiscaler.json` 同步加该源。

## 1.3.4 · 多帧生成解锁配套 + 单实例

- 启用 OptiScaler 启动游戏前检查游戏目录自带的 `nvngx_dlssg.dll`：版本低于 310.9 时弹窗「DLSSG 版本过低不支持解锁」，确认后用 OptiScaler 目录里的 310.9 替换（原文件备份为 `.bak`）；选择「仍然启动」则照常启动。搜索顺序与 OptiScaler 运行时一致（游戏 exe 目录根部 → 子目录广度优先）。
- 安装 OptiScaler 时自动准备一份 310.9 的 `nvngx_dlssg.dll`：本地已有就复用，没有则从托管地址下载（带 SHA256 校验），统一放到构建目录的 `OptiScaler\streamline\` 与 `OptiScaler\` 两个加载位置。
- dlss-unlocked 发布包的正身 `dxgi.dll` 落库后自动归一为 `OptiScaler.dll`，各构建路径识别一致。
- 修复多个启动器实例 / 历代版本进程并存：全局单实例，再次启动会唤起已在运行的主窗口并退出。

## 1.2.3 · 修复旧构建找不到 streamline

- 修复 1.2.2 配置分离引入的顺序问题：启动时先激活该游戏的 ini profile，再把 `OptiDllPath` 钉为构建目录的绝对路径。此前顺序相反，已存在的 profile（旧构建在 ini 还是 `auto` 时继承下来的）会把修正覆盖回 `auto`，旧构建（如 wilsjo2 v0.8.8）启动报 `Can't init DLSSG Output / missing the streamline folder`。
- `OptiDllPath` 自愈移到注入前：对所有已装 OptiScaler 构建生效，不再只在安装完成时处理；游戏退出回写 profile 后，下次启动路径即一致。

## 1.2.2 · 模块删除 + OptiScaler 配置按游戏分离

- 全局插件·模块卡片对所有已安装模块（含内置 / 远端目录模块）显示删除按钮：确认后真正删除模块目录（含下载的全部文件），清掉全局开关与每个游戏的勾选；删后可在「可下载」里重装。手动模块同时删除 DLL 文件本身。
- OptiScaler 的 `OptiScaler.ini` 按游戏分离：同一构建被多个游戏注入时，设置存在构建目录的 `profiles\<游戏>.ini`，注入前激活、游戏退出回写，首次使用从当前 ini 继承；叠加层里 Save 的设置也按游戏保留。
- 修 dlss-unlocked 叠加层显示 `unlock unavailable for this runtime`：运行时按 OptiDllPath 搜索 `nvngx_dlssg.dll`，找不到会沿目录 BFS 命中游戏自带的旧版（实测 310.6.0），而 MFG 解锁只认识 legacy 与 310.9 两套字节签名。现在安装 / 注入前自动把候选目录里版本最高的 dlssg（dlss-unlocked 0.9.10 自带 310.9.1）钉到 `OptiScaler\` 根。

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
