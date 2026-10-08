# 单套 ReShade 效果执行：NR 前处理与效率取舍

日期：2026-10-08。用户当前目标：不要维护两套滤镜/预设；最终效果应进入 renodx-dlss5.addon64 的 NR 输入。DLSS 前属于次要目标，困难时允许先放在 DLSS 后、NR 前。

本轮用户又要求比较 DX11/DX12 的开销，并研究通过修改 HoYoShade 只让一套效果生效。未部署或执行 GPU 测试；以下为源码与现有日志支持的方案取舍，不是性能实测结果。

## 当前进程的实际结构

日志记录同一个 ReShade64.dll 初始化，随后建立 source DX11 与原生最终 DX12 两个 effect_runtime。不能把两个 runtime 直接描述为加载了两份 ReShade DLL。

两个 runtime 可以各自运行效果、持有 GPU 资源、配置及菜单。只显示一个菜单不会自动停止另一套滤镜；全局关闭效果绘制也未必马上释放已编译 shader/纹理或所有 hook 开销。

现有 FsrBridgeDepthAddon 已有跨 runtime 的 DX12 → DX11 控制队列；用户已确认控制功能正常。因此“一个界面控制一套 DX11 效果”有现成基础，但目前还需将实际 DX11 效果执行移到 RenoDX 的 Present 捕获之前。

## 能回答和不能回答的性能问题

不能仅凭 DX11/DX12 名称给出减少多少毫秒或多少百分比。本地没有同一场景、同一 preset、同一分辨率、同一 NR/FG 状态的 GPU 对照数据。

需区分：

- 滤镜的 GPU shader、pass 数、工作分辨率和执行频率。
- CPU command recording、状态追踪、hook 和 runtime 更新。
- DX11/DX12 纹理复制、共享 fence 等待、队列串行化。
- 菜单闭合时的后台更新与打开时的 GUI 绘制。
- NR 的模型本身与帧生成。这些开销不能算成 ReShade API 的差异。

当前数据不足以宣称 DX11 固有地比 DX12 更快；更有依据的判断是：在当前 DX11-source 的 NR 路线上，用 DX11 做前置效果可以避免为 DX12 前置效果额外增加一次跨 API 颜色往返。

条件成本模型（不是实测）：若真实帧速率为 F，效果每次耗时为 E，输出倍率为 M，那么只在真实帧执行的效果调用率为 F，每个输出帧执行则为 M×F。M=6 时调用次数的理想比是 1:6；这只针对滤镜绘制次数/工作量，不代表全游戏 GPU 帧时减少 5/6 或 FPS 提升六倍。

当前 wrapped_swapchain.cpp 的 LocalPresent 会在 willPresent=true 时更新最终 DX12 runtime，但也存在 OptiScalerIsExternalPresent 直接旁路及其他 producer 路径；因此不能只凭“6倍开启”断言现有滤镜已经在全部六个输出帧运行。需记录每秒 source frame、native output Present、实际 technique draw 三个独立计数。

## 方案比较

| 方案 | 实际效果执行 | GUI/操作 | 对当前 NR-source 的新增颜色传输 | 当前判断 |
| --- | --- | --- | --- | --- |
| DX11 与 DX12 都启用滤镜 | 两套执行 | 两套或一个汇总界面 | 现有传输；另有两套 shader 工作 | 不作为默认目标 |
| DX11 在 NR 前执行；DX12 只画菜单 | 一套 DX11 效果 | 一个 Home 界面控制源效果 | 不因前置滤镜新增 DX11→DX12→DX11 往返 | 优先验证，最少改变现有颜色链路 |
| 同一个 DX12 runtime 在 NR 前执行；输出只画菜单 | 一套 DX12 效果 | 同一套预设和 Home | 需要处理 DX11→DX12→DX11 颜色往返和同步 | 满足“效果也用 DX12”的候选，成本/风险更多 |
| 仅最终 DX12 执行滤镜 | 一套 DX12 效果 | Home | 不新增前置往返 | 滤镜仍在 NR 后，未满足当前目标 |

“只生效一个”首先应指只执行一套滤镜，而不是先追求进程里只剩一个 runtime 对象。前者直接减少重复滤镜；后者是否有明显收益需单独测量，并需要保证 RenoDX 的 source 检测与生命周期不被破坏。

## 推荐：效果与最终菜单分开调度

推荐作为下一轮实现目标：DX11 Source 为唯一效果所有者；最终 DX12 为显示/控制界面；用户只维护一个源 preset。

目标画面顺序：

游戏场景 → DLSS → 唯一的一套 DX11 ReShade 效果 → RenoDX NR → Opt/MFG → 最终画面与 Home GUI。

这里保留 DLSS 当前位置，先满足更明确的 NR 前要求。

接入点沿用 docs/RESEARCH-RENODX-PRE-NR-RESHade-20261008.md 的 DX11 early present 方案。最终 DX12 只更新 GUI，不让它再执行一套效果。现有控制面板可继续使用，后续 HoYoShade 定制 GUI 可以将源效果列表/参数显示为主界面，避免两套 preset。

必须区分已有 FinalRuntimeOnly 开关：当前 FinalRuntimeOnly=1 禁用的是 DX11 效果，方向与此方案相反。不能直接把它打开冒充“只用一个”。需要明确的 EffectsOwner=DX11PreNR / DX12PostNR 等配置，分别决定源效果与输出效果，GUI 位置独立。

公开 API 支持 effects_state 与 overlay 独立控制，但简单调用 set_effects_state(false) 仍要检查 addon-owned effect，以及源 preset 的启用状态。若需要严格保证最终没有任何 effect pass，可在 Opti 原生 final runtime 的 Update 之前以 render_effects(cmd,0,0) 消耗该帧普通效果执行机会，再进行正常 GUI Update。SDK 明确说明零 RTV 不画效果、仍更新特殊 uniform。这是有意抑制最终绘制，不能在源效果调用前使用它。现有公开 API 的 reshade_render_technique 事件返回 void，不能错误地用“return true”当作阻止绘制接口。

无论用哪种抑制方法，都需要实际 technique draw 计数证明最终效果确实没有再执行；GUI 及 RenoDX addon 的运行不能被全局禁用。

## 同一 DX12 runtime 真正负责 NR 前效果的可行性

本地 ReShade runtime.cpp 的 render_effects 支持来自同设备的其他单样本 RTV，并按输入纹理的尺寸/格式创建 effect permutation；它并不要求每次都画在自己的最终 backbuffer。因此一个 runtime 可以同时拥有最终 GUI和源颜色效果的资源，属于公开 API 支持的方向。

但 DX11 颜色纹理不能直接交给 DX12 runtime：必须共享/open 到对应 DX12 设备并建立 RTV，处理完再回写同帧 DX11 source，让 RenoDX 后续 Present 使用该图。

现有 Dx11wDx12SC 有共享影子颜色和 DX11→DX12 fence，可参考其格式/句柄流程，但它目前主要是 NR 后向 FG 的单向传输。不能直接复用其 _WaitDx11ThenDx12 当作完整往返：它等待的是 COMPUTE copy queue，不能在那条队列执行 ReShade 的 graphics passes，也缺少 DX12→DX11 写回完成协议。

一个单 runtime 候选设计是：在原生最终 DX12 device 上创建独立 DIRECT 队列，使用公开 ReShadeCreateEffectRuntime 将该队列与原生输出 swapchain 配对；同一个 runtime 的效果处理与最终 GUI在这个队列提交。源码 addon.cpp 允许传入 graphics queue，创建时没有要求它必须就是最初创建 swapchain 的那条队列。实现仍需自己核实 device/adapter 兼容，并给最终 Present 建立显式 queue handoff；这只是接入依据，不是已经验证更换队列安全。

必须解决以下问题才可部署：

- source → 专用 DIRECT → source 的共享资源、barrier、双向 fence。
- GUI写原生输出时，真实输出队列与专用队列的完成交接。
- source render 与 FG native output thread 对同一 runtime 的 CPU访问串行化；仅锁 addon 不能覆盖 Opti 的 Update/Reset/Destroy。
- 不在持有 FG、source device、addon registry 等互相依赖锁时等待另外一条线程；不让 FG/present 队列等待它尚需当前源帧才能完成的 fence。
- 所有额外输出帧只更新 GUI，不再次画效果；不能只靠 _effects_rendered_this_frame，因为每次输出 on_present 会将其重置。
- 若连续 source 帧期间没有输出更新，render_effects 的防重复标志可能跳过源效果；需严格帧协议或按用户排序调用公开 render_technique。enumerate_techniques 在当前源码按 _technique_sorting 顺序列出。render_technique 不自动遵守 enabled 状态，调用方必须自己筛选。
- shader framecount/frametime 等不能随 6倍 GUI Present 冒充真实源帧；否则 temporal/动画效果时间步进会改变。
- CPU depth snapshot 当前可能已经有跨帧延迟；前置深度滤镜需要记录 source/color/depth frame token，不能直接把最终输出阶段可用的旧 depth 当成源图同帧深度。

这条路线的额外往返与同步很可能比保留 DX11 为唯一效果执行器更复杂；是否更快只能实测。单 runtime 数量不是充分的性能指标。

## 能否修改 HoYoShade 完全不创建第二个 runtime

本地 source/runtime_manager.cpp 有 INSTALL.MaxEffectRuntimes 和 GENERAL.Disable。它们按自动创建顺序/配置名生效，不能安全地替代“按 API与用途选择 runtime”的策略：source DX11 最先创建，直接 MaxEffectRuntimes=1 可能留下 source 并排除自动 final；当前最终 runtime 又是 Opti 的显式创建路线，单纯限制自动 runtime 不一定限制它。

若需要严格只创建一个，HoYoShade 可以按 source/output 角色定制 runtime 创建和 GUI调度。但闭源 RenoDX 当前在 source runtime 初始化/销毁阶段有回调，尚未证明完全去掉 DX11 runtime 后 NR的 native route 仍会启用。因此暂不以删除 source runtime 作为首轮优化。先保证一个效果所有者，一个界面，一个 preset；再测量 dormant runtime 残余开销，决定是否值得继续拆分 hooks 与 effect runtime。

## 如何得到可信的节省数值

用户手动测试时，固定同一场景/分辨率/相机与同一个 preset，保持 NR、DLSS与6倍设置不变。对照：

1. 两套 runtime 的所有普通滤镜关闭，GUI关闭，作为链路基线。
2. 只有 DX11源效果启用。
3. 只有 DX12最终效果启用。
4. 单套前置模式（调度接入后）。

计量 source FPS、输出 FPS、真实帧GPU时间、technique GPU时间总和与调用率，以及新增 copy/fence 时间。菜单打开另测。不能只读6倍输出FPS或用CPU Present耗时当作 shader GPU耗时，也不能把NR的约29ms记录归因于ReShade。

本轮仅更新研究，不改变用户确认正常的版本。
