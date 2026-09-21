# DLSS Enabler 使用与设置（本仓库集成版）

> 本文对应 `GAMES-AND-INJECT.md` §8.13 / §8.14：Hub 里「额外注入 DLL + DLSS Enabler 勾选框」这套。
> DLSS Enabler 本体是第三方项目（`artur-graniszewski/DLSS-Enabler`），内部就是 **OptiScaler**
> （它把 OptiScaler 的 `nvngx.dll` 改名成 `dlss-enabler-upscaler.dll`）+ 自己的 nvngx 代理。

## 0. 它是干什么的

在**原生支持 DLSS2 / DLSS3 的游戏**里，用别的上采样器顶掉 DLSS，或者给不支持帧生成的显卡补上帧生成。
典型用法是「DLSS 上采样 → 换成 FSR/XeSS」；**我们这套只开帧生成**：上采样继续用游戏原生 DLSS，
只让 OptiScaler 接管帧生成。

## 1. 当前这台机器上的状态

| 东西 | 位置 |
| --- | --- |
| 部署目录 | `D:\APPS\HoYoShadeHub\DLSS-Enabler`（Hub 也能自己装到 `<用户数据目录>\.hysx\dlss-enabler`） |
| 要注入的 DLL | `dlss-enabler-upscaler.dll`（= OptiScaler） |
| 配置 | 同目录 `nvngx.ini`（原件备份 `nvngx.ini.orig`） |
| 日志 | 同目录；默认**不写**，排查时把 `[Log] LogToFile=true` |

已经写好的「只开帧生成」配置：

```ini
[Upscalers]
Dx12Upscaler=dlss        ; 上采样保持游戏原生 DLSS，不换 FSR/XeSS

[FrameGen]
Enabled=true             ; 开帧生成
FGInput=dlssg            ; 用游戏自己的 DLSSG 当输入（游戏得支持 DLSS3 帧生成）
FGOutput=nvngxfg         ; 用 dlssg_to_fsr3_amd_is_better.dll 输出
```

## 2. 怎么用（Hub 三步 + 游戏内两步）

**Hub 里：**

1. **用管理员启动 Hub**（WeGame / 米哈游启动器起的游戏大多是管理员进程，注入需要同权限）；
2. 启动器页选中要玩的游戏 → 勾 **「注入模式」** → 勾 **「DLSS Enabler」**（它会把该游戏的「额外注入 DLL」
   自动指到 `dlss-enabler-upscaler.dll`；找不到部署会问你要不要下载部署）；
3. 点「开始游戏」→ 看到「已启动 HoYoShade 注入器」和「额外注入：已把 dlss-enabler-upscaler.dll 注入 …」
   两条提示后，**用游戏自己的启动器**（HoYoPlay / WeGame）把游戏拉起来。

**游戏里：**

4. 游戏设置里把 **DLSS 打开**，并且把 **帧生成（Frame Generation）也打开** —— `FGInput=dlssg` 要的就是游戏自己的
   DLSSG，游戏里没开它就没有输入；
5. 按 **Insert**（默认）打开 OptiScaler 菜单，看 **Frame Generation** 那一栏：Enabled / Input / Output 是不是
   我们要的三个值；顺便按 **End** 可以直接开关帧生成。

## 3. 快捷键（`[Menu]` 段，默认值）

| 键 | 作用 |
| --- | --- |
| `Insert`（0x2D） | 打开/关闭 OptiScaler 菜单 |
| `End`（0x23） | 帧生成 开/关 |
| `PageUp`（0x21） | FPS 覆盖层 开/关 |
| `PageDown`（0x22） | 切换 FPS 覆盖层样式 |

> ReShade 的覆盖层是 `Home`，两者不冲突。

## 4. 什么时候要改哪个键

| 想干什么 | 改哪里 | 值 |
| --- | --- | --- |
| 菜单不出来 / 帧生成被禁用 | `[Menu] OverlayMenu` | `true`（默认 auto：DLL 叫 `nvngx.dll` 时是 false，其它名字是 true；显式写死最保险） |
| 看不到帧率 | `[Menu] ShowFps` | `true`（`FpsOverlayType=0..6` 换样式） |
| 游戏里没有 DLSS 帧生成选项 | `[FrameGen] FGInput` | 换成 `fsrfg`（要 AMD 的 `amd_fidelityfx_framegeneration_dx12.dll`）或 `upscaler`（要开上采样，且**必须配合 Hudfix**）；`nvngxfg` 是 DLSS Enabler 自带的那条 |
| 帧生成在 30 系卡上开不了 | `[Spoofing] Dxgi` / `SpoofedDeviceId` | 默认 auto 会伪装成 4090（`0x2684`）；还不行把 `SpoofHAGS=true` |
| 画面有 UI 鬼影/抖动 | `[FrameGen] HudCutoff` | `0.0`–`1.0` 试（只对 FSR FG 和 NvngxFG 有效）；必要时 `DisableHudless=true` |
| 想限帧 | `[Framerate] FramerateLimit` | 显示器刷新率的一半左右（比如 60），**需要游戏支持并开着 Reflex** |
| 排查问题 | `[Log] LogToFile=true`、`LogLevel=1` | 日志默认写在同目录 `OptiScaler.log` |
| 想让 OptiScaler 自己加载 ReShade（代理路线才用） | `[Plugins] LoadReshade=true` | 我们的场景**不要开** —— ReShade 由 HoYoShade 注入，开了会加载第二份 |

## 5. 怎么确认生效

- 菜单 → Frame Generation：`Enabled=true / Input=dlssg / Output=nvngxfg`；
- 开 `ShowFps`：帧率大致翻倍（FG 的典型表现），关掉 FG 掉回原来；
- 日志：`[Log] LogToFile=true` 后看 `OptiScaler.log` 里有没有 hook 到 swapchain / `DLSSG`。

## 6. 出问题怎么退回

「注入」不是 OptiScaler 官方部署方式，官方推荐的是**代理 DLL**：

1. 把 `D:\APPS\HoYoShadeHub\DLSS-Enabler\version.dll` 复制到游戏 exe 旁边；或
2. 把 `dlss-enabler-upscaler.dll` 改名 `dxgi.dll` 放进游戏目录；
3. 同时把 `nvngx.ini` 也复制过去（OptiScaler 找配置看的是它自己/游戏目录）。

另外注意：**DLSS5 那套插件（renodx-dlss5 / neural-interposer）和 OptiScaler 抢的是同一层 DLSS 调用**，
同时开容易互相打架 —— 测的时候一次只开一个。
