# GIMI 与 OptiScaler DX12 共存研究：交换链接口边界

日期：2026-10-08。本轮研究未启动游戏、未修改部署组件，已工作的深度插件保持原状。

## 先纠正交接文档的关键判断

`Dx11wDx12 HWND swapchain creation failed: real 0, fg 0` 中的两个数字是创建调用的 HRESULT，0 表示 S_OK，不能解释成“交换链为空”。先前文档开头把它归因为工厂创建失败，证据不足。

当前同一 YuanShen.exe 进程里，原神/GIMI 处理 DX11，OptiScaler 建立自己的 DX12 设备、队列和帧生成交换链；不是两个独立游戏进程。

深度插件已由用户确认正常，DX12 界面可以控制 DX11 ReShade。此轮研究不修改该插件或覆盖其最新成果。

## 最新会话的真实证据

会话：`D:\APPS\HoYoShadeHub\log\sessions\20261008-073036_YuanShen.exe_5312`。

| 时间 | OptiScaler 日志 | 含义 |
|---|---|---|
| 07:30:46.029608 | WrappedIDXGISwapChain4 ... created | 已创建一个 OptiScaler 交换链包装对象；不代表它支持所有接口 |
| 07:30:46.029706 | Streamline: Created swap-chain ... buffers 6 | Streamline 确实创建过帧生成交换链 |
| 07:30:46.051750 | Hooking FG SwapChain present | 已进入 FG Present 钩子安装 |
| 07:30:46.051943 | interop creation failed: real 0, fg 0 | 上层 interop 条件失败，但两次创建调用返回成功 |

ReShade 日志只有一次 runtime 创建，指向 ReShade.ini，没有 ReShade2.ini。桥及深度获取正常。由此可确认 OptiScaler 未完成 DX11→DX12 interop 包装；不能确认“两个底层交换链都没创建”。

## 失败条件精确位置

`OptiScaler/hooks/DxgiFactory_Hooks.cpp`：

1. realScResult = 隐藏 DX11 CreateSwapChainForHwnd 返回值。
2. fgScResult = Streamline/FG CreateSwapChainForHwnd 返回值。
3. 当 fgScResult == S_OK 且 fgSwapChain1 非空时，执行 QueryInterface(IDXGISwapChain4)，旧代码忽略该返回值。
4. 只有 realScResult == S_OK、realDx11SwapChain1 非空、fgSwapChain4 非空，才创建 Dx11wDx12SC。
5. 失败日志只打印前两项 HRESULT，没有打印两个指针或 QueryInterface 结果。

因此当前优先怀疑第三项接口查询失败。也可能创建调用成功但返回空对象，需要新诊断明确区分。

## 找到更直接的 GIMI 线索

本机 3DMigoto 参考源码：

- D:\CODE\DX12Mod\reference\3Dmigoto\DirectX11\HackerDXGI.cpp
- D:\CODE\DX12Mod\reference\3Dmigoto\DirectX11\HookedDXGI.cpp

上游 3DMigoto 的 HackerSwapChain::QueryInterface 明确拒绝 IDXGISwapChain2、3、4。HookedDXGI.cpp 还有 DX12 命令队列转换成 DX11On12 设备、只用于警告界面的分支；该分支可以把非游戏原生 DX11 的交换链也包装成 HackerSwapChain。

实际部署的 `D:\APPS\XXMI\GIMI\d3d11.dll` 二进制含有：

- `returns E_NOINTERFACE as error for IDXGISwapChain4`
- `Preparing to enable D3D11On12 compatibility mode for overlay`

这是实际版本存在相应实现的指纹，不能单凭字符串确认本次运行执行了它。

另外，上游 HackerDXGI.cpp 注释说明新方案直接钩住 factory 的创建方法，不再包装 factory 本身。因此“factory 对象必定是 GIMI wrapper”也需要复核，不能因导出跳转至 GIMI 就下结论。

官方源码参考：https://github.com/bo3b/3Dmigoto/blob/master/DirectX11/HackerDXGI.cpp 。此源码不是本机 GIMI 的精确构建版本，研究边界如上。

## 首要假设（尚待游戏日志验证）

GIMI 的交换链钩子拦截了 OptiScaler/Streamline 的 DX12 输出交换链，使用 DX11On12 警告分支返回只支持 IDXGISwapChain1 的 HackerSwapChain。OptiScaler WrappedIDXGISwapChain4 保存该对象时也无法获得内部 _real4，向外 QueryInterface(IDXGISwapChain4) 返回 E_NOINTERFACE，随后 interop 创建条件失败。最终 DX12 ReShade 没能正常接管完整输出链。

这比“DXGI 工厂创建失败”的假设更贴合 S_OK/S_OK 和 Streamline 已创建交换链的日志，但仍需下一局直接记录 qi4_hr 和对象所属模块来证实。

## 已准备的单变量诊断补丁

两个 OptiScaler HWND interop 分支（Hooks 与 WrappedCalls）已加入：

- real_hr 与 real11 指针；
- fg_hr 与 fg1 指针；
- qi4_hr 与 fg4 指针；
- factory、HWND；
- fg1 的 QueryInterface/Present 方法地址所属模块。

HookToFactory 额外记录首次缓存的 factory QI 与 HWND 方法所属模块，以及 Factory2 接口方法所属模块。

补丁只记录现有查询的返回值，不新增解包尝试，不改导出机器码，不改注入顺序，也不绕过 GIMI 或 ReShade。诊断前缀为 `GIMI interop probe` / `GIMI factory probe`。

改动文件：

- OptiScaler-MFG-Ada/OptiScaler/hooks/DxgiFactory_Hooks.cpp
- OptiScaler-MFG-Ada/OptiScaler/hooks/DxgiFactory_WrappedCalls.cpp

诊断构建日志：`.build-temp/gimi-coexist-diagnostics/build.log`。实际构建状态以日志和后续记录为准；未部署到用户运行路径。

## 后续修复方向

若 qi4_hr=80004002、fg1 非空、对象链落在 GIMI/只支持1的包装层：

1. 优先研究只让 OptiScaler 私有 DX12 输出链避开 GIMI 的 DX12 警告包装，保留游戏 DX11 设备/上下文/交换链的模型替换与帧动作。
2. 不可通过把 IDXGISwapChain1 强制转换为 IDXGISwapChain4 解决；虚表不兼容。
3. 不可直接换成裸底层交换链而丢掉 Streamline/ReShade 的 Present 包装，否则虽接口查询成功却失去 FG/最终效果。
4. 可研究 GIMI 的非 DX11 队列分支、特定调用方/队列的透明转发，或 OptiScaler 创建私有交换链时明确的互操作旁路。具体做法需以诊断日志及精确 GIMI 源码为依据。

若实际失败是 real11/fg1 为空，则回到 factory 创建路径调查。只有确证函数/对象混配后，才研究独立真实 factory 和成对方法调用。暂不改写 GIMI 的 DXGI 导出跳板。

## 用户验证方式

仍由用户手动启动/退出游戏。第一轮只测诊断 DLL，不同时改链顺序、GIMI 配置或深度插件。

下一会话检索 `GIMI interop probe`，看 qi4_hr 是否为 80004002，是否已有 real11、fg1。无 GIMI 基线应成功得到 fg4 和 Created Dx11wDx12SC；带 GIMI 结果用于精确定位包装层。

## 本轮构建结果

诊断补丁 Release/x64 编译链接通过，退出码 0。原项目的类型布局、运行库混合与字符编码警告仍存在；本轮不扩大修改范围处理它们。

诊断文件：`D:\CODE\HoyoDLSS5\.build-temp\gimi-coexist-diagnostics\OptiScaler.diagnostic.dll`。
SHA256：`A4E45E9B5A8884E98821462DE654A28EC802154336EDE37F8C14C8385F43582A`。

安装中的 OptiScaler.dll 未替换，时间仍为 2026-10-07 12:16:33。桥、GIMI、启动器和深度插件未改动；未启动游戏。诊断产物需要通过用户手动的带/不带 GIMI 对照获取 qi4_hr，才可把接口截断假设升级为游戏实测结论。

## 07:52 游戏诊断结论与 08:00 首轮修复

新增实际会话：20261008-075227_YuanShen.exe_136828。

```text
factory QI owner dxgi.dll, HWND owner dxgi.dll
real_hr 0, real11 1C2A97030B0
fg_hr 0, fg1 1C2D9A4E0E0
qi4_hr 80004002, fg4 0
fg1 QI owner sl.interposer.dll, Present owner sl.interposer.dll
```

实测确认：两条交换链创建成功且非空，失败发生在向 Streamline 外层对象查询 IDXGISwapChain4。首次缓存的 factory 属于系统 DXGI。故旧文档“缓存了 GIMI 工厂对象导致创建失败”的假设被该诊断反驳。

尚未直接观测的部分：Streamline 内部交换链是否具体为 GIMI HackerSwapChain。GIMI 源码和部署 DLL 指纹支持这条推断；下一修复日志能进一步验证其警告分支是否执行。

首轮修复利用 XXMI-Libs-Package 的现有容错路径：GIMI 遇到 DX12 队列时，调用系统 D3D11On12CreateDevice 创建仅用于“不支持 DX12”提示的包装设备。该请求失败时，GIMI 不会创建 HackerSwapChain，而继续返回已有的交换链。

OptiScaler 新增线程局部私有 DX12 创建范围；现有 On12 hook 仅当“该范围内、调用方为具有 CBTProc/D3D11CreateDevice 导出的非系统 d3d11.dll（GIMI）”时返回 E_NOINTERFACE，并清空可选输出指针。普通 DX11 游戏交换链不进入该范围；ReShade、RTSS 等其他调用者和其他线程保持原处理。On12 hook 解析到已加载的系统 d3d11.dll，以覆盖 GIMI 直接使用的系统函数。

不改写 GIMI DLL/配置，不改导出跳板，不解包或绕过 Streamline Present，不修改桥和正常深度插件。

上游源码：SpectrumQT/XXMI-Libs-Package/DirectX11/HookedDXGI.cpp 的 prepare_devices_for_dx12_warning、sort_out_swap_chain_device_mess、wrap_factory2_swap_chain。GIMI 可能输出一次不支持 API 的日志/提示音，需用户实际确认；模型替换功能的共存尚未游戏验证。

新文件：OptiScaler/hooks/GimiDx12OutputScope.h。
修改：D3D11_Hooks.cpp、DxgiFactory_Hooks.cpp、DxgiFactory_WrappedCalls.cpp。

15/15 独立边界检查通过：GIMI 范围外请求、非 GIMI 请求、可选输出、嵌套、跨线程、正常退出、异常退出等。测试使用隔离的模拟调用方 DLL，不运行真实 GIMI、不创建 GPU 或游戏；不能替代完整 FG 游戏验证。

Release/x64 编译链接通过，保留项目已有链接/编码警告。

首轮修复已部署：D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.9\OptiScaler.dll。
SHA256：AD4E199FB1556BE5AA84F1F0665CD8D467690D1983B40278AC5C5F65B500A8AB。
诊断版备份：OptiScaler.dll.pre-gimi-on12-fix-20261008-080021.bak。
部署记录：.build-temp/gimi-coexist-fix/deployment.json。

下一次用户手动测试保持同样 GIMI/Opti/ReShade 加载顺序，检索：

- GIMI DX12 coexist: bypassed warning-only D3D11On12 wrapper ...
- GIMI interop probe ... qi4_hr 0, fg4 非零
- Created Dx11wDx12SC HWND
- ReShade2.ini / 最终 D3D12 runtime 创建
- 模型替换仍生效、DLSSG/MFG 实际插帧

若 bypass 日志没有命中且 qi4 仍失败，应继续检查 On12 detour 实际目标和调用边界，不能据此宣称 GIMI 不是原因。若 qi4 成功但 FG 未启用，进入下一层帧生成数据/Present 生命周期调查。

## 08:01 游戏反馈与 08:27 配套转发版

实际会话：20261008-080128_YuanShen.exe_180392。用户反馈提示音、模型替换消失、Opti 6 倍恢复、只有 F10 的 DX11 ReShade。

日志确认上一版命中 warning-only On12 拦截，qi4_hr=0，fg4 非零，Dx11wDx12SC 建立。但 ReShade 只生成 ReShade.ini 对应 runtime；没有最终 DX12 runtime。该版本不能作为完成的共存修复。

### 撤除失败式拦截

OptiScaler 不再故意让 GIMI 的 On12 请求失败，也不改变原有 On12 hook 的模块选择。嘟嘟声与 GIMI 的“不支持 API”失败提示相符。

改用配套官方 GIMI v1.2.2 源码：SpectrumQT/XXMI-Libs-Package，commit a7847eae82aaac32175c9a7d199673d5c9323bcd。与本机原 DLL 文件版本一致。保留 Core、Mods、ShaderFixes、用户配置，不覆盖资源或模型。

唯一新增 GIMI 转发分支在 Hooked_CreateSwapChainForHwnd 入口，满足两个条件才进入：

- OptiScalerIsPrivateDx12Output 导出报告当前线程位于私有 FG 输出创建范围；
- 输入对象支持 ID3D12CommandQueue。

分支直接调用原有 fnOrigCreateSwapChainForHwnd，不创建警告设备、不包装成旧版 HackerSwapChain。游戏原生 DX11 路径仍走原代码。因 hidden DX11 companion 的创建可能把 G->hWnd 暂时改成隐藏窗口，该分支将窗口句柄恢复为 Opti 提供的可见游戏窗口，保留 GIMI 前台判断和按键处理。

GIMI 新增能力导出 XXMIPrivateDx12PassthroughVersion=1。新 Opti 必须与新 GIMI 配套。

### 最终 ReShade 补建

这条输出创建路径可能绕过 ReShade 自动工厂接管。仅对具有上述 GIMI 能力导出的路线，Opti 在最终 FG Present 前检查已有 ReShade 代理；若没有，则用 ReShade 6.8 的公开 ReShadeCreateEffectRuntime 创建最终 DX12 runtime，使用已有 ReShade2.ini。

后续调用 ReShadeUpdateAndPresentEffectRuntime 渲染最终效果及菜单。该公开 API 不调用 IDXGISwapChain::Present；实际呈现仍由 Streamline 完成。运行时复用，并在 resize/reset 时销毁再创建；进程关闭时不调用已开始卸载的 ReShade。没有修改已经正常的深度控制插件。

### 验证范围

10/10 GIMI 转发选择检查通过：范围外、原生 DX11、空输入、其他线程、引用平衡等。
14/14 最终 runtime 生命周期检查通过：公开 ABI、正确队列和交换链、ReShade2.ini、复用、resize、避免重复接管、关闭阶段等。

测试使用模拟 COM 对象和隔离导出 DLL，不启动真实 GIMI、游戏或 GPU。不能证明实际模型替换/菜单/FG 同时正常。两份生产 DLL 编译链接通过，仍保留原项目的若干编译/链接警告。

GIMI 的工具依赖 D3D_Shaders.exe 有上游链接问题；本机先构建所需 BinaryDecompiler/DirectXTK，然后以 BuildProjectReferences=false 构建 DLL，未修改渲染源代码处理该无关工具问题。

### 部署与下一次测试

配套部署完成，哈希校验通过：

- OptiScaler：E02A5189B0CB2E0B68DCC177D152F9B9BF2AEE3118C6A0499061E59CB4C0FE6F
- GIMI d3d11.dll：BAE02283187DA74AC60925205A20DC95A214A8E12E4586CEF6E568AB1092A26C

部署与备份记录：.build-temp/gimi-coexist-native/deployment.json。
没有改变注入顺序、Mods/Core、桥、深度插件或游戏目录配置；没有启动游戏。

用户手动测试需要确认：提示音消失、模型替换出现、6 倍保持、Home 打开最终 DX12 ReShade。

新增 Opti 指纹：

- XXMI coexist: hidden DX11 swapchain ... Present module（含完整模块路径）
- XXMI coexist: explicit final DX12 ReShade runtime created ... config ...ReShade2.ini
- 或 final swapchain already has ReShade proxy; automatic runtime retained

模型替换是否恢复尚未游戏实测。如果仍消失，需结合隐藏 DX11 的 Present 模块路径、模型实际显示和 GIMI 的加载/frame-action 状态继续定位，不能以“原生分支未改”直接宣称模型正常。

## 08:29 会话和用户澄清：模型正常，缺的是 XXMI 提示

用户已明确澄清：模型替换成功，只是没有 XXMI 提示。此前把“没有 XXMI”按模型缺失处理的表述应以此更正；本轮不再修改模型替换路线。

实际会话：20261008-082945_YuanShen.exe_302824。

- GIMI 私有 DX12 转发正常，qi4_hr=0，FG 包装建立。
- 隐藏 DX11 swapchain 的 Present 属于 D:\APPS\XXMI\GIMI\d3d11.dll。
- 显式最终 DX12 runtime 已创建，ReShade2.ini 及深度快照绑定持续出现。
- Home 未打开菜单，所以问题已进入输入处理而非 runtime 缺失。

### 提示复制时序

旧路径在 GIMI Present 的 RunFrameActions/DrawOverlay 前复制 DX11 画面到共享纹理。提示和 GUI 随后绘制到隐藏窗口，未进入最终 DX12 输出。

配套 GIMI 新增 XXMIPreFlipCaptureVersion 能力；在 HackerSwapChain::Present 完成帧动作与提示绘制后、mOrigSwapChain1->Present 前，调用 OptiScalerCaptureAfterGimiOverlay。

Opti 使用现有 PresentCapture 的单帧线程局部请求，只对当前目标复制一次；即使嵌套 Present 也不重复复制。底层 flip/discard 之前完成复制，保留模型画面和提示。原有提前复制仍作为兜底。没有插入新共享 fence 或改变模型覆盖规则。

### 输入窗口分离

Opti 的 DX11 wrapper 原本在 GetDesc/ GetHwnd 中都暴露可见游戏 HWND，导致先创建的 DX11 ReShade 与手动 DX12 runtime 注册同一个 input 对象。ReShade 会在 primary runtime 的帧尾清除输入状态，后绘制的最终 runtime 可能看不到 Home/鼠标边沿。

新逻辑仅对配套 GIMI 路线且调用方属于 ReShade64.dll 时，让 DX11 source 在 GetDesc 保留底层隐藏 HWND，在 GetHwnd 返回该隐藏 HWND。游戏和其他调用方继续获取真实游戏 HWND，最终 DX12 FG 交换链也保持可见 HWND。ReShade DX11 实际使用 GetDesc 获取输入窗口，所以两条接口都覆盖。

原有已完成的 DX12 控制 DX11 插件不修改。菜单应在最终 DX12 中使用；源 DX11 的窗口不再消费最终可见窗口的输入。

### 验证与部署

38/38 非 GPU、非游戏的回归检查通过：私有转发 10 项，最终 runtime 生命周期 14 项，提示复制时序/输入调用方分离 14 项。两份 Release/x64 DLL 编译链接通过。

08:49 配套部署并备份、核对哈希：

- OptiScaler：BB8D7EAB2B2A3F78F93ADB538A81D04D4B036D5C7443ADAF0EE7AD823F90C475
- GIMI：D269ABC6BF57BD3E830E410585890A3E90ACA0E5D9110D19D5E98A721E82462F

部署记录：.build-temp/gimi-coexist-ui/deployment.json。没有启动游戏，没有修改 Mods/Core、加载顺序、配置或深度控制插件。

新日志证据：

- DX11 ReShade GetDesc keeps hidden input HWND ... final DX12 visible HWND ...
- GIMI post-frame-actions / pre-flip capture boundary enabled
- GIMI frame actions captured after overlay ... copied true
- FG companion capture ... boundary=GIMI after overlay

实际 Home 菜单和 XXMI 提示显示仍需要用户验证；独立检查不证明这两个用户可见行为已成功。

## NR 开启后最终菜单消失：渲染顺序修复

用户已实测确认 XXMI 提示、模型替换、Home、Opti 及 6 倍正常。剩余反馈：开启 NR 后 Home 菜单闪一下消失，快捷键关闭 NR 后恢复。

会话：20261008-085155_YuanShen.exe_398064。NR 开启期间日志仍出现最终 DX12 overlay open requested (Home)，所以不是按键和 runtime 创建问题。该日志也记录 NR native present 执行成功及 FG/GIMI 捕获持续工作。

代码检查发现：Dx11wDx12SC 原先在调用 FG swapchain Present 之前绘制最终 ReShade；FGHooks::FGPresent 内随后仍有 ApplyToFinishedPicture、fg->Present 的 UI command list 提交。存在后续全画面/UI 写入覆盖早先菜单的可能。当前尚未捕获实际菜单像素来证明具体覆盖来源，不把该推断记为游戏已验证结论。

新改动：

- 最终 runtime 的初始化/资源准备保留在原来位置和 FG 锁之外，不提前绘制。
- 对真实 FG Present 路线，建立当前真实帧的线程局部绘制请求。
- FGHooks 完成 NR/FG/UI 写入后，在设置 native Present 标志和调用原始 Present 之前消费该请求，绘制最终效果和菜单。
- 同一帧仅执行一次，跳过测试 Present、其他交换链和插值工作线程；嵌套及异常退出恢复之前请求。
- 非 FG 路线维持原绘制位置。NR 参数、模型、GIMI、深度和输入策略不改。

诊断指纹：`NR overlay order: final ReShade after FG/NR/UI writes, invoked true, final runtime true`。

11/11 顺序检查通过；15/15 runtime 生命周期检查通过，其中准备阶段只初始化而不提前绘制。测试无游戏/GPU，不能替代 NR 开启后的实际可见性验证。Release/x64 编译链接成功。

仅 OptiScaler 已部署，SHA256：52860D6221F5B5E08C842B62FD277D9467C4E68270B00A74EC7E032B26415EC5。
GIMI 仍为 D269ABC6BF57BD3E830E410585890A3E90ACA0E5D9110D19D5E98A721E82462F。
部署与备份：.build-temp/nr-final-overlay-order/deployment.json。

用户下一次测试：开启 NR，打开/关闭 Home，操作菜单并关闭 NR，确认模型、XXMI 提示和 6 倍保持。没有自动启动游戏。

## NR 菜单顺序补丁未解决：改采集实际 GUI 状态

用户 09:18:28 会话测试后确认：NR 下 Home 仍不可见。session 目录只收集到配置，实际日志从游戏 ReShade.log 与安装中的 OptiScaler.log 读取，并保存到 .build-temp/nr-menu-state-trace/evidence-091828 防止下次覆盖。

日志确认 NR overlay order invoked=true/final runtime=true，顺序补丁确实执行；NR 开启后 Home 打开请求仍触发。故不能再仅凭早先代码位置宣称是 Opti FG/UI 写入覆盖。

本轮只给现有深度/控制插件增加被动诊断：

- 记录最终 DX12 的打开及关闭请求、input source、runtime 和 HWND。
- 请求后 2.5 秒内，每 250ms 读取公开 ImGui MouseDrawCursor、Style.Alpha、WantCaptureMouse/Keyboard。
- 使用独立 mutex 保存诊断状态，避免已有“打开一个菜单、关闭另一个”回调的递归锁问题。
- 不改变菜单请求返回值，不修改 NR、光标、样式、输入或深度/控制功能。

观察口径：GUI cursor_mode=true 在输入已注册的情况下对应 ReShade 的 overlay 状态；requested=true/cursor_mode=false 需要继续检查请求是否被拒绝或输入是否失效，不能单凭它直接认定 NR 关闭了菜单。cursor_mode=true 且 alpha 正常但画面不可见，才支持后续输出覆盖或渲染目标问题。透明度异常则需查样式/上下文状态。

菜单没有公开可用的 get_overlay_state，故这里不读取私有 runtime 偏移，也不改动外部 NR DLL。

插件编译零错误、零警告，已部署到游戏和通用 Addons 两处，哈希一致：2B1663818DF1A18BAFA2A270F6165EA107C0B30B7781FCD56CD3A3800EF42E92。
Opti/GIMI 未更换。部署及备份：.build-temp/nr-menu-state-trace/deployment.json。

这不是已验证的修复。需要用户手动做一次对照：NR 开启时按 Home 一次停留 2 秒；快捷键关闭 NR 后再按 Home 一次停留 2 秒。日志前缀 NR menu trace。没有启动游戏。

## 09:45 被动诊断：菜单保持打开，进入原生输出层修复

会话：20261008-094510_YuanShen.exe_779372。用户看到界面闪现后消失。

诊断中，打开后连续约 2.4 秒 requested=1、cursor_mode=1、style_alpha=1.000。关闭请求之间有用户再次操作的间隔，并没有立即关闭事件。按 ReShade 的已注册输入条件，这说明菜单仍处于打开状态且全局样式透明度正常；不支持“NR 立刻关闭菜单”作为首要解释。

代码进一步确认，外层 Streamline swapchain 代理使用的缓冲区与 wrapped_swapchain.cpp 最后 LocalPresent 交给 DXGI 的原生交换链不是同一层。上一轮仅把 ReShade 挪到 FGHooks 尾部，仍修改代理输入层。

### 新方案

仅在 GIMI 配套私有 DX12 创建范围内，且新输出 wrapper 持有 ID3D12CommandQueue、原生 IDXGISwapChain4 时，将它登记为该窗口的最终输出。

- 在 WrappedIDXGISwapChain4 的原生 LocalPresent 完成场景、NR/Opti UI 工作后，原生 Present/Present1 前绘制 ReShade。
- runtime 绑定真正的原生交换链，并使用创建该交换链时提供的队列。不是继续给外层代理输入加菜单。
- 外层 Dx11wDx12SC 检测登记后，不再创建/绘制重复的代理 runtime。
- Present 和 Present1 都覆盖。原生输出 resize 时先释放 runtime 的缓冲区引用；释放交换链前销毁 runtime，进程关闭阶段不调用正在卸载的 ReShade。
- 使用已有 wrapper 的原生 Present 串行化和 ReShade 的 ImGui thread-local 配置；没有引入新的跨队列等待。
- 实际输出的真实/插值呈现都会经过原生绘制位置。这与显示输出的位置一致，可能增加最终效果在插值帧上的开销，需要实际测试帧率。此轮不修改 NR 或 MFG 设置规避问题。

输出登记按 HWND 计数，普通路线和其他窗口保持原逻辑。

9/9 CPU-only 输出归属、生命周期及分离输入/输出模型检查通过。该模型检查不能证明真实 GPU 菜单像素已可见。Release/x64 编译链接通过，保留项目既有链接/编码警告。没有启动游戏。

10:11 仅 OptiScaler 部署，SHA256：5DD3A6D906EC73B988A1D73F6A974FDAC3AD3B5FB598C7B2692F29BE35B0A846。
GIMI、控制插件和配置保持原样。部署备份记录：.build-temp/nr-native-final-output/deployment.json。

实际新指纹：

- NR native overlay: real output wrapper registered, raw ..., queue ..., hwnd ...
- NR native overlay: outer proxy effects skipped; native output owns final ReShade
- NR native overlay: rendered on raw output ..., queue ..., index ..., runtime ready true

用户下一轮需开启 NR 测试 Home；实际可见性和 MFG 稳定性仍未由游戏验证，不能仅凭登记成功宣称修复完成。

## 2026-10-08 用户确认原生最终输出修复成功，进入 NR 输入滤镜研究

用户确认当前工作正常，并指出 DX11 ReShade 滤镜看起来在 renodx-dlss5.addon64 的 NR 之后应用。此次 NR 特指 RenoDX addon，不能用 OptiScaler 普通 DX11 NGX Evaluate 日志代替其运行位置证据。

已完成第一轮执行顺序研究，见 docs/RESEARCH-RENODX-PRE-NR-RESHade-20261008.md。RenoDX 自有日志确认 native DX11 source 的 Present 路线；只读反汇编定位 present 事件注册与当前 backbuffer 获取。候选方案是在深度插件先注册的 DX11 present 回调里调用公开 render_effects，随后让 RenoDX 捕获修改后的源图。公开接口有本帧防重复保护。

本轮仅保存静态证据和研究文档，未修改或部署工作中的 DLL/addon/ini，未启动游戏。GPU 颜色流与全部 native bridge 分支仍需接入后验证。

## 2026-10-08 单套 NR 输入滤镜部署

本轮实际方案改为：源 DX11 在 RenoDX Present 捕获前执行唯一一套效果，最终 DX12 只承载 Home。不是只把DX12界面中的滤镜排序提前。Home直接嵌入NR Input Effects，复用一套源预设。

新增EffectsOwner=DX11PreNR；核心源HoYoShadeEffectsStage=1，最终=2。启动器Bootstrap维护共享预设，并保留各自深度定义。当前源预设Techniques为空，保持用户全部取消效果的状态。

核心从干净上游建立独立worktree D:\CODE\HoyoDLSS5\_research\HoYoShade-single-prenr，避免带入旧实验交换链代码。配套addon已部署。另补Opti原生guide格式适配和SRMI通用配套DLL，以处理崩铁motion图纵翻转换被格式拒绝、XXMI启动退出问题。GPU实际结果待用户测试，未自动启动游戏。

详见 .build-temp/hoyoshade-single-prenr/README.md、deployment.json、bootstrap-production-result.json、各build/tests日志。以deployment.json最终状态判断启动器配套部署是否完成。

## 用户指定启动器由另一位AI负责

2026-10-08 本轮结束时，原生ReShade/控制插件/Opti/SRMI及原神配置已备份部署，启动器程序集与version.ini未部署。用户要求之后启动器需求由他转交负责人；相关子智能体已关闭，未回退共享源码以免覆盖并行修改。

已提供 docs/HANDOFF-LAUNCHER-SINGLE-PRENR-SRMI-20261008.md，包含配置契约、现有改动清单、SRMI-first路由条件、最终测试与依赖发布注意。配套启动器发布仍待负责人完成；游戏/GPU验证仍由用户执行。
