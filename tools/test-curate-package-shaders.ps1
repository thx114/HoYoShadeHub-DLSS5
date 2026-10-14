$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot ('..\build\shader-curation-fixture-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw "Fixture already exists: $fixture" }
$source = Join-Path $fixture 'source'
$stage = Join-Path $fixture 'stage'
foreach ($root in @($source, $stage)) {
    $shaders = Join-Path $root 'reshade-shaders\Shaders'
    $textures = Join-Path $root 'reshade-shaders\Textures'
    New-Item -ItemType Directory -Force $shaders, $textures | Out-Null
    Set-Content -LiteralPath (Join-Path $shaders 'ASCII.fx') -Value 'texture T < source="ascii.png"; >;'
    Set-Content -LiteralPath (Join-Path $shaders 'Glitch.fx') -Value 'texture T < source="shared.png"; >;'
    Set-Content -LiteralPath (Join-Path $shaders 'Bloom.fx') -Value 'texture T < source="shared.png"; >;'
    Set-Content -LiteralPath (Join-Path $shaders 'CRT.fx') -Value '// protected by preset'
    Set-Content -LiteralPath (Join-Path $root 'Preset.ini') -Value "Techniques=CRT@CRT.fx`nTechniqueSorting=ASCII@ASCII.fx,Glitch@Glitch.fx`n[ASCII.fx]`nUnusedSavedValue=1"
    Set-Content -LiteralPath (Join-Path $textures 'ascii.png') -Value 'fixture-only'
    Set-Content -LiteralPath (Join-Path $textures 'shared.png') -Value 'fixture-only'
}
$tool = Join-Path $PSScriptRoot 'curate-package-shaders.ps1'
$reportPath = Join-Path $fixture 'report.json'
$before = @(Get-ChildItem -LiteralPath $source -Recurse -File | Get-FileHash | ForEach-Object Hash)
$plan = & $tool -ShadeRoot $stage -SourceRoot $source -ReportPath $reportPath
if ($plan.removedShaderCount -ne 2 -or $plan.removedTextureCount -ne 1) { throw 'Dependency plan incorrect' }
if (!(Test-Path (Join-Path $stage 'reshade-shaders\Shaders\ASCII.fx'))) { throw 'Dry run modified tree' }
$result = & $tool -ShadeRoot $stage -SourceRoot $source -ReportPath $reportPath -Apply
if (Test-Path (Join-Path $stage 'reshade-shaders\Textures\ascii.png')) { throw 'Exclusive texture remains' }
foreach ($file in @('reshade-shaders\Shaders\CRT.fx', 'reshade-shaders\Textures\shared.png')) {
    if (!(Test-Path (Join-Path $stage $file))) { throw "Protected asset missing: $file" }
}
$after = @(Get-ChildItem -LiteralPath $source -Recurse -File | Get-FileHash | ForEach-Object Hash)
if (Compare-Object $before $after) { throw 'Source was modified' }
$again = & $tool -ShadeRoot $stage -SourceRoot $source -ReportPath $reportPath
if ($again.removedShaderCount -ne 0 -or $again.removedTextureCount -ne 0) { throw 'Not idempotent' }
Set-Content -LiteralPath (Join-Path $stage 'reshade-shaders\Shaders\VHS.fx') -Value 'texture T < source="vhs.png"; >;'
Set-Content -LiteralPath (Join-Path $stage 'reshade-shaders\Textures\vhs.png') -Value 'fixture'
Add-Content -LiteralPath (Join-Path $stage 'reshade-shaders\Shaders\Bloom.fx') -Value 'texture U < source=MACRO_TEXTURE; >;'
$dynamic = & $tool -ShadeRoot $stage -SourceRoot $source -ReportPath $reportPath
if (!$dynamic.dynamicTextureSources -or $dynamic.removedTextureCount -ne 0) { throw 'Dynamic texture safety failed' }
Add-Content -LiteralPath (Join-Path $stage 'reshade-shaders\Shaders\Bloom.fx') -Value '#include "VHS.fx"'
$dependency = & $tool -ShadeRoot $stage -SourceRoot $source -ReportPath $reportPath
if ($dependency.removedShaderCount -ne 0) { throw 'Included effect removed' }
try { & $tool -ShadeRoot $source -SourceRoot $source -ReportPath $reportPath; throw 'Same-tree guard missing' }
catch { if ($_.Exception.Message -eq 'Same-tree guard missing') { throw } }
Write-Output 'PASS: dry run, apply, preset and include protection, shared/exclusive/dynamic textures, source SHA, idempotence, source guard.'
