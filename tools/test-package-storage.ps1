$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\package-storage.ps1')
$fixture = Join-Path $PSScriptRoot ('package-storage-fixture-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $fixture 'repo'
$build = Join-Path $repo 'build'
$release = Join-Path $build 'release'
$fallback = Join-Path $fixture 'cleanup'
$records = [System.Collections.Generic.List[object]]::new()
$passed = 0
function Check($condition, $message) {
    if (-not $condition) { throw $message }
    $script:passed++
    Write-Output "PASS $message"
}
try {
    foreach ($dir in @('full-1.0.0\HoYoShadeHub', 'portable-1.0.0', 'portable-launcher', 'release', 'unrelated-evidence')) {
        New-Item -ItemType Directory -Path (Join-Path $build $dir) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $build "$dir\sentinel.txt"), 'fixture')
    }
    [IO.File]::WriteAllText((Join-Path $release 'HoYoShadeHub_Portable_1.0.0_x64_Full.zip'), 'old zip')
    [IO.File]::WriteAllText((Join-Path $release 'overlay.zip'), 'unrelated overlay')
    Clear-PreviousPackages -RepoRoot $repo -Records $records -CleanupRoot $fallback
    Check (-not (Test-Path -LiteralPath (Join-Path $build 'full-1.0.0')) -and -not (Test-Path -LiteralPath (Join-Path $build 'portable-launcher'))) 'previous package stages removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $release 'HoYoShadeHub_Portable_1.0.0_x64_Full.zip'))) 'old portable ZIP removed'
    Check ((Test-Path -LiteralPath (Join-Path $release 'overlay.zip')) -and (Test-Path -LiteralPath (Join-Path $build 'unrelated-evidence\sentinel.txt'))) 'unrelated artifacts retained'
    $outside = Join-Path $fixture 'outside.txt'
    [IO.File]::WriteAllText($outside, 'keep')
    $rejected = $false
    try { Remove-PackageArtifact -LiteralPath $outside -AllowedRoot $build -Records $records -CleanupRoot $fallback } catch { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath $outside)) 'out-of-scope deletion rejected'
    $rejected = $false
    try { Remove-PackageArtifact -LiteralPath $build -AllowedRoot $build -Records $records -CleanupRoot $fallback } catch { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath $build)) 'scope root deletion rejected'
    $preserved = Join-Path $build 'full-2.0.0\HoYoShadeHub'
    New-Item -ItemType Directory -Path $preserved -Force | Out-Null
    Clear-PreviousPackages -RepoRoot $repo -Records $records -PreserveOutput $preserved -CleanupRoot $fallback
    Check (Test-Path -LiteralPath $preserved) 'SkipBaseBuild stage preserved during pre-cleanup'
    $junction = Join-Path $build 'junction'
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $build 'unrelated-evidence') | Out-Null
    $rejected = $false
    try { Remove-PackageArtifact -LiteralPath $junction -AllowedRoot $build -Records $records -CleanupRoot $fallback } catch { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath (Join-Path $build 'unrelated-evidence\sentinel.txt'))) 'junction cleanup rejected without touching target'
    [IO.Directory]::Delete($junction)
    # 模拟删除被拒绝，实际执行 Move-Item，核对用户指定的 fallback 行为。
    $locked = Join-Path $build 'cannot-delete.zip'
    [IO.File]::WriteAllText($locked, 'keep bytes')
    function Remove-Item { throw 'simulated delete denial' }
    try { Remove-PackageArtifact -LiteralPath $locked -AllowedRoot $build -Records $records -CleanupRoot $fallback }
    finally { Microsoft.PowerShell.Management\Remove-Item Function:\Remove-Item -ErrorAction SilentlyContinue }
    $moved = $records | Where-Object action -eq 'moved' | Select-Object -Last 1
    Check ($moved -and -not (Test-Path -LiteralPath $locked) -and [IO.File]::ReadAllText($moved.destination) -eq 'keep bytes') 'delete failure moves original bytes to cleanup'
    Write-Output "Package storage tests: PASS $passed, FAIL 0"
}
finally {
    if (Test-Path Function:\Remove-Item) { Microsoft.PowerShell.Management\Remove-Item Function:\Remove-Item }
    if ($fixture.StartsWith($PSScriptRoot.TrimEnd('\') + '\package-storage-fixture-', [StringComparison]::OrdinalIgnoreCase)) {
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
