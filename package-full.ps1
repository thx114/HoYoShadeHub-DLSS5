<#
.SYNOPSIS
  构建「完整包」：便携版启动器 + 自带 HoYoShade（框架本体 + 预置 + 精简滤镜/材质），插件目录留空。

.DESCRIPTION
  和普通便携包（package.ps1）的区别只有一处：多带一份 HoYoShade 框架，装完即用，不用再让用户自己装 HoYoShade。
  布局：

      version.ini
      HoYoShadeHub.exe
      app-<版本>\
      HoYoShade\
          ReShade64.dll  inject.exe  ReShade.ini  *.bat  LICENSE  ReShade_LICENSE
          InjectResource\  LauncherResource\  Presets\
          reshade-shaders\Shaders\    ← 只带必要滤镜（源目录本来就是精简集）
          reshade-shaders\Textures\   ← 只带必要材质
          reshade-shaders\Addons\     ← **空**（插件由用户在启动器里装）
          ScreenShot\                 ← 空

  说明：
  * 只拷「框架 + 必要资源」，**不拷用户状态**（.hysx\installed.json、.hysx\backup、日志、插件文件、截图）。
  * ReShade.ini 里的绝对路径会改写成相对路径 —— 源机是 D:\...，别人机器上根本不存在那条路径。
  * 滤镜/材质默认整目录照搬（HoYoShade 自带的就是精简集）；想再精简就传 -ShaderAllowList / -TextureAllowList
    （一个文本文件，一行一个通配符，例如 Lilium*.fx / renodx*）。

.EXAMPLE
  .\package-full.ps1 -Version 1.3.9.1
  .\package-full.ps1 -Version 1.3.9.1 -ShadeSource 'D:\APPS\HoYoShadeHub\HoYoShade'
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [ValidateSet("x64", "x86", "ARM64")]
    [string] $Architecture = "x64",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    # 打包工作目录（默认 build/full-<版本>/HoYoShadeHub）
    [string] $Output = "",

    # HoYoShade 框架来源目录（里面有 ReShade64.dll）。默认按顺序找：参数 → HYSHADE_SHADE_SOURCE → 仓库下 HoYoShade → D:\APPS\HoYoShadeHub\HoYoShade
    [string] $ShadeSource = "",

    # 只保留匹配这些通配符的滤镜 / 材质（一行一个，支持 # 注释）；不传 = 整目录照搬
    [string] $ShaderAllowList = "",
    [string] $TextureAllowList = "",

    # 跳过基础便携包构建（复用已有输出目录）
    [switch] $SkipBaseBuild,

    # 跳过 zip 结构与大小校验
    [switch] $SkipVerify
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

if (-not $Output) { $Output = "build/full-$Version/HoYoShadeHub" }

function Resolve-ShadeSource {
    param([string] $Explicit)

    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:HYSHADE_SHADE_SOURCE) { $candidates += $env:HYSHADE_SHADE_SOURCE }
    $candidates += (Join-Path $repoRoot "HoYoShade")
    $candidates += "D:\APPS\HoYoShadeHub\HoYoShade"

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path (Join-Path $candidate "ReShade64.dll")) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "找不到 HoYoShade 框架目录（要求里面有 ReShade64.dll）。用 -ShadeSource 指定，或设置环境变量 HYSHADE_SHADE_SOURCE。"
}

function Read-AllowList {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    if (-not (Test-Path $Path)) { throw "白名单文件不存在：$Path" }

    return @(Get-Content $Path -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") })
}

# 拷一个目录，可按白名单筛文件（白名单为空 = 全拷）
function Copy-Filtered {
    param(
        [string] $From,
        [string] $To,
        [string[]] $AllowList
    )

    if (-not (Test-Path $From)) { return 0 }
    New-Item -ItemType Directory -Force -Path $To | Out-Null

    $files = Get-ChildItem -Path $From -Recurse -File
    $copied = 0

    foreach ($file in $files) {
        if ($AllowList.Count -gt 0) {
            $match = $false
            foreach ($pattern in $AllowList) {
                if ($file.Name -like $pattern) { $match = $true; break }
            }
            if (-not $match) { continue }
        }

        $relative = $file.FullName.Substring($From.TrimEnd("\").Length).TrimStart("\")
        $target = Join-Path $To $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }

    return $copied
}

# 把 ReShade.ini 里的绝对路径改写成相对路径（源机路径在别人机器上不存在）
function ConvertTo-PortableIni {
    param(
        [string] $SourceIni,
        [string] $ShadeRoot,
        [string] $TargetIni
    )

    $text = Get-Content -Path $SourceIni -Raw -Encoding UTF8
    $prefix = $ShadeRoot.TrimEnd("\")

    $text = $text -replace [regex]::Escape("$prefix\"), ""
    $text = $text -replace [regex]::Escape("$prefix/"), ""

    Set-Content -Path $TargetIni -Value $text -Encoding UTF8
}

Push-Location $repoRoot
try {
    $shadeRoot = Resolve-ShadeSource -Explicit $ShadeSource
    Write-Host "HoYoShade 来源 : $shadeRoot" -ForegroundColor DarkGray
    Write-Host "配置           : $Configuration / $Architecture / $Version" -ForegroundColor DarkGray

    $outDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

    # 复用输出目录时，先清掉上一步完整包添加的 HoYoShade 框架：内层 package.ps1 会把
    # 输出目录里的所有内容打进「基础便携包」zip，不清的话第二次构建基础包从 180MB 肿到 235MB。
    $staleShade = Join-Path $outDir "HoYoShade"
    if (Test-Path $staleShade) { Remove-Item $staleShade -Recurse -Force }

    # 1) 基础便携包（app-<版本> + version.ini + HoYoShadeHub.exe）
    if (-not $SkipBaseBuild) {
        Write-Host "==> 先构建基础便携包" -ForegroundColor Cyan
        & (Join-Path $repoRoot "package.ps1") -Version $Version -Architecture $Architecture -Configuration $Configuration -Output $Output -SkipVerify
        if ($LASTEXITCODE -ne 0) { throw "基础便携包构建失败（exit $LASTEXITCODE）" }
    }

    if (-not (Test-Path (Join-Path $outDir "app-$Version"))) {
        throw "输出目录里没有 app-$Version（先别加 -SkipBaseBuild，或把 -Output 指对）：$outDir"
    }

    $target = Join-Path $outDir "HoYoShade"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $shaderList = Read-AllowList -Path $ShaderAllowList
    $textureList = Read-AllowList -Path $TextureAllowList

    # 2) 顶层文件：框架本体 + 脚本 + 许可证（ReShade.ini 最后单独改写）
    Write-Host "==> 拷 HoYoShade 框架" -ForegroundColor Cyan
    $topFiles = @("ReShade64.dll", "inject.exe", "LICENSE", "ReShade_LICENSE")
    $topFiles += @(Get-ChildItem -Path $shadeRoot -File -Filter "*.bat" | ForEach-Object { $_.Name })

    foreach ($name in $topFiles) {
        $file = Join-Path $shadeRoot $name
        if (Test-Path $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $target $name) -Force }
    }

    # 3) 目录：预置 / 资源 / 精简滤镜 / 精简材质
    $dirs = @("Presets", "InjectResource", "LauncherResource")
    foreach ($dir in $dirs) {
        $count = Copy-Filtered -From (Join-Path $shadeRoot $dir) -To (Join-Path $target $dir) -AllowList @()
        Write-Host ("      {0,-18} {1} 个文件" -f $dir, $count) -ForegroundColor DarkGray
    }

    $shaderCount = Copy-Filtered -From (Join-Path $shadeRoot "reshade-shaders\Shaders") -To (Join-Path $target "reshade-shaders\Shaders") -AllowList $shaderList
    $textureCount = Copy-Filtered -From (Join-Path $shadeRoot "reshade-shaders\Textures") -To (Join-Path $target "reshade-shaders\Textures") -AllowList $textureList
    Write-Host ("      {0,-18} {1} 个文件（必要滤镜）" -f "Shaders", $shaderCount) -ForegroundColor DarkGray
    Write-Host ("      {0,-18} {1} 个文件（必要材质）" -f "Textures", $textureCount) -ForegroundColor DarkGray

    # 4) 空目录：插件目录留空，截图目录留空
    New-Item -ItemType Directory -Force -Path (Join-Path $target "reshade-shaders\Addons") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $target "ScreenShot") | Out-Null

    # 5) ReShade.ini：相对路径
    $sourceIni = Join-Path $shadeRoot "ReShade.ini"
    if (Test-Path $sourceIni) {
        ConvertTo-PortableIni -SourceIni $sourceIni -ShadeRoot $shadeRoot -TargetIni (Join-Path $target "ReShade.ini")
        Write-Host "      ReShade.ini        已改写为相对路径" -ForegroundColor DarkGray
    }

    # 6) 不打包：用户状态 / 日志 / 插件 / 截图（Addons 必须是空的）
    $junk = @(
        (Join-Path $target ".hysx"),
        (Join-Path $target "reshade-shaders\Addons\*"),
        (Join-Path $target "ScreenShot\*")
    )
    foreach ($pattern in $junk) {
        Get-ChildItem -Path $pattern -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    Get-ChildItem -Path $target -Recurse -File -Include "*.log" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $addonFiles = @(Get-ChildItem -Path (Join-Path $target "reshade-shaders\Addons") -Recurse -File -ErrorAction SilentlyContinue)
    if ($addonFiles.Count -gt 0) { throw "插件目录必须为空，但现在有 $($addonFiles.Count) 个文件。" }

    # 完整包永远带「真便携」标记：缓存 / 配置 / 日志全留在包内不碰 C: 盘，
    # 自带 HoYoShade 也必须是包目录当用户数据目录才能被认出来。
    # （内层 package.ps1 不带 -PortableLocal 时还会主动删掉标记，所以这里自己写。）
    $markerPath = Join-Path $outDir ".portable"
    Write-Host "==> 写入真便携标记 => $markerPath" -ForegroundColor Cyan
    Set-Content -Path $markerPath -Value "portable-local=1" -Encoding UTF8

    # 7) 打 zip
    $zipDir = Join-Path $repoRoot "build/release"
    New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    $zip = Join-Path $zipDir ("HoYoShadeHub_Portable_" + $Version + "_" + $Architecture + "_Full.zip")
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host "==> 打包 zip => $zip" -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zipFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($zip)
    $zipFs = [System.IO.File]::Open($zipFull, [System.IO.FileMode]::Create)
    $zipArchive = New-Object System.IO.Compression.ZipArchive -ArgumentList @($zipFs, [System.IO.Compression.ZipArchiveMode]::Create)

    # 条目名相对输出目录（zip 根上就是 version.ini / HoYoShadeHub.exe / app-<版本>/ / HoYoShade/）
    $prefixLen = $outDir.TrimEnd("\").Length + 1
    Get-ChildItem -Path $outDir -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($prefixLen).Replace("\", "/")
        $entry = $zipArchive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $_.LastWriteTime

        $stream = $entry.Open()
        try {
            $input = [System.IO.File]::OpenRead($_.FullName)
            try { $input.CopyTo($stream) } finally { $input.Dispose() }
        } finally { $stream.Dispose() }
    }

    # 空目录（插件 / 截图）也进 zip：解压出来就能看到这两个目录在、且是空的
    foreach ($empty in @("HoYoShade/reshade-shaders/Addons/", "HoYoShade/ScreenShot/")) {
        $zipArchive.CreateEntry($empty) | Out-Null
    }

    $zipArchive.Dispose()
    $zipFs.Dispose()

    $zipInfo = Get-Item $zip
    $entries = 0
    if (-not $SkipVerify) {
        Write-Host "==> 校验 zip 完整性" -ForegroundColor Cyan
        $readArchive = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
        try {
            $entries = $readArchive.Entries.Count
            $names = $readArchive.Entries | ForEach-Object { $_.FullName }

            $need = @(
                "version.ini",
                "HoYoShadeHub.exe",
                "app-$Version/HoYoShadeHub.exe",
                "HoYoShade/ReShade64.dll",
                "HoYoShade/inject.exe",
                "HoYoShade/ReShade.ini"
            )
            foreach ($item in $need) {
                if (-not ($names -contains $item)) { throw "zip 里缺条目：$item" }
            }

            # 末尾带 / 的是空目录占位条目，不算文件
            $addonEntries = @($names | Where-Object { $_ -like "HoYoShade/reshade-shaders/Addons/*" -and -not $_.EndsWith("/") })
            if ($addonEntries.Count -gt 0) { throw "zip 里插件目录不是空的（$($addonEntries.Count) 个条目）" }

            $shaderEntries = @($names | Where-Object { $_ -like "HoYoShade/reshade-shaders/Shaders/*" })
            $textureEntries = @($names | Where-Object { $_ -like "HoYoShade/reshade-shaders/Textures/*" })
            if ($shaderEntries.Count -eq 0) { throw "zip 里没有滤镜" }
            if ($textureEntries.Count -eq 0) { throw "zip 里没有材质" }
        } finally { $readArchive.Dispose() }

        Write-Host "    校验通过，条目数：$entries" -ForegroundColor DarkGray
    } else {
        $readArchive = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
        try { $entries = $readArchive.Entries.Count } finally { $readArchive.Dispose() }
    }

    Write-Host ""
    Write-Host "完整包构建完成：" -ForegroundColor Green
    Write-Host "  启动器 : $(Join-Path $outDir 'HoYoShadeHub.exe')"
    Write-Host "  框架   : $target"
    Write-Host "  压缩包 : $zip"
    Write-Host ("  大小   : {0:F1} MB / {1} 个条目" -f ($zipInfo.Length / 1MB), $entries)
    Write-Host ("  内含   : app-$Version、HoYoShade 框架、$shaderCount 个滤镜、$textureCount 个材质、插件目录留空")
}
finally {
    Pop-Location
}
