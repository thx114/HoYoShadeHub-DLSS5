# GIMI（模型替换）与 DLSS5 / 帧生成共存 —— 交接文档

> 后续研究更正（2026-10-08）：`real 0, fg 0` 是两个 HRESULT=S_OK，不代表交换链为空。新日志同时证明 Streamline 创建过 FG 交换链，优先调查 `QueryInterface(IDXGISwapChain4)` 及 GIMI 的交换链包装，而非直接还原 DXGI 工厂导出钩子。详见 `RESEARCH-GIMI-OPTISCALER-DX12-COEXIST-20261008.md`。原文保留为历史推断记录。

> 日期：2026-10-08（晚）
> 交接人：上一轮 AI 会话（本机 DSH）
> 承接对象：更强的模型 / 后续开发者
> 目标读者假设：**不了解本项目历史，但从本文件 + 第 8 节列出的既有文档就能接手**。
> 全文按"实测 / 推断"严格标注，凡推断一律写明，请勿把推断当结论继续叠加。

---

## 0. 一句话结论

**GIMI（3DMigoto 的 `d3d11.dll` 代理）先加载后，会把 DXGI 工厂包装掉；OptiScaler 随后加载，从"当前工厂"的虚表槽 15 取到的"原始 `CreateSwapChainForHwnd`"实际上是 GIMI 的包装函数**，导致 OptiScaler 那条隐藏的 DX11→DX12 帧生成交换链创建失败
（日志：`Dx11wDx12 HWND swapchain creation failed: real 0, fg 0`），从而：

- 帧生成（`FGOutput=DLSSG`，MFG 插值 5）**不生效**；
- ReShade **不会**建立第二个 runtime（`ReShade2.ini`，即 Home 键打开的 DX12 界面）—— 因为第二个 runtime 是 ReShade 在 OptiScaler 自建的 D3D12 交换链出现时**自动**创建的；
- 于是 DX11 侧的 `dlss5-fg-dx11.addon64` 界面只能提示"请在 DX12 界面打开"。

**这三件事是同一个根因**，不是三个 bug。

代码锚点（`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\OptiScaler\hooks\DxgiFactory_Hooks.cpp`）：

```cpp
L289:  if (o_CreateSwapChainForHwnd == nullptr)
L291:      o_CreateSwapChainForHwnd = (PFN_CreateSwapChainForHwnd) pFactoryVTable[15];   // ← 从"当前工厂实例"的虚表取原始函数
L294:      DetourAttach(&(PVOID&) o_CreateSwapChainForHwnd, DxgiFactoryHooks::CreateSwapChainForHwnd);
...
L1003: realScResult = o_CreateSwapChainForHwnd(realFactory, pDevice, hiddenHwnd, &realDesc, …);  // ← 隐藏交换链：本局失败
L1075: LOG_WARN("Dx11wDx12 HWND swapchain creation failed: real {:X}, fg {:X}", …);              // ← 用户日志里那一行
```

`o_CreateSwapChainForHwnd` 只在**第一个被钩到的工厂**上赋值一次，而第一个工厂就是 GIMI 包装后交给游戏的那个 ⇒ 该指针 = GIMI 的包装函数。

---

## 1. 最终目标与当前架构

### 1.1 最终目标（用户原话）

> 最终目标要在原神内完成 opt + fsr桥 + dlss5

展开即：同一局游戏内同时具备

| 层 | 组件 | 作用 |
|---|---|---|
| 模型替换 | GIMI（XXMI 的 3DMigoto 实例） | 角色/皮肤 mod |
| 上采样/画质 | OptiScaler MFG-Ada 0.1.9（本项目 fork） | DLSS/FSR 上采样、NR |
| 帧生成 | 同上（`FGOutput=DLSSG`，MFG 解锁插值 5） | 多帧生成 |
| 画面/深度 | ReShade ×2（DX11 + DX12 两个 runtime） | 效果、NR、深度供给 |
| 深度桥 | Dx11FsrBridge（本项目自研） | 进程内定序注入 + 深度供给 + DLSSG 共存 workaround |

### 1.2 注入链架构（为什么不能靠外部注入）

- 原神有 `mhyprot`/`HoYoKProtect`，**CreateProcess 之后约 1 秒**反作弊生效，之后外部 `CreateRemoteThread` 注不进去。
- 因此唯一在窗口内注入的是**桥**（`Dx11FsrBridge.dll`，由启动器在 CreateProcess 时注入）；桥在 `DllMain` 返回、loader lock 释放后，按**链清单**在游戏进程内逐个 `LoadLibraryW`。
- 链清单 = 与桥同目录的 `Dx11FsrBridge.chain.txt`，由启动器每局覆写。**行序 = 加载顺序，这是整套方案的全部意义。**
- 动词（桥侧解析，`Dx11FsrBridge.cpp` L14832 起有完整中文注释）：
  - `wait <模块名>`：等模块进模块表（上限 15 秒，超时继续）
  - `wait swapchain`：**本会话新增**，等桥自己看到真正的交换链建出来（上限 45 秒，超时继续）。**注意：此动词已证明不能让 OptiScaler 晚挂，见 §2.3。**
  - `migoto <DLL路径>`：先建 `Local\3DMigotoLoader` 互斥体，再 `LoadLibraryW`（3DMigoto 需要）
  - `load <DLL路径>`：普通 `LoadLibraryW`
  - `#` / `;` 开头为注释；无动词的裸路径按 `load` 处理
- 启动器（`HoYoShadeHub`）有两条路径：
  - **链式**（XXMI 启用=开 + 手动启动）：写链清单，并把 XXMI 注入模式设成 `SKIP`（避免两份 `d3d11.dll` 抢顺序）；支持数据目录根部的 `bridge-chain.override.txt` **整体替换**内置链（排查/实验用）。
  - **非链式**（关闭 XXMI / 官方模式等）：撤走链清单，桥回落到 `Dx11FsrBridge.autoload.txt`（单行 = 只 load OptiScaler），OptiScaler/ReShade 由启动器自己外部注入。

### 1.3 关键组件路径表（本机实测）

| 组件 | 路径 |
|---|---|
| 桥 DLL（部署位） | `D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\genshin-fsr-bridge\v2.3.4-fg-20261006\Dx11FsrBridge.dll` |
| 链清单 / autoload / 覆写 | 同上目录 `Dx11FsrBridge.chain.txt`、`Dx11FsrBridge.autoload.txt`；覆写在 `D:\APPS\HoYoShadeHub\bridge-chain.override.txt` |
| 桥源码 | `D:\CODE\genshin_fsr_brigde\Dx11FsrBridge\Dx11FsrBridge.cpp` |
| 桥工程 | `D:\CODE\genshin_fsr_brigde\build-depth-yflip\Dx11FsrBridge.sln` |
| OptiScaler（部署） | `D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.9\OptiScaler.dll`（同目录 `OptiScaler\streamline\…`、`OptiScaler.ini`、`OptiScaler.log`） |
| OptiScaler（fork 源码） | `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\` |
| fork 打包脚本 | `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\package_release.ps1`（`-SkipBuild` 可用；自动找 MSBuild） |
| ReShade DLL | `D:\APPS\HoYoShadeHub\HoYoShade\ReShade64.dll` |
| ReShade addons | `D:\APPS\HoYoShadeHub\cache\games\hk4e_cn\Addons\`（含 `dlss5-fg-dx11.addon64`、`renodx-dlss5.addon64`、`FsrBridgeDepthAddon.addon64`） |
| 游戏目录 | `D:\APPS\miHoYo Launcher\games\Genshin Impact Game`（`YuanShen.exe`；ReShade 配置 `ReShade.ini` = DX11、`ReShade2.ini` = DX12） |
| GIMI 实例 | `D:\APPS\XXMI\GIMI\`（`d3d11.dll` = 3DMigoto 代理；`d3dx.ini`；`Mods\`） |
| 启动器 DLL | `D:\APPS\HoYoShadeHub\app-1.4.3.3\HoYoShadeHub.dll` |
| 每局日志归档 | `D:\APPS\HoYoShadeHub\log\sessions\<YYYYMMDD-HHMMSS>_YuanShen.exe_<pid>\`（含 `bridge-Dx11FsrBridge.log`、`opti-…-OptiScaler.log`、`shade-ReShade.log`、`cfg-ReShade2.ini`、`session.txt`） |

---

## 2. 本会话（2026-10-08 下午至晚）已完成的事

### 2.1 【已修，已验证】桥克隆 `ID3D11DeviceContext` 虚表少算 21 个槽

- 症状：链里带 `migoto` 步时，游戏起来后 `0xC0000005`，`error.log` 显示 `RIP: 0x00000000`、栈顶是 GIMI 的 `d3d11.dll+0xAC994`，字节 `48 8B 01 … FF 90 30 04 00 00`（= `call [rax+0x430]`）。
- 根因：桥 `clone_and_patch_vtable()` 按 `k_context_vtable_size = 128` 克隆，而真实对象是 `ID3D11DeviceContext4`（149 槽）；GIMI 作为第二层包装转发 Context4 方法时踩到克隆区之后的 0 ⇒ `RIP=0`。
- 修复：`context_vtable_size()` **一律返回 149**（`k_context4_vtable_size`），源码 `Dx11FsrBridge.cpp` 约 L5774，函数内有中文注释说明。
- 验证：用户 07:11 那局链走完后游戏继续在跑（`sys_line`：`coexist dlssg_dxgi_workaround auto_enabled` + 持续 `[depth_provider] publishing depth to ReShade`），退出码 0。
- 部署指纹：`Dx11FsrBridge.dll` 921088 B，SHA256 前 16 位 `DC491FF929C70C66`（该版本同时含 §2.3 的新动词）。

### 2.2 【已修】启动器内置链补回 `migoto` 步

- 背景：启动器内置链**曾经故意不含** GIMI，理由写在注释里："桥加载的 GIMI 会被 `[Loader] loader` 拒载，并留下 `CreateDXGIFactory1` 钩子把游戏打崩（`0xC0000005`、`at=<no-module>`）"。
- **该归因已被 §2.1 证明是错的**（真因是虚表槽数）。本会话把 4 处错误归因注释全部改正，并把 `migoto` 步加回内置链第 2 位。
- 改动点：
  - `src\HoYoShadeHub\Features\GameLauncher\GameLauncherPage.StartGame.cs`（链构建处，约 L2068–L2105）：第 1 步 `wait dxgi.dll`、第 2 步 `migoto`（路径来自 `XxmiInjector.FindLoader(CurrentGameId, …)`），随后 `load OptiScaler` → `wait OptiScaler.dll` → `load ReShade64.dll`。
  - `src\HoYoShadeHub.Extensions\OptiScaler\OptiScalerRuntime.cs`（`FsrBridgeChainStep.Migoto` 的 XML 文档）：改成"进内置链、排第 2 位"。
  - `src\HoYoShadeHub\Features\GameLauncher\GenshinEarlyLaunch.cs`、`src\HoYoShadeHub.Extensions\Games\GenshinLaunchRouting.cs`：注释同步改正。
- 顺序硬约束（用户实测 + 仓库测试断言）：GIMI **必须**在 OptiScaler/ReShade 之前（反过来 3DMigoto 装载返回 600）；ReShade 排最后。测试参照 `src\HoYoShadeHub.Extensions.Tests\Program.cs` L1942–L1968。

### 2.3 【已加，但**证明不可用于 OptiScaler**】`wait swapchain` 动词

- 动机：想让 OptiScaler/ReShade 等真正的交换链建好后再挂，从而落在 Present 链最外层，解决"帧生成帧簿记错位"。
- 实现：桥侧新增（`Dx11FsrBridge.cpp`：全局 `g_chain_swapchain_seen`（`g_swapchain_present_mutex` 附近）、`CreateSwapChainForHwnd` 钩子成功分支里置位、`wait` 分支里 `_wcsicmp(step.text, L"swapchain")` 特判、上限 45 秒）。
- 实测结果：动词**工作正常**（`chain_step index=2 wait_swapchain ok waited_ms=3235`），但**OptiScaler 在交换链已存在时才挂会空指针崩溃**：
  ```
  OptiScaler.dll caused an Access Violation (0xc0000005) in module OptiScaler.dll at 0033:25b9bc72
  Error occurred at 2026-10-08_072539   （Opti 挂上后约 12 秒）
  Read from location 00000000 caused an access violation
  Bytes at CS:EIP: 49 8b 06 …    (mov rax,[r14]，而 R14 = 0x00000000)
  ```
  转储目录：`%TEMP%\mihoyocrash_5e39082c7644b243ae56c80f79cdb6ca\`（`error.log` / `crash.dmp`）。
- **结论：不要在生产链里使用 `wait swapchain`**（动词本身保留，供将来复用）。**结论边界**：只证明"这个 OptiScaler 构建在交换链已存在时挂会崩"，不代表原理上不可行。

### 2.4 被证伪的旧结论（避免重走）

| 旧结论 | 现状 |
|---|---|
| "桥加载的 GIMI 被 `[Loader] loader` 拒载并留下钩子把游戏打崩" | **错**。真因是 §2.1 的虚表槽数。 |
| "崩溃是反作弊杀的" | **错**。是游戏自己的异常过滤器接住的可捕获 AV。 |
| "桥的 GetProcAddress 自递归" | **错**。跳过列表 + 原始函数指针缓存已排除。 |
| "没有 `create_device_enter` 说明桥钩子没跑" | **错**。桥的日志是异步队列，崩溃前最后几行会丢；要信 `Dx11FsrBridge.crash-probe.txt`（`sync_line` 同步写盘）。 |
| 链里每步之间需要 `wait` 来"稳定" | 不需要；`wait <模块名>` 是按模块名等，不是延时。 |
| "帧生成与 GIMI 冲突是因为 GIMI 挡住了 Opti 的工厂钩子（A 假设）" | **A 已排除**：带 GIMI 时 `DxgiFactoryHooks::CreateSwapChainForHwnd` 仍然出现 2 次。真正失败在**包装交换链的创建**（B，见 §3）。 |

---

## 3. 当前唯一阻塞问题：GIMI 在场 ⇒ 隐藏交换链创建失败

### 3.1 现象（用户视角）

- XXMI 开（GIMI 加载）：帧生成**不生效**（帧数不涨、Opti 菜单显示未激活）；ReShade 只有 DX11 那份（F10），**Home（DX12 那份）打不开**；`dlss5-fg-dx11.addon64` 界面提示"请在 DX12 界面打开"。
- XXMI 关（无 GIMI）：一切正常 —— 帧生成、NR/DLSS5、ReShade 双 runtime 全在。

### 3.2 日志级证据（两局对照，均为本机实测）

| 观测项 | 无 GIMI（会话 07:26:34） | 带 GIMI（会话 07:30:36） |
|---|---|---|
| 桥日志 `chain_parsed` | `source=Dx11FsrBridge.autoload.txt steps=1 [load:OptiScaler.dll]` | `source=Dx11FsrBridge.chain.txt steps=5 [wait:dxgi.dll] [migoto:GIMI\d3d11.dll] [load:OptiScaler] [wait:OptiScaler.dll] [load:ReShade64.dll]` |
| `DxgiFactoryHooks::CreateSwapChainForHwnd` | 2 次 | 2 次（A 假设排除） |
| `Failed to get ID3D12CommandQueue` | 1 次 | 1 次（两边一致） |
| `DLSSG_Dx12::CreateSwapchain1 Max supported interpolations` | 1 次（值 5） | 1 次（值 5） |
| `FGHooks::HookFGSwapchain` | 1 次 | 1 次 |
| `ReShadePresentCapture::Install FG companion capture` | **有** | **无** |
| `Dx11wDx12SC::Dx11wDx12SC … created` | **1 次** | **0 次** |
| `Present FG companion capture` | 有（`attempted=true copied=true`） | **0 次** |
| 桥 `hooked CreateSwapChainForHwnd` | **2 次**（游戏 + Opti 的包装链） | **1 次** |
| ReShade 第二 runtime（会话归档 `shade-ReShade.log` 中 `Recreated runtime environment` / `ReShade2.ini`） | 2 / 1 | 1 / 0 |

带 GIMI 那一局的决定性行（原样）：

```
[W] DxgiFactoryHooks::CreateSwapChainForHwnd Dx11wDx12 HWND swapchain creation failed: real 0, fg 0
```

`real 0` = 连"真交换链"这一步都返回空；且桥只看到 1 次 `CreateSwapChainForHwnd` ⇒ Opti 内部那次调用**根本没走到真实工厂**。

### 3.3 代码级根因（实测锚点 + 推断边界）

- 实测：GIMI 加载后，把 `dxgi!CreateDXGIFactory` / `CreateDXGIFactory1` / `CreateDXGIFactory2` **导出**都换成了 `jmp rel32` 跳板（桥的 `crash_probe` chase 在 `after_migoto` 一步抓到跳转目标 = GIMI 模块内地址）；OptiScaler 加载后又把同一批导出改成自己的跳板（`after_load` 抓到目标变为 OptiScaler 模块内地址）。
- 实测（代码）：`DxgiFactory_Hooks.cpp` L289–L295 用 `pFactoryVTable[15]` 作为 `CreateSwapChainForHwnd` 的"原始函数"，且只在第一个工厂上取一次。
- **推断（尚未逐行验证，请优先验证这一条）**：OptiScaler 的导出 detour 在 GIMI 之后加载 ⇒ 位于**外层** ⇒ 它拿到的工厂是 **GIMI 包装后的工厂** ⇒ 其虚表槽 15 是 **GIMI 的包装函数**，而非 DXGI 的真实函数 ⇒ L1003 那次"建真交换链"落回 GIMI 的包装里 ⇒ 返回空（`real 0`）⇒ FG 桥整体失败。
- 反证路径（如果上述推断不成立）：`realFactory` 的来源、`DxgiFactory_WrappedCalls.cpp` 的 `WrappedIDXGIFactory7`（L276 起）以及 `_real2` 的取得方式；以及 GIMI 是否对**第二次** `CreateSwapChainForHwnd`（非游戏主窗口）直接返回失败。

### 3.4 为什么"ReShade 的第二 runtime 也没了"

见 `docs/HOOK-STACKING-RESEARCH.md` §3.2（本机一手日志）：ReShade 的第二个 effect runtime 是 **ReShade 自己的既有能力** —— OptiScaler 的 DX11→DX12 桥建出 `OptiDx11SwapchainHost` 窗口与 D3D12 设备后，ReShade **自动**为其建立第二个 runtime，并自动命名为 `ReShade2.ini`。add-on 是**进程级注册一次**的，因此单个 add-on 同时看到两个 runtime（§3.3 记录了 `ReShade2.ini` 的 `AddonPath` 为空而 add-on 依然生效）。

⇒ **没有 D3D12 交换链 ⇒ 没有第二 runtime ⇒ DX12 界面不存在 ⇒ DX11 插件提示"去 DX12 开"。**

> 附带说明（回答用户当天的问题）：本会话**没有改动任何 ReShade 文件**。`ReShade.ini` / `ReShade2.ini` 的改动痕迹来自当天 04:31 / 05:39 的"共存"工作，均有 `.pre-*` 备份。现热键：`ReShade.ini`（DX11）`KeyOverlay=121`(=F10)、`KeyEffects=120`(=F9)，`ForceShortcutModifiers=1`（需按住 Ctrl）；`ReShade2.ini`（DX12）`KeyOverlay=36`(=Home)、`KeyEffects=189`。

---

## 4. 修复候选（按"成本 × 命中率"排序）

> 目标：让 GIMI 与 OptiScaler 的 DX11→DX12 帧生成桥在同一进程内共存。

### 候选 0：桥在加载 OptiScaler 之前**临时还原 GIMI 对 DXGI 工厂导出的补丁**（推荐先做，成本低、命中率高）

- 思路：GIMI 的核心职责是当 `d3d11.dll` 代理并包装**设备/上下文**；它对 `CreateDXGIFactory*` 的**导出**补丁（`jmp rel32`）并不是它能不能工作的前提。桥在链里执行到 `load OptiScaler` 之前：把 GIMI 改过的导出**临时或永久**还原（桥已经能把跳板目标解析出来 —— `hook_pointer_label()` / `crash_probe` chase 就是干这个的），让 OptiScaler 拿到**真实的** DXGI 工厂并缓存真实虚表槽；OptiScaler 加载完成后再决定是否把 GIMI 的补丁装回去（先试"不装回去"）。
- 收益：OptiScaler 的 `o_CreateSwapChainForHwnd` 变成真实函数 ⇒ 隐藏交换链可建 ⇒ FG + 第二 runtime 全回来；GIMI 的设备/上下文包装不受影响（mod 仍然生效）。
- 风险：GIMI 可能确实依赖工厂钩子来包装交换链（需实测）；需要在桥里做"导出补丁备份/还原/重放"，是本次唯一需要改桥的候选。
- 涉及：`Dx11FsrBridge.cpp`（新增一个链步骤或让 `migoto` 步自带该行为；备份/还原导出前 5–14 字节 + 记录 GIMI 跳板目标）。

### 候选 1：改 fork，让 `o_CreateSwapChainForHwnd` 不再信任"当前工厂"的虚表

- 位置：`OptiScaler-MFG-Ada\OptiScaler\hooks\DxgiFactory_Hooks.cpp` L289–L295（以及 L297 起的同族函数）。
- 可选做法：
  1. 从**真实 DXGI 工厂**取槽：例如由 Opti 自建的 D3D12 设备 → `IDXGIAdapter` → `GetParent(IID_IDXGIFactory*)`，或由真实 `IDXGIFactory1`（在 GIMI 补丁之外创建）取 `CreateSwapChainForHwnd`；
  2. 若检测到"当前工厂的虚表不在 `dxgi.dll` 模块范围内"（= 被别人包装过），主动降级到真实工厂；
  3. 在 L1003 建隐藏交换链时，改用"真实工厂 + 真实函数"，而不是 `realFactory + o_CreateSwapChainForHwnd` 的组合。
- 注意：**不要**指望"顺着 jmp 链追一层就能拿到真函数" —— GIMI 是**包装**（在它自己的模块里实现完整函数并内部调用真函数），不是简单 jmp 转发；追导出跳板只会追到 GIMI 自己。
- 风险：需要改 fork 并重新构建部署（见 §6.3）；要确保不影响非 GIMI 场景（回归：单 OptiScaler + ReShade 时 `Dx11wDx12SC … created` 仍为 1）。

### 候选 2：改顺序（OptiScaler 先于 GIMI）

- **已被否决**：用户实测"反过来 3DMigoto 装载返回 600"，且机制清楚 —— GIMI 必须抢在系统 `d3d11.dll` 之前占住这个名字，而 OptiScaler 一旦加载就会把系统 `d3d11.dll` 带进来。
- 除非另外解决"GIMI 不是游戏所用 `d3d11.dll`"的问题，否则此路不通。不要重复尝试。

### 候选 3：用 3DMigoto 的配置关掉相关包装

- `D:\APPS\XXMI\GIMI\d3dx.ini` L482–L488 有 `hook=`（可选 `deferred_contexts` / `immediate_context` / `device` / `all` / `recommended`），但**只作用于设备与上下文，不覆盖工厂/交换链包装** —— 所以它不是本问题的解，但值得作为< 5 分钟的低成本反证实验（改完若 `real 0, fg 0` 仍在，即可彻底排除）。
- 同一文件 L490–L503 的 `allow_create_device` / `allow_check_interface` 影响的是"额外设备创建请求"，与隐藏交换链可能相关，可作为次要实验。
- 该文件当前另有本会话留下的痕迹已还原：`calls = 0`、`unbuffered = 0`（诊断期曾设为 1）。

### 候选 4（不建议先做）：改 GIMI 侧或做 hook 引擎仲裁

- 让 GIMI 不包装工厂（未找到开关）、或引入第三方"钩子仲裁层"，成本与不确定性都远高于候选 0/1。

---

## 5. 复现与验证配方

### 5.1 启动方式（**必须用户手动启动游戏**，AI 不要代劳杀死或重启游戏进程）

- **带 GIMI**：启动器 → XXMI 启用 = 开 → 手动启动。链为 5 步（GIMI 第 2 步）。
- **不带 GIMI**：启动器 → XXMI 关闭 → 启动（非链式路径，桥只 autoload OptiScaler，ReShade 由启动器外部注入）。

### 5.2 日志读取位置（每局一份，互不覆盖）

```
D:\APPS\HoYoShadeHub\log\sessions\<时间戳>_YuanShen.exe_<pid>\
    bridge-Dx11FsrBridge.log        桥（异步队列，崩溃前最后几行可能丢）
    opti-mfg-ada-0.1.9-OptiScaler.log   OptiScaler（每局覆盖原文件，但归档里按局保留）
    shade-ReShade.log               ReShade（含第二 runtime 创建记录）
    cfg-ReShade.ini / cfg-ReShade2.ini
    session.txt                     退出码等收尾信息
%TEMP%\mihoyocrash_<hash>\error.log  游戏崩溃转储（UTF-16LE，含 RIP/访问地址/栈）
D:\APPS\HoYoShadeHub\Cache\modules\...\v2.3.4-fg-20261006\Dx11FsrBridge.crash-probe.txt
                                   桥的同步探针（**最可信**，崩溃前不会丢）
```

### 5.3 指纹行（成功 vs 失败）

成功（无 GIMI，07:26 局）：

```
DxgiFactoryHooks::CreateSwapChainForHwnd Failed to get ID3D12CommandQueue from pDevice, creating Dx11 swapchain!
DLSSG_Dx12::CreateSwapchain1 Max supported interpolations: 5 status 0
FGHooks::HookFGSwapchain Hooking FG SwapChain present
ReShadePresentCapture::Install FG companion capture: ReShade post-addon / pre-flip DX11 capture installed
Dx11wDx12SC::Dx11wDx12SC Dx11wDx12SC 1 created, real: … fg: … dx11: … dx12: … queue: …
DxgiFactoryHooks::CreateSwapChainForHwnd Created Dx11wDx12SC HWND: wrapper … real11 … fg12 …
Dx11wDx12SC::Present FG companion capture: attempted=true copied=true hiddenResult=0 dx11Index=0
```

失败（带 GIMI，07:30 局）：上面第 4–7 行**全部缺失**，取而代之：

```
[W] DxgiFactoryHooks::CreateSwapChainForHwnd Dx11wDx12 HWND swapchain creation failed: real 0, fg 0
```

ReShade 第二 runtime 是否建出来，看 `shade-ReShade.log` 里 `Recreated runtime environment` 的出现次数（成功=2，失败=1）与是否提及 `ReShade2.ini`。

### 5.4 一条命令式检查清单

```powershell
$s = Get-ChildItem 'D:\APPS\HoYoShadeHub\log\sessions' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$o = Join-Path $s.FullName 'opti-mfg-ada-0.1.9-OptiScaler.log'
foreach ($k in 'DxgiFactoryHooks::CreateSwapChainForHwnd','Dx11wDx12SC::Dx11wDx12SC','HookFGSwapchain',
               'Max supported interpolations','Present FG companion capture','swapchain creation failed') {
  '  {0,-46} {1}' -f $k, (Select-String -Path $o -Pattern ([regex]::Escape($k)) -Encoding UTF8 | Measure-Object).Count
}
Select-String -Path (Join-Path $s.FullName 'shade-ReShade.log') -Pattern 'Recreated runtime environment' -Encoding UTF8 | Measure-Object
```

---

## 6. 当前磁盘状态与回滚

### 6.1 已部署/已改动

| 对象 | 现状 | 备注 |
|---|---|---|
| 桥 DLL | 921088 B，SHA256 前 16 位 `DC491FF929C70C66` | 含 §2.1 修复 + §2.3 `wait swapchain` 动词 |
| `bridge-chain.override.txt` | **存在**，内容 = 5 步链（`wait dxgi` / `migoto GIMI` / `load OptiScaler` / `wait OptiScaler.dll` / `load ReShade64.dll`） | 用来挡住启动器内置链里的 `wait swapchain`（它会崩，§2.3） |
| 启动器 DLL | `app-1.4.3.3\HoYoShadeHub.dll` = 4133376 B（**会写 6 步链，含 `wait swapchain`**） | ⚠️ **下一个人请注意**：内置链目前仍含 `wait swapchain`；要么把 `GameLauncherPage.StartGame.cs` 里那一步去掉并重编，要么保留 override。 |
| `d3dx.ini` | `calls = 0`、`unbuffered = 0`（诊断已还原） | |
| 桥的崩溃探针 | **仍开启**（模块目录里 `Dx11FsrBridge.crash-probe.txt` 会持续写） | 收尾时移除：`probe_migoto` / `crash_probe::install()` 与各 `crash_probe::dump()` 调用；同时建议去掉链里 `device_call after_migoto` 那段 1 秒 sleep + 额外一次 `D3D11CreateDevice`（对反作弊可见面更干净） |

### 6.2 备份名（需要回退时用）

```
桥:      Dx11FsrBridge.dll.bak-20261008-waitswapchain        (919552 B, 虚表修复版)
         Dx11FsrBridge.dll.bak-20261008-probe5 … 等更多在模块目录
启动器:  app-1.4.3.3\HoYoShadeHub.dll.bak-20261008-pre-swapchain  (4132864 B, 未含 migoto 步的旧版)
         app-1.4.3.3\HoYoShadeHub.dll.bak-20261008-chainfix       (更早)
覆写:    bridge-chain.override.txt.bak-20261008-hubchain-v2   (6 步版)
         bridge-chain.override.txt.bak-20261008-hubchainfixed  (5 步版)
ReShade: ReShade.ini(.pre-coexist-20261008-053957|.pre-final-only-20261008-044403).bak
         ReShade2.ini(.pre-coexist-20261008-053957|.pre-cpu-depth-20261008-041559).bak
基线日志归档：D:\CODE\HoyoDLSS5\docs\fg-diag-20261008\
```

### 6.3 构建/部署

- 桥（原生 C++，MSVC）：
  ```
  D:\VS2022BuildTools\MSBuild\Current\Bin\MSBuild.exe \
    D:\CODE\genshin_fsr_brigde\build-depth-yflip\Dx11FsrBridge.sln \
    /p:Configuration=Release /p:Platform=x64 /m
  ```
  产物 `build-depth-yflip\Release\Dx11FsrBridge.dll`，复制到模块目录（源文件含中文注释，**必须带 `/utf-8`**，工程已配好）。
- 启动器（.NET 10 / WinUI3）：
  ```
  & 'D:\CODE\HoyoDLSS5\build-local.ps1'        # 本机无 VS，用这个脚本
  ```
  产物 `src\HoYoShadeHub\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\HoYoShadeHub.dll` → 复制到 `app-1.4.3.3\`（**启动器运行中该文件被锁**，需先关闭启动器）。
- OptiScaler fork：
  ```
  & 'D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\package_release.ps1' -Version <新版本号> [-IncludeDlssFrameGeneration] [-IncludeAmpereMfg]
  ```
  或直接用 MSBuild 构建 fork 的 sln；部署目标 = `D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.9\`（`OptiScaler.dll` + 同目录 `OptiScaler\streamline\…` 等资源必须一起更新）。构建前先看 `OptiScaler-MFG-Ada\docs\` 与 `package_release.ps1` 顶部注释里的许可/打包约束。

---

## 7. 环境与纪律约束（务必遵守）

1. **绝不往游戏目录安装/改写任何文件**（原神目录曾出现的 `d3d11.dll`/`dxgi.dll` 代理一律不用；本机所有组件都走"注入"）。
2. 反作弊窗口约 1 秒；**只有桥**能在 CreateProcess 时注入，其余层必须由桥在进程内加载。
3. **不要 kill 用户正在运行的游戏/启动器**；需要独占文件时先确认进程已退出（`Get-Process -Name YuanShen,HoYoShadeHub`），必要时把新文件放成 `.new-*` 待命。
4. 游戏启动必须由用户手动完成（GPU/反作弊相关实验同理）。
5. 崩溃取证优先看 `Dx11FsrBridge.crash-probe.txt`（同步写盘）与 `%TEMP%\mihoyocrash_*\error.log`；桥日志是异步队列，崩溃前最后几行可能丢。
6. 改动必须单变量、可回退，并写清"实测 / 推断"边界；文档与代码注释一律中文。
7. 不要动 Web GUI（http://127.0.0.1:19387）。

---

## 8. 相关文档索引（接手前建议按序读）

| 文档 | 为什么读 |
|---|---|
| `docs\XXMI-挂载顺序-调查-20261008.md` | 本项目注入链与 GIMI 的完整调查史；**§9 方案 A（"链上不做 GIMI"）**、§14 本会话的虚表修复与结论 |
| `docs\HOOK-STACKING-RESEARCH.md` | 钩子层叠研究；**§3.2/§3.3/§3.5** 解释 ReShade 第二 runtime 的真实机制与危害形态 |
| `OptiScaler-MFG-Ada\docs\DX11-BRIDGE-MFG-INVESTIGATION.md` | DX11→DX12 桥与 MFG 的既有调查（含"静默退化到 2x"等结论） |
| `OptiScaler-MFG-Ada\docs\FACE-NR-BLEND-PROBE-20261007.md` | `site=dlssDx11/dlssDx12/dx11wDx12`、`bridge=0/1` 探针语义，判断某次评估走的是哪条路径 |
| `HANDOFF-NR-GUIDE-20261003.md` | 更早一轮 NR/DLSS5 排障（含 `Dx11wDx12SC ResizeBuffers: real OK but FG failed (80070057)`、`DXGI_ERROR_ACCESS_DENIED`、device removed 的历史） |
| `DEPTH_FIX_SUMMARY.md` / `DEPTH_OUTPUT_SETUP.md` / `DEPTH_FIX_TEST_GUIDE.md` | 深度供给（桥 → ReShade）的设计与验证方式 |
| `_optiscaler_src\Config.md` | OptiScaler 上游配置说明，尤其 **Dx11wDx12 sync settings**（帧生成同步参数与 GPU/驱动强相关） |

---

## 附录 A：关键代码位置速查

| 位置 | 内容 |
|---|---|
| `Dx11FsrBridge.cpp` L57–65 | 各虚表槽数常量（`k_context_vtable_size=128`、`k_context4_vtable_size=149`、device/swapchain/factory 等） |
| `Dx11FsrBridge.cpp` ~L5774 | `context_vtable_size()` —— 已修为一律 149（本次崩溃修复点） |
| `Dx11FsrBridge.cpp` L5749 | `clone_and_patch_vtable()` |
| `Dx11FsrBridge.cpp` L14832–L14851 | 链清单格式的中文说明 + `ChainStep` 定义（`0=load 1=wait 2=migoto`） |
| `Dx11FsrBridge.cpp` ~L15000 | 链执行主循环（`kChainWaitMs=15000`；`wait swapchain` 分支在此） |
| `Dx11FsrBridge.cpp` ~L13776 | `CreateSwapChainForHwnd` 钩子成功分支（置 `g_chain_swapchain_seen`） |
| `GameLauncherPage.StartGame.cs` ~L2068 | 链构建（`wait dxgi` → `migoto` → `wait swapchain` → `load OptiScaler` → `wait OptiScaler.dll` → `load ReShade64.dll`） |
| `GameLauncherService.cs` L500 | `UsesGenshinFinalDx12()` —— 决定 `ReShade2.ini` 路线 |
| `GameIniBootstrap.cs` | `ReShade2.ini` 的预生成/同步逻辑 |
| `OptiScaler-MFG-Ada\OptiScaler\hooks\DxgiFactory_Hooks.cpp` L289–L295 | **本次问题的代码锚点**：`o_CreateSwapChainForHwnd = pFactoryVTable[15]` |
| 同上 L958 / L1003 / L1018 / L1030 / L1075 | FG 交换链创建分支与失败日志 |
| `OptiScaler-MFG-Ada\OptiScaler\with_dx12\dx11_with_dx12_sc.cpp` L91 | `Dx11wDx12SC` 构造函数（隐藏交换链的包装对象） |
| `OptiScaler-MFG-Ada\OptiScaler\wrapped\wrapped_factory.cpp` L276 | `WrappedIDXGIFactory7::CreateSwapChainForHwnd`（Opti 自己的工厂包装） |

## 附录 B：本会话时间线（关键节点）

| 时间 | 事件 |
|---|---|
| 07:11 | 链带 `migoto` 步成功进游戏 ⇒ §2.1 虚表修复被证实 |
| 07:19 | 5 步链（GIMI + Opti + ReShade）：游戏正常，但帧生成未生效、无第二 runtime |
| 07:25 | `wait swapchain`（6 步）实验：OptiScaler 空指针崩溃、退出码 `-1073741819` ⇒ §2.3 结论 |
| 07:26 | 关闭 XXMI 基线：一切正常（第二 runtime、FG、NR 全在）⇒ 拿到成功指纹并归档 |
| 07:30 | 用户再测带 GIMI：`real 0, fg 0` ⇒ 定位到 §3，并确认与 ReShade 第二 runtime 同一根因 |
