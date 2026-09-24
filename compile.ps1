<#
.SYNOPSIS
    通用编译脚本：编译 HoYoShadeHub 整个解决方案。

.DESCRIPTION
    - 自动查找 .NET 10 SDK（顺序：-DotNet 参数 > 环境变量 HYSHADE_DOTNET >
      %USERPROFILE%\.dotnet10\dotnet.exe > PATH 上的 dotnet，必须为 10.x）。
    - global.json 锁定 SDK 10.0.1xx，rollForward=latestFeature，10.0.4xx 也可用。
    - 本机环境曾出现多进程 MSBuild 静默失败，脚本固定使用 -m:1 -nodeReuse:false。
    - 在 Windows PowerShell 5.1 与 PowerShell 7 下均可运行。

.EXAMPLE
    .\compile.ps1
    Debug / x64 编译整个解决方案。

.EXAMPLE
    .\compile.ps1 -Configuration Release -Clean
    清理所有 bin/obj 后以 Release 编译。

.EXAMPLE
    .\compile.ps1 -DotNet C:\tools\dotnet\dotnet.exe
    使用指定的 dotnet 可执行文件。
#>
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [ValidateSet("x64", "x86", "ARM64")]
    [string] $Architecture = "x64",

    # 编译前删除 src 下所有 bin/obj
    [switch] $Clean,

    # 指定 dotnet.exe 路径（不传则自动查找）
    [string] $DotNet
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

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
    if ($Clean) {
        Write-Host "==> 清理 bin/obj" -ForegroundColor Cyan
        Get-ChildItem -Path src -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $dotnet = Resolve-DotNet -Explicit $DotNet
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_CLI_UI_LANGUAGE = "en"
    $env:DOTNET_NOLOGO = "1"

    Write-Host "dotnet : $dotnet ($(& $dotnet --version))" -ForegroundColor DarkGray
    Write-Host "配置   : $Configuration / $Architecture" -ForegroundColor DarkGray
    Write-Host "==> 编译 HoYoShadeHub.sln" -ForegroundColor Cyan

    & $dotnet build HoYoShadeHub.sln `
        -c $Configuration `
        -p:Platform=$Architecture `
        -m:1 `
        -nodeReuse:false `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

    Write-Host ""
    Write-Host "编译完成。" -ForegroundColor Green
}
finally {
    Pop-Location
}
