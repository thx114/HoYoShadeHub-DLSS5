[CmdletBinding()]
param(
    [int]$WaitSeconds = 60,
    [int]$WarmupFrame = 600
)

$ErrorActionPreference = 'Continue'
$hub = 'D:\APPS\HoYoShadeHub\HoYoShadeHub.exe'
$log = 'D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.log'

$args = @('testgame', '--biz', 'hk4e_cn', '--seconds', "$WaitSeconds")
$run = Start-Process -FilePath $hub -ArgumentList $args -Wait -PassThru -NoNewWindow
Write-Output ("testgame_exit=" + $run.ExitCode)

$maxFrame = 0L
$maxEvaluate = 0L
$copyback = 0
if (Test-Path $log) {
    foreach ($line in (Get-Content $log -Tail 2000 -ErrorAction SilentlyContinue)) {
        if ($line -match 'private copy enqueued total=(\d+)') {
            $n = [int64]$Matches[1]
            if ($n -gt $maxFrame) { $maxFrame = $n }
        }
        if ($line -match 'DLSSG EvaluateFeature succeeded count=(\d+)') {
            $n = [int64]$Matches[1]
            if ($n -gt $maxEvaluate) { $maxEvaluate = $n }
        }
        if ($line -match 'DLSSG output copied back to D3D11') { $copyback++ }
    }
}
Write-Output ("warmup_reached=" + $(if ($maxFrame -ge $WarmupFrame) { 'yes' } else { 'no' }))
Write-Output ("max_bridge_frame=" + $maxFrame)
Write-Output ("max_dlssg_evaluate=" + $maxEvaluate)
Write-Output ("dlssg_copyback_events=" + $copyback)

if ($run.ExitCode -ne 0 -or $maxFrame -lt $WarmupFrame -or $maxEvaluate -le 0 -or $copyback -le 0) {
    exit 2
}
