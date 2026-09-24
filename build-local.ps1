<#
.SYNOPSIS
    本地构建 HoYoShade Hub（含扩展管理）。

.DESCRIPTION
    上游的 build.ps1 / publish.ps1 假设你装了 Visual Studio：
      - build.ps1   最后会调 msbuild 编 HoYoShadeHub.Launcher，需要 VS；
      - publish.ps1 还要 7Zip4Powershell 和 Python 那套打包工具链。
    本机没有 VS，所以这个脚本只做「能跑起来的主程序」这一半：build / publish HoYoShadeHub。

    另外它会自己找 .NET 10 SDK。项目 global.json 要求 SDK 10.0.1xx，
    如果你的机器上 10 不在 PATH 里（比如装在 %USERPROFILE%\.dotnet10），
    直接敲 dotnet build 会静默用回 9.x 然后报错，这个脚本把这一步包掉。

.EXAMPLE
    .\build-local.ps1
    编译 Debug 全解决方案。

.EXAMPLE
    .\build-local.ps1 -Publish -Version 1.0.0-local
    发布一份可直接运行的 x64 程序到 build\HoYoShadeHub\app-1.0.0-local。
#>
param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string] $Architecture = "x64",

    [string] $Version = "0.0.0-local",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    # 发布可直接运行的程序（dotnet publish）
    [switch] $Publish,

    # 发布**便携版**：<Output>\HoYoShadeHub.exe（外层启动器）+ version.ini + app-<版本>\
    # 布局和上游 build.ps1 / publish.ps1 出来的一样，另外顺手打个 zip
    [switch] $Portable,

    # 删掉所有 bin/obj 再编
    [switch] $Clean,

    # 发布输出目录（-Publish / -Portable 时生效）
    [string] $Output = "build/HoYoShadeHub"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
Push-Location $repoRoot

function Resolve-DotNet {
    # 1) 环境变量显式指定
    if ($env:HYSHADE_DOTNET -and (Test-Path $env:HYSHADE_DOTNET)) {
        return (Resolve-Path $env:HYSHADE_DOTNET).Path
    }

    # 2) 用户级安装（dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet10" 的默认位置）
    $userLocal = Join-Path $env:USERPROFILE ".dotnet10\dotnet.exe"
    if (Test-Path $userLocal) {
        return $userLocal
    }

    # 3) PATH 里的 dotnet，但必须真的是 10.x
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $version = & $onPath.Source --version 2>$null
        if ($version -match '^10\.') {
            return $onPath.Source
        }
    }

    throw @"
找不到 .NET 10 SDK。

项目 global.json 要求 10.0.1xx，装的话（不需要管理员）：

    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile `$env:TEMP\dotnet-install.ps1
    & `$env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir `$env:USERPROFILE\.dotnet10 -NoPath

装好后再跑这个脚本；或者用 -Environment 把 HYSHADE_DOTNET 指到已有的 dotnet.exe。
"@
}

$dotnet = Resolve-DotNet
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:DOTNET_NOLOGO = "1"

Write-Host "dotnet  : $dotnet ($(& $dotnet --version))" -ForegroundColor DarkGray
Write-Host "配置    : $Configuration / $Architecture" -ForegroundColor DarkGray

try {
    if ($Clean) {
        Write-Host "==> 清理 bin/obj" -ForegroundColor Cyan
        Get-ChildItem -Path src -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item build -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($Portable) {
        $outDir = Join-Path $repoRoot $Output
        $appDir = Join-Path $outDir "app-$Version"

        Write-Host "==> [便携版] 发布主程序 => $appDir" -ForegroundColor Cyan
        & $dotnet publish src/HoYoShadeHub `
            -c $Configuration `
            -r "win-$Architecture" `
            -o $appDir `
            -p:Platform=$Architecture `
            -p:PublishReadyToRun=true `
            -p:PublishTrimmed=false `
            -p:Version=$Version `
            --nologo
        if ($LASTEXITCODE -ne 0) { throw "主程序 publish 失败（exit $LASTEXITCODE）" }

        Write-Host "==> [便携版] 编译外层启动器（入口，产物名字就叫 HoYoShadeHub.exe）" -ForegroundColor Cyan
        $launcherTmp = Join-Path $outDir "_launcher"
        Remove-Item $launcherTmp -Recurse -Force -ErrorAction SilentlyContinue
        & $dotnet publish src/HoYoShadeHub.PortableLauncher `
            -c $Configuration `
            -r "win-$Architecture" `
            -o $launcherTmp `
            -p:PublishSingleFile=true `
            -p:SelfContained=true `
            -p:PublishTrimmed=true `
            -p:Version=$Version `
            --nologo
        if ($LASTEXITCODE -ne 0) { throw "启动器 publish 失败（exit $LASTEXITCODE）" }

        Move-Item (Join-Path $launcherTmp "HoYoShadeHub.exe") (Join-Path $outDir "HoYoShadeHub.exe") -Force
        Remove-Item $launcherTmp -Recurse -Force -ErrorAction SilentlyContinue

        # 外层启动器靠 version.ini 找 app-* 目录（上游 build.ps1 也是写这一行）
        Set-Content -Path (Join-Path $outDir "version.ini") -Value "exe_path=app-$Version\HoYoShadeHub.exe" -Encoding UTF8

        # ⚠ 故意**不**往这里放 config.ini：
        #   更新包解压覆盖旧客户端时，zip 里带着 config.ini 会把用户那份（UserDataFolder / 各种设置）
        #   覆盖成默认的 —— 用户会以为「游戏没了」（真事，2026-09-20）。
        #   改由 PortableLauncher 在缺失时创建（EnsureConfigIni），App 保存设置时也会补上。
        Remove-Item (Join-Path $outDir "config.ini") -Force -ErrorAction SilentlyContinue

        $zip = Join-Path $repoRoot "build/release/HoYoShadeHub_Portable_${Version}_$Architecture.zip"
        New-Item -ItemType Directory -Force -Path (Split-Path $zip -Parent) | Out-Null
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Write-Host "==> [便携版] 打包 => $zip" -ForegroundColor Cyan
        # 不能用 Compress-Archive：Windows PowerShell 5.1 的实现把条目名写成反斜杠分隔，
        # 不符合 ZIP 规范，资源管理器等严格工具会判定压缩包损坏、无法打开。
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($zip)
        $zipFs = [System.IO.File]::Open($zipFull, [System.IO.FileMode]::Create)
        $zipArchive = New-Object System.IO.Compression.ZipArchive -ArgumentList @($zipFs, [System.IO.Compression.ZipArchiveMode]::Create)
        $zipPrefixLen = $outDir.TrimEnd('\').Length + 1
        $zipEntryPrefix = (Split-Path $outDir -Leaf) + '/'
        Get-ChildItem -Path $outDir -Recurse -File | ForEach-Object {
            $entryName = $zipEntryPrefix + $_.FullName.Substring($zipPrefixLen).Replace('\', '/')
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

        Write-Host ""
        Write-Host "便携版完成：" -ForegroundColor Green
        Write-Host "  启动器 : $(Join-Path $outDir 'HoYoShadeHub.exe')"
        Write-Host "  主程序 : $(Join-Path $appDir 'HoYoShadeHub.exe')"
        Write-Host "  压缩包 : $zip"
    }
    elseif ($Publish) {
        $outDir = Join-Path $repoRoot "$Output/app-$Version"
        Write-Host "==> 发布 $Architecture => $outDir" -ForegroundColor Cyan
        & $dotnet publish src/HoYoShadeHub `
            -c $Configuration `
            -r "win-$Architecture" `
            -o $outDir `
            -p:Platform=$Architecture `
            -p:PublishReadyToRun=true `
            -p:PublishTrimmed=false `
            -p:Version=$Version `
            --nologo
        if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit $LASTEXITCODE）" }

        $exe = Join-Path $outDir "HoYoShadeHub.exe"
        Write-Host ""
        Write-Host "完成：$exe" -ForegroundColor Green
    }
    else {
        Write-Host "==> 编译 HoYoShadeHub.sln" -ForegroundColor Cyan
        & $dotnet build HoYoShadeHub.sln `
            -c $Configuration `
            -p:Platform=$Architecture `
            --nologo `
            -clp:ErrorsOnly
        if ($LASTEXITCODE -ne 0) { throw "build 失败（exit $LASTEXITCODE）" }
        Write-Host ""
        Write-Host "完成。" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "跑一遍扩展安装/卸载自测：" -ForegroundColor DarkGray
    Write-Host "    & '$dotnet' run --project src\HoYoShadeHub.Extensions.Tests -c $Configuration" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
