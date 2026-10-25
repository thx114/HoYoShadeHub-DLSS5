# HoYoShade Hub 完整包构建

本文记录仓库内 Full 便携包的正式构建方式。启动器和应用必须由仓库脚本构建；不要手工复制旧的 `HoYoShadeHub.exe` 或 `app-*` 目录来代替构建。

## 构建链

入口脚本是仓库根目录的 `package-full.ps1`。它会先调用 `package.ps1 -SkipZip`，由该脚本使用 `dotnet publish` 发布主应用和外层单文件启动器，然后把 HoYoShade 框架及必要资源加入输出目录，最后创建 Full ZIP。基础发布不再额外压缩普通便携 ZIP。

`build.ps1`、`build-local.ps1` 和 `publish.ps1` 是其他构建/发布流程，不是本仓库 Full ZIP 的入口。Full ZIP 需要运行 `package-full.ps1`，不要只运行 `package.ps1`，后者只生成普通便携包。

## 前置条件

- 在仓库根目录运行 PowerShell。
- 使用符合仓库 `global.json` 的 .NET 10 SDK（`10.0.101`，允许 latestFeature）。`package.ps1` 会自动查找 SDK；本机复用 `C:\Users\thx11\.dotnet10\dotnet.exe`，不要为打包复制新的 SDK。
- HoYoShade 源目录必须包含 `ReShade64.dll`。默认探测 `HYSHADE_SHADE_SOURCE`、仓库内 `HoYoShade`、`D:\APPS\HoYoShadeHub\HoYoShade`；也可以通过 `-ShadeSource` 明确指定。
- 不要把用户状态目录、日志、插件或截图当作框架源文件的一部分。Full 脚本会按其实现筛选框架内容，并在输出包根写入 `.portable` 和 `portable-layout.json`。

## 构建命令

在 PowerShell 中：

```powershell
Set-Location 'D:\CODE\HoyoDLSS5'
$env:HYSHADE_DOTNET = 'C:\Users\thx11\.dotnet10\dotnet.exe'

# 使用已核对存在 ReShade64.dll 的本机 HoYoShade 源目录
.\package-full.ps1 `
    -Version '1.4.6.14' `
    -Architecture x64 `
    -Configuration Release `
    -ShadeSource 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShade' `
    -DisabledAddons ''
```

发布新版本时只需把 `-Version` 改为目标版本。`-DisabledAddons ''` 会清空随包 `ReShade.ini` 里的本机插件禁用状态，避免把构建机的开关状态预置给用户。Full ZIP 默认使用 `build/full-<版本>/HoYoShadeHub` 作为暂存目录，输出到：

```text
<ReleaseDirectory>/HoYoShadeHub_Portable_<版本>_x64_Full.zip
```

`-ReleaseDirectory` 可选；省略时输出到 `build/release`。仓库磁盘空间不足时可指定其他磁盘上的目录。

当前机器已核对的框架源是上例中的 test/HoYoShade。脚本只提取框架及资源，不携带 test 的用户状态、插件或下载缓存。

## 空间规则（2026-10-25 用户要求）

每次打包前删除之前的打包占用，不能逐版本积累。成功后只保留本次最终 ZIP、校验 JSON 和小型日志，立即清掉本次解包暂存。Full 脚本通过 `tools/package-storage.ps1` 自动执行：

- 清理仓库 `build` 下的 `full-<版本>`、`portable-<版本>`、`HoYoShadeHub`、旧 `portable-launcher` 暂存目录，以及默认或指定 release 目录内的 `HoYoShadeHub_Portable_*.zip` / `.7z` 和同前缀解包目录。
- `-SkipBaseBuild` 仅在打包前保留明确指定的待复用暂存，打包结束仍清理该暂存。不要把运行中的用户安装或唯一框架源当作暂存目录。
- 删除失败则移动到 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup\HoyoDLSS5-package-<时间>-<标识>`；原路径、逻辑字节数、去向和错误写入 `build/package-storage-cleanup.json`。同盘转移只减少工作区占用，不能宣称释放该磁盘空间。删除和移动均失败时中止并报告。
- 路径必须处于核准范围，拒绝重解析点；不清理 `.git`、SDK、`_research` 的研究源码、源码 `bin/obj` 构建缓存、test 安装、覆盖包或历史取证记录。截图中 `_research` 与 SDK 的占用不计作已清理的打包临时文件。
- 临时解包校验使用流式读取，不再完整解压第二份。逐文件哈希核验与必要结构检查通过后，记录最终 ZIP 大小、SHA256、条目数到 `<Full.zip>.verification.json`。

运行其他打包入口时也必须遵守上述清理要求，不能留下旧打包副本。规则摘要同步在 `AGENTS.md`；本次修改前备份位于 `docs/memory-backups/20261025-151952`。

## 构建后核验

脚本会核验 ZIP 中的必要路径，包括 `version.ini`、`portable-layout.json`、外层启动器、版本化应用、HoYoShade 的 `ReShade64.dll`、`inject.exe` 和 `ReShade.ini`。完成后另外读取每个 ZIP 条目，以检测压缩数据是否可完整解压，并计算文件大小与 SHA256：

```powershell
$zip = (Resolve-Path 'build\release\HoYoShadeHub_Portable_1.4.6.14_x64_Full.zip').Path
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $stream = $entry.Open()
        try {
            $buffer = New-Object byte[] 65536
            while ($stream.Read($buffer, 0, $buffer.Length) -gt 0) { }
        }
        finally { $stream.Dispose() }
    }
    $archive.Entries.Count
}
finally { $archive.Dispose() }

Get-Item -LiteralPath $zip | Select-Object FullName, Length, LastWriteTime
Get-FileHash -LiteralPath $zip -Algorithm SHA256
```

只有脚本成功退出、条目完整读取无异常，并记录了实际输出的文件大小和 SHA256，才能报告 Full 包构建与 ZIP 校验完成。构建成功不代表已上传、部署或通过真实游戏运行测试。

## 历史工作区记录

截至 2026-10-20，`package-full.ps1` 已支持 `-ReleaseDirectory`。版本 `1.4.6.13` Full ZIP 已由脚本构建并校验，输出位于 `build/release/HoYoShadeHub_Portable_1.4.6.13_x64_Full.zip`，大小 `339315491` 字节、2000 个条目，SHA256 为 `A39C449E7DD8B0799ACEED2AA94B60DE4BCD4275D71A2E2E38401B8749C1F60B`。该构建已解压部署到 Downloads 下同名目录；没有进行真实游戏运行测试。

## 本次成品（2026-10-25）

- Full ZIP：`D:\CODE\HoyoDLSS5\build\release\HoYoShadeHub_Portable_1.4.6.14_x64_Full.zip`，339300085 字节（323.6 MiB），2014 个条目。
- SHA256：`C8FCB838F61F9DBA4CB671860A20FC72AC15D8DEA5235D20BE35D08EA5D79097`。
- 由正式脚本重新 publish 主程序和外层启动器；包内包含当日 NR 默认库记账修复、当前指纹种子、HoYoShade 框架及资源。插件目录按标准 Full 口径留空，未混入 test 的数据库、游戏选择、下载缓存或插件。
- 逐条 SHA256 与暂存源一致，最终包结构、版本入口及便携标记检查通过。校验记录：同目录 `<Full.zip>.verification.json`；构建日志：`artifacts/package-full-1.4.6.14-20261025.log`。
- 实际删除旧 `build/portable-launcher`（73612644 逻辑字节）及空的旧 TestedStack 暂存；本轮 604887019 逻辑字节的解包暂存也已删除。没有生成普通便携 ZIP，没有遗留 `build/full-1.4.6.14`。本轮清理均成功，未使用 cleanup 转移；字节数不等同于硬链接/压缩情况下的物理释放量。
- 清理边界与删除失败转移行为的 8 项检查通过，包含范围外路径、范围根目录和 junction 拒绝，以及模拟删除失败后的实际移动。日志：`artifacts/package-storage-tests-20261025.log`。没有启动启动器或游戏，也未上传发布。
