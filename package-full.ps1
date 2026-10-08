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
          reshade-shaders\Shaders\    ← 只带必要滤镜（按预设算闭包，见下）
          reshade-shaders\Textures\   ← 只带必要材质（同上）
          reshade-shaders\Addons\     ← **空**（插件由用户在启动器里装）
          ScreenShot\                 ← 空

  说明：
  * 只拷「框架 + 必要资源」，**不拷用户状态**（.hysx\installed.json、.hysx\backup、日志、插件文件、截图）。
  * ReShade.ini 里的绝对路径会改写成相对路径 —— 源机是 D:\...，别人机器上根本不存在那条路径。
  * 滤镜/材质：框架源目录自带 766 个滤镜 + 161 个材质（约 100 MB，其中材质占 90 MB），全都不是必需的 ——
    ReShade 会把 EffectSearchPaths 下的每个 .fx 都编译一遍，多余滤镜既占体积又拖慢启动。两种收窄方式：
      -PresetIni '<游戏预设.ini>'        按预设算最小闭包（调 tools\make-shader-allowlist.ps1：`Techniques=` +
                                        `[某文件.fx]` 配置节 + 递归 `#include` + 材质引用）。再加
                                        -PresetIncludeFrameworkPresets 则连带框架自带 Presets\*.ini 用到的
                                        特效一起带（推荐：仍是几十个文件的量级，但随包预置切过去都能用）
      -ShaderAllowList / -TextureAllowList   直接给清单文件（一行一个，可写文件名或相对路径，`#` 是注释）
    两者都不给 = 整目录照搬（历史行为）。
  * 框架 ReShade.ini 里的 `[ADDON] DisabledAddons` 是**本机插件开关状态**，默认会被原样打进包；要出厂不预置
    任何禁用就传 `-DisabledAddons ''`（插件开关本该由启动器按游戏写，见 ReShadeProfile.SetDisabledAddons）。

.EXAMPLE
  .\package-full.ps1 -Version 1.3.9.1
  .\package-full.ps1 -Version 1.3.9.1 -ShadeSource 'D:\APPS\HoYoShadeHub\HoYoShade'
  .\package-full.ps1 -Version 1.4.3.14 -ShadeSource 'D:\APPS\HoYoShadeHub\HoYoShade' `
      -PresetIni 'D:\APPS\miHoYo Launcher\games\Genshin Impact Game\HoYoShade DX11 Before NR.ini' `
      -PresetIncludeFrameworkPresets
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

    # 只保留匹配这些清单的滤镜 / 材质（一行一个，可写文件名或相对路径，支持 # 注释）；不传 = 整目录照搬
    [string] $ShaderAllowList = "",
    [string] $TextureAllowList = "",

    # 按预设推导「必要滤镜/材质」白名单（自动调用 tools\make-shader-allowlist.ps1，清单写到 build\shader-allowlist\）
    [string[]] $PresetIni = @(),

    # 预设口径下，连带框架自带 Presets\*.ini 用到的特效一起带（推荐）
    [switch] $PresetIncludeFrameworkPresets,

    # 预设口径下额外要塞进闭包的特效文件名（例如插件自己带的 FSRBridgeDepthView.fx）
    [string[]] $PresetExtraEffect = @(),

    # 包内框架 ReShade.ini 的 [ADDON] DisabledAddons 覆盖值：不传 = 原样照抄源机那份；传空串 = 清空。
    # 为什么要这个开关：完整包会带上整个 HoYoShade 框架目录，其中的 ReShade.ini 是 ReShade 运行时读的那份；
    # 源机那份里的 DisabledAddons 是**本机插件开关状态**（比如本机把某个插件关着），会被出厂带给新用户，
    # 还会经 GameIniBootstrap 同步到各游戏 ini。插件开关本该由启动器在安装/启用时按游戏写
    # （src\HoYoShadeHub.Extensions\ReShade\ReShadeProfile.cs:497 SetDisabledAddons），不该在打包时照抄。
    [string] $DisabledAddons = $null,

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
        ForEach-Object { "$_".Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") })
}

# 拷一个目录，可按白名单筛文件（白名单为空 = 全拷）
#
# 匹配规则：**相对路径优先**。每条规则先看能不能按相对路径命中（正/反斜杠等价）；
# 整条规则一个相对路径都没命中时，- 若规则里没有路径分隔符，才退回按文件名匹配（历史清单写的是
# Lilium*.fx 这种名字通配）；- 若规则带分隔符，说明是写死的相对路径，命中不了就告警（清单过期）。
# 这样分工的原因：按文件名兜底会把别处的同名文件一起拖进来 —— 例如清单里一条 `ReShade.fxh`
# 会把 `CorgiFX/StageDepthPlus.../ReShade.fxh` 也拷进来，那份是第三方同名滤镜，还 include 了没带的
# 文件，ReShade 编译时会报错，甚至可能顶掉正确的 ReShade.fxh。
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
    $prefix = $From.TrimEnd("\").Length

    $relPatterns = @()
    $namePatterns = @()
    if ($AllowList.Count -gt 0) {
        foreach ($pattern in $AllowList) {
            $normalized = $pattern.Replace("/", "\")
            $hit = $false
            foreach ($file in $files) {
                if ($file.FullName.Substring($prefix).TrimStart("\") -like $normalized) { $hit = $true; break }
            }
            if ($hit) {
                $relPatterns += $normalized
            } elseif ($pattern -match '[\\/]') {
                Write-Warning "白名单规则按相对路径没命中任何文件（清单可能过期）：$pattern"
            } else {
                $namePatterns += $pattern
            }
        }
    }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($prefix).TrimStart("\")

        if ($AllowList.Count -gt 0) {
            $match = $false
            foreach ($pattern in $relPatterns) {
                if ($relative -like $pattern) { $match = $true; break }
            }
            if (-not $match) {
                foreach ($pattern in $namePatterns) {
                    if ($file.Name -like $pattern) { $match = $true; break }
                }
            }
            if (-not $match) { continue }
        }

        $target = Join-Path $To $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }

    # 白名单写了规则却一个都没命中，基本就是清单路径/来源目录不对 —— 宁可报错也别悄悄做出个缺滤镜的包
    if ($AllowList.Count -gt 0 -and $copied -eq 0) {
        throw "白名单里 $($AllowList.Count) 条规则一个都没匹配到：$From（清单第一行 = $($AllowList[0])）"
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

# 改 ini 里某个键的值（有就替换那一行，没有就在对应节里插一行；节不存在就补一个节）
# 逐行处理而不是正则替换：值里可能带 $ 或 . 等字符，正则替换的替换串会把这些当引用/转义。
function Set-IniKey {
    param(
        [string] $Path,
        [string] $Section,
        [string] $Key,
        [string] $Value
    )

    $lines = @(Get-Content -Path $Path)
    $out = New-Object System.Collections.Generic.List[string]
    $sectionAt = -1
    $keyAt = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*\[$([regex]::Escape($Section))\]\s*$") { $sectionAt = $i }
        elseif ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") { $keyAt = $i; break }
    }

    if ($keyAt -ge 0) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $out.Add($(if ($i -eq $keyAt) { "$Key=$Value" } else { $lines[$i] }))
        }
    } elseif ($sectionAt -ge 0) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $out.Add($lines[$i])
            # 插在节的第一行（AddonPath 那种也一起往后排无所谓，ReShade 只按键名取值）
            if ($i -eq $sectionAt) { $out.Add("$Key=$Value") }
        }
    } else {
        foreach ($line in $lines) { $out.Add($line) }
        if ($out.Count -gt 0 -and $out[$out.Count - 1] -ne "") { $out.Add("") }
        $out.Add("[$Section]")
        $out.Add("$Key=$Value")
    }

    Set-Content -Path $Path -Value ($out -join "`r`n") -Encoding UTF8
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

    # 预设口径：自动推导滤镜/材质白名单（显式给了 -ShaderAllowList / -TextureAllowList 就以显式为准）
    if ($PresetIni.Count -gt 0 -and -not $ShaderAllowList -and -not $TextureAllowList) {
        $listDir = Join-Path $repoRoot "build\shader-allowlist"
        $listTool = Join-Path $repoRoot "tools\make-shader-allowlist.ps1"
        if (-not (Test-Path $listTool)) { throw "找不到白名单工具：$listTool" }

        Write-Host "==> 按预设推导必要滤镜/材质：$($PresetIni -join ', ')" -ForegroundColor Cyan
        $toolArgs = @{ PresetIni = @($PresetIni); ShadeRoot = $shadeRoot; OutDir = $listDir }
        if ($PresetIncludeFrameworkPresets) { $toolArgs.IncludeFrameworkPresets = $true }
        if ($PresetExtraEffect.Count -gt 0) { $toolArgs.ExtraEffect = $PresetExtraEffect }
        & $listTool @toolArgs

        $ShaderAllowList = Join-Path $listDir "shaders.txt"
        $TextureAllowList = Join-Path $listDir "textures.txt"
    }

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

    # 5) ReShade.ini：相对路径（+ 可选：把本机插件开关状态从出厂包里摘掉）
    $sourceIni = Join-Path $shadeRoot "ReShade.ini"
    if (Test-Path $sourceIni) {
        $targetIni = Join-Path $target "ReShade.ini"
        ConvertTo-PortableIni -SourceIni $sourceIni -ShadeRoot $shadeRoot -TargetIni $targetIni
        Write-Host "      ReShade.ini        已改写为相对路径" -ForegroundColor DarkGray

        if ($PSBoundParameters.ContainsKey('DisabledAddons')) {
            Set-IniKey -Path $targetIni -Section "ADDON" -Key "DisabledAddons" -Value $DisabledAddons
            if ([string]::IsNullOrEmpty($DisabledAddons)) {
                Write-Host "      ReShade.ini        DisabledAddons 已清空（不把本机插件开关带出厂）" -ForegroundColor DarkGray
            } else {
                Write-Host "      ReShade.ini        DisabledAddons => $DisabledAddons" -ForegroundColor DarkGray
            }
        }
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
