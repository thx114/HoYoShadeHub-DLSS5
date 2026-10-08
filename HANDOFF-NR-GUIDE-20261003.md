# 原神 DLSS5 NR / Bridge 交接记录

日期：2026-10-03

## 发布完成：OptiScaler0.1.9 / Bridge2.3.2 / 原神覆盖包2.5

本机2026-10-04已完成用户授权发布。OptiScaler mfg-ada-0.1.9与Bridge v2.3.2-fg-20261004已公开GitHub Release、Latest、资产回下载SHA256与本地一致。源码分别在release/nr-guides-0.1.9（9e636343）/release/fg-2.3.2（5862dd4），root main提交1ce8cd4已推送。失败GPU Wait路径从OptiScaler发布source剔除，保留用户三操作复测正常的CPUreader退休版及NRguide/menu/resize修复。

组件文件：OptiScaler-MFG-Ada/release/optiscaler-mfg-ada-fg-only-0.1.9.zip；build/genshin-fsr-bridge-v2.3.2-fg-20261004.zip。公开独立组件包不携带第三方NR/FG模型。Opt0.1.9.0 / Bridge2.3.2.0均验证资源版本。

原神本地覆盖包已更新D:\APPS\test\原神6倍覆盖包_2.5.zip，60文件、521530479字节，SHA256 E861FBB51EC8A9288275A890632070519E7C14EAC5DBFEC945708E468CC195ED。manifest/self-size/ZIPCRC及内置DLL与组件包一致性通过，实际LocalPackageInstaller测试通过。旧2.4保留；临时stage因空间移到C:\Users\thx11\.codex\release-staging\overlay-genshin-6x-2.5-20261004-223656。覆盖包未声称上传（用户只要求两个组件上传和更新覆盖包）。

启动器source修改：Bridge tagPattern支持2.3.x和旧delaytag；激活Genshin profile后对支持的0.1.9+DLL维护NativeScreenSpaceGuides=true；移除旧normal-launch跳过HoYoShade限制；第二runtime AddonPath保持为空且配置同步幂等。catalog新增原神preset并更新source说明。扩展787项0失败，启动器1.4.1.3 self-contained发布通过，独立app目录D:\APPS\HoYoShadeHub\app-1.4.1.3已安装，version.ini切向新版本，下次启动生效，未关闭正在运行的启动器。rootexe/config/database未覆盖。

低原始帧率/NR两pass成本和切换长帧仍未解决，发行说明保留实测限制，未发布失败的performanceGPUwait实验。运行中的游戏active0.1.8目录仍为原CPU同步31D9BE51，用户可安装新release/导入2.5选择0.1.9。无关机。

## 最新状态：sync error性能优化回归，已退回CPU退休版

用户问弹窗sync error是否有关。22:12这轮日志显示Streamline GPU fence等待500ms/pacer flush timeout，随后22:12:09/14 Dx11wDx12SC::_WaitForCopyAllocator连续5秒超时，slot0 fence4268 completed4267。ReShade reports DEVICE_REMOVED但removal reason0，不得当作已经证实的TDR/DEVICE_HUNG。

按新GPUhandoff部署后的同步回归处理，已保存 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\performance-gpu-handoff-20261004-220935\failed-sync-20261004-221337` 并执行Rollback-Performance，实际DLL恢复31D9BE51...（用户之前三操作测试0错误/正常退出但帧率偏低的CPU退休版），hash核对。NativeScreenSpaceGuides=true及INI保持；NR对齐/menu补丁/Bridge61A58297...保持。没有再部署优化。

GPUhandoff包manifest已明确FAILED/rolled back。WARP生产双API测试虽通过，却未建模Streamline异步pacer/graphics的整个依赖图，新DX11 Wait可能参与循环等待；不能断言确切cycle或仅靠延长timeout修复，更不能声称popup一定来自哪个函数（未取得弹窗原文）。后续性能优化必须先验证真实队列图或使用不依赖新cross-queue wait的隔离策略。原游戏性能低仍未解决。

## 最新性能优化版已部署：稳定资源GPU等待，变化时保留CPU退休

用户继续要求性能优化。新增framegen/SharedInputReadFence_Dx11.h，稳定native资源条件命中时DX12读后shared-fence Signal→DX11 GPU Wait，再CopyResource/compute写共享cache；CPU不阻塞。源shared、指针/desc/feature/flags/config改变、sc/FG变化等不走快路径，保留先submit全部UI slot后CPU完成等待；queue不同/不支持共享同样fallback。GPU wait只用于桥拥有GPU写共享目标，不用于CPU直接写shared memory。每300次记录Native FG input retirement CPU timing和Native DLSS pipeline CPU timing（非GPU耗时）。

测试使用真实D3D11 CopyResource生产+shared NT texture和DX12读，16GPU交接循环数据正确；人为blocked consumer时CPU Enqueue约0.0039ms；原CPU退休/control/queue change/failure guards继续通过，0debug警告/错误。最初CPU UpdateSubresource写shared测试失败，改为真实production GPU copy后通过，因此不泛化到CPUshared写。Release构建exit0。此轮尚无实机FPS提升/稳定性验证。

已部署 `2026-10-04T22:09:35+08:00`；OptiScaler `89A28453E20367AE9CAD604A4DEA8394FE541AB7BBFCB01E7AB37685BD64E4C5`，INI不变，Bridge61A58297...保持，NR对齐/menu修复保持。包 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\performance-gpu-handoff-20261004-220935` 含baseline31D9BE51...（上轮3操作未hung但低fps）、manifest、实现/测试、日志、README、Rollback-Performance.ps1。未降NR pass/resolution，无关机。

下一验证：同一场景停留30秒，再C/活动/进房间，保留新CPU分段计时与NR telemetry；gpu wait比例应稳定场景增加，转场cpu fallback正常。若多数cpu回退，查resource/flags变化，不绕过guard。NR两pass35–43ms本身仍在；GPU handoff减少CPUstall而不保证GPU critical path缩短。不能声称已提升多少fps。

## 最新三场景性能复测：本轮未挂起，原始帧率低

用户说做了反复C、活动、进入房间/切地图三件事并要求看性能。已保存21:52–21:54这轮三个日志：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\performance-three-actions-20261004-215620`，分析PERFORMANCE.md/performance.json。本轮0errors/DEVICE_HUNG/REMOVED/bridge lost/fence失败，正常退出。没有更改配置/部署。

ReShade30秒窗口Present/s=148.39(启动混合)、11.47、14.08、14.00、24.90(加载/无guide混合)。中间两窗口avg71ms、P50=71.5ms，有效原始帧率约14；不是MFG显示fps。NR工作1792x1120、scale0.7、2pass；runtime请求5插帧。NR cost估计35–43ms GPU、CPU提交约1ms；lease_starved升369，不能当完整GPUprofile。切换FGPresent占用237–276ms，日志峰值本轮去启动后可达1320ms，不能每条映射用户操作（无时间标记）。显存峰值5.46GB后回落4.73GB，4context未达limit。

新共享reader CPU等待未独立计时，不能断言具体性能损失都由它造成。用户本次只要求性能核对，未自行降NR分辨率/pass数或关闭FG/NR。下一优化应先打点retirement/evaluate/NR/FG，保留已验证同步正确性，不能为fps撤销读写保护。

## 最新反复C仍GPU HUNG；FG共享输入读取退休修正已部署

用户复测多次C仍崩溃。真实loaded哈希：Bridge61A58297...、OptiScaler4BB63090...，不同instance的context确实创建。日志依旧DXGI_ERROR_DEVICE_HUNG；菜单修复正确打印device unavailable并skip upload，没有旧nullMap崩溃新转储。角色实例隔离不能被当作实机成功。失败证据 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\character-instance-contract-20261004-213800\failed-c-repeat-20261004-214200`，前包manifest已更新失败结论。

查native DX11→FG原Motion/Depth共享单份cache，只存在生产写后DX12读的sync，缓存下一次重写前未保证UI flip/copy已读完。新增生产helper `framegen/Dx12InputReadRetirement.h`，IFGFeature_Dx12::RetireSharedInputReads先提交全部pending UI slots，再等全部已提交fence；native UpscaleStart在EvaluateState/StartNewFrame/PrepareCache之前执行；失败退出避免缓存重写；WaitForUIAllocator拒绝index越界及UINT64_MAX removed fence。

`tests/fg_shared_input_retirement_gpu.cpp` 真实D3D11/D3D12 WARP共享NT纹理+fence证明旧单向sync读到重写后的下一帧输入；修正版16轮4slot/非相邻slot交替输入正确，提交失败不进入wait、wait失败阻止继续，0debug警告/错误。它只证明共享缓存读写同步，不覆盖NVIDIA Streamline内部异步任务，也不证明GPU HUNG根因。

最终Release x64 OptiScalerFgOnly=true构建exit0，新activeOptiScaler SHA256 `31D9BE51C975C0D46EE5CCDF986821ABE70BE624EBD3504368CC863AAA57BFA7`，部署 `2026-10-04T21:49:11+08:00`。包 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\character-shared-input-20261004-214911` 含baseline菜单DLL4BB63090...和当前INI、实现/测试/构建日志/manifest/README/Rollback-SharedInput.ps1。INI未改，NativeScreenSpaceGuides=true；三份成功NR/resize实现与v2逐字节一致。Bridge仍61A58297...。此轮没有额外回滚Bridge，没有修改NR/FG配置，没有关机。

实机反复C/角色切换和性能待验证。该方法增加native帧共享输入复用前CPU fence等待，可能损失重叠/帧率；不能当作“无成本修复”。必要时rollback仅退回菜单修复DLL，保留已经解决的NR对齐。若hung继续，应获取新真实GPU故障证据，而不是继续按键猜测。

## 当前状态：用户取消关机；角色实例/创建参数隔离补充版已部署

用户明确说“不需要关机，你继续工作即可”。立即执行shutdown /a，exit0，关机成功取消，此后未安排关机。下文历史关机安排均已撤销。

继续检查补齐实例创建参数：同实例尺寸相同但depth_inverted、motion_vectors_jittered、hdr10_pq_color、use_direct_linear_color改变时，必须重建对应native上下文，不能复用旧flags/颜色合同。每实例session新增这些元数据并参与matches。增加测试确保depth/MV创建标志变化仅重建该实例，不污染主场景。

最终production WARP测试通过40次两实例交替、context/texture/history/resize/create-flag隔离、8session上限和销毁，0debug警告/错误。Release Bridge构建exit0。真实C界面GPU HUNG因果/效果仍待用户复测。

实际部署 `2026-10-04T21:38:00+08:00`；active Bridge SHA256 `61A58297C88E2A7D8DF3690FF59BBB122BEDE62D8F20260B21D9A1D093B218E8`。菜单OptiScaler仍 `4BB63090B4830ACE210ED107F9905750D1A7F765FE97B653D171CB6F10CDD52E`，NativeScreenSpaceGuides=true。Bridge INI哈希确认未改。包 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\character-instance-contract-20261004-213800` 含DLL、源代码、测试、manifest、构建日志、README和Rollback-Bridge.ps1（rollback只恢复原Bridge，保留NR/菜单修复）。原始baseline Bridge DEB3B3C3...、上一隔离版A7910655...均保留。

下一步须从新游戏运行获得证据：C开关角色界面、切角色、再resize/开Opt菜单；不同FSR实例创建独立context日志，正常主场景对齐不变。如再次hung，核查native NGX多handle与外部RenoDX capture source选择及FG资源同步。不能靠已有mock/WARP断言实机完全解决。

## 最新角色实例隔离修正已部署，按用户要求准备关机

用户澄清C为角色界面，并要求“解决c崩溃代码完成后关闭电脑”，随后要求继续。已完成源代码、回归验证、Release Bridge构建、备份和部署；真实角色界面C复测仍待用户，不宣称GPU HUNG根因已证实。

实际本机部署时间 `2026-10-04T21:33:46+08:00`。Bridge源码 `D:\CODE\genshin_fsr_brigde\Dx11FsrBridge\Fsr2TranslationLayer.cpp/.h` 和 Dx11FsrBridge.cpp：将原单例native上下文和prepared资源按call_params.instance隔离；每实例拥有history reset/尺寸/时间，resize只重建对应实例，最多8实例，全局reset释放所有会话。日志fsr2_shim_instance_context_created。备份源文件在D:\CODE\genshin_fsr_brigde\_backup-shim-instance-isolation-20261004。

实际生产dispatch的WARP回归 `Fsr2TranslationInstanceTest.cpp` 通过两实例40次交替、独立context和motion纹理、reset/resize隔离、bounded cache、全部清理；0debug警告/错误，仅FSR/NGX入口为mock。Release Bridge构建exit0。

active Bridge SHA256 `A791065546D5115765CC0AED4851C8658876DC6634996DAA527CD7A961F4176D`，原 `DEB3B3C3542D37E8538CDF3AEE3979C4467E16B17248A49B50205EB64B70D49D`。OptiScaler仍为已安装菜单修复 `4BB63090B4830ACE210ED107F9905750D1A7F765FE97B653D171CB6F10CDD52E`，NR guide对齐保留，Bridge INI未改。包 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\character-instance-fix-20261004-213346` 含DLL、源码、测试、构建日志、manifest、README和Rollback-Bridge.ps1。该rollback只恢复原Bridge，不撤销已解决的NR坐标和菜单修复。

依据是日志出现多个FSR实例而原shim共享单个上下文和资源；源码确认隔离缺陷，但不能把它直接等同于GPU HUNG根因。用户醒后需C反复开关角色界面实机复测。所有交接记录已保存，按用户授权执行正常关机，无强制关闭参数。

## 最新状态：菜单修复已直接安装；C触发记录为GPU HUNG

用户报告“一按C又崩溃，和这次修复是否一个问题”，并已关闭游戏。核查发现上一轮hidden helper尝试安装时DLL仍被占用，worker日志FAILED，实际磁盘DLL仍为成功NR v2的3D88BCD3...；因此C故障不是新版菜单补丁已加载后失败。

- 本机05:29后已直接运行Install-MenuFix.ps1成功。active DLL SHA256 `4BB63090B4830ACE210ED107F9905750D1A7F765FE97B653D171CB6F10CDD52E`，manifest Installed并核验。INI NativeScreenSpaceGuides=true且其他内容与本次退出时baseline相同。Rollback的baselineDLL仍是成功NR v2，哈希核对。
- worker记录的FAILED保留；已补MANUAL INSTALL VERIFIED记录，不能只看旧FAILED判定当前部署。安装失败当时是退出后文件尚未解除占用，不是此后仍运行游戏。该次helper已经结束。
- C事件日志已保存：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\menu-resize-fix-20261004-051809\c-key-run-20261004-053618`。
- 此次首先出现ReShade DeviceRemoved，OptiScaler实际GetDeviceRemovedReason为DXGI_ERROR_DEVICE_HUNG（0x887a0006）；NR private bridge也lost 0x887a0006。未生成比04:53:19更新的mihoyocrash转储。故不能断言与先前null uploadBuffer->Map是完全同一个崩溃。
- 现有配置OptiScaler菜单ShortcutKey=auto，源码默认VK_INSERT；ReShade菜单KeyOverlay=36（Home），NR toggle F6/screenshot F5。没有证据把C当Opti菜单键；C具体触发的游戏界面需确认，不能用单纯按C描述归因字体上传。Bridge快捷键检索同样无C。
- 现在真实菜单修复包已部署；新进程复测后才可判断该补丁是否改善C/GPU挂起。用户已确认解决的NR闪烁和depth/motion对齐继续保留，未改NR/FG设置，未禁用C或角色功能。

## 最新用户验证：NR问题已解决，处理菜单resize闪退

用户明确确认：“闪烁问题完全解决，深度运动匹配完美”。此结论对应成功v2（DLL SHA256 3D88BCD3...）。不要为了菜单bug撤销该guide坐标修正或回到错位旧版。

新报告：切分辨率后打开OptiScaler菜单闪退。实际crash报告在 `C:\Users\thx11\AppData\Local\Temp\mihoyocrash_339db1cd6fd6b740b20cc330d0be1b77`，本机04:53:19。已存到 `OptiScaler-MFG-Ada/release/menu-crash-evidence-20261004-045319`。RVA0x169ebc映射到ImGui_ImplDX12_UpdateTexture+0x36c，机器码核对一致；uploadBuffer->Map访问null；CreateCommittedResource失败后仅IM_ASSERT检查，Release无效。原始HRESULT/底层device失效原因不能由现有dump确认。

已实现菜单修复：`menu/MenuGpuLifetime_Dx12.h`，菜单slot/idle fence保护allocator复用及resize资源清理；menu_overlay_dx.cpp保存device/queue引用并在替换时重新初始化，处理HWND变化与索引/资源检查；imgui_impl_dx12.cpp检查创建/Map/Close/device失败，安全返回清理未提交资源，未完成纹理上传不draw。native guide及FG resize三个实现文件与成功v2逐字节一致。

真实生产后端WARP测试通过：GPU人为阻塞时等待拒绝复用，解除后完成、queue替换、实际字体上传与4个不同尺寸RT的菜单draw、关闭/重建后端、分配失败descriptor回收、removed-device skip；有效操作debug layer0警告/错误。旧实际backend控制组0xc0000005，新版通过。最终Release构建exit0。游戏菜单复测仍待。

包：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\menu-resize-fix-20261004-051809`；新DLL SHA256 `4BB63090B4830ACE210ED107F9905750D1A7F765FE97B653D171CB6F10CDD52E`。Install-MenuFix.ps1备份退出时INI/成功v2 DLL；Rollback返回成功v2。已启动hidden after-exit helper PID52008，最长等待10分钟；当时游戏PID78824仍活跃，当前实际部署状态以manifest.json和install-after-exit.log为准。脚本已修复HasExited残留判断及INI带空格键的验证。不要未经查看状态就说已部署。

详见 `OptiScaler-MFG-Ada/docs/MENU-DX12-RESIZE-CRASH-20261004.md`。后续复测切分辨率再开Opt菜单，同时确认NR匹配效果保留；不改用户NR设置。

## 最新部署确认：v2 已安装（本机 2026-10-04 04:42:20）

覆盖下文“等待退出/未安装”。用户说游戏已退出，检查确认名称枚举仍返回PID15188，但HasExited=True、按PID查无活进程；后台旧guard把已退出条目视为活进程导致安装等待。已停止本会话helper（不是游戏进程），修改v2 installer/worker为Get-LiveGenshin，只忽略明确HasExited的条目，无法查询状态则保守保留。随后手动Install成功。

- active DLL SHA256：`3D88BCD33CDDADCE892DDFE68A36E4FC880DE1DEB7230D3DD33AEA501851EE93`，与package一致。
- active INI：`[DLSS] NativeScreenSpaceGuides=true`，仅一个键；Dx11Upscaler=dlss。验证除该键外内容与退出时保存的baseline完全一致。
- baseline DLL/INI均核对哈希，退出时原配置保存到package baseline，实际同目录备份后缀：`.bak-nr-native-guides-v2-20261004-044219`。
- package：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-v2-20261004-043525`，manifest deployment已为Installed and hash-verified；install-after-exit.log已记录INSTALLED。主要脚本Install-NativeGuidesV2.ps1（Enable/Disable/Rollback/Validate）同样修正了退出进程检测。
- 尚无v2新游戏运行与复测；不要宣称方向/卡死/亮度实机已解决。下一次启动后检查native active日志、FG resize解析result和RenoDX合同，再看motion方向与depth-on画面变化。

## 最新 v2：构建已通过，等待游戏退出后安装（本机 2026-10-04 04:35）

用户要求继续修复。完成了保留 native guide转换的 v2，加入 borrowed resources/SRV及时释放及 DX11→FG resize参数纠正。当前安装状态以 package manifest / install-after-exit.log 为准；不要仅凭这一段声称部署完成。

- native原游戏 resources/SRV仅保留到当前 evaluate，成功/失败/异常退出均释放。现有WARP测试增加实际DXGI swapchain resize周期，旧版header在新断言下失败，新版通过。
- 额外发现 creation / resize不一致：DX12 FG创建时将 sRGB映射为UNORM、至少2 buffers，但旧 resize直接转发游戏的单buffer/sRGB/flags。新增 `OptiScaler/with_dx12/Dx11FgResize.h`，根据native实际GetDesc解析尺寸，BufferCount=0保留FG ring，Flags取FG自身，sRGB映射UNORM。两个resize入口都调用此helper，ResizeBuffers1没有real3时仅走一次base fallback；native resize成功后重置fake index；copy fence等待失败时不再释放/resize飞行中资源。
- `tests/dx11_fg_resize_gpu.cpp` 实际运行D3D11 WARP单buffer sRGB chain和D3D12 flip四buffer chain：旧原样转发复现E_INVALIDARG，新helper连续8次resize成功，保留flags/ring，含zero/UNKNOWN尺寸解析与格式/拒绝测试。期望旧版错误之后清掉debug消息，新版0警告/错误。native转换WARP测试同样0警告/错误。
- 最终 Release x64 / OptiScalerFgOnly=true 构建通过。包：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-v2-20261004-043525`。新DLL SHA256：`3D88BCD33CDDADCE892DDFE68A36E4FC880DE1DEB7230D3DD33AEA501851EE93`。包含实现、集成源码快照、测试、日志、README、VALIDATION.txt、Install-NativeGuidesV2.ps1。
- 用户原神仍运行（安装准备时PID15188）；已启动hidden helper PID64192，等待游戏正常退出（10分钟上限），再执行包内Install。不会终止游戏。若超时，用户退出后执行Install即可。安装瞬间重新备份当时的原DLL/INI，验证original DLL hash，保持用户退出时最新INI并仅启用一个NativeScreenSpaceGuides键，失败自动恢复baseline。
- 安装/回滚状态以 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-v2-20261004-043525\manifest.json` 和 `install-after-exit.log` 为准。开关Enable/Disable和Rollback只在游戏退出后执行。等待期间实际加载的仍为baseline DLL D24D3D9E...。
- 这是两项可复现代码缺陷修正，不代表真实Streamline/RenoDX resize或ACCESS_DENIED已验证解决。depth开启墙面黑白跳变也未证明修好；不擅自改用户NR motion比例、depth mode、tone/style参数。
- 下一实机确认：native active日志 / RenoDX negative-Y+R32_FLOAT合同，motion图方向，DX11 FG resize解析日志result成功，切分辨率之后无bridge lost，depth-on亮度和frame time。保留Bridge原始dump，不用其仍倒立作为native转换是否执行的依据。

## 新截帧视觉证据：f1116

用户反馈：motion 应该仍上下颠倒，天空隐约有倒着的黑影。已读取新抓取 `fsr2dump/f1116_color.png`、`f1116_depth.png`、`f1116_motion_mvdec.png` 与 `f1116_output.png`，生成 `comparison-f1116.png` 并实际查看。

- f1116 的内部 color 中人物倒立；depth 人物轮廓与 decoded motion 中人物轮廓同样倒立，三者行方向一致。因此不是仅 decoded motion 相对内部 color/depth 单独翻转，而是内部输入整组相对正常屏幕存在坐标方向差异。
- meta：render=1280×800、display=2560×1600、motion_scale=1280,800、reset=0；depth 原纹理 fmt19、motion 原纹理 fmt23。开启翻转的 native 实验 DLL 已回滚，当前 baseline DLL 哈希 D24D3D9E... 核对一致。
- output dump 是原游戏内部 output 纹理；Bridge 在当前 shim dispatch 前排队 dump（随后才进行 native DLSS dispatch），不应拿它当同次 evaluate 的最终屏幕/当前输出来证明像素对应。它也不是 Present backbuffer 截图。
- 倒着的天空黑影与 guide/Present 坐标错位相符，但不能凭此断言 motion 是唯一原因；depth/时序也未排除。不能单独翻转 motion 使 native DLSS 的 color/depth/motion 失去内部对齐。
- 对照图在 `D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\fsr2dump\comparison-f1116.png`。本轮没有部署或改动运行参数。

## 当前深度截帧配置（用户要求输出至 fsr2dump）

用户要求将深度图输出到 `D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\fsr2dump` 以检查方向。已经启用现有 Bridge dump，无需新 DLL：Fsr2InputDump=1，Frames=6，Raw/Png=1，AutoStartSec=0，Hotkey=122（F11），IntervalMs=200。用户启动/重启游戏、进入场景后按 F11 抓一轮；同帧 depth/color/raw motion/decoded motion/meta 一起保存。

INI 备份：`Dx11FsrBridge.ini.bak-depth-capture-20261004-041558`。之前 121 个截帧文件已保留到 dump 目录下 `previous-20261004-041558`，避免混淆旧图和新图。其他 Bridge 设置均保持原样。当前实验 OptiScaler 仍处于回滚状态，原 DLL 哈希已核对。

源码确认 dump_desc.depth=depth_tex，而随后的 native shim frame.depth=make_srv(depth_tex)，且 Fsr2TranslationLayer dispatch depth 使用 frame.depth 的原资源；因此此处是原始 native 提交深度来源。不是 RenoDX private D3D12 NR 内部转换后的截图。depth PNG 按 min/max 拉伸灰度用于形状/方向观察，跨帧 PNG 黑白不能直接代表原始深度值变化；raw 保存实际存储。新截图尚未产生（配置时游戏未运行），成功需日志 `fsr2_input_dump frame=... files=... dir=...`。

## 最新状态：native 修复版复测失败，已回滚（本机 2026-10-04 04:09 后）

本节覆盖下文“native 修复已部署 / 开关 true”。

- 用户报告：切换分辨率后画面卡死；另开启深度后画面动态变化很大，墙面一会黑一会白，非常不适。两个问题都未宣称修好。
- 已保存本次 OptiScaler/ReShade/Bridge 日志和配置到 `D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-20261004-035949\failed-resolution-20261004-040921`。
- 游戏已退出；已执行完整 rollback 并核对原 DLL/INI SHA256。当前 DLL `D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`，INI `BCC5B875F59019AEE602D7CD1E64A871DF3CCE6D275013C895393F42637EB892`。本次 native 开关随原配置恢复而撤销。没有改用户 ReShade depth/motion/tone 设置。
- 本次真实日志确认转换命中：OptiScaler 有 `NR NativeScreenSpaceGuides active`，输出 2560×1440，depth/motion 1280×720，depth 转成 fmt41，MV.Scale.Y=720→-720；RenoDX guide contract 同样记录 scale=1280,-720 和 R32_FLOAT depth。因此这次不是上轮“后端没命中”。
- 04:08:12：`Dx11wDx12SC ResizeBuffers: real OK but FG failed (80070057)`。04:08:13 RenoDX 报 private D3D12 device removed，reason=0x887a002b。Windows SDK winerror.h 确认为 `DXGI_ERROR_ACCESS_DENIED`。随后 FG 的 motion/depth shared handle open 持续失败，proxyBuffer=NULL。日志不支持直接把问题断言成 GPU TDR 或驱动版本问题。
- source review 发现 native converter 会跨 evaluate 保留原游戏 resources/SRVs，Output 可能是 swapchain buffer。已改为 evaluate 成功/失败/异常退出后释放所有借用游戏资源/SRVs，Prepare 失败也清理；只缓存自有转换纹理。
- 新测试对旧已部署 header 运行，明确失败 `game resource retained across frame`；新源码通过相同断言，并增加 hidden window 的真实 DXGI swapchain 连续 ResizeBuffers 及失败/异常回调场景。完整转换 WARP 测试通过，debug layer 0 警告/错误。candidate Release 构建通过。
- **这个生命周期缺陷已经修正，但还不能证明它导致/修复实机 FG resize 和 ACCESS_DENIED。** 日志中真实 swapchain resize 成功，FG resize 失败，不能简单等同于游戏 backbuffer 被本代码 pin 住。候选未部署，保持原版。
- 候选包：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-resize-candidate-20261004-041437`，含 DLL、源码、测试、构建日志和 VALIDATION.txt。没有覆盖之前失败版 package 的 DLL/源码，便于复现对照。
- 亮度跳变线索：本次 ReShade.log 记录 25 次 guide contract/surface rebuild，在 1280×720、negative-Y、low-res-MV 合同（13次）与 2560×1440、positive-Y、display-MV 合同（12次）之间交替。可能与用户切换 depth/guide 模式有关，不能未经验证归因于混入外部 Feed。Feed 旧日志未更新，用户明确不用 Feed。
- 下一步需确认合同交替的触发条件，以及 FG resize失败后设备/资源的重建与访问权限；必须覆盖真实 native NGX+RenoDX+FG resize，不能以 WARP 大小变化测试替代。深度关闭可作为用户临时使用方式，但这里没有自动改用户设置。

## 最新修复：native DX11 guide 转换已部署（本机 2026-10-04 03:59）

本节覆盖下文“当前保持回滚 DLL”的状态。用户明确授权“开始修复吧”后，已经完成 native DX11 实现、GPU 测试、最终构建、备份和部署。**游戏里的 motion 预览方向和 depth 闪烁尚待复测，不是已经确认解决。**

- 接入点：`OptiScaler-MFG-Ada/OptiScaler/upscalers/dlss/DLSSFeature_Dx11.cpp` 的 native NGX D3D11 evaluate 调用前后。实现：`NativeScreenSpaceGuides_Dx11.h`。仍为 `Dx11Upscaler=dlss`，没有切换到 DLSS_on12。
- 新开关：`[DLSS] NativeScreenSpaceGuides=true`，active INI 已开启；源码默认 false。旧 `[Dx11withDx12] ScreenSpaceGuides` 原型与本修复无关，active 原始配置没有开启它。
- 完整翻转 color/depth/decoded motion/reactive mask 行，保留运动分量，临时取反 NGX MV.Scale.Y / Jitter.Offset.Y；将 packed depth 转为 R32_FLOAT，保持 depth inverted 值域；native DLSS 输出保持原始尺寸和格式，执行后翻回游戏坐标。
- 实际 output 是 R8G8B8A8_UNORM_SRGB：新增专门 raw copy 路径，NGX/RenoDX 仍看到原 SRGB 格式，翻回时不额外进行 gamma 转换；兼容原输出没有 UAV bind。
- 返回前 RAII 恢复 original resource pointers、Y metadata、signed/unsigned Reset。FG 后续使用原始资源，保持现有 ResourceFlip。模式切换触发 history reset。
- 输入不支持时整体旁路，有原因和 HRESULT 日志。关键运行日志：`NR NativeScreenSpaceGuides active: native DX11 NGX`（首帧和每 300 次尝试），列出 depth/motion 指针、格式、尺寸及原/新 Y metadata；`bypassed` 不能当作修复已执行。
- 此实现没有修改窗口/swapchain 的创建/resize代码，Bridge、RenoDX DLL、Feed 和游戏 ReShade.ini 均未改。保留用户实际 NR motion multipliers 2/2，试验只改变坐标转换开关。
- 最终 Release x64 / OptiScalerFgOnly=true 构建成功，MSBuild exit 0。`tests/nr_native_guides_gpu.cpp` 在 D3D11 WARP 上执行实际生产转换并模拟 NGX callback：motion 行翻转、packed depth 解平面、FP16 负分量、alpha/mask、低/高分辨率 motion、SRGB output raw copy、奇数尺寸、cache/resize、成功/失败/异常恢复、signed/unsigned Reset、CS 状态恢复及输入拒绝全部通过；debug layer 0 警告/错误。这不验证真实 NVIDIA NGX/RenoDX。
- active DLL：`D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.8\OptiScaler.dll`；SHA256：`00224D6B74E4F3FBB6DCEF9599DD738BA386006A1362FCCA73CD9468F7FB50A8`。
- 包：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-native-guides-20261004-035949`。包含新 DLL、baseline DLL/INI、运行前游戏 ReShade.ini 副本、manifest、构建日志、GPU 验证记录、实现头与测试源码、README、`Set-NativeGuides.ps1`。
- 同目录旧 DLL/INI 备份后缀：`.bak-nr-native-guides-20261004-035949`。baseline DLL SHA256 为 `D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`，INI 为 `BCC5B875F59019AEE602D7CD1E64A871DF3CCE6D275013C895393F42637EB892`。
- 包内脚本 `-Mode Enable` / `-Mode Disable` 用于 B/A；`-Mode Rollback` 恢复 baseline DLL/INI。执行前退出游戏，每次重启；开关脚本已实测只新增/修改 NativeScreenSpaceGuides，其他原 INI 保留。
- 下一实机验证：先确认 OptiScaler active 日志，再看游戏 ReShade.log 的 native surface set 是否变为 R32_FLOAT depth、guide contract 的 Y scale 是否取反；用户观察运动图方向、depth on 闪烁、天空人物、转镜头和帧时间。若 active/bypassed 之外无新日志，应先查 DLL 加载与 native 后端，不能继续猜翻转符号。
- 详见 `OptiScaler-MFG-Ada/docs/NR-NATIVE-DX11-GUIDE-FLIP.md`。本会话没有启动游戏。

## 最新澄清及实际 motion 来源

用户澄清“还是反的”指运动图上下颠倒，不是游戏最终画面上下颠倒。窗口变小是另一项反馈，原因仍未确定。

实际运行 ReShade 日志位于 `D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.log`，不能用 Hub 模板或旧 add-on 日志代替。已复制到上述失败证据目录的 `ReShade-game.log` / `ReShade-game.ini`，并补存本次 Bridge 日志。

- ReShade 日志 121 行：RenoDX 在 native D3D11 会话中创建自己的 private D3D12 bridge，`NRHookPoint=2`，在 Present backbuffer 上运行 NR。
- 日志 201 行：NR 输入 surface set 的 output 为 1707×1067；motion 852×532 fmt34 (R16G16_FLOAT)；depth 852×532 fmt19 (R32G8X24_TYPELESS)，RenoDX 转换 depth 为 R32_FLOAT。
- 日志 212 行：motion base=0,0，window=852×532 of 852×532，scale=852,532 (game)；flag=0xa（low-res MV + inverted depth）。因此本次日志未表现出 motion crop 或尺寸声明矛盾；这不证明坐标方向正确。
- Bridge 本次日志确认 `fsr2_get_proc_address_shim_ready`、`fsr2_shim_result DISPATCH_OK hook=1`。真实路线是游戏输入 → Bridge `Fsr2TranslationLayer.cpp` → FSR2 detour → OptiScaler native DX11 DLSS → RenoDX native DX11 guide capture → RenoDX 私有 DX12 NR Present。
- `D:\CODE\genshin_fsr_brigde\Dx11FsrBridge\Fsr2TranslationLayer.cpp` 的 input prepare HLSL 将 decoded motion 按 `PreparedMotion[dispatchThreadId.xy]` 原位置写入，未翻转行；dispatch.description.motionVectors 使用 `g_prepared_motion`。depth 使用原始 `frame.depth` 的 resource。`Ffx12MotionFlipY` / `Ffx12DepthFlipY` 未进入该 shim 转换代码。
- OptiScaler native evaluate 之后另走 `UpscalerInputsDx11wDx12` 给 FG 提交 guide，DLSSG 的 ResourceFlip 修改的是 FG Streamline tags；不应据此推断 RenoDX native DX11 捕获的 guide 已翻转。
- 实际游戏目录 ReShade.ini：`DX11Source=native`、`NRHookPoint=2`、`NRPresentGuides=1`，且 `NRMVecScaleX=2` / `NRMVecScaleY=2`。Hub 的 `HoYoShade/ReShade.ini` 仅有 X/Y=1，与游戏运行配置不同。保留用户当前设置，不自动覆盖。
- 此轮仅查证/保存证据/更新交接，没有新部署。下一修复必须作用于实际 native 捕获/提交点；仅修改 Y scale 不能翻转纹理行，单独翻转 native DLSS 的 motion 则会破坏其与 color/depth 的坐标对齐。

## 最近复测：失败，已回滚（日志本机时间 03:31–03:32）

本节覆盖下文“已部署 / ScreenSpaceGuides=true”的状态。

- 用户复测反馈：没有效果，guide 仍然是反的，而且游戏窗口变小。
- 已保存失败运行的 DLL 配置与日志到 `OptiScaler-MFG-Ada/release/nr-screen-space-guides-20261004-031803/failed-run-20261004-033322/`。
- 已恢复本次部署前的原 DLL 和原 INI，并按 package manifest 验证两者哈希完全相同。当前 DLL：`D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`。原 INI：`BCC5B875F59019AEE602D7CD1E64A871DF3CCE6D275013C895393F42637EB892`。新增 ScreenSpaceGuides 开关已随原 INI 恢复而撤销。
- **关键纠错：当前实际后端是原生 D3D11 DLSS，不是修改的 DX11→DX12 upscaler 路径。** 失败日志 38 行仅证明读到了开关。1948 行创建 DLSS feature，1960 行 `DLSSFeatureDx11::DLSSFeatureDx11 binding complete!`，1961 行 `DLSSFeatureDx11::InitInternal Creating DLSS feature`。整个日志没有 `NR ScreenSpaceGuides active/bypassed` 或 Dx11wDx12 ratio diag。
- `FeatureProvider_Dx11.cpp` 中 `Upscaler::DLSS` 创建 `DLSSFeatureDx11`，只有 `Upscaler::DLSS_on12` 才创建已修改的 `DLSSFeatureDx11on12`。因此不能再把 INI 的 `Dx11Upscaler=dlss` 当成 DLSS-on-DX12，也不能以开关读取成功推断转换已发生。
- 失败日志 1402 行：03:31:22 创建 swapchain 1707×1067。1958 行：03:31:37 DLSS render 852×532、display 1707×1067。窗口尺寸在 DLSS feature 创建前已确定，新增 evaluate 翻转代码也未执行。窗口缩小原因尚未查明，不应断言翻转 shader 导致或已恢复窗口尺寸。
- 源码和 WARP 测试仅保留作 DX11→DX12 路径原型；该测试不证明原神实际加载路径或外部 RenoDX 输入经过此处。不要再次部署这份 DLL 作为已修复版本，也不要为了命中这段代码强行把游戏切到 DLSS_on12。
- 后续应沿实际 native D3D11 evaluate 和 FG 标签路径核对外部 RenoDX 最终消费的 guide 来源、坐标与尺寸；先确认提交/消费路径，再实现局部转换。当前没有启动游戏或修改其窗口/分辨率设置。

## 最新实施状态（2026-10-04 本机时间）

本节覆盖下文旧的 active OptiScaler 状态；Bridge / RenoDX DLL 与旧记录一致。

- 已在 DX11→DX12 的 DLSS evaluate 交接处实现屏幕空间输入转换，源码位于 `OptiScaler-MFG-Ada/OptiScaler/upscalers/Dx11ScreenSpaceGuides.h`、`Dx11ScreenSpaceGuideParams.h`、`IFeature_Dx11wDx12.cpp`。
- 开关：`[Dx11withDx12] ScreenSpaceGuides=true`。源配置默认 false，实际加载目录中的 INI 已开启 true。
- 统一翻转 color/depth/motion/reactive mask 行，保留全部分量（包括 alpha）和深度值域；NGX 的 MV.Scale.Y 与 jitter Y 取反；输出翻回游戏内部坐标。参数在返回 DX11 调用方前恢复。模式切换触发一帧 history reset。
- 不支持的 extent/subrect/format 或缺失 Y metadata 会整体绕过转换并记录 `NR ScreenSpaceGuides bypassed`。成功时记录 `NR ScreenSpaceGuides active`，含原始与 clone 指针、尺寸、格式及 Y metadata。
- FG 仍读原始 shared cache，保留原来的 ResourceFlip。没有修改 Feed、Bridge、RenoDX DLL 或 ReShade.ini。
- 已构建并部署到 `D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.8\OptiScaler.dll`。
- 新 DLL SHA256：`D7BDC02DA468A0C85EBEA4370E1BEE9C1E6A6537B1CEDE01598C641E4BDAC32A`。
- 原 DLL/INI 同目录备份后缀：`.bak-nr-screen-space-guides-20261004-031803`。原 DLL SHA256 仍为 `D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`。
- 可复现包：`D:\CODE\HoyoDLSS5\OptiScaler-MFG-Ada\release\nr-screen-space-guides-20261004-031803`，包含新 DLL、原 DLL/INI、manifest、构建日志、验证结果及 `Set-ScreenSpaceGuides.ps1`。
- A/B：游戏退出后执行包内脚本 `-Mode Disable`（A）或 `-Mode Enable`（B），每次重启；完整恢复旧 DLL/INI 用 `-Mode Rollback`。A/B 脚本已验证只修改新增开关，其他 INI 内容保留。
- 验证：Release x64 / OptiScalerFgOnly=true 构建成功；生产转换在 D3D12 WARP + debug layer 上通过 float32/float16/UNORM/typeless depth、负 motion 分量、alpha、奇数尺寸、输出往返、尺寸变化、双 slot，以及 subrect/metadata/format 拒绝测试。调试层 0 警告/错误。
- **游戏中闪烁是否解决尚未验证**，游戏未由本会话启动。接下来保持同一 RenoDX hook 与设置做 A/B，优先确认成功转换日志，再比较 depth off/on、转镜头闪烁、天空人物、果冻、画面方向与帧时间。
- 注意：开启开关会绕过原来的 1:1 DLSS 直拷捷径，确保 NGX evaluate 被调用以供外部 NR 捕获 guide；DLAA/1:1 下可能明显增加模型耗时。详情见 `OptiScaler-MFG-Ada/docs/NR-DX11-SCREEN-SPACE-GUIDES.md`。

## 1. 实际加载模块

- NR：`D:\APPS\HoYoShadeHub\Cache\games\hk4e_cn\Addons\renodx-dlss5.addon64`
- OptiScaler：`D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.8\OptiScaler.dll`
- FSR Bridge：`D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\Dx11FsrBridge.dll`

用户明确表示没有使用 `DLSS5_Feed.fx`。后续不要把 Feed 当作 NR 输入来源。

## 2. 用户最终观察

- NR 不使用深度时完全不闪烁。
- 开启深度后会严重闪烁。
- 运动输入仍然有问题。
- 修改 RenoDX 的“运动比例 X/Y”会影响现象，但那只是 motion 分量缩放/取反，不是整张 motion texture 的上下翻转。
- motion 比例调为 0 时，天空人物会消失。
- Streamline Hook 点和 Present Hook 点都试过，问题仍然存在。

## 3. Bridge 截帧证据

截帧目录：

`D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\fsr2dump\`

示例：

- `f200_color.png`
- `f200_depth.png`
- `f200_motion.png`
- `f200_motion_mvdec.png`
- `f200_output.png`
- `f200_meta.txt`

日志已经确认截帧成功：

`fsr2_input_dump frame=... files=10 dir=D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\fsr2dump`

视觉上 color、depth、raw motion、decoded motion、output 都相对屏幕上下颠倒。这证明 Bridge 输入存在统一的坐标空间差异，但不等于 RenoDX NR 一定直接消费了这些 Bridge 纹理。

Bridge 的 depth 行翻转和 motion 行翻转测试都没有解决 RenoDX NR，因此目前更像是 OptiScaler 转交给 RenoDX/NGX NR 时重新选择或转换了 guide。

## 4. 当前 active 文件状态

### Active Bridge

文件：

`D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\Dx11FsrBridge.dll`

当前 SHA256：

`DEB3B3C3542D37E8538CDF3AEE3979C4467E16B17248A49B50205EB64B70D49D`

这是加入诊断开关代码的 Bridge 构建，但当前配置已经恢复：

```ini
Ffx12GpuInterop=1
Ffx12AsyncUpscale=1
Ffx12DepthFlipY=0
Ffx12MotionFlipY=0
Fsr2InputDump=0
```

两个开关的语义：

- `Ffx12DepthFlipY`：翻转 depth 纹理行坐标，不改变逆深度值域；
- `Ffx12MotionFlipY`：翻转 motion 纹理行坐标，不改变 motion 分量。

当前 Bridge 配置：

`D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\Dx11FsrBridge.ini`

### Active OptiScaler

文件：

`D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.8\OptiScaler.dll`

SHA256：

`D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`

OptiScaler 当前日志显示：

`OptiFG.ResourceFlip: true`

因此不要默认认为 OptiScaler 没有翻转资源。

### Active RenoDX DLSS5

文件：

`D:\APPS\HoYoShadeHub\Cache\games\hk4e_cn\Addons\renodx-dlss5.addon64`

SHA256：

`DCD93881E976AD033D83C2BB01F4BC3E4DDC59C15FE0DD4CA165BC5FC7D1AC68`

没有修改这个 DLL。

当前 `ReShade.ini` 的 RenoDX 段已恢复为：

```ini
[RenoDX.DLSS5]
NRMVecScaleX=1
NRMVecScaleY=1
```

之前添加的 `NRHookPoint=2`、`NRPresentGuides=1`、显式 `EnableHooks=1` 已撤销。

## 5. 已撤销的错误方向

### DLSS5_Feed.fx

曾错误修改：

`D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Shaders\DLSS5_Feed.fx`

用户明确说没有使用 Feed，而且 Feed 会导致严重帧数损失。该文件已恢复原版。备份仍在：

`DLSS5_Feed.fx.bak-guide-texture-flip-y-20261004`

后续不要再改这个文件。

### OptiScaler 自带 DLSS-NR

曾错误创建的未完成文件已经删除：

`OptiScaler-MFG-Ada/OptiScaler/shaders/dlssnr/DlssNr_Dx12_DepthFlip.h`

本次问题不能直接归到 OptiScaler 自带 DLSS-NR。

## 6. 技术判断

用户最新证据表明：

1. Depth 开启后才闪烁，说明 depth + motion 的时空对齐失败；
2. `NRMVecScaleY=-1` 不是正确修复，因为它只做 `MV(x,y) -> MV(x,-y)`；
3. 真正需要检查的是 motion/depth resource 的行坐标、SRV/clone、subresource、尺寸和坐标变换；
4. Bridge 的 FSR 输入上下颠倒不代表 RenoDX NR 直接使用同一份 resource；
5. OptiScaler 的 `ResourceFlip=true` 已生效，但 RenoDX NR 仍有问题；
6. 下一步应追 `OptiScaler -> DLSS/NGX NR` 的 guide/resource 交接，而不是继续改 Feed。

## 7. 推荐下一步

### A. 在 OptiScaler NR 输入提交点做一次性诊断

重点源码：

- `OptiScaler/dlssnr/DlssNr_Proxy.cpp`
- `OptiScaler/dlssnr/forwarder/dlssnr_forwarder.cpp`
- `OptiScaler/dlssnr/DlssNr_Dx12*.cpp`
- `OptiScaler/inputs/` 中 DLSS/Streamline input copy/flip 代码

需要记录：

- NR evaluate 使用的 Depth resource 指针；
- NR evaluate 使用的 Motion resource 指针；
- resource desc、format、width、height；
- resource subresource/array/slice；
- `ResourceFlip` 产生的 clone resource 指针；
- upscaler 使用的 input resource 指针；
- `DLSSNR.MVecScaleX/Y`；
- depth/motion 是否来自同一帧、同一套坐标空间。

### B. 严格 A/B

每次只改一项：

1. NR depth off + motion on；
2. NR depth on + motion on；
3. `ResourceFlip=false`；
4. `ResourceFlip=true`；
5. 只对 NR clone 做纹理行翻转；
6. 不改 motion sign；
7. 不改 `InterpolationCount`。

验收：

- 正对墙移动不闪；
- 转视角不闪；
- 天空人物不消失；
- 无果冻；
- 帧率不明显下降。

## 8. 回滚点

Bridge：

- `Dx11FsrBridge.dll.bak-motion-rowflip-20261004`
- `Dx11FsrBridge.dll.bak-20261004-depth-yflip`
- `Dx11FsrBridge.ini.bak-after-capture-20261004`

ReShade：

- `ReShade.ini.bak-nr-hook-present-20261004`
- `ReShade.ini.bak-nr-present-guides-required-20261004`
- `ReShade.ini.bak-restore-mv-scale-20261004`

核心结论：**不要继续改 `DLSS5_Feed.fx`。下一步直接检查 OptiScaler 传给 RenoDX DLSS5 NR 的 Depth/Motion resource 是否发生了行坐标错位。**
