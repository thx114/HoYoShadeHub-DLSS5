# 在同一游戏进程内叠加多层图形 Hook 的技术现实

**研究对象**：miHoYo 游戏（原神 / HK4E、星穹铁道、绝区零）Windows + NVIDIA 平台，在**同一个 DX11 进程链**中同时运行：

- XXMI / 3DMigoto（GIMI）模型替换
- 基于 ReShade 的 DLSS5 插件（RenoDX 风格，`renodx-dlss5.addon64`）
- `Dx11FsrBridge.dll`（拦截 DX11 调用并把 FSR 超分/帧生成交给 D3D12 后端）
- OptiScaler（fork：OptiScaler-MFG-Ada），做 DLSS/FSR/XeSS 超分 + 帧生成，可内部自建 D3D12 设备/共享纹理

---

## 证据分级约定

本报告对每条论断标注来源等级，**不把推断当作事实**：

| 标记 | 含义 |
|---|---|
| **(A) 一手** | 来自上游官方文档、上游源码、或本机实际部署产物/运行日志的直接证据 |
| **(B) 社区** | 来自第三方整合项目、社区文档、issue 报告，未经上游确认 |
| **(C) 推断** | 我基于 (A)(B) 的技术推理，**未经任何来源直接验证** |

引用的本机路径是运行环境中的真实产物；引用的源码行号来自对应仓库的本地快照，可复核。

---

## 0. 结论摘要（先读这一节）

1. **3DMigoto 不是"放在游戏目录里的 d3d11.dll 代理"这一个形态。** XXMI 的 GIMI 发行版明确声明"本发行版意图由 XXMI Launcher 加载"**(A)**，且本机日志证实 XXMI Launcher 是**把 `d3d11.dll` 注入（inject）进 `YuanShen.exe`**，游戏目录里根本没有 `d3d11.dll`**(A)**。这一点与你问题描述中"in the game directory"的前提不同。

2. **OptiScaler 官方支持的文件名里没有 `d3d11.dll`。** 官方 wiki 列出的支持名是 `dxgi.dll` / `winmm.dll` / `d3d12.dll` / `dbghelp.dll` / `version.dll` / `wininet.dll` / `winhttp.dll` / `OptiScaler.asi`**(A)**。所以"3DMigoto 用 d3d11.dll，OptiScaler 也用 d3d11.dll"这种同名冲突**在 OptiScaler 官方形态下不成立**。

3. **ReShade 的"第二 runtime"是 ReShade 自己的既有能力，不需要把第二个 ReShade 改名成 `d3d12.dll`。** 本机 `ReShade.log` 直接证实：OptiScaler 的 DX11→DX12 桥创建 `OptiDx11SwapchainHost` 窗口和 D3D12 设备后，ReShade **自动**为其创建了第二个 effect runtime，并自动命名为 **`ReShade2.ini`****(A)**。

4. **真正的冲突点是 swapchain / Present / device 的"所有权"，不是"文件名撞车"。** 社区整合项目已经把这条总结成明确规则："不要同时把另一个 ReShade `dxgi.dll`/`d3d11.dll` 放入游戏目录，也不要额外启动 GIMI Loader；这些做法会**重新引入 SwapChain 和 Hook 所有权冲突**"**(B)**。

5. **有可靠证据表明"GIMI + ReShade + OptiScaler + DLSS5"在同一个原神 DX11 进程里可以工作，但代价是所有组件都要改。** 该整合项目的所有权规则是：**GIMI 是唯一的 D3D11 device/context 包装者，也是唯一的真实 Present 所有者；ReShade 的图形 Hook 被关闭，改由 GIMI 通过 ReShade 的 public C Runtime API 托管创建 runtime；OptiScaler 的 D3D11 device vtable hook 被禁用****(B)**。

6. **DX11 游戏上的帧生成必然引入一个自建 D3D12 世界。** OptiScaler 官方 wiki 明说 OptiFG "**Only supported in DX12**"**(A)**，其 Known-Issues 也确认 DX11→DX12 超分走"background DirectX 12 device (D3D11on12)"并有 10–15% 性能损失**(A)**。本机 fork 源码里的 `Dx11wDx12SC : public IDXGISwapChain4` 就是这条路径的实体：它自建 D3D12 设备、D3D12 命令队列、`ID3D11Fence`/`ID3D12Fence` 共享围栏、共享后缓冲**(A)**。

7. **"3DMigoto 存在时 DX11 帧生成不兼容"有具体机制证据，不是传闻。** GIMI 整合项目的架构文档记录了一个真实修复：**DX11 包装后的 swapchain Present 路径曾把 `ID3D11DeviceContext` 传给 D3D12 feature 的 timing 方法，vtable 槽位被误读为 `PSSetConstantBuffers`，产生误导性的 GIMI/D3D11 崩溃****(B)**。

---

## Q1. 3DMigoto / XXMI：loader DLL 名、可否改名/旁加载、`d3dx.ini` 的 `[loader]` 段、能否共存同名代理

### 1.1 DLL 名与加载方式

**(A) 一手（本机 GIMI 发行版 `d3dx.ini`，`D:\APPS\XXMI\GIMI\d3dx.ini` 第 1–29 行）**

```ini
; Settings used by the external 3DMigoto Loader
[Loader]
; WARNING! This release is intended to be loaded by XXMI Launcher and is missing required 3dmigoto DLLs!
; XXMI Launcher can be installed with https://github.com/SpectrumQT/XXMI-Installer/releases/latest

; Target process to load into:
target = YuanShen.exe

; Allow 3dmigoto DLL to be loaded by following .exe:
; DLL must support following features:
; * Load from nested directories relative to "loader" exe: https://github.com/bo3b/3Dmigoto/commit/e7d70cc779887dd43d3c68b5c6e43711aa9a0e7f
; * Load from any location by specified "loader" exe: https://github.com/bo3b/3Dmigoto/commit/0a029a748c7e64cd48a1f571374d322e8aa065e0
loader = XXMI Launcher.exe

; WARNING! Options below are left for advanced users and will NOT be taken into account by XXMI Launcher!

; Automatically launch the game from the loader:
launch = C:\Games\Genshin Impact\DATA\Genshin Impact game\GenshinImpact.exe

; This tells the loader where to find 3DMigoto. This DLL must be located
; in the same directory as 3DMigoto Loader.exe and will be loaded in the target
; process under the same name. If d3d11.dll doesn't work try 3dmigoto.dll
module = d3d11.dll

; Uncomment to always elevate the loader to support games that run as admin.
; This will display a UAC prompt so only enable it if you actually need it.
require_admin = true
```

- **loader DLL 名 = `d3d11.dll`**，且注释明确给出备选名 **`3dmigoto.dll`**（`module` 项）。
- 该 GIMI 发行版**故意不含** 3DMigoto 的必需 DLL：注释原文 "is missing required 3dmigoto DLLs"，即它依赖 XXMI Launcher 提供加载机制。

**(A) 一手（本机 XXMI Launcher 运行日志，`D:\APPS\XXMI\XXMI Launcher Log.txt`，2026-10-08 02:27:24–25）**

```
core.event_manager DEBUG FIRED: ApplicationEvents.Inject(library_name='d3d11.dll', process_name='YuanShen.exe')
core.utils.dll_injector DEBUG Successfully injected DLL to process YuanShen.exe (PID: 766348): D:\APPS\XXMI\GIMI\d3d11.dll
```

**这是决定性证据：XXMI Launcher 用 DLL 注入的方式把 `D:\APPS\XXMI\GIMI\d3d11.dll` 注入 `YuanShen.exe`**，而不是把 DLL 放进游戏目录让 Windows 的 DLL 搜索顺序去加载。

**(A) 一手（本机游戏目录清点，`D:\APPS\miHoYo Launcher\games\Genshin Impact Game`）**
该目录下 `.dll/.exe/.ini` 只有：`config.ini`、`DisplayCommander.ini`、`HoYoShade DX11 Disabled.ini`、`mhypbase.dll`、`ReShade.ini`、`ReShade2.ini`、`rtlbase.dll`、`YuanShen.exe`。**没有任何 `d3d11.dll` / `dxgi.dll` / `OptiScaler.dll`** —— 与本机所有组件都走"注入"而非"目录代理"的事实一致。

### 1.2 `[Loader]` 段各键的实际语义

**(A) 一手（3DMigoto 源码快照 `D:\CODE\DX12Mod\reference\3Dmigoto\DirectX11\`）**

- `target` = 目标进程名过滤。`DLLMainHook.cpp:231–286` 注释说明：DLL 被注入陌生进程时，会**读 DLL 同目录的 `d3dx.ini`**、找 `[Loader]` 段、取 `target` 与当前 exe 路径比对，不匹配就自我剔除，避免污染无关进程并把 DLL 锁住。比对使用完整路径的**尾部匹配**，并强制要求匹配点紧跟目录分隔符（`DLLMainHook.cpp:275–286`）。
- `loader` = 允许哪个 exe 来加载本 DLL（GIMI 里是 `XXMI Launcher.exe`）。注释里两条 commit 链接对应"从相对嵌套目录加载"和"由指定 loader exe 从任意位置加载"两个能力。
- `module` = loader 应该注入哪个 DLL 文件名；同时说明"该 DLL 必须与 loader exe 同目录，并会以同名被载入目标进程"。
- `launch` / `require_admin` / `delay` = 便捷项（自动启动游戏、强制提权、注入确认后延时）。GIMI 的注释明确说这些"不会被 XXMI Launcher 采纳"。

**关于 `exclude_d3d11`：在本机 GIMI 的 `d3dx.ini` 中不存在这个键。**(A) `D:\APPS\XXMI\GIMI\d3dx.ini` 的 `[Loader]` 段（第 4–35 行）只有 `target` / `loader` / `launch` / `module` / `require_admin` / `delay`。文件中确有 `exclude_recursive`，但它在 `[Include]` 段（第 52–53 行，值为 `DISABLED*`、`desktop.ini`），语义是**包含文件扫描时的排除**，与 D3D11 代理无关。

> 你问题里提到的 `exclude_d3d11` 未能在本次核对的一手文件中找到。**(C) 推断**：它可能是其他 3DMigoto 分支/其他游戏的 d3dx.ini 中的键，或与 `[Include] exclude_recursive` 混淆。建议不要把它当作 GIMI 的既有开关。

**(A) 一手：3DMigoto 确实支持"从游戏目录被当作 d3d11.dll 代理加载"这一形态，并有对应开关。**

`D3D11Wrapper.cpp:49–104`：

```cpp
// This function checks if 3DMigoto is running in the intended executable - it
// is similar to verify_intended_target() that we use in DllMain to bail out of
// unwanted executables when using the 3DMigoto loader, and uses the same
// [Loader]target ini setting (because adding a second setting would be
// confusing). But in this case we could be loaded from the game directory so
// we can't simply unload outselves and still need to pass d3d11.dll exported
// functions through to the original. We just will skip wrapping/hooking the
// device/context or intercepting the swap chain.
static bool verify_intended_target_late()
{
    ...
    if (!GetIniBool(L"Loader", L"check_target_even_without_loader", false, NULL))
        return true;
```

- 存在 `[Loader] check_target_even_without_loader`，说明 3DMigoto 明确考虑过"不经 loader 注入、而是作为游戏目录代理"的运行方式。
- 该形态下的失败行为是**降级而非卸载**：把 `d3d11.dll` 的导出函数**透传给原始系统 DLL**，但**跳过 device/context 包装与 swapchain 拦截**。

### 1.3 能否改名成 `dxgi.dll` / 旁加载？能否两个同名代理共存？

| 问题 | 结论 | 依据 |
|---|---|---|
| 3DMigoto 可否以 `dxgi.dll` 存在 | **(C) 未找到一手证据表明官方支持。** 一手文件只给出 `d3d11.dll` 与备选 `3dmigoto.dll` 两个名字。 | `d3dx.ini` 第 22–25 行 (A) |
| 3DMigoto 可否旁加载（不经 loader） | **(A) 可以**，即作为游戏目录内的 `d3d11.dll` 代理；此时由 `check_target_even_without_loader` 决定是否校验 target。 | `D3D11Wrapper.cpp:49–104` (A) |
| 两个同名代理能否共存 | **(A) 在 Windows 文件系统层面不能**：同一个游戏目录里只能有一个 `d3d11.dll`。这是文件系统约束，不是 3DMigoto 的选择。 | 常识/文件系统约束；本机游戏目录清点亦无代理 DLL (A) |
| 同一进程内两层都 hook DX11 能否共存 | **(A) 能，但必须有明确的所有权规则**，见 Q4/Q6。 | 见下 |

### 1.4 3DMigoto 自己的 swapchain 包装（为 Q6 铺垫）

**(A) 一手（`HackerDXGI.h`）**

```cpp
//	HackerSwapChain -> IDXGISwapChain1 -> IDXGISwapChain -> IDXGIDeviceSubObject -> IDXGIObject -> IUnknown
class HackerSwapChain : public IDXGISwapChain1
{
	IDXGISwapChain1 *mOrigSwapChain1;
	...
	HRESULT STDMETHODCALLTYPE Present(...);
	HRESULT STDMETHODCALLTYPE GetBuffer(...);
	HRESULT STDMETHODCALLTYPE ResizeBuffers(...);
	HRESULT STDMETHODCALLTYPE GetLastPresentCount(...);
	HRESULT STDMETHODCALLTYPE Present1(...);
};

class HackerUpscalingSwapChain : public HackerSwapChain
{
private:
	IDXGISwapChain1 *mFakeSwapChain1;
	ID3D11Texture2D *mFakeBackBuffer;
	...
	STDMETHOD(GetBuffer)(...);
	STDMETHOD(SetFullscreenState)(...);
	STDMETHOD(GetDesc)(...);
	STDMETHOD(ResizeBuffers)(...);
	STDMETHOD(ResizeTarget)(...);
};
```

**(A) 一手（`HookedDXGI.cpp:458–469` 注释与代码）**
> `// wrap the returned swapchain as either HackerSwapChain or HackerUpscalingSwapChain.`
> `swapchainWrap = new HackerUpscalingSwapChain(origSwapChain, hackerDevice, hackerContext, ...)`

**(A) 一手（`HookedDXGI.cpp:801–807`，3DMigoto 自己记录的 hook 顺序问题）**
> "Note that the Steam overlay is known to rely on this same DirectX implementation detail and in the past we inadvertently bypassed them by redirecting the swap chain creation in CreateDeviceAndSwapChain ourselves in such a way that their hook may never have been called, **depending on which tool managed to hook in first (3DMigoto getting in first was the fail case** as we could then call the original CreateSwapChain without going through Steam's hook)."

**这段注释是 3DMigoto 上游自己承认"hook 顺序决定谁能看到 swapchain 创建"的一手证据**，并且它把"3DMigoto 先进"列为**失败场景**。这与你的 Q2"injection order matters"关切直接相关。

3DMigoto 的 hook 面（`HookedDXGI.cpp`）覆盖：`CreateDXGIFactory/1/2`、`IDXGIFactory::CreateSwapChain`、`IDXGIFactory2::CreateSwapChainForHwnd` / `ForCoreWindow` / `ForComposition`，并为重入设置了 `hooking_quirk_protection` 保护（`:559–562`、`:834–841`）。

---

## Q2. OptiScaler：可用的 DLL 名、加载顺序/"谁赢"规则、已有 hooker 时的行为

### 2.1 官方支持的 DLL 文件名

**(A) 一手（OptiScaler wiki, Automated Installation）**
> ### OptiScaler supports these filenames:
> * dxgi.dll
> * winmm.dll
> * d3d12.dll
> * dbghelp.dll
> * version.dll
> * wininet.dll
> * winhttp.dll
> * OptiScaler.asi _(needs [Ultimate ASI Loader x64](https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases) or similar)_

来源：<https://github.com/optiscaler/OptiScaler/wiki/Automated-Installation>

> [!IMPORTANT]
> **Please don't rename the ini file, it should stay as `OptiScaler.ini`**.

**⚠️ 关键更正：列表中没有 `d3d11.dll`。** 你问题里假设 OptiScaler 可作为 `d3d11.dll` 安装 —— **官方文档不支持这个说法**。

**(A) 一手（OptiScaler 源码交叉验证，本机 fork `OptiScaler-MFG-Ada\OptiScaler\dllmain.cpp`）**
代理名分支实际只有：`version.dll`（:379）、`winmm.dll`（:433）、`wininet.dll`（:486）、`dbghelp.dll`（:539）、`winhttp.dll`（:628）、`dxgi.dll`（:681）、`d3d12.dll`（:736）。**没有 `d3d11.dll` 分支。** 文件中对 `d3d11.dll` 的唯一提及是 `:929 LOG_DEBUG("d3d11.dll already in memory")`，那是**加载系统 d3d11 的检测**，不是"OptiScaler 叫 d3d11.dll"。

**(B) 社区旁证（Genshin FSR Bridge 文档）** 也把 OptiScaler 描述为与 `Dx11FsrBridge.dll` 并存、且要求"Bridge 先于 OptiScaler"的注入对象，而 Bridge 自己是 `Dx11FsrBridge.dll` 独立命名，不占用标准代理名。
来源：<https://github.com/AizawaHikaru233/genshin_fsr_brigde>

### 2.2 已有另一个 mod 占用同名 DLL 时怎么办（官方规则）

**(A) 一手（wiki, Compatibility with other mods）**
> ## Renaming Optiscaler to other supported filenames
> * If another mod is already using `dxgi.dll` for example, you can try renaming Optiscaler to another name, like `winmm.dll`
> * Depending on the game and mods in question, not all names will work

> ## General "plugins" folder method
> If there is another mod (e.g. **Reshade** etc.) that uses the same filename (e.g. `dxgi.dll`), you can create a new folder called `plugins` where you can put other mod files. OptiScaler will check this folder and if it finds the same dll file (for example `dxgi.dll`), it will load that file instead of the original library.
> * Above `plugins` folder method also works for loading **ASI plugins**
> * Requires setting `LoadAsiPlugins=true` in **Optiscaler.ini**

来源：<https://github.com/optiscaler/OptiScaler/wiki/Compatibility-with-other-mods-%28Reshade%2c-SpecialK%29>

**(A) 一手（源码，`dllmain.cpp:686–730`）** 证实"plugins 优先"逻辑：当 OptiScaler 以 `dxgi.dll` 运行时，会先找 `pluginPath / L"dxgi.dll"`，找到就 `LOG_INFO("OptiScaler working as dxgi.dll, original dll loaded from plugin folder")`，否则回退系统 DLL。`version.dll`/`winmm.dll`/`wininet.dll`/`dbghelp.dll`/`winhttp.dll`/`d3d12.dll` 分支同构。

### 2.3 ReShade 的具体串联方式

**(A) 一手（wiki）**
> ## Reshade
> **1.** Rename **Reshade dll** (usually `dxgi.dll`) to `ReShade64.dll` and put it next to Optiscaler
> **2.** Set `LoadReshade=true` in **OptiScaler.ini**

**(A) 一手（源码，本机 fork `OptiScaler\hooks\Dxgi_Hooks.cpp:37–86`）** —— 这是最硬的一手证据：

```cpp
static void CheckLumaAndReShade(IDXGIFactory* factory)
{
    ...
    auto rsFile = Util::ExePath().parent_path() / L"ReShade64.dll";
    ...
    // Loading Reshade after Luma's D3D12 device creation to prevent conflicts
    if (reshadeModule == nullptr && Config::Instance()->LoadReShade.value_or_default())
    {
        SetEnvironmentVariableW(L"RESHADE_DISABLE_LOADING_CHECK", L"1");

        if (skModule != nullptr)
            SetEnvironmentVariableW(L"RESHADE_DISABLE_GRAPHICS_HOOK", L"1");

        State::EnableServeOriginal(201);
        reshadeModule = NtdllProxy::LoadLibraryExW_Ldr(rsFile.c_str(), NULL, 0);
        State::DisableServeOriginal(201);

        LOG_INFO("Loading ReShade64.dll, result: {0:X}", (size_t) reshadeModule);
    }
}
```

**可读出的机制（(A) 源码事实 + (C) 解读）**：
- **(A)** OptiScaler 通过设置环境变量 **`RESHADE_DISABLE_LOADING_CHECK=1`** 后再 `LoadLibrary` ReShade，来绕过 ReShade 的加载检查。
- **(A)** 若已检测到 SpecialK（`skModule != nullptr`），它会额外设置 **`RESHADE_DISABLE_GRAPHICS_HOOK=1`** —— 即**要求 ReShade 不安装自己的图形 Hook**。
- **(A)** 源码注释明写"Loading Reshade after Luma's D3D12 device creation to prevent conflicts"，确认存在"先建 D3D12 设备、再加载 ReShade"的顺序要求。
- **(C) 推断**：`RESHADE_DISABLE_GRAPHICS_HOOK` 是把 ReShade 降级为"被托管的 runtime"的关键开关，这与 Q4 中 GIMI 整合方案"A 方案：关闭 ReShade 图形 Hook + 由宿主托管 runtime"的做法同源。**我没有从 ReShade 上游文档直接验证这两个环境变量的语义**，故仅列为推断。

### 2.4 "谁赢"与注入顺序——官方明确写了什么

**(A) 一手（wiki, SpecialK 段）：OptiFG 与 SpecialK 互斥，官方直接写死。**
> ### **Option 3** - using LoadSpecialK option in **Optiscaler.ini**
> > [!NOTE]
> > _**This method will not work with OptiFG!**_

**(A) 一手（源码强制生效，本机 fork `dllmain.cpp:1662–1667`）**

```cpp
if (Config::Instance()->LoadSpecialK.value_or_default() && State::Instance().activeFgInput != FGInput::NoFG && ...
{
    Config::Instance()->LoadSpecialK.set_volatile_value(false);
    State::Instance().detectedQuirks.push_back("FG Inputs are enabled, LoadSpecialK disabled");
    LOG_INFO("FG Inputs are enabled, LoadSpecialK disabled");
}
```

**这是"OptiScaler 在启用 FG 时会主动压制其他 hooker"的一手源码证据。**

**(A) 一手（wiki）：OptiFG 会主动关闭覆盖层。**
> * Due to compatibility issues, Optiscaler automatically disables overlays when OptiFG is enabled (Steam, RTSS, Ubisoft, EA App, Overwolf).

来源：<https://github.com/optiscaler/OptiScaler/wiki/OptiFG>

**(B) 社区：OptiScaler 官方 wiki 没有给出类似 3DMigoto 的"注入顺序"总则。** 3DMigoto 侧则有上游自己承认的顺序敏感性（见 Q1.4 引用的 `HookedDXGI.cpp:801–807`）。**(C) 推断**：所谓"injection order matters"在 3DMigoto 有源码级依据，在 OptiScaler 官方文档里主要是"谁能加载谁"（plugins 目录 / LoadReshade / LoadSpecialK）而不是"先注入谁"。

---

## Q3. ReShade：如何与其他代理串联；第二个 D3D12 runtime；`AddonPath`；D3D12 swapchain 如何被注入

### 3.1 本机实测：ReShade 是注入的，并且同时 hook D3D11 与 D3D12

**(A) 一手（`D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.log`）**

```
INFO | Initializing crosire's ReShade version '6.8.0.2155' (64-bit) loaded from
     'D:\APPS\HoYoShadeHub\HoYoShade\ReShade64.dll' into
     'D:\APPS\miHoYo Launcher\games\Genshin Impact Game\YuanShen.exe' (0x3A2C606C) ...
INFO | Registering hooks for 'C:\Windows\system32\d3d11.dll' ...
INFO | Registering hooks for 'C:\Windows\system32\d3d12.dll' ...
INFO | Initialized.
```

- ReShade 由 HoYoShade 的 `ReShade64.dll` **注入**进 `YuanShen.exe`，而**不是**以 `dxgi.dll` 代理存在（游戏目录确无 `dxgi.dll`，见 Q1.1）。
- ReShade 6.8.0 **同时注册 d3d11 和 d3d12 的 hook** —— 这是它能感知 OptiScaler 自建 D3D12 世界的前提。

### 3.2 "第二个 ReShade runtime"的真实机制（本机一手日志，决定性证据）

**(A) 一手（同一份 `ReShade.log`，时间序）**

```
INFO | Redirecting RegisterClassExW(lpWndClassEx = ... { "OptiDx11SwapchainHost", style = 0 }) ...
INFO | Redirecting D3D12CreateDevice(pAdapter = ..., MinimumFeatureLevel = b000, riid = {...}, ppDevice = ...) ...
INFO | Redirecting ID3D12Device::CreateCommandQueue(...) ...
INFO | Redirecting IDXGIFactory2::CreateSwapChainForHwnd(...) ...
INFO | [FSR Bridge Depth Provider] FsrBridgeDepthAddon: D3D11 effect runtime initialized
INFO | Recreated runtime environment on runtime 00000285BB4FE3B0 ('...\Genshin Impact Game\ReShade.ini').
...
INFO | [FSR Bridge Depth Provider] FsrBridgeDepthAddon: final D3D12 runtime initialized (bridge CPU snapshots; no cross-queue waits)
INFO | Recreated runtime environment on runtime 00000285CAB011A0 ('...\Genshin Impact Game\ReShade2.ini').
...
INFO | [DLSS 5 Neural Rendering] DLSS5 Generic: 2 ReShade effect runtimes are live
       (a second window or swap chain); the status HUD, the overlay hotkeys and the window dock keep their
       current state until only one is left
```

**由此可确立（(A) 事实）：**
1. OptiScaler 的 DX11→DX12 路径创建了名为 **`OptiDx11SwapchainHost`** 的窗口类，并调用 `D3D12CreateDevice` —— 也就是**它自建了一个 D3D12 设备和一个新的 swapchain**。
2. ReShade **自动**为该新 swapchain 建立了**第二个 effect runtime**，并写入独立的配置文件 **`ReShade2.ini`**。
3. 不需要把第二个 ReShade DLL 改名成 `d3d12.dll`。**(A)** ReShade 自己就支持多 runtime，并按 "<主 ini 名>2.ini" 的规则命名第二个。
4. ReShade add-on 是**进程级注册一次**的（API 语义），因此单个 add-on 能同时看到两个 runtime 的 device/swapchain 事件。**(A)** 参见 `ReShade2.ini` 的 `[ADDON] AddonPath=` **为空**（见下），而 add-on 依然在两个 runtime 中生效。

> ⚠️ 上句第 4 点我需要标明边界：**(A)** 能直接证实的是"`ReShade2.ini` 里 `AddonPath` 为空、但 FSR Bridge Depth Provider 与 DLSS5 NR 两个 add-on 在两个 runtime 上都打了日志"。**(C) 推断**：这说明 add-on 的注册与"从哪个目录扫描 add-on"是两件事 —— 扫描只在主 runtime 做一次，之后 add-on 对全部 runtime 生效。这个推断与 ReShade 的 add-on 全局注册模型一致，但我没有逐行核对 ReShade 上游源码。

### 3.3 `ReShade.ini` vs `ReShade2.ini` 的实际差异（本机一手）

| 项 | `ReShade.ini` | `ReShade2.ini` |
|---|---|---|
| `[ADDON] AddonPath` | `D:\APPS\HoYoShadeHub\cache\games\hk4e_cn\Addons` | **(空)** |
| `[ADDON] DisabledAddons` | `RenoDX DLSS@renodx-dlss.addon64` | `RenoDX DLSS@renodx-dlss.addon64` |
| `[GENERAL] PresetPath` | `...\HoYoShade DX11 Disabled.ini` | `D:\APPS\HoYoShadeHub\HoYoShade\Presets\Mod OFF.ini` |
| `[INPUT] InputProcessing` | `0` | `2` |
| `[INPUT] KeyOverlay` | `0,0,0,0`（禁用） | `36,0,0,0`（Home） |
| `NoReloadOnInit` | `1` | `0` |

**(A) 结论**：两个 runtime 有**各自独立的 ini 与各自独立的输入/预设/覆盖层状态**。这正是"双 ReShade runtime"为什么必须被当作**设计的一部分**而不是 bug。

**(A) 一手（本项目自己的工程记录，`D:\CODE\HoyoDLSS5\PROGRESS.md:252–256`）**
> ### 双 ReShade runtime（by design，勿当 bug 修）
> - DX11 游戏 = runtime 1（游戏目录 `ReShade.ini`）；OptiScaler FG 桥的 D3D12 世界 = runtime 2
>   （`ReShade2.ini`）。1.4.0.0 起每次启动镜像同步 ReShade2.ini，1.3.9.9 不会（永远 stale）。
> - 排查「插件没生效」先看游戏日志 ReShade.log 的 `Loading add-on from` 列表。

**(A) 一手（本项目发布说明，`D:\CODE\HoyoDLSS5\release-notes\1.4.0.0.md:29`）**
> D3D12 + NR 私有交换链场景预生成第二个 runtime 的 `ReShade2.ini`，不再二次弹欢迎窗口；教程标记一并完成。

### 3.4 "把第二个 ReShade 命名成 `d3d12.dll`" 这种做法的证据

**(A) 一手（`D:\CODE\HoyoDLSS5\_research\dlss5-bridge\dlss5-bridge-main\README.md:169`）**
> | `unwrap` | 1 | Hand NGX the D3D12 device underneath ReShade's proxy. `0` keeps the proxy. **A neural add-on build measured to need the proxy, and ReShade loaded as `d3d12.dll`, both override `1` automatically.** `2` forces unwrapping despite those checks; use only for a targeted diagnostic. |

**(A)** 这段确认"ReShade loaded as `d3d12.dll`"是该项目**已知并显式处理**的一种真实配置（它会让工具自动不做 device unwrap）。**(C) 推断**：把 ReShade 装成 `d3d12.dll` 的意义在于"只进入 D3D12 世界、不去代理 DX11"，从而避免与 DX11 侧已有 hooker 抢 `dxgi.dll`/`d3d11.dll` 名位；但这是推断，我没有找到官方文档说明这一命名法的用途。

### 3.5 社区方案里"第二 ReShade runtime"引发的真实冲突

**(B) 社区（CXP-2024/dlss5_for_genshinimpact，v1.1 工作原理段）**
> 旧桥接插件还处理了 **ReShade 二次包装自建 D3D12 设备造成的启动冲突**：创建内部设备时临时绕过 ReShade 的设备 hook、还原原生 adapter，随后恢复 hook；不修改系统 DLL。

来源：<https://github.com/CXP-2024/dlss5_for_genshinimpact>

**(B) 社区（同仓库 v1.3 段）**
> 创建私有 DLSS-on-DX12 设备时**解包 ReShade adapter**，并**只在创建调用期间临时绕过 `D3D12CreateDevice` detour**，完成后立即恢复；不修改系统 DLL。

**(B) 社区（`D:\CODE\HoyoDLSS5\_research\dlss5-feeder\DLSS5-Feeder-main\README.md:225–226`）**
> proxy swapchain (`InvisibleWindowClassNvPresent`), and ReShade creates an effect runtime on **each** — `ReShade.ini` for one, `ReShade2.ini` for the other.

**(B) 社区（`...\DLSS5-Feeder-main\docs\PLAN-ISSUES-2026-09-02.md:188`）** —— 这段直接解释了双 runtime 的危害形态：
> Both ReShade logs show what Smooth Motion does on D3D11: NvPresent64 creates a **second D3D11 device and an invisible proxy swapchain** (`RegisterClassExA "InvisibleWindowClassNvPresent"`, then `CreateSwapChain` on a different `pDevice`), and ReShade creates an effect runtime on each — `ReShade.ini` for one, `ReShade2.ini` for the other. The feeder had one global "current runtime" and bound to whichever initialised **last**; under Smooth Motion that was routinely the runtime *without* your preset, so it resolved `DLSS5_Feed.fx technique MISSING` there and then ignored every render of the technique on the other runtime.

**(C) 推断（重要的架构结论）**：多 runtime 环境里，add-on 若持有**单一全局 "current runtime" 指针**，就会绑到"最后初始化的那个 runtime"，从而在错误的 runtime 上做完所有事情 —— 表现为"日志正常但画面无效果"。这是叠加层设计中最容易踩的坑，与是否有 3DMigoto 无关。

---

## Q4. 社区是否已实现 GIMI + ReShade + OptiScaler 组合；坏在哪里；顺序是什么

### 4.1 是否存在可用的整合方案 —— 存在

**(B) 社区（`CXP-2024/GIMI_reshade_dlss_integration`）**：一个专门做"GIMI + ReShade + DLSS + DLSS5 双模式神经渲染"的整合仓库。
来源：<https://github.com/CXP-2024/GIMI_reshade_dlss_integration>

**(B)** 其验证环境："Windows 11、RTX 5080、NVIDIA 616.56 驱动、3840×2160 输出和 0.8 渲染比例：Feature 18 持续执行 `3072×1728 -> 3072×1728`，随后原 Feature 1 执行 `3072×1728 -> 3840×2160`。**GIMI Mod、最终 ReShade、HDR 与截图功能同时正常。**"

**(B) 其声明的前置条件**：
> - 已有 GIMI / 3DMigoto 目录，其中应包含 `3DMigoto Loader.exe`、`d3dx.ini` 与用户自己的 `Mods`。
> - 启动前退出原神、旧的 `unlockfps_nc.exe` 和单独运行的 `3DMigoto Loader.exe`。

**(B) 其启动器行为**："启动器会同步更新 GIMI `[Loader] target`" —— 即它会代管 `d3dx.ini` 的 `target` 键（与 Q1.2 的语义一致）。

### 4.2 最重要的那条警告：不要叠加同名代理

**(B) 社区（同仓库 README，原始中文）**
> **不要同时把另一个 ReShade `dxgi.dll`/`d3d11.dll` 放入游戏目录，也不要额外启动 GIMI Loader；这些做法会重新引入 SwapChain 和 Hook 所有权冲突。**

来源：<https://github.com/CXP-2024/GIMI_reshade_dlss_integration>

这是**直接针对你问题的答案**：GIMI + ReShade 的组合之所以能成立，前提是**不**再往游戏目录里放第二个 DX11 代理，也**不**另外再起一个 GIMI Loader。

**(B) 社区（`CXP-2024/dlss5_for_genshinimpact`）** 同向警告：
> 普通版与 GIMI 版请分别解压到独立文件夹，并严格按照各自 README 配置；**不要把两个版本的启动器、DLL、`payload`、Profile 或 Add-ons 相互覆盖、混装。**

**(B) 社区（`AizawaHikaru233/genshin_fsr_brigde`，NVIDIA 表格）**

| Component | AMD | NVIDIA | Intel |
|---|---|---|---|
| **OptiScaler + ReShade enabled together** | ✅ | ⚠️ **May be unstable** | ✅ |

> ### ⚠️ On NVIDIA, enabling OptiScaler and ReShade together may be unstable
> **Using OptiScaler alone, or ReShade alone, is fine; enabling both at the same time may misbehave or be unstable on NVIDIA.** That depends on how the two interact with NVIDIA drivers and **on their load order**, not on this project's code — this project only loads them in the default order.

来源：<https://github.com/AizawaHikaru233/genshin_fsr_brigde>

**(B)** 同一 README 给出**明确的注入顺序规则**：
> **Injection order**: **Bridge must load before OptiScaler**. Other plugins have no strict requirement.

**(B)** 同一 README 还报告了 3DMigoto 兼容组件的 NVIDIA 问题（这对你要把 GIMI 放进同一条链很关键）：
> **TextureLoader is not recommended on NVIDIA** (texture Mods may fail to load) … **On NVIDIA cards there is a very high chance that replaced texture mods fail to load — it is not completely unusable, but it is not recommended**; sometimes it works fine, and no specific condition has been identified.

⚠️ **注意区分**：`TextureLoader` 是**该 Bridge 项目自己实现的、3DMigoto 兼容的纯贴图 Mod 加载器**，**不是 3DMigoto/GIMI 本体**。它的 NVIDIA 问题**不能直接推广**成"GIMI 在 NVIDIA 上有问题"。**(C) 推断**：这条只说明"用另一个独立实现去复刻 3DMigoto 的贴图替换路径，在 NVIDIA 上不可靠"，与"运行真正的 GIMI"是两件事。

### 4.3 已知会坏的地方（本机一手工程记录）

**(A) 一手（`D:\CODE\HoyoDLSS5\PROGRESS.md:262–266`）**
> ### OptiScaler fork 已知坑
> - launcher profile 会**全量覆盖** `OptiScaler.ini`；`FGOutput=auto→NoFG` 是 FG 不跑的根因；
>   DX11 需 Upscaler+DLSSG 且 `AdaMfgUnlock=false`；注意回写再感染。
> - 原神 DX11+DLSSG：**Streamline FG swapchain ResizeBuffers E_INVALIDARG → device removed**，
>   Upscaler 侧不可修，备选 FSRFG/XeFG。

**(A) 一手（`D:\CODE\HoyoDLSS5\HANDOFF-NR-GUIDE-20261003.md:153`）**
> 04:08:12：`Dx11wDx12SC ResizeBuffers: real OK but FG failed (80070057)`。04:08:13 RenoDX 报 private D3D12 device removed，reason=0x887a002b。Windows SDK winerror.h 确认为 `DXGI_ERROR_ACCESS_DENIED`。随后 FG 的 motion/depth shared handle open 持续失败，proxyBuffer=NULL。

**(A) 一手（同文件:186–189，实际链路确认）**
> - ReShade 日志 121 行：RenoDX 在 native D3D11 会话中创建自己的 private D3D12 bridge，`NRHookPoint=2`，在 Present backbuffer 上运行 NR。
> - Bridge 本次日志确认 `fsr2_get_proc_address_shim_ready`、`fsr2_shim_result DISPATCH_OK hook=1`。真实路线是游戏输入 → Bridge `Fsr2TranslationLayer.cpp` → FSR2 detour → OptiScaler native DX11 DLSS → RenoDX native DX11 guide capture → RenoDX 私有 DX12 NR Present。

**(A) 一手（`D:\CODE\HoyoDLSS5\HANDOFF-NR-GUIDE-20261003.md:86`）**
> 此次首先出现 ReShade DeviceRemoved，OptiScaler 实际 GetDeviceRemovedReason 为 `DXGI_ERROR_DEVICE_HUNG`（0x887a0006）；NR private bridge 也 lost 0x887a0006。

**(B) 社区（`clshortfuse/renodx` issue #299）**：标题即 "RenoXD + Optiscaler + Reshade64.dll (renamed reshade) - crash in The Outer Worlds Spacer's Choice"。
来源：<https://github.com/clshortfuse/renodx/issues/299>
> ⚠️ **我未能取回该 issue 的正文**（网络抓取失败），仅能确认其**标题**同时包含 RenoXD + OptiScaler + 改名后的 ReShade 并报告 crash。请按"存在这样一份社区报告"来采信，**不要**引用我未能读到的细节。

### 4.4 顺序：GIMI 整合方案给出的答案

**(B) 社区（`GIMI_reshade_dlss_integration` README「实际渲染链」）**
```text
原神 DX11 低分辨率 FSR2 carrier
  -> GIMI：唯一 DX11 包装器和最终 Present 所有者
  -> Dx11FsrBridge：提取颜色、深度、运动向量与抖动合同
  -> OptiScaler DLSS-on-DX12：通过 GIMI 原生 Device/Context 通道建立 NGX
  -> R8/R10/R11 packed/typeless 资源按需使用 FP16 输入/输出载体
  -> Mode 2：渲染分辨率 Feature 18 NR -> 原始 Feature 1 DLSS SR（默认）
     或 Mode 1：原始 Feature 1 DLSS SR -> 输出分辨率 Feature 18 NR
  -> GIMI Mod 与最终 Present
  -> GIMI 托管 ReShade 后处理、UI 与 HDR 截图
```

**注意这里的顺序与你在 Q2 看到的 OptiScaler 默认规则不同**：在这个整合方案里，**GIMI 在最外层**（DX11 包装 + 最终 Present），Bridge 在其内，OptiScaler 被"降格"为只借用 GIMI 的 native Device/Context 通道做 NGX。

---

## Q5. 帧生成的具体机制：OptiScaler 在 DX11 游戏里自建了什么？对链上其他层的 device/swapchain 有何约束？

### 5.1 官方立场：OptiFG 只支持 DX12

**(A) 一手（OptiScaler wiki, OptiFG）**
> * **OptiFG** was added with **v0.7**
> * **Only supported in DX12**
> * **Experimental way of adding FG to games without native Frame Generation**

来源：<https://github.com/optiscaler/OptiScaler/wiki/OptiFG>

**(A) 一手（OptiScaler wiki, Known-Issues → "DirectX 11 with DirectX 12 Upscalers"）**
> These implementations use a **background DirectX 12 device (D3D11on12)** to be able to use DirectX 12-only upscalers. There is an **up-to 10-15% performance penalty** for this method, but allows many more upscaler options.

来源：<https://github.com/optiscaler/OptiScaler/wiki/Known-Issues>

**(A) 一手（本机 fork `docs\DLSS-FRAME-GENERATION.md`）**
> Start with a supported **Windows DX12** game and an enabled temporal upscaler. … **D3D11/Vulkan bridges and individual games need separate validation.** … **Do not enable two FG implementations at once.**

**(A) 一手（wiki, Frame-Generation-Options）**
> * **DLSSG via Streamline inputs** — * Only supports **DX12** and **Streamline 2+ games**

### 5.2 本机 fork 源码：DX11 游戏的 FG 实际自建了什么

**(A) 一手（`OptiScaler-MFG-Ada\OptiScaler\with_dx12\dx11_with_dx12_sc.h`）** —— 这是**最直接的答案**：

```cpp
class DECLSPEC_UUID("23b064bb-482d-416c-93b1-829acedfb3d0") Dx11wDx12SC final : public IDXGISwapChain4
{
  public:
    Dx11wDx12SC(IDXGISwapChain* real, IDXGISwapChain4* fgSC, ID3D11Device* pDevice, HWND hWnd, UINT flags);
    ...
    HRESULT STDMETHODCALLTYPE Present(UINT SyncInterval, UINT Flags) override;
    HRESULT STDMETHODCALLTYPE GetBuffer(UINT Buffer, REFIID riid, void** ppSurface) override;
    HRESULT STDMETHODCALLTYPE ResizeBuffers(...) override;
    HRESULT STDMETHODCALLTYPE Present1(...) override;
    HRESULT STDMETHODCALLTYPE ResizeBuffers1(...) override;
    ...
  private:
    IDXGISwapChain* _real = nullptr;
    // ReShade's native object; used only for an armed pre-flip capture, never to bypass ReShade.
    IDXGISwapChain* _captureNative = nullptr;
    IDXGISwapChain4* _fgSwapChain = nullptr;          // <-- 第二条（FG）交换链
    IFGFeature_Dx12* _fg = nullptr;

    ID3D11Device* _dx11Device = nullptr;
    ID3D11DeviceContext* _dx11Context = nullptr;

    ID3D12Device* _dx12Device = nullptr;               // <-- 自建 D3D12 设备
    ID3D12CommandQueue* _dx12CommandQueue = nullptr;
    ID3D12CommandQueue* _copyQueue = nullptr;          // 独立 COMPUTE 队列做 DX11->DX12 拷贝
    std::vector<ID3D12CommandAllocator*> _copyAllocators;
    std::vector<ID3D12GraphicsCommandList*> _copyCommandLists;

    ID3D12Fence* _copyFence = nullptr;
    ID3D11Fence* _dx11Fence = nullptr;                 // <-- DX11 侧围栏
    ID3D12Fence* _dx12SharedFence = nullptr;           // <-- 共享围栏
    HANDLE _sharedFenceHandle = nullptr;

    std::vector<ID3D11Texture2D*> _sharedDx11BackBufferCopies;   // <-- 共享后缓冲
    std::vector<ID3D12Resource*> _openedDx11BackBuffers;         // <-- 把 DX11 后缓冲当 D3D12 资源打开
    std::vector<HANDLE> _sharedBackBufferHandles;
    ...
};
```

**清单化（(A) 源码事实）**：OptiScaler 在 DX11 游戏里做 FG/超分桥时，会创建：

1. 一个 **`IDXGISwapChain4` 包装对象**，拦截 `Present` / `Present1` / `GetBuffer` / `ResizeBuffers` / `ResizeBuffers1`；
2. **第二条交换链** `_fgSwapChain`（FG 自己的 swapchain）；
3. **自建 `ID3D12Device`** + 至少一条 DIRECT 队列 + 一条独立 **COMPUTE 拷贝队列**；
4. **DX11↔DX12 共享围栏**（`ID3D11Fence` + `ID3D12Fence` + 共享 handle）做跨 API 同步；
5. **共享后缓冲**：把 DX11 后缓冲拷贝到共享纹理，并在 D3D12 侧以资源形式打开（`_openedDx11BackBuffers`）。

**(A) 一手（源码注释，同一文件 :112–118）** —— 解释了为什么要独立队列：
> Dedicated COMPUTE queue for the DX11->DX12 interop copy. The copy previously ran on the DIRECT queue alongside the game's rendering and DLSS-G's dispatch, so every backbuffer copy serialized against the render workload and amplified camera-cut/ultimate stalls into multi-hundred-ms hitches.

### 5.3 对链上其他层的约束 —— 有硬证据

**(A) 一手（`ReShadePresentCapture.h`，本机 fork）**：OptiScaler 为了拿到"ReShade 处理完之后、flip 之前"的画面，**在 ReShade 的代理之下再挂一层 native Present hook**：

```cpp
// ReShade 6.8.0 source/com_utils.hpp and dxgi/dxgi_swapchain.cpp:
// on_present (including addon processing) precedes _orig->Present.
constexpr GUID unwrappedObject = {0x7f2c9a11, ...};
IDXGISwapChain* native = nullptr;
if (proxy == nullptr || FAILED(proxy->QueryInterface(unwrappedObject, (void**)&native)) || native == nullptr)
    return nullptr;
if (native == proxy) { native->Release(); return nullptr; }

const auto address = (*reinterpret_cast<void***>(native))[8];
...
if (capturePresentAddress == address)
    return native;
LOG_WARN("FG companion capture: different native Present implementation; retaining legacy copy");
```

**可读出的约束（(A) 源码事实）：**
- OptiScaler 需要**通过一个私有的 `QueryInterface` GUID 把 swapchain"解包"**（unwrap）到 ReShade 代理之下的 native 对象，才能挂到正确的 Present 上。
- 它**按 vtable 槽位 `[8]` 取 Present 地址**，并缓存一个全局的 `capturePresentAddress`。
- **如果 native Present 实现与已缓存的不一致，它会放弃并回退**（`LOG_WARN("...different native Present implementation; retaining legacy copy")`）。

**(C) 推断（明确标记为推断）**：当 3DMigoto 也在链上时，`native` 很可能指向的是 **3DMigoto 的 `HackerSwapChain`/`HackerUpscalingSwapChain`**（Q1.4 已证实 3DMigoto 会包装成一个 `IDXGISwapChain1` 派生对象，且 `HackerUpscalingSwapChain` 还会返回**伪造后缓冲** `mFakeBackBuffer`）。此时：
- OptiScaler 的 `[8]` 槽位取到的**不是游戏的 Present，而是 3DMigoto 的 Present**；
- 若 3DMigoto 提供的是 `HackerUpscalingSwapChain`，OptiScaler 拿到的"backbuffer"可能是**伪造的、尺寸被改过的纹理**（`mFakeBackBuffer`，见 `HackerDXGI.h:197–198`），这会破坏 FG 对"真实后缓冲尺寸/格式"的假设。
- 多个工具都会**缓存一份 Present 原始地址**（OptiScaler 用 `capturePresentAddress`，3DMigoto 用 `fnOrigCreateSwapChain` 等），抢先安装的一方会让后装方"看不到"真正的原始调用 —— 这正是 3DMigoto 源码 `HookedDXGI.cpp:801–807` 自述的失败模式。

**我没有实测验证上述三条推断**，请当作待验证假设，而不是结论。

### 5.4 本项目的独立佐证：第二条交换链在同一 HWND 上会被 DXGI 拒绝

**(A) 一手（`D:\CODE\HoyoDLSS5\DLSS5-FG\README.md`）**
> 原探针只验证了：**不能在已有 flip-model 交换链的同一 HWND 上，直接再加第二条 flip-model 交换链。** 这不等于"addon 配合修改宿主也无法实现 FG"。

**(A) 一手（`D:\CODE\HoyoDLSS5\_research\PRESENT_PATH_ANALYSIS_20261002.md:161`）**
> addon 可以调用第二次 Present，但原神的**单缓冲 DISCARD 链没有提供第二个独立的可见输出槽**；因此必须把 Present ownership 上移到 HoYoShade 所携带的 ReShade DXGI host，建立独立 proxy compositor。

**(A) 一手（`D:\CODE\HoyoDLSS5\DLSS5-Reshade-AIO\README.md`，版本 1.7.10）** —— 这条直接描述了**其他注入器与 Present 的竞争**：
> Version 1.7.10 serializes native proxy initialization. **Some injectors can re-enter or concurrently invoke Present while `CreateSwapChainForHwnd` is still running**; previous builds could respond by creating multiple proxy threads and topmost windows before either swapchain became ready. Nested Presents now defer until the single in-progress proxy initialization completes.

**(A) 一手（同 README，第 19 行）**
> In games without a secondary ReShade runtime on the native proxy, opening ReShade temporarily shows the game's lower-resolution presentation.

---

## Q6. "3DMigoto + DLSS/FSR 帧生成"冲突的文档化证据

### 6.1 有具体机制、被修复过的冲突（最强证据）

**(B) 社区（`GIMI_reshade_dlss_integration` 的 `docs/ARCHITECTURE.md`，"Required compatibility fixes" 第 1 条）**
> ### 1. Cross-API Present guard
> The **DX11 wrapped-swapchain Present path previously passed an `ID3D11DeviceContext` to a D3D12 feature's timing method. The vtable slot was misread as `PSSetConstantBuffers`, producing a misleading GIMI/D3D11 crash.** Timing and interop calls are now made only when the feature API matches DX11.

来源：<https://github.com/CXP-2024/GIMI_reshade_dlss_integration/blob/main/docs/ARCHITECTURE.md>

**(C) 解读**：这是"3DMigoto 的 DX11 swapchain 包装"与"DLSS 帧生成需要的跨 API 调用"之间冲突的**具体、有代码位置的记录**。冲突本质是：FG 组件在 DX11 包装路径上想调用 D3D12 的接口，而 3DMigoto 的包装层把 vtable 解释成了 DX11 的语义。

### 6.2 GIMI 与 OptiScaler 的 device/context 冲突（同源文档）

**(B) 社区（同文件，第 2 条）**
> ### 2. GIMI native-device and native-context trampolines
> **Querying a newer D3D11 context interface does not bypass GIMI's process-wide vtable detours.** GIMI publishes its existing original-context trampoline through **private GUID `91ACFD68-5A6F-45EA-B8D0-71ACC32151B7`**; the DLSS-on-DX12 bridge uses that context for NGX Create/Evaluate only.
> … A second private GUID, **`DB17DC9A-5A5A-4AC7-A4CE-EF41F7C51D5C`**, exposes GIMI's original D3D11 Device.

**(C) 解读**：这句话点明了核心约束 —— **3DMigoto 的 vtable detour 是进程级的，绕不过去**。想让 DLSS 的 NGX 拿到"干净的" device/context，**必须由 3DMigoto 主动把原始 trampoline 通过私有 GUID 暴露出来**。这是"必须改 3DMigoto"而不是"配置一下就行"的根本原因。

**(B) 社区（同文件，"Ownership rules"）** —— 这套规则值得逐条记住：
> 1. **GIMI is the only D3D11 device/context wrapper and the only owner of the real final Present.**
> 2. **ReShade graphics hooks are disabled.** GIMI creates the visible ReShade effect runtime through ReShade's public C Runtime API at final Present.
> 3. The pre-NR add-on is loaded by that hosted ReShade runtime for its Add-on API, but observes the private D3D12 NGX calls generated inside OptiScaler.
> 4. **OptiScaler's D3D11 device vtable hooks remain disabled.** Only the NGX work uses GIMI's private pass-through context; normal game rendering and Mods retain the wrapped GIMI context.
> 5. OptiScaler's built-in DLSSNR path is disabled. The external add-on owns the only Feature 18 pass and selects Mode 1 or Mode 2; both cannot run at once.

**(B) 社区（同文件，第 6 条）** —— 关闭 ReShade 图形 Hook 的**代价**：
> ### 6. Hosted ReShade HDR color-space handoff
> **Disabling ReShade's own graphics hooks also disables its normal interception of `IDXGISwapChain3::SetColorSpace1`.** Without an explicit handoff, the hosted runtime treated GIMI's `R10G10B10A2_UNORM` HDR10/PQ back buffer as sRGB and saved washed-out, untagged 8-bit screenshots.

**(C) 解读**：这是"把 ReShade 改成被托管模式"必然的副作用 —— 你关掉了它的 hook，就必须**手工补上它原本通过 hook 拿到的所有信息**（这里是 HDR 色彩空间）。

**(B) 社区（同文件，第 5 条）** —— 一个纯命名冲突，但很典型：
> ### 5. Private bridge name collision
> **OptiScaler's generic NGX DLL check intentionally uses suffix matching. `nrchain_nvngx.dll` therefore matched `_nvngx.dll`** and was replaced with the OptiScaler module handle; all four `NRChain_*` lookups then failed.

**(C) 解读**：多层 hooker 叠加时，**"代理/转发识别规则"本身**就是冲突面 —— 一个通用后缀匹配会把另一个组件的私有 DLL 误认成自己要代理的目标。

### 6.3 本机一手记录中的同类冲突

**(A) 一手（`HANDOFF-NR-GUIDE-20261003.md:119`）** —— OptiScaler 自己的 DX12 FG 与游戏 swapchain 参数不一致：
> 额外发现 creation / resize 不一致：DX12 FG 创建时将 sRGB 映射为 UNORM、至少 2 buffers，但旧 resize 直接转发游戏的单 buffer/sRGB/flags。新增 `OptiScaler/with_dx12/Dx11FgResize.h`，根据 native 实际 `GetDesc` 解析尺寸，`BufferCount=0` 保留 FG ring，Flags 取 FG 自身，sRGB 映射 UNORM。

**(A) 一手（`PROGRESS.md:265`）**
> 原神 DX11+DLSSG：Streamline FG swapchain ResizeBuffers E_INVALIDARG → device removed，Upscaler 侧不可修，备选 FSRFG/XeFG。

**(A) 一手（`HANDOFF-NR-GUIDE-20261003.md:212`）** —— 本项目自己在 DX11→DX12 交接处做屏幕空间输入转换：
> 已在 DX11→DX12 的 DLSS evaluate 交接处实现屏幕空间输入转换，源码位于 `OptiScaler-MFG-Ada/OptiScaler/upscalers/Dx11ScreenSpaceGuides.h`、`Dx11ScreenSpaceGuideParams.h`、`IFeature_Dx11wDx12.cpp`。

**(A) 一手（`DLSS5-FG\AI_ANALYSIS_RESPONSE_20261001.md:93`）** —— 说明为什么"再调一次 Present"不安全：
> 要保留手动 Present：在"不建第二交换链、不 CPU 回读、单一最终呈现"约束下，双倍输出帧率只能在每个游戏帧内多出一次真实 Present，没有其他调用点。**ReShade addon API 没有"绕过 hook 的原始 Present trampoline"可拿（`get_native()` 拿到的就是被 hook 的 vtable）。**

**(C) 解读**：这句是本报告里最有价值的一条架构性结论 —— **在多层 hook 的进程里，"拿到底层原始调用"这件事本身是不可靠的**。OptiScaler 需要靠私有 `QueryInterface` GUID 解包（Q5.3），ReShade add-on API 干脆不提供绕过机制，3DMigoto 则靠自己的 `fnOrig*` 缓存。**每一层都只能看到"上一层愿意给它看的那个 Present"。**

### 6.4 汇总：冲突面的分类

| 冲突类型 | 具体表现 | 证据 |
|---|---|---|
| swapchain / Present 所有权 | 谁是真 Present、谁拿到"原始"调用；抢先 hook 者让后装者看不到原始调用 | 3DMigoto `HookedDXGI.cpp:801–807` (A)；GIMI `ARCHITECTURE.md` rule 1 (B) |
| device / context vtable | 进程级 detour 绕不过；新版接口 QueryInterface 不解决问题 | GIMI `ARCHITECTURE.md` 第 2 条 (B) |
| 跨 API 语义错配 | DX11 vtable 槽位被 D3D12 调用误读为 `PSSetConstantBuffers` | GIMI `ARCHITECTURE.md` 第 1 条 (B) |
| swapchain 参数不一致 | FG 创建 2 buffers/sRGB→UNORM，游戏 resize 传单 buffer/flags → `ResizeBuffers` 失败 → device removed | `HANDOFF-NR-GUIDE-20261003.md:119`、`PROGRESS.md:265` (A) |
| 模块名/代理识别 | 通用后缀匹配把他人私有 DLL 误认成代理目标 | GIMI `ARCHITECTURE.md` 第 5 条 (B) |
| 多 runtime 绑定 | add-on 持有单一 "current runtime"，绑到最后初始化的那个，导致"日志正常但无效" | `DLSS5-Feeder/docs/PLAN-ISSUES-2026-09-02.md:188` (B) |
| Present 重入 | 其他注入器在 `CreateSwapChainForHwnd` 未完成时并发/重入调用 Present | `DLSS5-Reshade-AIO/README.md` v1.7.10 (A) |
| 关闭 hook 的信息缺失 | 关掉 ReShade 图形 hook 后丢失 `SetColorSpace1` 拦截 → HDR 截图发灰 | GIMI `ARCHITECTURE.md` 第 6 条 (B) |

---

## 7. 对"同一个原神 DX11 进程里跑齐四层"的可行性判断

### (A) 已被一手/准一手证据支持的事实
- XXMI 以**注入**方式加载 GIMI，不需要在游戏目录放 `d3d11.dll`。[XXMI Launcher Log]
- OptiScaler **官方不支持** `d3d11.dll` 作为自身代理名。[OptiScaler wiki]
- ReShade 能对 OptiScaler 自建的 D3D12 swapchain **自动**建立第二 runtime（`ReShade2.ini`）。[本机 `ReShade.log`]
- DX11 游戏里做 D3D12 超分/FG 需要自建 D3D12 设备 + 共享纹理/围栏，并拦截 `Present`/`ResizeBuffers`。[本机 fork `dx11_with_dx12_sc.h`]
- OptiFG 官方只支持 DX12；OptiScaler 在 FG 输入启用时会**强制关闭 SpecialK**。[OptiScaler wiki + 源码 `dllmain.cpp:1662`]

### (B) 社区报告（未获上游确认）
- 存在可工作的 "GIMI + ReShade + DLSS5" 整合，验证于 RTX 5080；**前提是所有组件都被改过**，且**不叠加第二个 DX11 代理**。[`CXP-2024/GIMI_reshade_dlss_integration`]
- NVIDIA 上 OptiScaler + ReShade 同时启用"**may be unstable**"，且依赖**加载顺序**。[`genshin_fsr_brigde`]
- 该组合存在 DX11↔DX12 跨 API 崩溃、`ResizeBuffers` 失败导致 device removed 等实际故障。[`ARCHITECTURE.md`、本机 `PROGRESS.md`]

### (C) 我的推断（**未经验证，请勿当结论**）
1. **"XVMI 的 `d3d11.dll` 与 OptiScaler 同名冲突"这个问题在当前官方形态下基本不成立**，因为 OptiScaler 不接受 `d3d11.dll`。真正的选择是：OptiScaler 用 `dxgi.dll`（与 ReShade 争位，需 `plugins` 目录或 `LoadReShade`），或用 `d3d12.dll`/`winmm.dll` 等避让名。
2. **四层叠加的主要障碍不是"名字"，而是三条所有权**：DX11 device/context 归谁、Present 归谁、D3D12 世界归谁。任何两层都想当 Present 所有者就会出问题。
3. **最可能稳定工作的拓扑**（与社区整合方案一致）：**GIMI 当唯一的 DX11 包装者与 Present 所有者 → ReShade 降级为被托管 runtime → OptiScaler 放弃自己的 D3D11 hook、只借 GIMI 暴露的 native device/context 做 NGX → FG/超分作为最内层的 D3D12 工作**。这需要改 GIMI 与 OptiScaler 的源码，不是配置能达成的。
4. **不要在游戏目录里同时放 `dxgi.dll` 和 `d3d11.dll` 两个代理**，也不要同时用 XXMI Launcher 注入 GIMI 再手动跑一次 `3DMigoto Loader.exe`。这两件事都会直接制造双 swapchain 所有者。

---

## 8. 待验证项 / 本次未能解决的问题

| # | 问题 | 状态 |
|---|---|---|
| 1 | `exclude_d3d11` 是否存在于某个 3DMigoto 分支？ | **未找到**。GIMI 的 d3dx.ini 中不存在该键（Q1.2）。 |
| 2 | 3DMigoto 能否以 `dxgi.dll` 命名运行 | **未找到官方依据**。一手文件只提 `d3d11.dll` / `3dmigoto.dll`。 |
| 3 | `RESHADE_DISABLE_GRAPHICS_HOOK` / `RESHADE_DISABLE_LOADING_CHECK` 的上游官方语义 | **未从 ReShade 上游文档验证**；仅在 OptiScaler 源码中看到其被设置。 |
| 4 | ReShade 多 runtime 的 ini 命名规则（`ReShade2.ini`）是否是文档化行为 | **仅由本机日志证实**（`Recreated runtime environment on runtime ... 'ReShade2.ini'`）；未核对上游源码实现。 |
| 5 | `clshortfuse/renodx` issue #299 的技术细节 | **仅确认标题**（RenoXD + OptiScaler + renamed ReShade → crash）。正文抓取失败，未采信其内容。 |
| 6 | 3DMigoto 在场时 OptiScaler 的 native Present 解包实际指向哪个对象 | **推断未验证**（Q5.3）。建议用调试器在 `ReShadePresentCapture::Install` 打断点，打印 `native` 的 vtable 与 `HackerSwapChain` 的身份。 |
| 7 | OptiScaler-MFG-Ada fork 与上游 OptiScaler 在加载顺序规则上的差异 | **未逐一比对**。本报告引用的是本机 fork 与上游 wiki 的并集。 |

---

## 附：引用源清单

**一手（上游官方文档 / 上游源码）**
- OptiScaler wiki — Automated Installation（支持的文件名列表）：<https://github.com/optiscaler/OptiScaler/wiki/Automated-Installation>
- OptiScaler wiki — Compatibility with other mods (Reshade, SpecialK)：<https://github.com/optiscaler/OptiScaler/wiki/Compatibility-with-other-mods-%28Reshade%2c-SpecialK%29>
- OptiScaler wiki — OptiFG：<https://github.com/optiscaler/OptiScaler/wiki/OptiFG>
- OptiScaler wiki — Frame Generation Options：<https://github.com/optiscaler/OptiScaler/wiki/Frame-Generation-Options>
- OptiScaler wiki — Known Issues：<https://github.com/optiscaler/OptiScaler/wiki/Known-Issues>
- OptiScaler wiki — Home：<https://github.com/optiscaler/OptiScaler/wiki/Home>
- 3DMigoto 源码（本机快照 `D:\CODE\DX12Mod\reference\3Dmigoto\DirectX11\`）：`DLLMainHook.cpp`、`D3D11Wrapper.cpp`、`HookedDXGI.cpp`、`HackerDXGI.h`、`HackerDXGI.cpp`
- 3DMigoto 上游：<https://github.com/bo3b/3Dmigoto>
- XXMI Launcher：<https://github.com/SpectrumQT/XXMI-Launcher>
- XXMI Launcher 运行日志（本机）：`D:\APPS\XXMI\XXMI Launcher Log.txt`
- GIMI 发行版 `d3dx.ini`（本机）：`D:\APPS\XXMI\GIMI\d3dx.ini`
- ReShade 运行日志（本机）：`D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.log`
- `ReShade.ini` / `ReShade2.ini`（本机，同上目录）
- 本机 fork 源码：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\OptiScaler\{dllmain.cpp, hooks\Dxgi_Hooks.cpp, with_dx12\dx11_with_dx12_sc.h, with_dx12\ReShadePresentCapture.h, Config.h}`
- 本机 fork 文档：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\docs\DLSS-FRAME-GENERATION.md`
- 本项目工程记录：`D:\CODE\HoyoDLSS5\PROGRESS.md`、`HANDOFF-NR-GUIDE-20261003.md`、`release-notes\1.4.0.0.md`、`DLSS5-FG\*.md`、`_research\PRESENT_PATH_ANALYSIS_20261002.md`

**社区**
- CXP-2024 / GIMI_reshade_dlss_integration：<https://github.com/CXP-2024/GIMI_reshade_dlss_integration> ；架构文档：<https://github.com/CXP-2024/GIMI_reshade_dlss_integration/blob/main/docs/ARCHITECTURE.md>
- CXP-2024 / dlss5_for_genshinimpact（原神 DLSS5 一键包）：<https://github.com/CXP-2024/dlss5_for_genshinimpact>
- AizawaHikaru233 / genshin_fsr_brigde（Dx11FsrBridge）：<https://github.com/AizawaHikaru233/genshin_fsr_brigde>
- clshortfuse / renoDX issue #299（仅标题）：<https://github.com/clshortfuse/renodx/issues/299>
- DLSS5-Feeder（本机快照 `D:\CODE\HoyoDLSS5\_research\dlss5-feeder\`）；上游 <https://github.com/jlrouzies-fr/DLSS5-Feeder>
- DLSS5-Reshade-AIO（本机 `D:\APPS\XXMI\DLSS5-Reshade-AIO\README.md`）；上游 <https://github.com/kibblerz/DLSS5-Reshade-AIO>
- 3DM 论坛帖（未能取回正文）：<https://bbs.3dmgame.com/forum.php?mod=viewthread&tid=6666626>
