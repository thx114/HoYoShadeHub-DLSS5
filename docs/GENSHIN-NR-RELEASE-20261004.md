# 原神 NR/FG 发布（2026-10-04）

组件：OptiScaler MFG Ada0.1.9，Bridge2.3.2（v2.3.2-fg-20261004），原神6倍覆盖包2.5。启动器1.4.1.3识别Bridge新小版本，原神激活profile后开启支持版本NativeScreenSpaceGuides，并保留HoYoShade正常启动。ReShade2.ini不重复加载Present插件，配置同步幂等。

OptiScaler发布保留native整组color/depth/motion坐标修正、输出翻回、FG共享输入CPU退休同步、DX11→DX12 flip resize参数修正、菜单fence/设备队列重建/字型上传失败处理。Bridge按真实FSR实例隔离native上下文和临时纹理，创建参数变更重建对应实例。失败的共享GPU Wait优化明确排除。

用户确认NR闪烁消失且深度/运动匹配；CPU退休版的一轮反复C、活动、进房间/切地图日志无error/hung/等待失败并正常退出。低原始帧率及NR两pass成本/切换长帧仍在，不宣称普遍性能提升或所有机器无限次切换保证。

验证：native guide、packed depth/sRGB/参数恢复、真实DXGI resize、ImGui字型分配失败及菜单fence、双API共享texture CPU retirement、Bridge两实例40次交替及合同重建GPU测试通过；启动器扩展787项0失败；原神2.5覆盖包真实LocalPackageInstaller落位/归档通过。组件ZIP按SHA256清单验证且远端资产digest对应本地文件。

独立OptiScaler公开包仅含本DLL、INI、许可证与官方运行库下载器/原神预设；Bridge公开包仅含DLL/INI/许可证。覆盖包沿用既有游戏模型/SDK布局，本地更新为2.5。升级退出游戏，保留旧DLL/INI与应用目录；原神preset的NativeScreenSpaceGuides=true仅在原神使用，其他游戏默认false。
