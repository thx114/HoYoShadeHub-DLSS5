# 原神 DLSS5 方案（CXP-2024 配方）

> 目标：让原神（DX11、只有 FSR2、无原生 DLSS）跑上 DLSS5 神经渲染 + 帧生成。

## 0. 先回答：要不要 ReShade 插件？

**要，但只对「DLSS5 神经渲染」那一半要。** 让 OptiScaler 认到 FSR2 输入的那一半，跟 ReShade 无关。

| 环节 | 组件 | 是 ReShade 插件吗 | 作用 |
| --- | --- | --- | --- |
| 输入桥 | `Dx11FsrBridge.dll` | 否，原生 DLL | Hook 原神内部的 FSR2，把标准 `ffxFsr2*` 接口垫出来，OptiScaler 才知道有 FSR2 |
| 超分 | `OptiScaler.dll` | 否，原生 DLL | 在私有 D3D12 设备上跑 DLSS SR（`Dx11Upscaler=dlss_12`） |
| 神经渲染（NR） | `*.addon64`（NIGos `dlss5-bridge` / RenoDX DLSS5 / CXP `nr-before-sr`） | 是，ReShade addon | 真正做 DLSS5 NR，必须由 ReShade 的 addon 系统加载 |

结论：

- **OptiScaler 本身不依赖 ReShade。** 它是独立 DLL；配置里 `LoadReShade = false` 就是明确不让它自己去加载 ReShade（因为 ReShade 已经由启动器单独注入了）。
- **DLSS5 NR 只以 ReShade add-on（`.addon64`）形态发布**，ReShade 在这里只是「宿主 / 加载器」（`ReShade.ini` 的 `[ADDON] AddonPath` + `LoadFromDllMain`），不需要它的任何 shader / 滤镜。
- 所以：**只要 DLSS / FSR 超分，不需要 ReShade；要 DLSS5 神经渲染，必须有 ReShade。**
- **例外（本机现在要走的路线）**：OptiScaler 自带的 `[DlssNr]` 内建 NR 是跑在 OptiScaler 内部的原生实现，**不经过 ReShade**。见第 9 节。
- 好消息：HoYoShade 本来就会注入 `ReShade64.dll`（`D:\APPS\HoYoShadeHub\HoYoShade\inject.exe`），ReShade 这一环现成；真正缺的是把 `Dx11FsrBridge.dll` 和 `OptiScaler.dll` 也塞进进程（见第 6 节）。

> 注意：桥的上游 README 明确警告：**N 卡上 OptiScaler 与 ReShade 同时启用可能不稳定**（加载顺序 + 驱动交互，与桥代码无关）。DLSS5 方案恰恰是「桥 + OptiScaler + ReShade 三家同开」，属于最吃紧的组合。

## 1. 为什么原生 OptiScaler 不行

实测（证据）：

| 检查 | 结果 |
| --- | --- |
| `YuanShen.exe` 导出表 | 只有 `AmdPowerXpressRequestHighPerformance` / `NvOptimusEnablement`，**无 FSR2** |
| `YuanShen.exe` 二进制搜 `ffx_fsr2` / `amd_fidelityfx` / `FidelityFX` | 全无 |
| 游戏目录 + `YuanShen_Data\Plugins` | **没有任何 FSR 的 dll** |
| `mhypbase.dll` | 无 FSR2 标识 |

结论：原神的 FSR2 是**内部静态链接**进 exe/引擎的，符号不导出。
OptiScaler 的 DX11 FSR2 输入靠 `GetProcAddress(exeModule, "ffxFsr2ContextCreate")`，自然拿不到，报 `FSR Hooks: Don't Exist`。

上游维护者也在 issue #433 确认过：

> Genshin runs on DX11 and only has FSR2, so I'm gonna say it won't work
> These games have a kernel anti-cheat, so don't bother unless you want to get banned

## 2. CXP-2024 的解法：四个组件

来源：<https://github.com/CXP-2024/dlss5_for_genshinimpact>（一键包，含启动器 / ReShade / DLSS DLL）

| 组件 | 来源路径 | 类型 | 作用 |
| --- | --- | --- | --- |
| **`Dx11FsrBridge.dll`** | `release/configs/` | 原生 DLL（注入进游戏进程） | 关键。独立 hook 原神内部 FSR2（Detours 拦 `D3D11CreateDevice` / `GetProcAddress`），对外导出标准 `ffxFsr2ContextCreate` 等 6 个符号，让 OptiScaler 能检测到 FSR |
| `OptiScaler.dll`（补丁版） | 本地编译，见第 3 节 | 原生 DLL（注入进游戏进程） | DLSS-on-DX12 路径（`Dx11Upscaler=dlss_12`） |
| `nr-before-sr.zh-CN.addon64` | `release/pre-nr/` | **ReShade addon** | 前置 NR（低渲染分辨率先跑 DLSS5 NR，再由 DLSS SR 放大） |
| `nrchain_nvngx.dll` | `release/pre-nr/` | NGX 链 DLL（addon 的依赖） | 自定义 NGX 链，给 addon 提供 Feature 18 的 NR 入口 |

补丁：`src/patches/OptiScaler-DLSSOn12-pre-NR.patch`
基座：`Dagherbou/OptiScaler_DLSSNR` @ `973761621353b99bee3dc7d4bb27b117fef2644f`
v1.3 还有增量补丁 `src/patches/OptiScaler-RTX30-dual-mode.delta.patch`（RTX30 双模式）。

桥的 ini：`release/configs/Dx11FsrBridge.ini`（`EnableFsr2GetProcAddressShim=1`）

桥的上游：<https://github.com/AizawaHikaru233/genshin_fsr_brigde>（GPL-3.0，CXP 只是预编译后打包）。
上游定位是「**不装 OptiScaler 也能自己跑 FSR4/FSR3/FSR2**」，接 OptiScaler 只是为了多出 DLSS / XeSS。

### 桥的导出与 hook（实测证据）

`dumpbin /exports Dx11FsrBridge.dll`：

```
1  ffxFsr2ContextCreate
2  ffxFsr2ContextDestroy
3  ffxFsr2ContextDispatch
4  ffxFsr2GetJitterPhaseCount
5  ffxFsr2GetRenderResolutionFromQualityMode
6  ffxFsr2GetUpscaleRatioFromQualityMode
```

二进制字符串里能看到 `GetProcAddress intercepted`、`D3D11CreateDevice`、`already-created device, GetProcAddress path, or module loaded later`，
说明它**兼容晚注入**（设备已经建好了也能挂上），不要求卡在 `D3D11CreateDevice` 之前。

## 3. 本地编译（已完成）

```
环境: MSBuild D:\VS2022BuildTools\MSBuild\Current\Bin\MSBuild.exe
      MSVC 14.44.35207 + Windows SDK 10.0.26100
源码: D:\CODE\_ref\opti-build   (git clone --recursive)
补丁: git apply  10 改 + 2 新，全部 cleanly
编译: MSBuild OptiScaler.sln /p:Configuration=Release /p:Platform=x64   0 错误
产物: x64\Release\a\OptiScaler.dll  (24.67 MB)
已装: D:\APPS\HoYoShadeHub\OptiScaler\cxp-genshin\v1.3\
```

**这份缺 `AdaMfgUnlock`（40 系解帧）**：字符串搜索证实为 0。
`wilsjo2/v0.8.8` 那份反而有（AdaMfg + AmpereMfg + DlssNr 全有）。

## 4. 正确的配置（从 configure_and_start.ps1 提取）

### OptiScaler.ini

```ini
[Upscalers]
Dx11Upscaler = dlss_12              ; DX11 输出 / DX12 跑 DLSS

[Inputs]
EnableFsr2Inputs  = true
UseFsr2Dx11Inputs = true            ; 强制走 exe-export 路径（配合桥的垫片）
UseFsr2Inputs     = true

[Libraries]
OptiDllPath      = <构建目录>
NvngxDlssPath    = <构建目录>\nvngx_dlss.dll
NvngxFeaturePath = <构建目录>

[Plugins]
LoadReShade = false                 ; 不让 OptiScaler 自己加载 ReShade

[Hooks]
SkipD3D11DeviceVTableHooks = false

[DlssNr]
Enabled = false                     ; 外部 addon 路线（第 2 节）；要走内建 NR 就改 true，见第 9 节

[Log]
LogToFile  = true
LogLevel   = 2
LogFileName = <构建目录>\OptiScaler.log
```

### ReShade.ini

```ini
[ADDON]
AddonPath       = <pre-nr 目录>
DisabledAddons  =
LoadFromDllMain = nr-before-sr.zh-CN.addon64   ; 前置 NR 靠这一行
```

## 5. 运行链路

```
原神 DX11 低分辨率帧
    [Dx11FsrBridge 垫出 FSR2 符号]
    OptiScaler (Dx11Upscaler=dlss_12) 在私有 D3D12 设备上跑 DLSS SR
    nr-before-sr.addon64 做前置 NR      <- 这里必须有 ReShade 当宿主
    ReShade / UI
```

游戏内操作：**抗锯齿必须选 FSR2**；`Insert` 开 OptiScaler，`Home` 开 ReShade，`F6` 切前置 NR。

## 6. 接入方式：怎么把桥和 OptiScaler 塞进进程

CXP 用 `unlockfps_nc.exe` + `fps_config.json` 启动游戏，并**按固定顺序注入** ReShade, `Dx11FsrBridge.dll`, `OptiScaler.dll`。

HoYoShade 用的是它自己的 `inject.exe`，**只注入 `ReShade64.dll`**。所以桥和 OptiScaler 需要额外方案：

1. ~~让 HoYoShade 的 inject.exe 也注入这两个~~：**本 fork 已经有现成实现**。
   `src/HoYoShadeHub/Features/GameLauncher/DllInjector.cs` 是一个通用的
   `OpenProcess + VirtualAllocEx + WriteProcessMemory + CreateRemoteThread(LoadLibraryW)` 注入器，
   注释里写明就是给「OptiScaler / DLSS Enabler 那套」用的，可注入任意 DLL。
   （注意：进程若以管理员启动，Hub 也要管理员；注入晚了游戏可能已经过了关键 hook 点，见下。）
2. ~~把桥做成 proxy DLL 放进游戏目录~~：不需要。桥本身就是被注入的原生 DLL，**不改名 `dxgi.dll` / `d3d12.dll`**；而且它兼容「设备已建好」的晚注入场景。
3. 用 CXP 自己的启动器（绕过 HoYoShade）：会丢掉 Hub 的集成，且 CXP 的注入顺序 / 配置路径跟 Hub 不一致。

**加载顺序（重要）**：ReShade, 桥, OptiScaler。ReShade 要先在，NR addon 才有宿主；桥在 OptiScaler 之前，OptiScaler 的 `GetProcAddress` 才垫得到。

**下一步要查**：

- `DllInjector` 目前**还没有被任何地方调用**（`DllInjector.Inject` 全仓库无引用），需要在启动流程里按顺序接线，并处理「等进程, 等 ReShade 就绪, 再注入桥 / OptiScaler」的时序。
- `inject.exe` 是 HoYoShade 的闭源外部二进制，能不能让它顺带注入，或者干脆用 `DllInjector` 完全替代它，需要测。
- 想直接看上游实现：`AizawaHikaru233/genshin_fsr_brigde` 的 `tools/FpsUnlockInstaller/`（安装器）和 `FufuGraphicsPlugin/`（芙芙启动器插件）。

## 7. 版本绑定风险

桥的 ini 里有硬编码进 `YuanShen.exe` 的 RVA：

```ini
[RenderScaleMenu]
BuildCmdBuffersRva       = 0x6DE3950
RenderScaleApplyRva      = 0xADA0900
RenderScaleOffset        = 0x88
SourceScaleRva           = 0x505E5FC
RenderScaleKeyPointerRva = 0x52B1180
GraphicsOptionLookupRva  = 0x128B8590
GraphicsIndexResolverRva = 0xC209120
```

**原神一更新就可能失效**，整个方案会挂。这也是 CXP 要自己维护整套包的原因。
（上游 AizawaHikaru233 的 README 声称已适配到原神 7.0 且「之后如无意外不用再更新」，指的是桥本体；上述 RVA 仍以 CXP 包内实测为准。）

## 8. 反作弊

原神目录里有 `HoYoKProtect.sys`（内核反作弊，3.99 MB）。上游维护者明确警告过这类游戏注入有封号风险。HoYoShade 的 ReShade 注入是社区默认容忍的，但**再叠 OptiScaler + 桥**是不同的风险级别，自行判断。

## 9. 路线 B：OptiScaler 内建 NR（不用 ReShade）

`[DlssNr]` 是 OptiScaler 自带的 DLSS 5 神经渲染实现（Dagherbou 分支的内建 NR），**跑在 OptiScaler 内部，不经过 ReShade**。
调用点就在 DX11-on-DX12 桥里（`IFeature_Dx11wDx12::Evaluate` 里调 `DlssNr::EvaluateAfterUpscale`），正是原神这条路径，所以不需要任何 addon。

### 需要的文件

| 文件 | 说明 | 放哪 |
| --- | --- | --- |
| `nvngx_dlssnr.dll` | NVIDIA 的 NR 模型（约 165 MB），要自己从带 NR 的驱动里取 | OptiScaler.dll 旁边，或游戏 exe 旁边 |
| `nvngx.dll_dlssnr.dll` | forwarder（约 112 KB），包里有 | 同上 |

> 第二个文件故意起成带 `nvngx.dll` 的名字：NVIDIA 的模型入口会检查调用者模块路径里有没有 `nvngx.dll`，这是唯一原因。

现成的两份在 `D:\APPS\HoYoShadeHub\OptiScaler\cxp-genshin\v1.3\`（`nvngx_dlssnr.dll` 165830144 字节、`nvngx.dll_dlssnr.dll` 114688 字节）。

### ini

```ini
[DlssNr]
Enabled = true                      ; 默认 auto = false
```

其余按第 4 节，但注意：

- **不要**装 `nr-before-sr.zh-CN.addon64` / `nrchain_nvngx.dll`（那是 ReShade 路线，两条会重复处理）
- `LoadReShade = false` 保持

### 游戏内

`Insert` 开 OptiScaler 菜单，进「DLSS Neural Rendering」勾 `Enable Neural Rendering`。
也可以在 Keybinds 里绑键，那一条叫 `Neural Rendering`。

### 怎么确认真的生效

看 `OptiScaler.log`：

- `DLSS-NR: the D3D11 bridge reached the hand-off (upscale ok: true, enabled: true)`：桥这条路通了
- `DLSS-NR did not run: <原因>` / `DLSS-NR unavailable: <原因>`：没跑起来，原因会写清楚（缺 model / NGX 核心没初始化 / 颜色编解码编译失败 / 深度或运动矢量读不出来 等）
- `[DlssNr] DebugView = 3`：把模型改动放大 20 倍的差异视图，整幅纯灰就说明模型没起作用（`= 1` 看模型拿到的输入，`= 2` 看模型的原始输出）

### 补丁管什么、不管什么（重要）

- 让 OptiScaler **认到 FSR2** 的是 **桥**（`Dx11FsrBridge.dll` 的 `GetProcAddress` 垫片 + 6 个 `ffxFsr2*` 导出），**不是这个补丁**。
- `src/patches/OptiScaler-DLSSOn12-pre-NR.patch` 实际改的是：
  1. 原神 HDR 颜色是 `R10G10B10A2_TYPELESS`，`R10` 正好是 DLSS5 pre-NR 不接受的格式；补丁把它提升成 typed UNORM，再在 NGX 载体调用前转成 FP16（`PreNrColorTransfer`）
  2. 与 GIMI / 3DMigoto 共存（`SkipD3D11DeviceVTableHooks`、GIMI device / context 透传）
  3. 不让 OptiScaler 劫持外部 addon 的 `nrchain_nvngx.dll`

所以两个都要：**桥负责让 OptiScaler 看见 FSR2，补丁负责让 NR 在原神的颜色格式上跑得起来。**

### 还缺的一步

上面这些是"装了就能跑"的部分；把它**送进游戏进程**仍然是第 6 节那个问题：
`Dx11FsrBridge.dll` 和 `OptiScaler.dll` 都需要注入，HoYoShade 的 `inject.exe` 只注入 ReShade64.dll，
fork 里现成的 `DllInjector` 还没接线。