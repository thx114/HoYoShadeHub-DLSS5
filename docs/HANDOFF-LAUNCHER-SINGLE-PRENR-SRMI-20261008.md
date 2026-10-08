# 交接：HoYoShade 单套效果与崩铁 SRMI 启动配套

日期：2026-10-08。用户指定启动器由另一位 AI 负责。本代理已停止对启动器的追加修改和发布；相关子智能体已关闭。

## 当前生产状态（请先看）

已备份并部署原生部分：

- HoYoShade/ReShade64.dll：6.8.0.2156，SHA256 3220FBB3CCE69D9ADCF3325ACB015C446A0537AECCD1865616CE6D72B814D3AF。
- OptiScaler.dll：SHA256 E467EC37A9A4691D3B892E3F72A3D2BD8F44AAD96856A250C81E07F1F6CDCE71。
- FsrBridgeDepthAddon.addon64：原神 cache、崩铁 cache、通用 Addons 三处，SHA256 E5597EAD9C58E3AF8DF7284EB07E26B1359AD8708B642F1116299E2954EAD0BF。
- SRMI/d3d11.dll：官方 XXMI-Libs v1.2.2 的通用配套版本，SHA256 D269ABC6BF57BD3E830E410585890A3E90ACA0E5D9110D19D5E98A721E82462F，与已工作的 GIMI 配套版本一致。没有改 Mods/ShaderFixes/Core 或 SRMI ini。
- 原神两份 runtime ini 已由 GameIniBootstrap 的单独 CLI 应用 stage1/2，指向同一用户源 preset，保留原深度定义与 preset 文件内容。

启动器生产文件尚未发布：D:\APPS\HoYoShadeHub\version.ini 仍指向 app-1.4.3.10\HoYoShadeHub.exe，旧托管程序集未替换；没有创建 app-1.4.3.11 或结束旧 Hub 进程。

原生备份：D:\APPS\HoYoShadeHub\_backups\single-prenr-20261008-122644。清单：.build-temp/hoyoshade-single-prenr/deployment.json。

用户要求测试交给他；本代理没有启动游戏、XXMI Launcher 或 GPU 测试宿主。

## 需求一：每次启动保留单套 NR 输入效果策略

仅原神当前最终 DX12 路线启用该模式。识别 source AddonPath 下 FsrBridgeDepthAddon.ini：

```ini
[General]
FinalRuntimeOnly=0
EffectsOwner=DX11PreNR
```

源 ReShade.ini：

```ini
[GENERAL]
HoYoShadeEffectsStage=1
NoReloadOnInit=0
SkipLoadingDisabledEffects=0
PresetPath=<唯一用户预设>
HoYoShadeSharedPresetPath=<上次共享预设，迁移/冲突判断标记>
[INPUT]
KeyOverlay=0,0,0,0
```

关闭源 HUD；不擅自启用任何 technique。当前源 preset 是游戏目录的 HoYoShade DX11 Before NR.ini，Techniques= 为空，但有排序及效果参数，是有效用户预设，不是 Disabled 占位。

最终 ReShade2.ini：

```ini
[GENERAL]
HoYoShadeEffectsStage=2
PresetPath=<同一个用户预设>
[OVERLAY]
HoYoShadeHomeOverlay=NR Input Effects
```

保留用户 Home 热键/NR设置。stage2 核心禁止普通效果加载/绘制，但继续 GUI、输入和 addons；Home直接展示NR输入滤镜控制面板。

第一次启用：优先有效 source 用户预设，否则采用 final。有效性不能只看 Techniques 非空，要接受 TechniqueSorting 或实际 .fx/.addonfx 参数段。之后保留用户改变的选择并同步路径；同时改变两端时 source 优先。保留两端自己的 PreprocessorDefinitions，不把final方向定义覆盖source。关闭模式/删除addon配置/退出Bridge路线时清理新增keys，恢复旧行为。

特别注意：启动时 ApplyPackUserContent / 路径对齐不能覆盖单套用户选中的 preset。两份ini中的stage/home-provider键也不能丢失。

## 已写入共享源码的变更（尚未发布，请审阅合并）

- src/HoYoShadeHub.Extensions/Games/GameIniBootstrap.cs：模式识别、ConfigureSingleEffects、共享路径与退出清理。
- src/HoYoShadeHub.Extensions.Tests/GameIniBootstrapTests.cs：70项配置回归。

仓库原先已存在未提交修改，不应直接以全仓库diff当成可无脑应用的本轮补丁，也不要整体reset这些文件。本轮与现有改动共存，后续应由启动器负责人审阅集成。

一次性配置工具：.build-temp/hoyoshade-single-prenr/bootstrap-tool。production bootstrap结果：bootstrap-production-result.json。它只改配置，不启动游戏。已经在有备份的生产原神配置上成功执行，所有SourceSelection/Definitions/Stage/SharedSelection/PresetHash检查通过。

## 需求二：崩铁 XXMI + Opt 的加载顺序

现有12:12/12:13启动：SRMI原版DLL没有私有DX12能力，Hub走ReShade→Opt、官方XXMI创建或手动交接，两次在约8秒后退出；崩溃位于d3d11.dll+0xc474f。配套DLL已部署，但真实闪退是否消失仍需用户测试。

共享源码里已经有一个受条件限制的候选修复：

- src/HoYoShadeHub.Extensions/Games/StarRailXxmiLaunchRouting.cs：只读 PE 检查（不LoadLibrary）。必须同时存在 XXMIPrivateDx12PassthroughVersion、XXMIPreFlipCaptureVersion 两个非空导出，且为x64。
- src/HoYoShadeHub/Features/GameLauncher/GameLauncherPage.StartGame.cs：符合条件时使用配套 SRMI-first 路线。
- src/HoYoShadeHub/Features/GameLauncher/XxmiInjector.cs：挂起创建本次游戏进程，先预加载系统DXGI，再 SRMI → Opt → 所选ReShade，成功后恢复并返回准确PID。
- 对应 StarRailXxmiLaunchRoutingTests.cs 与测试 Program.cs。

最终候选是“无模块/无Bridge”的崩铁原生DX11 DLSS路线：hkrpg_*、XXMI开启、Opt开启、modules关闭、nativeDX12关闭、非inject-only、非Starward，Opt/SRMI/3dmloader.dll存在且SRMI具有上述能力。条件不满足保持旧路线；原神GIMI及ZZMI不变。

该路径不启动官方XXMI Launcher，避免它的更新流程覆盖配套DLL。不修改模型目录或SRMI ini。失败时不得让部分注入的挂起进程恢复，也不能干预用户已有的游戏进程。

如果未来要支持崩铁打开modules/Bridge的路线，请独立验证，不要把这个候选条件直接放宽到所有游戏。

## 验证与发布提醒

最终有936/936 CPU自测、70/70 Bootstrap检查及x64构建成功。准确日志是 launcher-no-bridge-build.log 与 deploy-dll-cpu-check；较早 launcher-build-final.log 属于调整前版本。CPU测试不证明游戏实际模型、NR、MFG或稳定性。

最终编译输出目录：src/HoYoShadeHub/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64。

产物包括主程序集、Extensions、托管Core、NuGet.Versioning、deps.json、runtimeconfig.json、resources.pri。此构建的托管Core源码没有修改，但版本/哈希与旧安装不同；只换DLL而沿用不匹配的资源与依赖可能导致启动失败。

该输出的resources.pri还包含仓库中既有界面改动，这是本代理没有直接发布整套启动器的原因之一。请在你负责的版本中合并上述必要逻辑并重新构建发布。不要把这个未审阅的完整App构建当作仅包含本轮启动修复的独立包。

本轮没有触碰127.0.0.1:19387 Web GUI需求。未来启动器问题由用户转交给启动器负责人，本代理只继续处理原生渲染/深度/NR与诊断。
