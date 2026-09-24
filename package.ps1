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
    [switch] $SkipVerify
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

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

    # 清理本次版本的旧产物，保证发布结果干净
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    $staleLauncher = Join-Path $outDir "_launcher"
    if (Test-Path $staleLauncher) { Remove-Item $staleLauncher -Recurse -Force }
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
    Remove-Item $staleLauncher -Recurse -Force -ErrorAction SilentlyContinue

    Set-Content -Path (Join-Path $outDir "version.ini") `
        -Value "exe_path=app-$Version\HoYoShadeHub.exe" -Encoding UTF8

    # 不打包 config.ini：解压覆盖旧客户端时需保留用户已有配置。
    Remove-Item (Join-Path $outDir "config.ini") -Force -ErrorAction SilentlyContinue

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
    $entryPrefix = (Split-Path $outDir -Leaf) + '/'

    Get-ChildItem -Path $outDir -Recurse -File | ForEach-Object {
        $entryName = $entryPrefix + $_.FullName.Substring($prefixLen).Replace('\', '/')
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
            $s = $e.Open()
            $buf = New-Object byte[] 8192
            for (;;) {
                $r = $s.Read($buf, 0, $buf.Length)
                if ($r -le 0) { break }
            }
            $s.Close()
            $entryCount++
        }
        $check.Dispose()
        Write-Host "    校验通过，条目数：$entryCount" -ForegroundColor DarkGray
    }

    Write-Host ""
    Write-Host "便携版构建完成：" -ForegroundColor Green
    Write-Host "  启动器 : $(Join-Path $outDir 'HoYoShadeHub.exe')"
    Write-Host "  主程序 : $(Join-Path $appDir 'HoYoShadeHub.exe')"
    Write-Host "  压缩包 : $zip"
}
finally {
    Pop-Location
}
