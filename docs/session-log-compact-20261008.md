# 会话日志瘦身：`session.txt` v2 + `digest.txt` + `config-compact.txt`（2026-10-08）

一局游戏的快照现在产出三份**给人/AI 读**的紧凑文件，几 MB 的原始日志与注释占七成的配置都不必整份读。

## 为什么做

| 事实 | 数据 |
| --- | --- |
| 一局日志原文 | bridge 0.1～3.6 MB / OptiScaler 0.5～2.9 MB / ReShade 366～410 KB / Hub 570 KB |
| 一局配置原文 | `OptiScaler.ini` 59 KB（1906 行里 1200 行是注释）+ bridge ini 17.5 KB + ReShade ini 11 KB |
| 直接丢给 AI | 约 **45 万 token**，且 99.9% 是逐帧重复行或注释 |
| 旧 `session.txt`（v1） | 每次快照把「文件名 + 完整绝对路径 + 大小 + 修改时间」整表重抄：实测 125 行里只有 **74 行唯一**，16.5 KB 里九成是重复 |

目标：**写入侧就小**（不是让人去手动挑文件），AI 读快照目录时只需要读
`session.txt` + `digest.txt` + `config-compact.txt`。

## 改了什么

| 文件 | 说明 |
| --- | --- |
| `src/HoYoShadeHub.Extensions/Diagnostics/SessionLogManifest.cs` | 新增。`session.txt` v2 紧凑写入器：路径基 + 文件目录 + **只写变化项**的快照行 |
| `src/HoYoShadeHub.Extensions/Diagnostics/LogDigest.cs` | 新增。日志摘要器（分级/归一化/合并/末尾原文/预算）+ 配置紧凑视图（去注释，键值全留） |
| `src/HoYoShadeHub/Features/GameLauncher/GameSessionLogCollector.cs` | 接线：清单改走 v2 写入器；收尾时生成 `digest.txt` 与 `config-compact.txt`，并在 `session.txt` 末尾补一行索引 |
| `src/HoYoShadeHub.Extensions.Tests/Program.cs` | 新增 24 项断言（含真机 `session.txt` 的前后对比回归、末尾原文不被警告刷屏挤掉） |

纯逻辑放在 `Extensions`（app 已引用该程序集），所以自测套件能直接覆盖，不必跑起 WinUI。
清单与摘要**都不抛异常**：写失败只记 Hub 日志，绝不影响启动 / 退出 / 注入。

## `session.txt` v2 长什么样

真机产出（`collectlogs` 端到端跑出来的一份，**1430 字节**）：

```
HSH-SESSION 2
legend: L=日志 C=配置 @N=路径基 s=快照 id=字节 +id=新出现 miss=缺失
t0=2026-10-08 11:08:34.254+08:00
pid=0 exe=manual game=hk4e_cn hub=1.0.0
dir=C:\Users\thx11\AppData\Local\HoYoShadeHub
log=C:\Users\thx11\AppData\Local\HoYoShadeHub\log
sess=...\log\sessions\20261008-110834_manual_0
mod OptiScaler 0.2.1.0 @0\mfg-ada-0.1.9\OptiScaler.dll
@0=D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada
@1=D:\APPS\miHoYo Launcher\games\Genshin Impact Game
L 01 shade-ReShade.log @1\ReShade.log
L 04 opti-mfg-ada-0.1.9-OptiScaler.log @0\mfg-ada-0.1.9\OptiScaler.log
C 07 cfg-ReShade.ini @1\ReShade.ini
s 0 +0.1 手动导出
 01=+375283 02=miss 03=+56874 04=+730660 06=+1658 07=+5290 09=+60488
s 1 +0.1 收尾：手动导出
 06=1948
```

规则：

- **路径基**：开头 `@N=...` 声明一次，正文只写 `@0\...`（贪心算法按「省下的字节 − 多一行声明」选，收益为负就不选）。
- **文件目录只列一次**：每条给一个 2 位 id，后面全部按 id 引用。
- **快照只写变化**：`s <序号> +<相对秒> <原因>`，第二行才是变化项；`id=字节`、`id=+字节`（新出现）、`id=miss`（缺失/消失）。没变化就只占一行。
- **不再逐行写修改时间**：快照自己的时间戳已经表达了先后；旧格式里这一列纯属重复。

## `digest.txt` 长什么样

```
== HSH-DIGEST v1 2026-10-08 11:08:34 ==
sess=20261008-110834_manual_0 game=hk4e_cn hub=1.0.0 pid=0 exit=?
files=4 in=1.11M out=26.5k (-97.7%) E=51 W=572
idx opti-mfg-ada-0.1.9-OptiScaler.log 712.0k/4990L E25 W110
idx shade-ReShade.log 366.1k/1928L E11 W459
idx shade-RenoDX-DLSS5-crash.log 55.4k/1047L E14 W0
idx HoYoShadeHub_261008.log 1.9k/26L E1 W3
## opti-mfg-ada-0.1.9-OptiScaler.log 712.0k/4990L 10:14:22-10:29:59 E=25 W=110 O=3516
E(fail) 1x 10:14:26 | [I] DxgiFactoryHooks::CreateSwapChainForHwnd Failed to get ID3D12CommandQueue from pDevice, creating Dx11 swapchain!
E(error) 3x 10:14:52 | #[ngxLog] ... Error: NGXLoadFromPath failed for \OptiScaler: 0x#
W(not found) 20x 10:14:29 | ... SnippetLocationInfo::load:#] Module not found at \streamline
TAIL 10
  2026-10-08 10:29:59.531 [INFO] [depth_provider] publishing depth to ReShade
  ...
```

规则：

- `E(...)` / `W(...)` 括号里是**判级的依据**（级别标签，或命中的关键词）——长行会被截断，不标出来就看不出凭什么判成错误。
- 同形状的行归一化后合并计数：时间戳、句柄地址 `0x#`、GUID `{guid}`、绝对路径只留文件名、长数字 `#` 全部折叠。
- 归一化后会剥掉长行开头的纯元数据层（`[I] SL Log: [10-14-30][streamline][info][tid:#][3s:#ms:#us]commonEntry.cpp:` …），否则真正的错误正文会被挤到截断线之外。
- `O=` 是「其它行」计数，**只计数不留内容**；末尾另附 10 行原文（崩的那一下永远在尾巴上）。

## `config-compact.txt` 长什么样

配置原文里注释能占七成（`OptiScaler.ini` 60 KB / 1906 行 → 14 KB / 708 行），而 AI 要的只是键值：

```
== HSH-CONFIG v1 2026-10-08 11:35:25 ==
sess=… game=hk4e_cn hub=1.0.0 pid=0 exit=?
cfgs=6 in=70.1k out=24.4k (-65.2%) 注释/空行/连续重复已去，键值全留；原文见同目录 cfg-*
idx cfg-mfg-ada-0.1.9-OptiScaler.ini 59.1k/1906L → 13.8k/708L
## cfg-mfg-ada-0.1.9-OptiScaler.ini 59.1k/1906L → 13.8k/708L
[Upscalers]
Dx11Upscaler=fsr2
…
```

去的是：整行注释（`;` `#` `//`）、空行、连续重复行、行内注释、`key = value` 两侧空白。
留的是：**所有键值**（不做「默认值猜测」，猜错比多几百字节更贵）。原文照旧全量在同目录，人工核对不受影响。

## 实测（本机真数据）

以**最新一局真机会话** `20261008-112820_YuanShen.exe_34188` 为准：

| 对象 | 之前 | 之后 | 省 |
| --- | --- | --- | --- |
| `session.txt` | 11 293 B（v1，91 行 / 3 次快照） | **1 841 B**（v2） | **−83.7%** |
| 5 个日志 1 715 045 B | 整份丢给 AI | **18 036 B**（`digest.txt`） | **−98.95%** |
| 6 个配置 89 938 B | 整份丢给 AI | **27 059 B**（`config-compact.txt`） | **−69.9%** |
| **AI 要读的合计** | **1 816 276 B**（≈45 万 token） | **46 936 B**（≈1.2 万 token） | **−97.4%** |

`collectlogs` 端到端另一份：日志 1 000 933 B → `digest.txt` 15 986 B（E=57 W=1192）；
配置 71 780 B → `config-compact.txt` 25 860 B；`session.txt` 约 1.4 KB。

摘要里保留下来的正是要看的东西：那局 bridge 的 47 572 行里只有 `crash` ×6（`ntdll.dll` 里崩 6 次）、
`device_call after_migoto hr=0x#` ×1、`no d3d11 create import hooks found` ×1；OptiScaler 那边留下了
`DxgiFactoryHooks::CreateSwapChainForHwnd Failed to get ID3D12CommandQueue from pDevice, creating Dx11 swapchain!`
和 5 条 `nvLoadSignedLibraryW() failed`。

## 判级规则（这是摘要可信度的关键）

1. 有明确级别标签就信标签：`[ERROR]/[E]` → E，`[WARN]/[W]` → W。
2. 否则按**词边界**匹配强关键词（error / fail / fatal / exception / crash / assert / denied / 失败 / 异常 / 崩溃 …）→ E。
3. 「线索型」词（not found / missing / invalid / unsupported / cannot / unable / 未找到 / 无法 …）→ 降一档记 W。
4. `xxx=0` / `xxx: 0` 说的是「没发生」（`exception=0`、`failed=0`），**不算错误**。
5. 版本一路调下来，同一份日志的 E 从 384 → 51，`E=51` 里每一条都站得住。

## 预算与「绝不静默丢信息」

- 单文件日志摘要 ≤ 12 KB（其中 **末尾原文先占位**、W 形状 ≤ 30% 预算且 ≤ 40 条），整份 ≤ 48 KB，单文件 E 形状 ≤ 120 条。
- 单文件配置紧凑视图 ≤ 16 KB，配置合计 ≤ 48 KB。
- 撞上限时**显式写** `… 省略 N 条形状（预算/条数上限，看原文件）…` / `… 省略 N 行（注释/空行/连续重复/预算）…`，末尾原文被裁也一样标明。
- 完整日志与配置原文照旧全量抄在同一目录，人工核对时不受影响。

**为什么末尾原文要单独占位**：第一版是「E/W 形状先写满、末尾原文捡剩下的」，真机上 1073 条 `not found`
警告把 `OptiScaler` / `ReShade` 的末尾原文挤到只剩 51 字节 —— 崩的那一下反而没了。
改成先算末尾原文的字节数并预留、W 单独限额之后：摘要 29.3 KB → 18.0 KB，末尾原文全部回来。

## 已知限制

1. 第三方日志（OptiScaler / ReShade / Bridge）里的 GBK 中文会乱码：摘要是按 UTF-8 读的。要保留就在 `LogDigest` 里注册 `CodePagesEncodingProvider` 再 `Encoding.GetEncoding(0)`。Hub 自己写的日志是 UTF-8，不受影响。
2. `digest.txt` 在会话收尾时生成一次；进程被强杀就没有这一份（此时只能看 5 分钟定时快照抄下来的日志副本）。
3. `E/W` 是**关键词+标签**的机械判定，不是语义理解：它保证「可疑的都留着」，不保证「留下的都重要」。

## 怎么验证

```powershell
# 1) 自测（含真机 session.txt 前后对比、真机 bridge 日志压缩、配置紧凑视图）
.dotnet10\dotnet.exe run --project src\HoYoShadeHub.Extensions.Tests -c Release
#    → PASS 916 / FAIL 0，其中会打印（数字跟着本机最新一局走）：
#    清单：v1 11293 字节（91 行/3 次快照）→ v2 1841 字节（省 83.7%）
#    实测 bridge-Dx11FsrBridge.log：111238 字节 → 1276 字节（E=7 W=1）

# 2) 端到端（真起一次导出，不需要游戏在跑）
src\HoYoShadeHub\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\HoYoShadeHub.exe collectlogs --biz hk4e_cn

# 3) 摘要质量目视（临时工具，在 .build-temp 下）
.dotnet10\dotnet.exe run --project .build-temp\digest-peek\digest-peek.csproj -c Release
```

> 部署提醒：这些改动只在**仓库构建**里。装在 `D:\APPS\HoYoShadeHub` 的那份还是旧版，
> 它写出来的 `session.txt` 仍是 v1、也不会生成 `digest.txt` —— 要真机生效得把新构建发布过去。

## 后续可选（没做，留个口子）

- 摘要加**时间窗**：只压「游戏跑起来之后」那一段，去掉启动前的噪音。
- 配置里再区分「默认值/非默认值」：现在键值全留（不做默认值猜测），
  但 `OptiScaler.ini` 的 708 个键里真正常改的就几十个，有默认值表就能再省一半。
- UI 上直接给一个「导出给 AI」按钮：只带 `session.txt` + `digest.txt` + `config-compact.txt`。
- 把 `digest.txt` 里 E>0 的文件名回写到 Hub 日志开头，工单第一眼就能看到。
