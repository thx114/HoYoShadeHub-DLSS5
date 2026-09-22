# 更新日志（便携包 1.0.8 → 1.2.0）

> 便携包版本号 = 发布用的号；括号里是对应的开发实例 `app-<ver>`。
> 更细的「问题 → 根因 → 改法」在 [GAMES-AND-INJECT.md](./GAMES-AND-INJECT.md) 的 §8.x。

## 1.0.8（app-9.9.17）· 基线

- 注入模式提示条：合并成一条常驻提示，标题右侧红色「停止」，再次启动先停掉上一个注入器。

## 1.0.9（app-9.9.18）

- 新增**「额外注入 DLL」**：跟 HoYoShade 的注入器并行，把任意 DLL（OptiScaler / DLSS Enabler 那类）注进游戏进程。
- 新增 DLSS Enabler 部署：自动下载官方安装包、静默安装、`nvngx.ini` 配成「只开帧生成」。

## 1.0.10（app-9.9.19）

- DLSS Enabler 在启动器里一键启用（绿色勾选框）：没部署会问你要不要下载部署。

## 1.0.11（app-9.9.20）· 一批 16 条反馈

- 撤掉 DLSS Enabler（改回只保留通用「额外注入 DLL」，入口挪进「开始游戏」右边的设置界面）。
- 注入模式下按钮文案改「启动注入器」；额外注入不再要求开注入模式；壁纸在游戏退出后能恢复。
- 插件：装新版清同族旧文件、删除连坐同族、版本下拉 + 更新徽标、interposer 的 dll 拷到游戏目录。
- hook：`HookPoint` / `HookStage` 两个键都写；关插件时摘掉 `LoadFromDllMain`；删插件 / 全局禁用清 ini 残留（新增「清理失效条目」）。
- 顶部游戏栏：修固定状态、取消固定给提示、「设置背景图…」挪到右键菜单、添加游戏改静态毛玻璃（不实时模糊）。
- 新增 NVIDIA 驱动版本红/黄提示（用 DLSS5 插件时）。

## 1.0.12（app-9.9.21）· 一批 7 条反馈

- 驱动检测改读**真实显卡驱动**（不再是 NV 面板那个版本）。
- DLL 变体记账：装 `310.8.SF-v2` 不会再显示成 `SF`。
- 覆盖更新后**跳过首次引导**；hook 灰的判定放宽（`renodx-dlss*` 都可改）。
- 全局插件「插件文件」不再误报缺 dll；该视图加「删除」（二级确认）；神经插帧器改回英文名。

## 1.0.13（app-9.9.22）

- 便携包**不再带 `config.ini`**（解压覆盖不会冲掉你的配置）；缺了由启动器创建。
- App 启动时**自动找现成的数据目录**（含扫解压根的同级目录）。

## 1.0.14（app-9.9.23）· 修「游戏列表看起来丢了」

- 修：切数据目录时必须**连数据库一起切**（否则游戏列表 / 安装路径读不到，只剩自定义游戏）。
- 修：「自动查找游戏」不再**清空**顶部列表 —— 现在只增不减，找不到就什么都不做。

## 1.0.15（app-9.9.24）

- 修驱动版本检测：改成读 NV app 里那串 GeForce Game Ready 驱动版本（本地化的「NVIDIA 图形驱动程序」卸载项），
  不再把旁边那块 AMD 显卡的版本号算进来。

## 1.0.16（app-9.9.25）

- 插件页**始终显示**识别到的 NVIDIA 驱动版本（绿的「要求区间内」），不再只有超范围才提示。

## 1.0.17（app-9.9.26）

- 新增 **OptiScaler**：全局插件多一个「OptiScaler」页签，能下载 3 个社区 DLSS-NR 分支的 release，
  本地库**单选**启用一个（整包解压到 <用户数据目录>/OptiScaler）。
- 启动器页「启动选项」新增「启动 OptiScaler」（按游戏）：启动游戏时把它跟「额外注入 DLL」一起注进游戏进程。
- HoYoShade / OpenHoYoShade 与 OptiScaler 同时启用时，「启动选项」上方出现滚动提示（可能插件冲突）。

## 1.0.18（app-9.9.27）

- 修：注入模式下**没勾**「启动 HoYoShade / OpenHoYoShade」也被注入了 HoYoShade —— 现在只等游戏进程，
  注「额外注入 DLL / OptiScaler」，不架 ReShade 注入器。按钮文案相应变成「等游戏进程」。

## 1.0.19（app-9.9.28）

- 新增来源 **DLSS NR on AMD**（danielblnc/DLSS-NR-on-AMD）：它只有自己的安装程序，下载后直接运行、由你选游戏目录；
  不参与「启用 / 注入」。同时删掉一直 404 的 OptiScaler DLSSNR Multipass MFG。
- OptiScaler 页签：删掉「一次只能启用一个」提示框；版本改成**点开下拉自动获取**；
  装过之后下拉默认选中当前版本（带「(当前)」后缀），按钮变成「更新到此版本」。
- 删掉两处提示文本：插件页 hook 那行「ini 里现在是 off（读的是 …）」、DLL 配置页「可以装的组件（清单来自 RankFTW/RHI…）」。

## 1.0.20（app-9.9.29）

- DLSS NR on AMD：运行它的安装程序**之前**先弹窗提醒「请装到默认目录（<用户数据目录>\OptiScaler\dlssnr-amd\<版本>）」；
  点「先不装」就只下载不运行。
- 装完目录里的 `version.dll` 会被认成注入目标（代理名表：version / dxgi / winmm / dinput8 / wininet / dbghelp），
  于是这个来源也能像别的构建一样「单选启用 + 启动时注入」。

## 1.0.21（app-9.9.30）

- 新增**远端目录**：插件清单 + OptiScaler 来源改成从 GitHub 仓库的 `catalog/plugins.json`、`catalog/optiscaler.json` 拉，
  按 id 覆盖内置条目 —— 以后加插件 / 换来源 / 加 OptiScaler 分支**不用重新发版**。
- 远端目录**每天最多自动拉一次**；拉不到就用上次缓存 / 内置表。设置页（关于）新增「拉取插件目录」手动刷新。

## 1.0.22（app-9.9.32）· 首个公开发布

- 远端目录地址指向本 fork 的仓库：`https://raw.githubusercontent.com/thx114/HoYoShadeHub-DLSS5/main/catalog/`。
- 同一份 zip 作为 GitHub Release **v1.0.22** 发布（以后「一键更新 / 退回」就读这个 Release）。

## 1.0.23（app-9.9.33）· GitHub 更新渠道 + 一键更新 + 退回

- 设置 → 关于新增**「更新渠道」**：官方（RPC 元数据）/ **GitHub · 本分支**。切到 GitHub 后：
  - 「刷新版本列表」列出本仓库所有 Release（标出「当前版本 / 比当前新 / 比当前旧」）；
  - 「下载并安装」一键装：下载 `HoYoShadeHub_Portable_<版本>_x64.zip` → 解压到便携包根目录（多出 `app-<版本>\` 并改写 `version.ini`）→「重启生效」；
  - **退回**：在版本列表里选一个比当前旧的 tag 装即可；旧版本目录原样留着，装前还会把当前 `version.ini` 备份到 `<用户数据目录>\.hysx\update-backup\`。

## 1.1.0（app-9.9.64）· 模型替换（XXMI）+ 一批整理

- 新增**左侧「模型替换（XXMI）」页**：MI 实例路径（自动查找顺序：手动指定 → `%AppData%`/`%LocalAppData%\XXMI Launcher`
  → 开始菜单快捷方式 → 注册表卸载项 → 各盘浅层目录；标题与左侧导航按当前游戏显示 ZZMI/GIMI/SRMI…）
  ＋ **Mods 管理**（列出 `<MI>\Mods` 一级目录，启用/禁用按 3DMigoto 约定改名字加/去 DISABLED，打开/删除/导入文件夹/导入 zip）。
- 新增启动选项**「启用XXMI」**：按 XXMI 的方式启动游戏 —— 后台静默调用 `XXMI Launcher.exe "<游戏 exe>" -x ZZMI -n`
  完成模型替换注入（不弹 XXMI 界面）；**ReShade 仍由本启动器的注入器负责**，两者顺序处理好，不再互相抢 d3d11。
- **插件汉化**（实验性，入口在「设置 → 实验性功能」，插件页那两颗按钮已隐藏）：把 addon DLL 里的英文界面文本原地换成中文。
  除了 `.rdata` 字面量，还会改**代码里的立即数**（短标签在代码里是 mov 常量）和**单字节 store 的尾巴**；
  改前自动备份（按路径哈希分开存），启动游戏前自动重打一遍。详见 GAMES-AND-INJECT.md §10。
- **更新检查改成一天一次**（「关于」页手动检查不受限）；「更新内容」窗口只在便携版 + 官方渠道才弹，开发实例不再打扰。
- 删掉「显示主窗口」全局快捷键（不再注册 Alt+H，设置页那个输入框已隐藏）。
- 修体验问题：用 XXMI 启动时不再弹控制台黑窗（`inject.exe` 改为隐藏窗口启动）。

## 1.0.24（app-9.9.34）· 给 OptiScaler 自动补 nvngx_dlssnr.dll

- 各 OptiScaler 分支的手册都要求把 **`nvngx_dlssnr.dll`** 放在包旁边（wilsjo2 的 INSTALL-DLSSNR.md 第 3 步等）；
  现在装完 OptiScaler 会自动从「DLL 配置」装好的插件目录**复制一份到构建目录**（已经有一份同样大小的就不动，
  不覆盖你自己换的版本；找不到就提示去 DLL 配置装一个）。
- OptiScaler 页签的构建卡片：缺运行时的那行会标黄 **「缺 nvngx_dlssnr.dll」**，并多一个 **「放入 nvngx_dlssnr.dll」**按钮。

## 1.1.1 · 修「版本」下拉的排序

- 修：插件 / OptiScaler 的**「版本」下拉不是按发布时间排的**。GitHub 的 `/releases` 列表是按 release
  **对象的创建时间**排的：同一批创建的几条会挨在一起，后发布的反而排在下面（实测 `RankFTW/rhi-repo`
  前四条的发布时间是 09-19 / 09-18 / 09-20 / 09-21），看起来就像乱序。
  现在按 release 卡片里的**真实发布时间**（就是 API 的 `published_at`）重排成新 → 旧；
  没拿到时间的保持原顺序垫底。
- 修：atom 解析原来用一个**跨 entry** 的正则，把每条 tag 配成了**下一条**的 `<updated>`（时间全错位）。
  改成逐条 entry 取：tag 从 `<id>` 拿、时间从本条 `<updated>` 拿。
- 修：「装最新」（不指定版本）原来取 atom 里**第一个**命中的 tag，但 atom 并不是时间序 ——
  改成取命中的里面发布时间最晚的，和下拉第一条保持一致。
- 版本下拉现在显示 **`tag · 发布日期`**，顺序一眼可见。

## 1.2.0 · 卡片视觉 + 新来源 + 一批体验修复

- 全局插件页换回**精致版卡片**：统一卡片描边 / 圆角、标题行点开、标签 chip、状态徽标（已装 / 有新版 / 缺必需 DLL 红 / 缺建议 DLL 黄）；展开后有版本下拉、安装 / 切换 / 删除 / 打开目录和主页链接。
- 新增**插件**：MFG Unlock（ImDreamt/MFGAdaUnlock-RenoDx，RTX 40 系多帧生成 3x/4x/6x 解锁）。
- 新增**模块**：MFG Unlock（matiasLombo/mfg-unlock）。
- 新增 **OptiScaler 来源**：DLSS Unlocked（ShyVortex，自动只取 standalone zip）、DLSS Enabler（artur-graniszewski，setup exe）。
- 「可下载」的插件 / 模块卡片新增**版本下拉**：能选具体版本再装，不再只能装最新。
- DLSS5 兼容性检测：**结果排序改成红 → 黄 → 提示 → 绿**（同级按编号），问题项排最前；修了读显卡型号时 `Properties` 子键 ACL 抛 `Requested registry access is not allowed` 导致整条显卡检查失败（现在能读到 RTX 5090 / Radeon 610M 等全部适配器）。
- 修：全局插件页**无法上下滑动**（内容区错放在 Auto 行，改到 `*` 行，ScrollViewer 拿到有界高度）。
- 修：OptiScaler「可下载」卡片比其它可下载卡片高（删掉一个从已装构建误复制、处理器按错类型会静默失败的多余开关）。
- 更新内容窗口：Hub 的更新日志改读**本 fork 仓库**的 release（之前读官方上游 `DuolaD/HoYoShade-Hub`）；Hub 的 release 链接也指向本仓库。








