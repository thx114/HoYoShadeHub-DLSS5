# HoYoShade Hub · DLSS5 魔改版

> ⚠️ 这是 [DuolaD/HoYoShade-Hub](https://github.com/DuolaD/HoYoShade-Hub) 的**非官方Fork**。
> 本 fork 的问题请提到本仓库的 Issues，**不要去打扰上游作者**。

## 这个版本多了什么

- **游戏发现**：自动 / 手动找游戏，顶部一行图标随便切；也支持自定义游戏（WeGame、非米哈游的游戏）。
- **多游戏 ReShade.ini 管理**：插件开关按游戏各写各的 ini，互不影响。
- **注入模式**：Hub 不替你启动游戏，只架 HoYoShade 的注入器等进程（游戏用 HoYoPlay / WeGame 自己拉起来）。
- **插件（addon）管理**：装 / 删 / 换版本、全局开关，还会按插件族把缺的 dll 直接红黄标出来。
- **DLSS5 / 神经渲染**：NVIDIA 驱动版本检查（用 DLSS5 插件时提示要求区间）、HookPoint（hook 点）直接改。
- **OptiScaler**：全局插件里能下载社区 DLSS-NR 分支（wilsjo2 / NeuRotic / DLSS NR on AMD），**同一时间只启用一个**；
  启动器页勾上「启动 OptiScaler」，启动游戏时就把它注进游戏进程。
  （DLSS NR on AMD 只有它自己的安装程序：下载后会弹窗提醒，装到默认目录即可。）
- **DLSS 组件变体识别**：dlssnr 族（50 系 / RTX40 / SF / Lecram）按记账 + 文件特征自动辨认，DLL 组件目录随版本分发。
- **插件汉化**：RenoDX DLSS 插件的界面文本原地中文化（插件页「已安装版本」行一键打补丁 / 还原，带自动备份；
  `renodx-dlss5` / dlss5-bridge 自带多语言，不做处理）。

## 下载与安装

在 [Releases](https://github.com/thx114/HoYoShadeHub-DLSS5/releases) 下 `HoYoShadeHub_Portable_<版本>_x64.zip`，
解压**覆盖**到你的便携版目录（例如 `D:\APPS\HoYoShadeHub`）。

- 包里**不带 `config.ini`**，不会覆盖你已有的配置和游戏列表；
- 便携版根目录只有 `HoYoShadeHub.exe` + `version.ini` + `app-<版本>\`；
- **更新 / 退回**：设置 → 关于 → 更新渠道，切到「GitHub · 本分支」→「刷新版本列表」→ 选一个版本「下载并安装」→「重启生效」。
  选比当前旧的版本就是退回。

## 运行要求

- Windows 10 1809 (17763) 或更高；
- WebView2 Runtime（系统一般自带）；
- 要用 DLSS5 神经渲染插件的话，**游戏目录里要有 `nvngx_dlssnr.dll`**（各 DLSS-NR 分支的手册都要求这个文件，
  本仓库不替用户分发它）。

## 仓库里有什么

- `src/` 源码；`compile.ps1` 构建、`package.ps1` 打便携包（不需要 Visual Studio）；
- **不放** ReShade / HoYoShade / 插件 / OptiScaler 的二进制 —— 那些由启动器按需下载（完整包除外，见下）。

## 打包

两种包，共用同一套源码，区别只在「自带不带 HoYoShade 框架」：

```powershell
# 1) 普通便携包：只有启动器，用户自己装 HoYoShade（约 180 MB）
./package.ps1 -Version 1.3.9.1

# 2) 完整包：启动器 + HoYoShade 框架（ReShade64.dll / inject.exe / 预置 / 精简滤镜与材质），
#    插件目录留空（约 235 MB）；HoYoShade 来源默认自动找，可用 -ShadeSource 指定
./package-full.ps1 -Version 1.3.9.1
./package-full.ps1 -Version 1.3.9.1 -ShadeSource 'D:\APPS\HoYoShadeHub\HoYoShade'
```

> **完整包不进 GitHub Release**：体积大、且内含 HoYoShade 第三方框架，只在本地构建、自行分发
> （Release 上只放普通便携包）。

完整包的内容与规则（`package-full.ps1` 头部注释里也有）：

- 布局：`version.ini` + `HoYoShadeHub.exe` + `app-<版本>\` + `HoYoShade\`；
- 自带 `HoYoShade` 框架本体、`Presets`、`InjectResource`（字体）、`LauncherResource`；
- **滤镜 / 材质只带必要的那套**（源目录里本来就是精简集：Shaders 42 个、Textures 5 个），
  想再精简就传 `-ShaderAllowList` / `-TextureAllowList`（一行一个通配符）；
- **插件目录 `reshade-shaders\Addons` 留空**（插件由用户在启动器里按需装）；
- 不打包用户状态（`.hysx\installed.json`、`.hysx\backup`、日志、截图）；
- `ReShade.ini` 里的绝对路径会改写成相对路径（源机 `D:\...` 在别人机器上不存在）。

### 「真便携」包（`-PortableLocal`）

普通便携包默认仍然把**缓存**放在 `%LOCALAPPDATA%\HoYoShadeHub`（webview / 缩略图 / 更新包）。
想要一个「解压到任何地方都不碰 C: 盘」的包，加 `-PortableLocal`：

```powershell
./package.ps1 -Version 1.3.9.1 -PortableLocal
```

它只是在包根多写一个 `.portable` 标记文件；主程序看到这个标记就把下面这些全部留在包内：

| 内容 | 普通便携包 | 真便携包（有 `.portable`） |
| --- | --- | --- |
| `config.ini` | `<包根>\config.ini` | 同左 |
| 用户数据 / 数据库 | `<包根>\`（便携根） | 同左 |
| 缓存（webview / 缩略图 / 更新包 / github-cache） | `%LOCALAPPDATA%\HoYoShadeHub` | `<包根>\.cache\` |
| 临时文件（下载中的 zip、解压中间文件、汉化/更新/dll 备份） | `%TEMP%` | `<包根>\.cache\temp\` |
| 数据库自动备份 | `%LOCALAPPDATA%\HoYoShadeHub\DatabaseBackup` | `<包根>\.cache\DatabaseBackup\` |
| 日志 | `<包根>\log\` | 同左 |

也可以不重新打包，直接在已有便携包的根目录放一个空文件 `.portable`
（或设环境变量 `HYSHADE_PORTABLE_LOCAL=1`）达到同样效果。
**默认不带标记** —— 老用户升级上来缓存目录不会突然搬家（看起来像「数据丢了」）。

## 许可与致谢

- **HoYoShade Hub**：MIT，Copyright (c) 2025 哆啦D夢|DuolaD（见 [LICENSE](./LICENSE)）；本 fork 沿用 MIT。
- **HoYoShade 框架**：BSD 3-Clause，Copyright (c) 2024 哆啦D夢|DuolaD。
- 本项目基于 [Starward](https://github.com/Scighost/Starward)（MIT）二次开发；MiSans 字体版权归小米集团。
