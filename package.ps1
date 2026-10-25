<#
.SYNOPSIS
    通用构建与打包脚本：发布主程序并打成便携版（含 zip）。

.DESCRIPTION
    产出便携版目录结构：

        <Output>\
        ├─ HoYoShadeHub.exe       外层启动器（自包含单文件，双击入口）
        ├─ version.ini            exe_path=app-<版本>\HoYoShadeHub.exe
        └─ app-<版本>\            主程序（dotnet publish，自包含、ReadyToRun）

    同时生成 zip：
        build\release\HoYoShadeHub_Portable_<版本>_<架构>.zip

    注意：
    - zip 使用 .NET ZipArchive 逐条写入，条目名强制使用正斜杠。
      Windows PowerShell 5.1 的 Compress-Archive 会写入反斜杠，严格的解压工具
      （如 Windows 资源管理器）会判定压缩包损坏，因此这里不使用 Compress-Archive。
    - 便携包根目录故意不包含 config.ini，避免解压覆盖时冲掉用户已有配置；
      缺失时由外层启动器自动创建。
    - 外层启动器使用本仓库 C# 复刻的 PortableLauncher（无需 Visual Studio C++ 工作负载）。
    - 自动查找 .NET 10 SDK，规则与 compile.ps1 相同。

.EXAMPLE
    .\package.ps1 -Version 1.3.6
    Release / x64 发布 1.3.6 便携版并打 zip。

.EXAMPLE
    .\package.ps1 -Version 1.3.6 -Output build/portable-1.3.6/HoYoShadeHub
    指定干净的输出目录（推荐，避免历史目录混入 zip）。

.EXAMPLE
    .\package.ps1 -Version 1.3.6 -Architecture ARM64
    构建 ARM64 版本。

.EXAMPLE
    .\package.ps1 -Version 1.3.9.1 -PortableLocal
    「真便携」包：包根多一个 .portable 标记，缓存 / config.ini / 数据库备份全在包内（<包根>\.cache），
    一个字节都不写 C: 盘。不传这个开关就是老行为（缓存仍然走 %LOCALAPPDATA%\HoYoShadeHub）。
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [ValidateSet("x64", "x86", "ARM64")]
    [string] $Architecture = "x64",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    # 便携版输出目录（发布前会清空其中的 app-* 与临时文件）
    [string] $Output = "build/HoYoShadeHub",

    # 指定 dotnet.exe 路径（不传则自动查找）
    [string] $DotNet,

    # 打包后跳过 zip 完整性校验
    [switch] $SkipVerify,

    # Full 流程只需要发布目录，不重复压缩普通便携 ZIP。
    [switch] $SkipZip,

    # 「真便携」：包根写入 .portable 标记 —— 主程序据此把缓存 / config.ini / 数据库备份全部
    # 留在便携目录内。默认关闭（老用户升级不上来缓存目录突然搬家）
    [switch] $PortableLocal
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
. (Join-Path $repoRoot 'tools\package-storage.ps1')
$packageCleanup = [System.Collections.Generic.List[object]]::new()
$ownsStaging = $false
$ordinarySucceeded = $false
$zipFull = $null

# MSBuild / NuGet 的 Version 必须是合法 SemVer；"1.3.6b1" 这种写法会让 restore 静默失败
# （报 MSB4181: RestoreTask returned false but did not log an error，看不到真正原因）。
# 这里只把传给编译器的值归一化成 <x.y.z>b<n> -> <x.y.z>-b<n>；
# 目录名（app-<版本>）、zip 名、version.ini 仍用用户输入的原文。
$msbuildVersion = $Version
if ($Version -match '^(\d+\.\d+(?:\.\d+)?)b(\d+)$') {
    $msbuildVersion = "$($matches[1])-b$($matches[2])"
}

function Resolve-DotNet {
    param([string] $Explicit)

    if ($Explicit -and (Test-Path $Explicit)) {
        return (Resolve-Path $Explicit).Path
    }
    if ($env:HYSHADE_DOTNET -and (Test-Path $env:HYSHADE_DOTNET)) {
        return (Resolve-Path $env:HYSHADE_DOTNET).Path
    }

    $userLocal = Join-Path $env:USERPROFILE ".dotnet10\dotnet.exe"
    if (Test-Path $userLocal) {
        return $userLocal
    }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $v = & $onPath.Source --version 2>$null
        if ($v -match '^10\.') {
            return $onPath.Source
        }
    }

    throw "找不到 .NET 10 SDK。可用 -DotNet 指定 dotnet.exe，或设置环境变量 HYSHADE_DOTNET。"
}

Push-Location $repoRoot
try {
    $dotnet = Resolve-DotNet -Explicit $DotNet
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_CLI_UI_LANGUAGE = "en"
    $env:DOTNET_NOLOGO = "1"

    Write-Host "dotnet : $dotnet ($(& $dotnet --version))" -ForegroundColor DarkGray
    Write-Host "配置   : $Configuration / $Architecture / $Version" -ForegroundColor DarkGray

    $outDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
    $appDir = Join-Path $outDir "app-$Version"

    if (-not $SkipZip) {
        $buildScope = (Join-Path $repoRoot 'build').TrimEnd('\') + '\'
        if ($repoRoot.Equals($outDir, [StringComparison]::OrdinalIgnoreCase) -or $repoRoot.StartsWith($outDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            ($outDir.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $outDir.StartsWith($buildScope, [StringComparison]::OrdinalIgnoreCase))) { throw '仓库内打包暂存必须位于 build 下。' }
        foreach ($state in @('HoYoShadeHubDatabase.db', 'config.ini', 'cache', 'log', '.cache', '.hysx')) {
            if (Test-Path -LiteralPath (Join-Path $outDir $state)) { throw "暂存目录含用户状态，拒绝清理：$outDir\$state" }
        }
        Clear-PreviousPackages -RepoRoot $repoRoot -Records $packageCleanup
        $ownsStaging = $true
    }

    # 清理本次版本的旧产物，保证发布结果干净
    Remove-PackageArtifact -LiteralPath $appDir -AllowedRoot $outDir -Records $packageCleanup
    $staleLauncher = Join-Path $outDir "_launcher"
    Remove-PackageArtifact -LiteralPath $staleLauncher -AllowedRoot $outDir -Records $packageCleanup
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    Write-Host "==> 发布主程序 => $appDir" -ForegroundColor Cyan
    & $dotnet publish src/HoYoShadeHub `
        -c $Configuration `
        -r "win-$Architecture" `
        -o $appDir `
        -p:Platform=$Architecture `
        -p:PublishReadyToRun=true `
        -p:PublishTrimmed=false `
        -p:Version=$msbuildVersion `
        -m:1 `
        -nodeReuse:false `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "主程序 publish 失败（exit $LASTEXITCODE）" }

    Write-Host "==> 发布外层启动器 => HoYoShadeHub.exe" -ForegroundColor Cyan
    & $dotnet publish src/HoYoShadeHub.PortableLauncher `
        -c $Configuration `
        -r "win-$Architecture" `
        -o $staleLauncher `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:PublishTrimmed=true `
        -p:Version=$msbuildVersion `
        -m:1 `
        -nodeReuse:false `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "启动器 publish 失败（exit $LASTEXITCODE）" }

    Move-Item (Join-Path $staleLauncher "HoYoShadeHub.exe") (Join-Path $outDir "HoYoShadeHub.exe") -Force
    Remove-PackageArtifact -LiteralPath $staleLauncher -AllowedRoot $outDir -Records $packageCleanup

    Set-Content -Path (Join-Path $outDir "version.ini") `
        -Value "exe_path=app-$Version\HoYoShadeHub.exe" -Encoding UTF8

    # 不打包 config.ini：解压覆盖旧客户端时需保留用户已有配置。
    Remove-Item (Join-Path $outDir "config.ini") -Force -ErrorAction SilentlyContinue

    # 「真便携」标记：<包根>\.portable。主程序 AppConfig 看到它就把 CacheFolder / ConfigPath /
    # 数据库自动备份 / github 缓存全部指到包内（.cache），不再碰 %LOCALAPPDATA%。见 PortableDataFolderScope。
    $markerPath = Join-Path $outDir ".portable"
    if ($PortableLocal) {
        Write-Host "==> 写入真便携标记 => $markerPath" -ForegroundColor Cyan
        Set-Content -Path $markerPath -Value "portable-local=1" -Encoding UTF8
    }
    else {
        Remove-Item $markerPath -Force -ErrorAction SilentlyContinue
    }

    # 清掉同一输出目录里历史版本的 app-*：否则它们会被一起塞进 zip（实测每发一版 zip 就大出 175MB，
    # 1.3.8.5 那个包因此涨到 877MB，而 1.3.8.1 只有 180MB）。
    Get-ChildItem -Path $outDir -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "app-$Version" } |
        ForEach-Object {
            Write-Host "==> 清掉历史目录 $($_.Name)" -ForegroundColor DarkGray
            Remove-PackageArtifact -LiteralPath $_.FullName -AllowedRoot $outDir -Records $packageCleanup
        }

    if ($SkipZip) {
        Write-Host "基础发布完成（Full 流程跳过普通便携 ZIP）：$outDir" -ForegroundColor Green
        return
    }

    $zipDir = Join-Path $repoRoot "build/release"
    New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    $zip = Join-Path $zipDir "HoYoShadeHub_Portable_${Version}_$Architecture.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host "==> 打包 zip => $zip" -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zipFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($zip)
    $zipFs = [System.IO.File]::Open($zipFull, [System.IO.FileMode]::Create)
    $zipArchive = New-Object System.IO.Compression.ZipArchive `
        -ArgumentList @($zipFs, [System.IO.Compression.ZipArchiveMode]::Create)

    $prefixLen = $outDir.TrimEnd('\').Length + 1

    # 注意：条目名**必须**相对 $outDir（version.ini / app-<版本>/... 在 zip 根上）。
    # 之前这里给条目名强加了输出目录的叶子名（HoYoShadeHub/），整个包被套进一层 —— 解压后
    # 根上的 version.ini 原封不动，启动器照旧启动旧 app-<版本>，用户「更新完还是旧版本」
    # （v1.3.8.x 的包都中了招，GithubUpdateService.ApplyAsync 现在对这种包做了兼容剥壳）。
    Get-ChildItem -LiteralPath $outDir -Recurse -File -Force | ForEach-Object {
        $entryName = $_.FullName.Substring($prefixLen).Replace('\', '/')
        $entry = $zipArchive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $_.LastWriteTime
        $entryStream = $entry.Open()
        $fileStream = [System.IO.File]::OpenRead($_.FullName)
        try {
            $fileStream.CopyTo($entryStream)
        }
        finally {
            $fileStream.Dispose()
            $entryStream.Dispose()
        }
    }
    $zipArchive.Dispose()
    $zipFs.Dispose()

    if (-not $SkipVerify) {
        Write-Host "==> 校验 zip 完整性" -ForegroundColor Cyan
        $check = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
        $entryCount = 0
        foreach ($e in $check.Entries) {
            $sourceFile = Join-Path $outDir $e.FullName
            if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "ZIP 条目无对应源文件：$($e.FullName)" }
            $s = $e.Open()
            try {
                $archiveHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($s))
            }
            finally { $s.Dispose() }
            if ($archiveHash -ne (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash) { throw "ZIP 内容哈希不匹配：$($e.FullName)" }
            $entryCount++
        }
        if ($entryCount -ne @(Get-ChildItem -LiteralPath $outDir -Recurse -File -Force).Count) { throw 'ZIP 条目数与源目录不一致。' }
        foreach ($required in @('HoYoShadeHub.exe', 'version.ini', "app-$Version/HoYoShadeHub.exe", "app-$Version/HoYoShadeHub.dll")) {
            if ($required -notin $check.Entries.FullName) { throw "普通包缺少必要条目：$required" }
        }
        if (@($check.Entries | Where-Object { $_.FullName -like 'HoYoShade/*' }).Count) { throw '普通包不应包含 HoYoShade 框架。' }
        $check.Dispose()
        Write-Host "    校验通过，条目数：$entryCount" -ForegroundColor DarkGray
    }

    $receipt = [ordered]@{ version = $Version; architecture = $Architecture; kind = 'ordinary-portable'; zip = $zipFull; bytes = (Get-Item -LiteralPath $zipFull).Length; sha256 = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash; contentsVerified = -not $SkipVerify; uiTested = $false; gameTested = $false }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath ($zipFull + '.verification.json') -Encoding utf8
    $ordinarySucceeded = $true
    Write-Host ""
    Write-Host "便携版构建完成：" -ForegroundColor Green
    Write-Host "  压缩包 : $zip"
}
finally {
    try {
        if ($ownsStaging -and (Test-Path -LiteralPath $outDir)) {
            Remove-PackageArtifact -LiteralPath $outDir -AllowedRoot ([IO.Path]::GetDirectoryName($outDir)) -Records $packageCleanup
            $parent = [IO.Path]::GetDirectoryName($outDir)
            if ([IO.Path]::GetFileName($parent) -eq "portable-$Version" -and -not @(Get-ChildItem -LiteralPath $parent -Force).Count) { Remove-Item -LiteralPath $parent -Force }
        }
        if (-not $SkipZip) {
            if (-not $ordinarySucceeded -and $zipFull) { Remove-PackageArtifact -LiteralPath $zipFull -AllowedRoot ([IO.Path]::GetDirectoryName($zipFull)) -Records $packageCleanup }
            $packageCleanup | ConvertTo-Json -Depth 5 -AsArray | Set-Content -LiteralPath (Join-Path $repoRoot 'build\package-storage-cleanup.json') -Encoding utf8
        }
    }
    finally { Pop-Location }
}
