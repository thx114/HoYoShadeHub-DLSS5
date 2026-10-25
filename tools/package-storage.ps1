# 只处理明确生成的打包路径；不把源码、SDK、用户安装或历史取证目录当作打包垃圾。
function Remove-PackageArtifact {
    param(
        [Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)][string] $AllowedRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]] $Records,
        [string] $CleanupRoot = 'D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup'
    )
    $full = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\', '/')
    $scope = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\', '/')
    if (-not $full.StartsWith($scope + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理范围外路径或范围根目录：$full（范围：$scope）"
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    # 先核对最终绝对路径和重解析点，再执行递归删除；不跟随 junction 到范围外。
    for ($part = $full; ; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "打包清理路径包含重解析点：$part"
        }
        if ($part.Equals($scope, [StringComparison]::OrdinalIgnoreCase)) { break }
    }
    $item = Get-Item -LiteralPath $full -Force
    $children = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $full -Recurse -Force) } else { @() }
    if (@($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "打包清理目录内包含重解析点：$full"
    }
    $bytes = if ($item.PSIsContainer) { ($children | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum } else { $item.Length }
    try {
        Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
        $Records.Add([pscustomobject]@{ path = $full; action = 'deleted'; logicalBytes = [long]$bytes })
        Write-Host "==> 清理打包产物：$full" -ForegroundColor DarkGray
    }
    catch {
        $deleteError = $_.Exception.Message
        $fallbackRoot = [IO.Path]::GetFullPath($CleanupRoot)
        $destination = Join-Path $fallbackRoot ('HoyoDLSS5-package-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $destination = Join-Path $destination $item.Name
        if (-not $destination.StartsWith($fallbackRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '清理转移路径越界。' }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Move-Item -LiteralPath $full -Destination $destination -ErrorAction Stop
        $Records.Add([pscustomobject]@{ path = $full; action = 'moved'; logicalBytes = [long]$bytes; destination = $destination; deleteError = $deleteError })
        Write-Warning "删除失败，已转移到：$destination（同盘转移不代表释放空间）"
    }
}

function Clear-PreviousPackages {
    param(
        [Parameter(Mandatory)][string] $RepoRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]] $Records,
        [string] $ReleaseDirectory,
        [string] $PreserveOutput,
        [string[]] $ProtectedPaths = @(),
        [string] $CleanupRoot = 'D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup'
    )
    $buildRoot = Join-Path $RepoRoot 'build'
    $candidates = @(Get-ChildItem -LiteralPath $buildRoot -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(full-\d|portable-\d|HoYoShadeHub$|portable-launcher$)' })
    foreach ($release in @((Join-Path $buildRoot 'release'), $ReleaseDirectory) | Where-Object { $_ } | Select-Object -Unique) {
        $candidates += @(Get-ChildItem -LiteralPath $release -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^HoYoShadeHub_Portable_.+\.(zip|7z)(\.partial)?$' -or ($_.PSIsContainer -and $_.Name -like 'HoYoShadeHub_Portable_*') })
    }
    foreach ($candidate in $candidates | Sort-Object FullName -Unique) {
        if ($PreserveOutput -and ([IO.Path]::GetFullPath($PreserveOutput)).StartsWith($candidate.FullName.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($PreserveOutput -and $candidate.FullName.Equals([IO.Path]::GetFullPath($PreserveOutput), [StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($protected in $ProtectedPaths) {
            if (([IO.Path]::GetFullPath($protected)).StartsWith($candidate.FullName.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $candidate.FullName.Equals([IO.Path]::GetFullPath($protected), [StringComparison]::OrdinalIgnoreCase)) {
                throw "打包源位于待清理目录内：$protected"
            }
        }
        Remove-PackageArtifact -LiteralPath $candidate.FullName -AllowedRoot ([IO.Path]::GetDirectoryName($candidate.FullName)) -Records $Records -CleanupRoot $CleanupRoot
    }
}
