param(
    [string] $Architecture = "x64",
    [string] $Version = "1.0.0",
    [string] $Output = "build/HoYoShadeHub",
    [switch] $Dev
)

$ErrorActionPreference = "Stop";

if ($Dev) {
    dotnet publish src/HoYoShadeHub -c Release -r "win-$Architecture" -o "$Output/app-$Version" -p:Platform=$Architecture -p:DefineConstants=DEV -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:Version=$Version;
}
else {
    dotnet publish src/HoYoShadeHub -c Release -r "win-$Architecture" -o "$Output/app-$Version" -p:Platform=$Architecture -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:Version=$Version;
}

$env:Path += ';C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\';
$env:Path += ';C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\';

msbuild src/HoYoShadeHub.Launcher "-property:Configuration=Release;Platform=$Architecture;OutDir=$(Resolve-Path "$Output/")";

# 覆盖写（不是追加）：Add-Content 会让 version.ini 攒多条 exe_path，而便携启动器取第一条存在的 —— 会启动到旧版本。
# 带 BOM 的 UTF8，和部署流程手写的 version.ini 保持一致
[System.IO.File]::WriteAllText("$Output/version.ini", "exe_path=app-$Version\HoYoShadeHub.exe" + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($true)));

Remove-Item "$Output/HoYoShadeHub.pdb" -Force;