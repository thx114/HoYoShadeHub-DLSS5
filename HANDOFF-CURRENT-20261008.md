# 当前交接（2026-10-08）

## 目录
- 主仓库：`D:\CODE\HoyoDLSS5`
- 启动器源码：`D:\CODE\HoyoDLSS5\src\HoYoShadeHub`
- 当前测试启动器：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.4_x64_Full`
- 当前测试版本：`app-1.4.4.1`；1.4.4.1 已发布，1.4.4.2 未部署。
- 生产安装：`D:\APPS\HoYoShadeHub`

## 当前链路
- HoYoShade 核心：原版 Beta.9，`D:\APPS\HoYoShadeHub\HoYoShade\ReShade64.dll`
- FSR Bridge 源码：`D:\CODE\genshin_fsr_brigde\Dx11FsrBridge`
- Bridge 当前模块：`D:\APPS\HoYoShadeHub\cache\modules\genshin-fsr-bridge\genshin-fsr-bridge\v2.3.4-fg-20261006`
- Opt 源码：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada`
- 测试 Opt：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.4_x64_Full\OptiScaler\mfg-ada\mfg-ada-0.1.9\OptiScaler.dll`
- 深度/前置滤镜插件源码：`D:\CODE\FsrBridgeDepthAddon`
- 当前插件：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.4_x64_Full\cache\games\hkrpg_bilibili\Addons\FsrBridgeDepthAddon.addon64`
- Rocket/GIMI 不覆盖、不替换；抓包/反作弊不处理。

## 已确认
- 崩铁：40 系解锁已正确；NR 运动图日志显示正确，DX11 ReShade 滤镜仍读不到深度；Home 配置已恢复。
- 原神：Bridge 深度正常；FSR Bridge + Opt + ReShade 链路正常。
- ZZZ：关闭 NR、关闭 FG、换旧 Opt 后仍在 `LocalPresent -> DXGI_ERROR_INVALID_CALL`，因此暂不能归因 FsrBridgeDepthAddon、NR 或新 Opt 兼容改动。
- ZZZ 最新确定日志：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.4_x64_Full\log\sessions\20261009-020757_ZenlessZoneZero.exe_65592`
- ZZZ 当前 addon 已用准确禁用名：`FSR Bridge Depth Provider@FsrBridgeDepthAddon.addon64`。
- ZZZ 当前最有效诊断方向：原生 DX12 + Opt/Streamline 交换链/LocalPresent；已准备 Opt no-FG pass-through 修复候选，未部署。

## 当前未部署候选
- ZZZ no-FG pass-through Opt：`C:\Users\thx11\.codex\tmp\zzz-nofg-candidate\OptiScaler.dll`
- 构建日志：`C:\Users\thx11\.codex\tmp\zzz-nofg-candidate-build.log`
- 该改动：原生 DX12 且 FGOutput=NoFG 时不创建/包装 Opt FG 交换链，直接返回游戏交换链；仅用于诊断。

## 覆盖包
- 崩铁：`D:\APPS\test\TEST\星穹铁道6倍覆盖包_2.8.zip`
- 原神：`D:\APPS\test\TEST\原神6倍覆盖包_3.1.zip`
- ZZZ：`D:\APPS\test\TEST\绝区零6倍覆盖包_1.2.zip`
- 覆盖包更新候选/记录：`D:\CODE\HoyoDLSS5\.build-temp\overlay-update-defaults-20261009`
- 当前缓存动作：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.4_x64_Full\cache\games\{hkrpg_bilibili,hk4e_cn,nap_cn}\auto.json`
- 更新动作 revision：`overlay-defaults-20261009-r2`；启动器重启后执行一次。
- 绝区零临时诊断 revision：`zzz-fg-diagnostic-20261009-r3`；当前 NR 隔离，FG 基线曾被旧 profile 回写，需看下一次日志确认。

## 启动器修复
- DisabledAddons `@file` 解析修复：`D:\CODE\HoyoDLSS5\src\HoYoShadeHub.Extensions\ReShade\ReShadeProfile.cs`
- once 失败不再永久记账：`D:\CODE\HoyoDLSS5\src\HoYoShadeHub\Features\Plugins\LauncherActionRunner.cs`
- once 结果辅助：`D:\CODE\HoyoDLSS5\src\HoYoShadeHub.Extensions\ReShade\PackActionOutcome.cs`
- 完整 Extensions 自测：`998 PASS / 0 FAIL`
- 更新策略测试：`36 PASS`
- 当前测试启动器仍可能是旧运行进程；重启后才加载新程序集。

## 适配/独立包
- 独立包目录：`D:\CODE\HoyoDLSS5\build\RocketInterop-Minimal-20261008`
- 独立运行 ZIP：`D:\CODE\HoyoDLSS5\build\RocketInterop-Minimal-20261008.zip`
- 独立源码 ZIP：`D:\CODE\HoyoDLSS5\build\RocketInterop-Sources-20261008.zip`
- 只用于 Rocket 私下适配；不带启动器/GIMI/注入器/抓包/反作弊组件。

## 回滚
- 覆盖包/启动器部署备份：`D:\APPS\HoYoShadeHub\_backups\overlay-update-defaults-final-*`
- ZZZ 旧 Opt A/B 备份：`C:\HoYoShadeHub-backups\zzz-old-opti-ab-*`
- ZZZ 精确插件禁用备份：`C:\HoYoShadeHub-backups\zzz-addon-disable-exact-*`
- 当前最终部署备份：`D:\APPS\HoYoShadeHub\_backups\overlay-update-defaults-final-012712`
- 任何游戏测试前先确认游戏进程已退出；不要覆盖 GIMI。

## 下一步
1. 重启测试启动器，确认 r2 更新动作执行；检查崩铁 Opt 日志中的 `AdaMfgUnlock=true / InterpolationCount=5`。
2. 测崩铁 DX11 深度；NR 运动图不要再改。
3. 用 ZZZ no-FG pass-through 候选做一次原生 DX12 稳定性 A/B；不启用 NR，不加载 FsrBridgeDepthAddon。
4. 若 ZZZ no-FG 仍崩，测试纯原生/无 Opt；若稳定，再恢复 Opt、FG、NR 逐项。
