# HoYoShadeHub 命令行

用**根目录的 `HoYoShadeHub.exe`**（便携启动壳）即可：它会自动把命令转给最新 `app-*` 目录里的真身，**等命令跑完并回传退出码**，子进程输出直接显示在你的控制台里。也可以直接调 `app-*\HoYoShadeHub.exe`。

`run` 走完整插件注入管线（等同启动页启动，勾选什么注什么），无弹窗、无同意流程（命令行本身就是授权）。退出码：0 全成功，1 有步骤失败 / 参数不对。

## 启动 / 结束游戏

```bat
:: 启动原神（按启动页勾选的插件/模块/OptiScaler 注入）
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe run --biz hk4e_cn --json "{\"steps\":[{\"action\":\"launch_game\"}]}"

:: 结束星铁
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe stopgame --biz hkrpg_cn

:: 一条命令完成：启动 → 等待 60 秒 → 关闭（适合自动读取 ReShade 日志）
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe run --biz hk4e_cn --json "{\"steps\":[{\"action\":\"test_game\",\"seconds\":60}]}"
```

游戏代码（--biz）：`hk4e_cn` 原神国服 · `hkrpg_cn` 星铁国服 · `nap_cn` 绝区零国服

> **管理员（UAC）**：`run` / `stopgame` 等含启动、结束游戏步骤的动词，检测到未以管理员运行时
> 会自动弹 UAC 提权重跑，子进程的输出通过临时接力文件**实时**回显在你的控制台里，
> 退出码也是子进程的真实退出码。取消 UAC 授权则什么都不执行，退出码 1。
> `rpc` / `playtime` 等不碰游戏的动词不弹 UAC。

cmd 里 JSON 要整段包 `\"`（如上行）；PowerShell 里用单引号包整段。步骤多时建议写文件：

```bat
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe run --biz hk4e_cn --file steps.json
```

## 自动进入界面（可选）

```json
{"steps":[{"action":"launch_game"},{"action":"wait","seconds":20},{"action":"click_game","x":0.5,"y":0.92},{"action":"press_key","key":"enter"},{"action":"wait","seconds":30},{"action":"stop_game"}]}
```

## run 动词

```
HoYoShadeHub.exe run --biz <游戏代码> --file <动作.json>
HoYoShadeHub.exe run --biz <游戏代码> --json "<动作 JSON>"
```

JSON 三种形态等价：`{"steps":[...]}` / `{"actions":[...]}` / 裸数组 `[...]`。
每个动作：`{"action":"<名>", "enabled":true, ...动作参数}`；也可用命名动作包 `{"name":"...","run_on_launch":true,"steps":[...]}`。

## 常用动作

| action | 作用 |
|---|---|
| `launch_game` | 启动游戏（完整注入管线，含 HoYoShade/模块/OptiScaler/帧解锁） |
| `stop_game` | 结束游戏进程（先 CloseMainWindow，5s 后强杀） |
| `wait` | 等待一段时间；参数 `milliseconds`/`ms`/`duration_ms` 或 `seconds` |
| `click_game` | 激活游戏窗口并点击客户区；`x`/`y` 可用归一化坐标（默认 `0.5,0.92`） |
| `press_key` | 向游戏窗口发送按键；例如 `key=enter`/`space`/`escape` |
| `test_map` | 启动 → 等待加载 → 点击 → 按键 → 等待 → 关闭；默认等待 50/30 秒 |
| `test_game` | 一步完成启动 → 等待 → 关闭；等待参数同 `wait`，默认 60 秒 |
| `set_dx12` | 切换 DX12（参数 `enabled`） |
| `set_smooth_motion` | AI 插帧开关 |
| `set_launch_option` | 启动项（`key` = `plugin`/`module`/`opt`/`fps_target`/`start_argument`/`use_popup_window`… + `enabled`） |
| `set_opt` / `set_module` / `set_addon` | OptiScaler / 模块 / 插件开关 |
| `import_preset` / `apply_preset` | 预设导入 / 应用 |
| `switch_dll` | DLL 版本切换 |
| `set_inject_mode` / `set_inject_delay` / `set_cmd_launch` / `set_cmd_args` / `set_game_setting` | 其余启动设置 |

## 其它动词

```
HoYoShadeHub.exe startgame --biz hk4e_cn   :: 裸启动游戏进程（不带任何插件）
```

## 日志快照（每局一份）

```bat
:: 把「当前这一局」的日志抄一份出来（Bridge / OptiScaler / ReShade / Hub），并打印目录
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe collectlogs --biz hk4e_cn
D:\APPS\HoYoShadeHub\HoYoShadeHub.exe collectlogs --pid 43980
```

- 走启动页 / `run` 启动的游戏**本来就会自动快照**到
  `<启动器目录>\log\sessions\<时间>_<进程名>_<pid>\`：启动瞬间记状态 + 抄配置、启动后 45 秒、
  之后每 5 分钟、进程退出各抄一次（进程被强杀 / 卡死也留得下 5 分钟内的现场）。
- `collectlogs` 是给「游戏由官方启动器启动、Hub 没参与」的情况兜底：一条命令拿到当前一局。
- 每个文件夹里有 `session.txt`（源路径、大小、修改时间、Bridge/OptiScaler 版本）+ 抄来的
  `bridge-*.log` / `opti-*.log` / `shade-ReShade.log` / 各 ini / Hub 当天日志。
- 为什么必须这样：**Bridge、ReShade、OptiScaler 都是每次启动重写自己的日志**
  （OptiScaler 是 basic_file_sink truncate，Bridge 是 ini `truncate_on_start=1`），
  让客户手动挑文件必然把不同几次运行混在一起 —— 实测两起工单都发来了上一次运行的 OptiScaler.log。
- 保留最近 15 局、单文件最多 16 MiB、总量 256 MiB，超了自动从最旧的删。

完整动作词汇见 `src/HoYoShadeHub/Features/Plugins/LauncherActionRunner.cs` 顶部分发；JSON 解析见 `HoYoShadeHub.Extensions/ReShade/PackAutoAction.cs`。每次 CLI 调用都会写日志：`D:\APPS\HoYoShadeHub\log\HoYoShadeHub_当天日期.log`。

