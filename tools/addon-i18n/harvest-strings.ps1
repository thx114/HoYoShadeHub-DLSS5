# Harvest UI-ish strings from ReShade addon DLLs (PE parsing, no dependencies).
# Strategy:
#   1) parse PE sections
#   2) collect printable ASCII runs from non-executable data sections (.rdata/.data)
#   3) keep only strings referenced by code: RIP-relative dword in .text, or absolute qword in .data/.rdata
#   4) filter: UI-looking (Capitalized labels / sentences), drop log & framework noise
param(
    [string[]] $Paths = @(),
    [int] $MinLength = 3,
    [int] $MaxLength = 90,
    [int] $Sample = 0,
    [string] $OutDirectory = ""
)

function Get-PeSections([byte[]] $bytes) {
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) { throw "not a PE" }
    $coff = $peOffset + 4
    $sectionCount = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $optional = $coff + 20
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $imageBase = if ($magic -eq 0x20B) { [BitConverter]::ToUInt64($bytes, $optional + 24) } else { [uint64][BitConverter]::ToUInt32($bytes, $optional + 28) }
    $sections = @()
    $cursor = $optional + $optionalSize
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $cursor + ($i * 40)
        $name = [System.Text.Encoding]::ASCII.GetString($bytes, $s, 8).Trim([char]0)
        $va = [BitConverter]::ToUInt32($bytes, $s + 12)
        $rawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
        $rawPtr = [BitConverter]::ToUInt32($bytes, $s + 20)
        $chars = [BitConverter]::ToUInt32($bytes, $s + 36)
        $sections += [pscustomobject]@{
            Name  = $name
            VA    = [uint64]$va
            Size  = [uint64]$rawSize
            Ptr   = [uint64]$rawPtr
            Exec  = (($chars -band 0x20000000) -ne 0)
            Writable = (($chars -band 0x80000000) -ne 0)
        }
    }
    return [pscustomobject]@{ ImageBase = $imageBase; Sections = $sections }
}

function Get-CandidateStrings([byte[]] $bytes, $pe) {
    $result = @()
    $skip = @(".pdata", ".rsrc", ".reloc", ".tls", ".debug", ".bss", ".idata", ".edata", ".CRT")
    foreach ($section in $pe.Sections) {
        if ($skip -contains $section.Name) { continue }
        if ($section.Exec) { continue }
        if ($section.Ptr -eq 0 -or $section.Size -eq 0) { continue }
        $end = [Math]::Min([int64]($section.Ptr + $section.Size), $bytes.LongLength)
        $start = 0
        for ($j = $section.Ptr; $j -lt $end; $j++) {
            $c = $bytes[$j]
            $printable = ($c -ge 0x20 -and $c -le 0x7E)
            if ($printable) {
                if ($start -eq 0) { $start = $j }
            } else {
                if ($start -ne 0) {
                    $length = $j - $start
                    if ($length -ge $MinLength -and $length -le $MaxLength -and $c -eq 0) {
                        $text = [System.Text.Encoding]::ASCII.GetString($bytes, $start, $length)
                        $va = $pe.ImageBase + $section.VA + ($start - $section.Ptr)
                        $result += [pscustomobject]@{ Text = $text; VA = $va; FileOffset = $start; Length = $length }
                    }
                    $start = 0
                }
            }
        }
    }
    return $result
}

function Get-ReferencedVa([byte[]] $bytes, $pe, $candidates) {
    $starts = New-Object 'System.Collections.Generic.HashSet[uint64]'
    foreach ($candidate in $candidates) { [void]$starts.Add($candidate.VA) }
    $hit = New-Object 'System.Collections.Generic.HashSet[uint64]'

    foreach ($section in $pe.Sections) {
        if ($section.Ptr -eq 0 -or $section.Size -eq 0) { continue }
        $end = [Math]::Min([int64]($section.Ptr + $section.Size), $bytes.LongLength)
        if ($section.Exec) {
            # RIP-relative dword references
            for ($j = $section.Ptr; $j -lt $end - 4; $j++) {
                $disp = [BitConverter]::ToInt32($bytes, [int]$j)
                $target = $pe.ImageBase + $section.VA + ($j - $section.Ptr) + 4 + [int64]$disp
                if ($target -gt 0 -and $starts.Contains([uint64]$target)) { [void]$hit.Add([uint64]$target) }
            }
        } elseif (-not $section.Name.StartsWith(".reloc")) {
            # absolute qword pointers (ImGui tables etc.)
            for ($j = $section.Ptr; $j -lt $end - 8; $j += 1) {
                $q = [BitConverter]::ToUInt64($bytes, [int]$j)
                if ($q -gt $pe.ImageBase -and $starts.Contains($q)) { [void]$hit.Add($q) }
            }
        }
    }
    return $hit
}

function Test-UiLooking([string] $text) {
    if ($text -match "::|\.\?A|\?\?|\.dll|\.exe|\.cpp|\.h$|^[A-Z0-9_]{6,}$") { return $false }
    if ($text -match "(?i)\b(failed|failure|error|invalid|unsupported|unable|skipped|attach|detach|created|destroyed|assert|assertion|0x[0-9a-f]{4,})\b") { return $false }
    if ($text -match "%[0-9.+\-]*[duifszxX]") { return $false }
    $words = ($text -split "\s+") | Where-Object { $_ -match "^[A-Za-z]" }
    if ($text -match " ") {
        if ($text -notmatch "^[A-Z#\[]") { return $false }
        return ($words.Count -ge 2)
    }
    $single = @("Enabled","Disabled","Enable","Disable","On","Off","Auto","Automatic","Manual","Default","Reset","Apply","Save","Load","Reload","Refresh","Quality","Balanced","Balance","Performance","Ultra","High","Medium","Low","Strength","Passes","Pass","Mode","Preset","Resolution","Scale","Sharpness","Denoise","Detail","Details","Colour","Color","Lighting","Skin","Scenery","Frame","Generation","Hook","Method","Stage","Override","Profile","Advanced","Debug","Logging","Verbose","Native","Custom","Pre","Post","Upscaling","Upscaler","Rendering","Debug","Help","About","None","Info","Warning","Close")
    return ($single -contains $text)
}

$summary = @()
foreach ($path in $Paths) {
    if (-not (Test-Path $path)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $pe = Get-PeSections $bytes
    $candidates = Get-CandidateStrings $bytes $pe
    $referenced = Get-ReferencedVa $bytes $pe $candidates

    $ui = @()
    foreach ($candidate in $candidates) {
        if (-not $referenced.Contains($candidate.VA)) { continue }
        if (-not (Test-UiLooking $candidate.Text)) { continue }
        $ui += $candidate.Text
    }
    $ui = $ui | Sort-Object -Unique

    $name = Split-Path $path -Leaf
    $summary += [pscustomobject]@{ File = $name; Candidates = $candidates.Count; Referenced = $referenced.Count; Ui = $ui.Count }
    if ($OutDirectory -ne "") {
        New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
        $ui | Set-Content -Path (Join-Path $OutDirectory ($name + ".txt")) -Encoding UTF8
    }
    Write-Host ("=== " + $name + "  候选 " + $candidates.Count + " / 被引用 " + $referenced.Count + " / UI 文本 " + $ui.Count)
    if ($Sample -gt 0) {
        $ui | Select-Object -First $Sample | ForEach-Object { "    " + $_ }
    }
}
Write-Host ""
$summary | Format-Table -AutoSize
