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

- `src/` 源码、`build-local.ps1` 本地构建脚本（不需要 Visual Studio 就能 publish）、`docs/` 文档；
- **不放** ReShade / HoYoShade / 插件 / OptiScaler 的二进制 —— 那些由启动器按需下载。

## 许可与致谢

- **HoYoShade Hub**：MIT，Copyright (c) 2025 哆啦D夢|DuolaD（见 [LICENSE](./LICENSE)）；本 fork 沿用 MIT。
- **HoYoShade 框架**：BSD 3-Clause，Copyright (c) 2024 哆啦D夢|DuolaD。
- 本项目基于 [Starward](https://github.com/Scighost/Starward)（MIT）二次开发；MiSans 字体版权归小米集团。
- 第三方库清单见 [docs/ThirdParty.md](./docs/ThirdParty.md)。
