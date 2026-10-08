<#
.SYNOPSIS
  从 ReShade 预设（.ini）推导「只带必要滤镜 / 材质」的白名单，给 package-full.ps1 的
  -ShaderAllowList / -TextureAllowList 用。

.DESCRIPTION
  完整包以前是把 HoYoShade 框架的 reshade-shaders\Shaders 与 Textures 整目录照搬
  （766 个滤镜 / 161 个材质 / 约 100 MB），而 ReShade 会把 EffectSearchPaths 下的每个 .fx
  都编译一遍，多余的滤镜既占体积又拖慢启动。本脚本按「实际用到的预设」算最小闭包：

    1. 根特效：预设里 `Techniques=` 行（`名字@文件.fx` 取 @ 后面的文件）与所有 `[某文件.fx]` 配置节
    2. 递归闭包：每个根特效的 `#include "..."`，解析顺序 = 当前文件所在目录 → 滤镜根 → 材质根 → 框架根 →
       （兜底）按文件名在滤镜/材质树里找唯一同名文件
    3. 材质：闭包内所有字符串字面量里出现的图片/立方图（.png/.dds/.jpg/.jpeg/.bmp/.tga/.cube）
    4. 输出：相对路径清单（正斜杠、一行一个、`#` 开头是注释），写在 -OutDir 下 shaders.txt / textures.txt

  注意：`TechniqueSorting=` 不是启用列表（它只是排序表，动辄几百项），所以**不**作为根。

.PARAMETER PresetIni
  预设 ini 路径。可以多个，用逗号分隔。

.PARAMETER ShadeRoot
  HoYoShade 框架目录（里面有 reshade-shaders\Shaders 与 reshade-shaders\Textures）。
  默认按 参数 → HYSHADE_SHADE_SOURCE → 仓库下 HoYoShade → D:\APPS\HoYoShadeHub\HoYoShade 顺序找。

.PARAMETER OutDir
  清单输出目录，默认 build\shader-allowlist。

.PARAMETER IncludeFrameworkPresets
  连带框架自带 Presets\*.ini（不含 .bak）里用到的特效一起带 —— 这样「Mod OFF / FSR Bridge Depth Debug /
  角色柔化」等随包预置切过去也能用，代价是白名单从个位数涨到几十个特效。

.PARAMETER ExtraEffect
  额外要塞进闭包的特效文件名（例如插件自己带的 FSRBridgeDepthView.fx）。

.EXAMPLE
  .\tools\make-shader-allowlist.ps1 -PresetIni 'D:\...\HoYoShade DX11 Before NR.ini'
  .\tools\make-shader-allowlist.ps1 -PresetIni '<游戏预设>' -IncludeFrameworkPresets
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $PresetIni,

    [string] $ShadeRoot = "",

    [string] $OutDir = "build\shader-allowlist",

    [switch] $IncludeFrameworkPresets,

    [string[]] $ExtraEffect = @()
)

$ErrorActionPreference = "Stop"

function Resolve-ShadeRoot {
    param([string] $Explicit)

    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:HYSHADE_SHADE_SOURCE) { $candidates += $env:HYSHADE_SHADE_SOURCE }
    $candidates += (Join-Path $PSScriptRoot "..\HoYoShade")
    $candidates += "D:\APPS\HoYoShadeHub\HoYoShade"

    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if (Test-Path (Join-Path $c "ReShade64.dll")) { return (Resolve-Path $c).Path }
    }
    throw "找不到 HoYoShade 框架目录（要求里面有 ReShade64.dll）。用 -ShadeRoot 指定，或设置 HYSHADE_SHADE_SOURCE。"
}

# 建立「小写文件名 -> 文件列表」索引，供引用解析兜底
function New-NameIndex {
    param([string] $Root)

    $index = @{}
    if (-not (Test-Path $Root)) { return $index }
    foreach ($f in Get-ChildItem -Path $Root -Recurse -File) {
        $key = $f.Name.ToLowerInvariant()
        if (-not $index.ContainsKey($key)) { $index[$key] = New-Object System.Collections.Generic.List[System.IO.FileInfo] }
        $index[$key].Add($f)
    }
    return $index
}

# 解析一个预设：返回根特效文件名集合（不含 .addonfx）
function Get-PresetRootEffects {
    param([string] $Path)

    $roots = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path $Path)) { throw "预设不存在：$Path" }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8

    $m = [regex]::Match($text, '(?m)^Techniques=(.*)$')
    if ($m.Success) {
        foreach ($item in ($m.Groups[1].Value -split ',')) {
            $item = $item.Trim()
            if (-not $item) { continue }
            $file = ($item -split '@')[-1].Trim()
            if ($file -and $file -notlike "*.addonfx") { [void]$roots.Add($file) }
        }
    }

    foreach ($sec in [regex]::Matches($text, '(?m)^\[([^\]\r\n]+\.(?:fx))\]')) {
        [void]$roots.Add($sec.Groups[1].Value.Trim())
    }

    return $roots
}

$shade = Resolve-ShadeRoot -Explicit $ShadeRoot
$shaderRoot = Join-Path $shade "reshade-shaders\Shaders"
$textureRoot = Join-Path $shade "reshade-shaders\Textures"
if (-not (Test-Path $shaderRoot)) { throw "找不到滤镜目录：$shaderRoot" }
if (-not (Test-Path $textureRoot)) { throw "找不到材质目录：$textureRoot" }

$shaderIndex = New-NameIndex -Root $shaderRoot
$textureIndex = New-NameIndex -Root $textureRoot

$presets = @()
foreach ($p in $PresetIni) { if ($p) { $presets += $p } }
if ($IncludeFrameworkPresets) {
    $presetDir = Join-Path $shade "Presets"
    if (Test-Path $presetDir) {
        $presets += @(Get-ChildItem $presetDir -File -Filter "*.ini" | Where-Object { $_.Name -notlike "*.bak*" } | ForEach-Object { $_.FullName })
    }
}

$rootEffects = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
$perPreset = @()
foreach ($p in $presets) {
    $r = Get-PresetRootEffects -Path $p
    foreach ($x in $r) { [void]$rootEffects.Add($x) }
    $perPreset += [pscustomobject]@{ Preset = (Split-Path $p -Leaf); Roots = @($r) }
}
foreach ($x in $ExtraEffect) { if ($x) { [void]$rootEffects.Add($x) } }

# 引用 -> 实际文件：当前文件目录 → 滤镜根 → 材质根 → 框架根 → 同名兜底
function Resolve-Reference {
    param([string] $Reference, [string] $FromFile)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($FromFile) { $candidates.Add((Join-Path (Split-Path $FromFile -Parent) $Reference)) }
    $candidates.Add((Join-Path $shaderRoot $Reference))
    $candidates.Add((Join-Path $textureRoot $Reference))
    $candidates.Add((Join-Path $shade $Reference))

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return (Resolve-Path -LiteralPath $c).Path }
    }

    $name = (Split-Path $Reference -Leaf).ToLowerInvariant()
    foreach ($index in @($shaderIndex, $textureIndex)) {
        if ($index.ContainsKey($name) -and $index[$name].Count -gt 0) { return $index[$name][0].FullName }
    }
    return $null
}

$includePattern = [regex]'#include\s+["<]([^">\r\n]+)[">]'
$texturePattern = [regex]'"([^"\r\n]+\.(?:png|dds|jpg|jpeg|bmp|tga|cube))"'

$shaderClosure = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
$textureClosure = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
$unresolvedIncludes = New-Object System.Collections.Generic.List[string]
$unresolvedTextures = New-Object System.Collections.Generic.List[string]

$queue = New-Object System.Collections.Generic.Queue[string]
foreach ($name in $rootEffects) {
    $resolved = Resolve-Reference -Reference $name -FromFile $null
    if ($null -eq $resolved) { $unresolvedIncludes.Add("根特效找不到：$name"); continue }
    $queue.Enqueue($resolved)
}

$visited = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
while ($queue.Count -gt 0) {
    $file = $queue.Dequeue()
    if (-not $visited.Add($file)) { continue }
    $isTexture = $file.StartsWith($textureRoot, [StringComparison]::OrdinalIgnoreCase)
    if ($isTexture) { [void]$textureClosure.Add($file) } else { [void]$shaderClosure.Add($file) }

    $text = Get-Content -LiteralPath $file -Raw -Encoding UTF8

    foreach ($m in $includePattern.Matches($text)) {
        $ref = $m.Groups[1].Value.Trim()
        $target = Resolve-Reference -Reference $ref -FromFile $file
        if ($null -eq $target) {
            $unresolvedIncludes.Add("$(Split-Path $file -Leaf) -> $ref")
            continue
        }
        if (-not $visited.Contains($target)) { $queue.Enqueue($target) }
    }

    foreach ($m in $texturePattern.Matches($text)) {
        $ref = $m.Groups[1].Value.Trim()
        $target = Resolve-Reference -Reference $ref -FromFile $file
        if ($null -eq $target) {
            $unresolvedTextures.Add("$(Split-Path $file -Leaf) -> $ref")
            continue
        }
        if (-not $visited.Contains($target)) { $queue.Enqueue($target) }
    }
}

function Get-RelativeList {
    param([System.Collections.Generic.HashSet[string]] $Set, [string] $Root)

    $prefix = $Root.TrimEnd("\") + "\"
    return @($Set | ForEach-Object { $_.Substring($prefix.Length).Replace("\", "/") } | Sort-Object)
}

$shaderList = Get-RelativeList -Set $shaderClosure -Root $shaderRoot
$textureList = Get-RelativeList -Set $textureClosure -Root $textureRoot

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$shaderOut = Join-Path $OutDir "shaders.txt"
$textureOut = Join-Path $OutDir "textures.txt"

$header = @(
    "# 由 tools\make-shader-allowlist.ps1 生成，不要手改（改预设后重跑）",
    "# 预设来源："
) + @($presets | ForEach-Object { "#   $_" }) + @(
    "# 根特效 $($rootEffects.Count) 个；闭包滤镜 $($shaderList.Count) 个、材质 $($textureList.Count) 个",
    "# package-full.ps1 -ShaderAllowList '$shaderOut' -TextureAllowList '$textureOut'"
)
Set-Content -LiteralPath $shaderOut -Value (($header + $shaderList) -join "`r`n") -Encoding UTF8
Set-Content -LiteralPath $textureOut -Value (($header + $textureList) -join "`r`n") -Encoding UTF8

$shaderBytes = ($shaderClosure | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
$textureBytes = ($textureClosure | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
$allShaderBytes = (Get-ChildItem $shaderRoot -Recurse -File | Measure-Object Length -Sum).Sum
$allTextureBytes = (Get-ChildItem $textureRoot -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host "框架来源   : $shade"
Write-Host "解析预设   : $($presets.Count) 个"
foreach ($item in $perPreset) { Write-Host ("  {0,-34} 根特效 {1}: {2}" -f $item.Preset, $item.Roots.Count, (($item.Roots | Sort-Object) -join ", ")) }
Write-Host ("根特效合计 : {0} 个 -> {1}" -f $rootEffects.Count, (($rootEffects | Sort-Object) -join ", "))
Write-Host ("滤镜闭包   : {0} / {1} 个，{2:N1} / {3:N1} MB" -f $shaderList.Count, (Get-ChildItem $shaderRoot -Recurse -File).Count, ($shaderBytes / 1MB), ($allShaderBytes / 1MB))
Write-Host ("材质闭包   : {0} / {1} 个，{2:N1} / {3:N1} MB" -f $textureList.Count, (Get-ChildItem $textureRoot -Recurse -File).Count, ($textureBytes / 1MB), ($allTextureBytes / 1MB))
Write-Host "清单输出   : $shaderOut , $textureOut"

if ($unresolvedIncludes.Count -gt 0) {
    Write-Host "未解析的 #include（需要人工确认，可能是 ReShade 内置文件）：" -ForegroundColor Yellow
    $unresolvedIncludes | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
if ($unresolvedTextures.Count -gt 0) {
    Write-Host "未解析的材质引用（需要人工确认）：" -ForegroundColor Yellow
    $unresolvedTextures | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
