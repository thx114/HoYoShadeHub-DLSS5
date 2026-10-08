# RenoDX NR 之前应用 DX11 ReShade：执行顺序研究

日期：2026-10-08。对象为用户已安装的 renodx-dlss5.addon64，不以 OptiScaler 的普通 DLSS Evaluate 日志代替 NR 证据。

## 结论与范围

现有证据支持一个最小接入方案：让 FsrBridgeDepthAddon 在 DX11 swapchain 的 addon_event::present 中先调用对应 DX11 effect_runtime 的 render_effects，再由已注册在后面的 RenoDX Present 回调捕获画面并执行 NR。最终原生 DX12 ReShade runtime 保持现有路径。

这是可实现的候选方案，尚未写入/部署正式插件，也没有进行游戏/GPU 测试。静态研究证明了回调顺序、当前 backbuffer 获取位置以及公开的提前绘制接口；尚不等同于证明当前用户预设像素已进入 NR 的模型输入。RenoDX 的条件分支、共享纹理复制和视觉输出仍需接入后在游戏中验证。

用户已确认 10:11 的原生最终输出修复工作正常：Home、XXMI/模型和 Opt/MFG 当前属于需保留的工作基线。

## 固定证据

证据目录：D:\CODE\HoyoDLSS5\.build-temp\nr-input-order-research。

- ReShade.reference.log：从游戏目录实际日志复制，包含 11:28:22 起的启动；不能把旧 session 目录名误当作这份日志的启动时间。
- OptiScaler.reference.log：对应实时 Opti 日志副本。
- ReShade.reference.ini、ReShade2.reference.ini：研究时的配置副本，不写回。
- renodx-dlss5.reference.addon64：只读分析对象副本。
- binary-identity.json：插件导出表和 SHA256。原 DLL SHA256 为 DCD93881E976AD033D83C2BB01F4BC3E4DDC59C15FE0DD4CA165BC5FC7D1AC68。
- registration-xrefs-full.txt：ReShadeRegisterEvent 字符串引用。
- event-registrations.txt：事件注册/卸载及控制路线。
- present-callback.txt、present-dispatch.txt、present-render-entry.txt：关键函数的只读反汇编。
- inspect_binary.py、inspect_full.py、inspect_events.py、inspect_present.py：静态分析脚本；没有执行 addon 或加载其 DllMain。

插件日志身份：文件版本 v0.2026.926.210，内部 Generic v8.5.0-rc10，ReShade API 18；当前 ReShade 为 6.8.0.2155，深度/控制插件使用 API 20。

## 当前 NR 实际路线

RenoDX 自己的日志，而非 Opti DLSS 日志，给出：

- DX11Source=native (stored)，并解析为 native route。
- NR effective settings 的 path=present。
- NR-VERDICT 的 api=d3d11 path=present，随后 state=ENGAGED。
- present feature 18 evaluation succeeded ... [native]。
- 工作输入 1280x800，guide 1536x960，输出 2560x1600，2 pass。
- NRHookPoint=2、EnableHooks=2、NRPresentFrames=1。
- present_pre_fg=1，但 telemetry 的 prefg=0、prefg_evaluates=0；不能只看选项开启就声称 pre-FG 路线执行成功。

这说明最终 DX12 菜单控制着进程内同一个 addon，但当前增强源仍是它的 DX11/native Present 路线。菜单所在 API 与实际颜色来源/取图时机是不同问题。NR 内部可能使用私有 D3D12 设备做计算，并不使颜色来源自动变成最终 DX12 backbuffer。

## ReShade 顺序与公开 API

本地 ReShade source/dxgi/dxgi_swapchain.cpp 的 DXGISwapChain::on_present：

1. DX11 execute_command_list 事件。
2. addon_event::present(queue, swapchain, ...)。
3. present_effect_runtime(_impl)，即普通效果及 GUI 的运行时更新/绘制。
4. 原生交换链 Present（由调用者随后执行）。

source/addon_manager.cpp 的 ReShadeRegisterEventForAddon 将回调 push_back 到列表；source/addon_manager.hpp 按列表顺序调用。日志中 FsrBridgeDepthAddon 11:28:25.308 先加载，RenoDX 11:28:25.313 后加载，因此现有深度插件的初始化时机可以用于提前注册 present 回调。实际功能要限定此加载路线；将来顺序变化需要显式检查，不能只依赖文件名。

公开 SDK reshade_api.hpp 的 effect_runtime::render_effects 文档明确允许在指定时刻应用效果，并阻止当前帧默认 Present 再次绘制效果。

source/runtime.cpp：

- render_effects 开头检查并设置 _effects_rendered_this_frame。
- 普通 on_present 中的后续 render_effects 会跳过本帧重复绘制。
- on_present 结束重置标志，因此不会永久禁用效果。
- 提前调用时 _is_in_present_call=false；当前源码会 capture_state/apply_state。SDK 仍提示可能修改状态，因此接入时需验证实际 6.8.0.2155 的行为和当前上下文，不能仅凭源码放弃状态检查。
- 仍触发 reshade_begin_effects/reshade_finish_effects，现有深度绑定有继续运行的路径。
- render_effects 只移效果绘制，不能用 ReShadeUpdateAndPresentEffectRuntime 代替：后者会完整更新 GUI/输入并完成帧末工作，容易再次影响 Home 和多个 runtime 的输入处理。

官方 examples/13-effects_during_frame 展示了提前 render_effects，且提醒正式实现正确区分线性/sRGB RTV 与状态恢复。

## 已安装 RenoDX 的只读反汇编

以下是该文件哈希下的 RVA，仅用于复核；不是补丁地址或公开 ABI，不应写死到运行时。

1. RVA 0x1071F5 加载回调地址 0xB3440；0x1071FC 将事件编号 0x4A（74，即 present）传给 ReShadeRegisterEvent。
2. 0xB3440 保存 queue 和 swapchain 参数；0xB3C51/0xB3C56 将它们传给 0x103E40 做路线管理。
3. 同一回调 0xB3CFE 进入 0x8410 包装；0x8479 进入 0xE3450 的 Present 处理函数。
4. 0xE3450 会区分 DX11/DX12 来源。0xE3F3C-0xE3F56 使用传入 swapchain 调用 get_current_back_buffer_index（vtable +0x38）和 get_back_buffer（+0x28）。这些槽位按已安装 SDK 对照，最终句柄从返回结构取出。
5. DX11 分支从该对象调用资源接口和纹理 GetDesc（0xE3F89-0xE3FFF），获取尺寸/格式，检查单样本纹理；DX12 分支另行检查资源维度/格式。
6. 0xE48BC 判断 DX11 分支，并在随后检查 immediate context、交换链工作集及 guide 匹配。存在原生 bridge/shared surface 的进一步处理。

因此，这条 Present 处理路径不是只使用更早的 NGX 参数来判断“颜色已经被滤镜修改”：它确实在当前 Present 阶段获取交换链颜色资源。提前绘制方案具有具体接入依据。尚未完整反编译所有桥接/历史/共享面分支，不能声称所有 NR 模式都遵循同一取图规则。

导出表只有 NAME 与 DESCRIPTION，没有稳定的外部 RunNR/GetInput/SetInput 接口。此轮未找到与 v8.5.0-rc10 完全匹配的公开源码，不能拿其他 DLSS5 项目冒充该版本实现。

## 接入方案（待实现）

在现有 FsrBridgeDepthAddon 内增加独立的 DX11 早期效果阶段，使用公开 SDK，不修改 RenoDX 二进制或全局 ReShade 事件列表：

```cpp
// 伪代码：不是已部署代码。
on_dx11_present(queue, swapchain) {
    // 严格匹配此 swapchain 对应的 DX11 runtime 和同设备 immediate context。
    // 检查开关、FinalRuntimeOnly、有效 RTV、单样本格式以及递归保护。
    auto runtime = source_runtime_for(swapchain);
    auto views = current_backbuffer_views(runtime);
    runtime->render_effects(queue->get_immediate_command_list(), views.linear, views.srgb);
    // 返回后 ReShade 继续调用后面的 RenoDX present 回调。
}
```

设计约束：

- 可切回正常 DX11 Present 效果位置；开关只管效果执行阶段，不能通过关闭 NR 来伪装成功。
- 只匹配真实 DX11 source runtime、交换链/当前 backbuffer、同设备 context；不得靠 HWND 选中最终 DX12 runtime。
- 匹配交换链时优先核实公开 handle/设备/当前缓冲区身份；effect_runtime 与底层 api::swapchain 可能是不同对象，不可只做指针相等假设。
- 不持有现有 g_mutex/控制队列 mutex 调用 render_effects；它会回调深度和滤镜事件，持锁会递归死锁。
- 不在首次创建或资源失效时先绘制到空 RTV；不要用 render_effects(...,0,0) 当“准备”调用，它也会消耗本帧绘制机会。
- 缓存当前 backbuffer 的线性/sRGB RTV，resize/destroy 清理。单样本、格式可创建且启用的 runtime 才接管；MSAA/未知格式保持默认流程。
- 检查渲染目标状态与 DX11 SRV/RTV 绑定冲突；保留或验证完整上下文状态恢复，确保 RenoDX 复制/共享面处理和 GIMI 后续 frame actions 不受污染。
- 已编译效果可提前绘制。普通 update_effects 尚在后面的 on_present：首次编译、热加载、启用新效果可能需要一个过渡帧，不能在回调里递归强制完整 Present。
- 现有控制命令继续在 source runtime 自己的线程执行，不跨 DX12/11 调用 effect handles。
- 现有深度发布回调需验证仍在实际绘制前执行，尤其 addon_current 的递归事件过滤。不得把“render_effects 被调用”当成深度效果已经输出。
- 首版把同一 DX11 预设整体移动到 NR 前；DX12 预设保留 NR 后。若需要同一 DX11 runtime 的部分效果前、部分效果后，不能直接调用整套 render_effects 两次，需要另行设计。
- 不调整 DX11/DX12 输入 HWND、不重建当前最终输出 runtime、不改 GIMI/Opt 创建顺序。

目标顺序：

游戏 DX11 完成画面 → DX11 ReShade 效果 → RenoDX native Present 捕获与 NR → GIMI/桥接最终颜色复制 → Opt/MFG → 原生最终 DX12 ReShade 效果与 Home。

这里 NR 原生 bridge 的 GPU 计算/回写由现有 addon 完成；箭头表示源图处理次序，不宣称每个 GPU 阶段都在同一线程/设备。

## 实际验证要求

此轮没有启动游戏或 GPU 测试宿主。

首个接入版本应记录 source runtime、swapchain、backbuffer 句柄/索引、设备、线程、early begin/finish、该帧绘制技术数；普通晚期效果开始次数用于排除二次绘制。遇到未知对象或无 RTV 时保持默认路径并记录原因。

验证应证明两个不同层次：

1. 调度：早期渲染完成，普通后段效果没有重复绘制；RenoDX 的 feature 18 仍成功，Home/MFG/模型仍正常。
2. 颜色：用固定、明显的诊断效果做前后对照，必要时在现有游戏内异步采样早期效果前/后及 NR 回写后的颜色。若效果只是最终贴上去，不能算它进入 NR 输入。仅比较菜单勾选状态或截图中“滤镜看得见”不足以证明输入关系。

同时检查深度方向、sRGB 色彩、NR 历史重置与热重载。调色、锐化等可作为首轮顺序验证；会大幅移动像素的效果可能与原 motion/depth guides 不匹配，应在顺序成立后再验证具体预设。

## 网上资料核对

检查了官方 ReShade include/reshade_api.hpp 和本地同版本 SDK/源码；公开 API 的提前绘制与防重复约定一致。曾查找 clshortfuse/renodx 的 dlss5-anywhere 入口，但没有获得与已安装二进制一一对应的 Generic v8.5.0-rc10 源码。本报告的 RenoDX 结论来自已安装文件、配置和其实际日志，而非网上另一版本的行为描述。

## 目标更新：单套效果与性能优先

用户进一步要求：不用维护两套ReShade效果，优先让一套效果进入NR输入，DLSS前可作为后续选项；并询问DX11/DX12与两个runtime的性能差异。

新取舍文档：docs/RESEARCH-SINGLE-RESHade-NR-PERFORMANCE-20261008.md。建议优先验证“DX11在NR前为唯一效果执行器，DX12只保留Home及控制界面”，沿用现有控制队列，避免为DX12前置滤镜额外引入颜色往返。真正同一DX12 runtime处理源色也有公开接口依据，但需要独立graphics queue、共享颜色回写、runtime线程串行化以及GUI输出同步，尚未实现或实测。

本地无严格API性能对照数据，不能给出固定节省比例；6倍输出也不能直接视为滤镜实际执行六次。先测实际technique调用率及GPU时间。用户当前工作基线未改动。
