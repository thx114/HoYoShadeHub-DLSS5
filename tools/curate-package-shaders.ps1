# Only call against a freshly copied package tree. Source assets are read-only.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ShadeRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [string[]]$PresetIni = @(),
    [Parameter(Mandatory)][string]$ReportPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ShadeRoot).Path.TrimEnd('\')
$source = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
if ($root -eq $source -or $root.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package staging tree and source must be separate directories.'
}
$shaderRoot = Join-Path $root 'reshade-shaders\Shaders'
$textureRoot = Join-Path $root 'reshade-shaders\Textures'
$files = @(Get-ChildItem -LiteralPath $shaderRoot -Recurse -File | Where-Object Extension -in '.fx', '.fxh')
$texts = @{}
foreach ($file in $files) { $texts[$file.FullName] = [IO.File]::ReadAllText($file.FullName) }
$protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$presets = @($PresetIni) + @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.ini' | ForEach-Object FullName)
foreach ($preset in $presets) {
    foreach ($line in [IO.File]::ReadAllLines($preset)) {
        # Sorting and saved inactive sections are not enabled effects.
        if ($line -notmatch '^\s*Techniques\s*=(.*)$') { continue }
        foreach ($entry in ($Matches[1] -split ',')) {
            $entry = $entry.Trim()
            if (!$entry) { continue }
            if ($entry -match '@([^@]+\.fx)$') {
                [void]$protected.Add([IO.Path]::GetFileName($Matches[1].Replace('/', '\')))
            } else {
                # Older presets can name a technique without a filename; protect all owners.
                foreach ($file in $files) {
                    if ($texts[$file.FullName] -match ('\btechnique(?:\d+)?\s+' + [regex]::Escape($entry) + '\b')) { [void]$protected.Add($file.Name) }
                }
            }
        }
    }
}
$novelty = @('Glitch.fx', 'ASCII.fx', 'NightVision.fx', 'VHS.fx', 'VHS_RA.fx', 'VHSPro.fx', 'Cartoon.fx', 'BilateralComic.fx')
$candidates = @($files | Where-Object { $_.Extension -eq '.fx' -and ($_.Name -in $novelty -or $_.Name -match '^(?:Basic|Meta|Retro|Mattias|TV|zfast_)?CRT(?:[-_.]|[A-Z]|$)') })
$selected = @($candidates | Where-Object { !$protected.Contains($_.Name) })
do {
    $previousCount = $selected.Count
    $paths = @($selected | ForEach-Object FullName)
    $included = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        if ($file.FullName -in $paths) { continue }
        foreach ($match in [regex]::Matches($texts[$file.FullName], '#\s*include\s*["<]([^">]+)[">]')) {
            [void]$included.Add([IO.Path]::GetFileName($match.Groups[1].Value.Replace('/', '\')))
        }
    }
    $selected = @($selected | Where-Object { !$included.Contains($_.Name) })
} while ($selected.Count -ne $previousCount)
$paths = @($selected | ForEach-Object FullName)
$removedImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$keptImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$dynamic = $false
foreach ($file in $files) {
    $isRemoved = $file.FullName -in $paths
    if (!$isRemoved -and $texts[$file.FullName] -match '\bsource\s*=\s*(?!")\S') { $dynamic = $true }
    foreach ($match in [regex]::Matches($texts[$file.FullName], '"([^"\r\n]+\.(?:png|dds|jpg|jpeg|bmp|tga|cube))"', 'IgnoreCase')) {
        $name = [IO.Path]::GetFileName($match.Groups[1].Value.Replace('/', '\'))
        if ($isRemoved) { [void]$removedImages.Add($name) } else { [void]$keptImages.Add($name) }
    }
}
$textures = @(Get-ChildItem -LiteralPath $textureRoot -Recurse -File)
$textureRemove = @()
if (!$dynamic) {
    $textureRemove = @($textures | Group-Object Name | Where-Object { $_.Count -eq 1 -and $removedImages.Contains($_.Name) -and !$keptImages.Contains($_.Name) } | ForEach-Object { $_.Group[0] })
}
$remove = @($selected) + @($textureRemove)
$report = [ordered]@{
    source = $source; staging = $root; applied = [bool]$Apply
    removedShaderCount = $selected.Count; removedTextureCount = $textureRemove.Count
    dynamicTextureSources = $dynamic
    protectedCandidates = @($candidates | Where-Object { $_.FullName -notin $paths } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
    removed = @($remove | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($root.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes = $_.Length } })
}
New-Item -ItemType Directory -Force -Path (Split-Path $ReportPath -Parent) | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
if ($Apply) {
    foreach ($item in $report.removed) {
        $path = Join-Path $root $item.path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) { throw "Staging file changed: $path" }
        Remove-Item -LiteralPath $path -Force
    }
}
Write-Output ([pscustomobject]$report)
