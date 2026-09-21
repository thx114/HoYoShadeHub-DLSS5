# 开源前的协议与上游对照（2026-09-21）

> 这份文档回答三件事：① 我们能不能开源、要保留什么声明；② 上游 DuolaD/HoYoShade-Hub 有没有新东西要拉；
> ③ 开源仓库该怎么放（代码 / 目录 JSON / Release）。

## 1. 协议：官方原话与实测结论

| 项目 | 协议 | 来源 | 对我们（fork）意味着什么 |
| --- | --- | --- | --- |
| **HoYoShadeHub** | **MIT**（Copyright (c) 2025 哆啦D夢\|DuolaD） | 仓库根 LICENSE | 可以自由 fork / 改 / 再发布（含闭源），**但必须保留版权声明和 MIT 许可全文**；不能用作者名义背书 |
| **HoYoShade**（框架本体） | **BSD-3-Clause**（Copyright (c) 2024 哆啦D夢\|DuolaD） | 安装目录里的 HoYoShade/LICENSE | 只约束**二进制再分发**：Release 包里带了 HoYoShade 的文件就要一起带这份 LICENSE + 版权声明，且不能用作者名义宣传 |
| **Starward** | **MIT** | Scighost/Starward（GitHub API 查到） | HoYoShadeHub 是「二次开发 Starward」，MIT→MIT 兼容，保留声明即可 |
| ReShade / 各 addon / 各 OptiScaler 分支 | 各自不同（ReShade 不是 MIT） | 各上游 | **不要**把它们的二进制塞进我们的源码仓库，让启动器按需下载 |
| MiSans 字体 | 小米字体许可（免费商用，不得单独售卖字体） | 项目 Assets/Font 里的 MiSans VF.ttf | 字体来自资产包 HoYoShadeHub.Assets；仓库里若带字体文件，保留许可说明 |

### 结论（照这个做就能开源）

1. **许可证**：沿用 MIT。仓库根保留原有 LICENSE（MIT + 原作者版权）；我们新增的实质代码在文档里注明
   「基于 HoYoShadeHub（MIT）修改」即可，不强制改许可人。
2. **README 必须保留**：原 Sources 表（Starward / HoYoShade / MiSans / TimeSyncTool）、docs/ThirdParty.md，
   以及「本项目是 HoYoShadeHub 的修改版（fork）」这句话 —— 既是 MIT/BSD 的要求，也避免用户搞混官方版本。
3. **不要放进仓库**：ReShade 本体、HoYoShade 框架文件、第三方 addon / OptiScaler 的二进制。
   这些一律走「启动器按需下载」（正好就是下面要做的远端目录 + Release 更新）。
4. **版本号**：我们自己的便携包版本（1.0.x）和上游的 app-9.9.x / 0.0.0-Beta.x 不同源，README 里要说清楚。

## 2. 上游对照：有没有要拉的

DuolaD/HoYoShade-Hub（MIT，默认分支 main）最近提交：

| 日期 | commit | 内容 | 我们这边 |
| --- | --- | --- | --- |
| 2026-09-17 | d90b6dd | Fix Beta Client add issue due config.ini file not exist（GameLauncherPage.xaml.cs + GameSettingPage.xaml.cs） | **没有** —— 待拉 |
| 2026-09-17 | b11c63b | HEA/PP 测试服选项迁到官方 API、删掉本地 HEA/PP 选项（14 个文件，含 Core/GameBiz、GameId、AppConfig、GameSelector、PlayTime…） | **没有** —— 待拉，但和我们改过的文件大面积重叠，不能直接覆盖 |
| 2026-09-16 | 4cef52d | 游戏设置加「忽略 DX12 兼容性检查」开关 | **已有**（Lang 里有 GameLauncherSettingDialog_IgnoreDX12CompatibilityCheck） |
| 2026-09-16 | 2ab1684 | 腾讯云域名表去掉 hoyoshadehub-glasses-edgeone.edgeone.app | **已有**（CloudProxyManager.cs 与上游一致） |
| 2026-09-15 及更早 | 一批 l10n 翻译提交 | 只有 resx 变化 | 部分差异，可按需同步 |

逐文件比对（本地 vs 上游 main，2026-09-21）：

- **一致**：CloudProxyManager.cs、LauncherUpdateProxyManager.cs、HoYoPlayService.cs；
- **不一致、我们没改过**（= 纯落后，可安全覆盖）：GameSettingPage.xaml.cs、GameBiz.cs、GameRegistry.cs、
  GameId.cs、LauncherId.cs、GameFeatureConfig.cs、GameLauncherService.cs、PlayTimeButton.xaml.cs、
  PlayTimeService.cs、Lang.Designer.cs、Lang.resx（及 zh-CN/zh-HK/zh-TW resx）；
- **不一致、我们改过**（要三方合并，**不能覆盖**）：GameLauncherPage.xaml.cs、AppConfig.cs、
  GameSelector.xaml.cs、GameBizIcon.cs、GameLauncherSettingDialog.xaml(.cs)、DatabaseService.cs、BackgroundService.cs。

### 建议的拉取方式（重要）

当前工作目录**不是 git 仓库**（D:\CODE\HoyoDLSS5 没有 .git），现在只能「下载上游文件覆盖/手工合并」，
很容易把我们这些天的改动冲掉。正确姿势：

1. 本地 git init，先把**当前这棵树的现状**提交一次（当作我们的基线）；
2. git remote add upstream https://github.com/DuolaD/HoYoShade-Hub.git；git fetch upstream main；
3. git merge / cherry-pick d90b6dd b11c63b，冲突按「我们的 UI 改动保留 + 上游的行为修复吸收」处理；
4. 以后每批上游更新都走这条路，别再手动覆盖文件。

## 3. 开源仓库要放什么

| 放 | 不放 |
| --- | --- |
| src/、build-local.ps1、docs/、LICENSE、README.md | ReShade / HoYoShade / addon / OptiScaler 的二进制 |
| catalog/plugins.json、catalog/optiscaler.json（远端目录，见 §4） | 用户的 ReShade.ini、游戏数据库、下载缓存 |
| GitHub Release：HoYoShadeHub_Portable_<版本>_x64.zip（+ app-<版本> 可选） | build/、bin/、obj/ 等中间产物（.gitignore 掉） |

## 4. 远端目录（插件 + OptiScaler）的约定

仓库根建 catalog/，两个文件，地址形如
raw.githubusercontent.com/<owner>/<repo>/main/catalog/{plugins,optiscaler}.json：

- **plugins.json**：与内置目录 src/HoYoShadeHub.Extensions/Resources/catalog.builtin.json 同一格式（extensions: [...]），
  按 id 覆盖内置条目 —— 改插件来源 / 加新插件**不用重新发版**；
- **optiscaler.json**：{"sources":[{ "id","name","repository","description","tagPattern" }]}，同样按 id 覆盖内置来源。

客户端行为：

1. **每天最多拉一次**（AppConfig.LastCatalogFetchUtc，超 24h 才拉）；拉不到就用上次缓存 / 内置表，不打扰用户；
2. 下载到 <用户数据目录>\.hysx\catalog\{plugins,optiscaler}.json，页面从缓存文件读（离线可用）；
3. **设置里有「立即拉取目录」**按钮（绕过节流，手动刷新）。

## 5. GitHub 更新渠道 + 一键更新 / 退回（待做，需要仓库地址）

现状：App 自更新走的是官方 RPC 元数据（Features/Update/UpdateService.cs → MetadataClient，
按 EnablePreviewRelease 分「稳定 / 预览」两个渠道），**没有 GitHub 渠道**，也没有「退回旧版本」。

要做的（等仓库地址确定后实现）：

1. 新渠道：从我们仓库的 GitHub Release 解析（复用 Extensions 里现成的 GithubReleaseResolver：
   releases.atom + expanded_assets HTML，不吃 API 限额），列出所有版本 → 设置里能切「官方 / GitHub（本分支）」；
2. 一键更新：下载 HoYoShadeHub_Portable_<版本>_x64.zip → 复用现有 SetupService/UpdateWindow 的落盘逻辑换包；
3. 退回：版本列表里选比当前**旧**的 tag 也能装（列表里标出「当前版本」「比当前新 / 旧」），这就是退回；
   顺带在本地保留上一份 zip（<用户数据目录>\.hysx\update-backup\）以防新版本起不来。
