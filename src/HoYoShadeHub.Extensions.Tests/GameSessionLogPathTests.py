"""Compile the collector's pure policy against the existing Extensions test dependency, without UI."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
collector = root / 'src/HoYoShadeHub/Features/GameLauncher/GameSessionLogCollector.cs'
with tempfile.TemporaryDirectory(prefix='hub-log-path-tests-') as temporary:
    work = Path(temporary)
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>')
    policy = collector.read_text(encoding='utf-8-sig').split('// Pure path policy kept with the collector', 1)[1]
    (work / 'Policy.cs').write_text('using HoYoShadeHub.Extensions.ReShade;\n' + '// Pure path policy kept with the collector' + policy)
    reference = root / 'src/HoYoShadeHub.Extensions/HoYoShadeHub.Extensions.csproj'
    (work / 'Tests.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>')
    (work / 'Program.cs').write_text(r'''
using System;
using System.IO;
string root = Path.Combine(Path.GetTempPath(), "opti-path-fixtures-" + Guid.NewGuid());
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
try {
    string build = Path.Combine(root, "0.2.2.5"); Directory.CreateDirectory(build);
    string old = Path.Combine(root, "0.2.2.3"); Directory.CreateDirectory(old);
    DateTime start = DateTime.UtcNow.AddMinutes(-1), now = DateTime.UtcNow;
    void Ini(string text) => File.WriteAllText(Path.Combine(build, "OptiScaler.ini"), "[Log]\n" + text);
    void Log(string path, DateTime time) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); File.SetLastWriteTimeUtc(path, time); }
    string absolute = Path.Combine(old, "OptiScaler.log");
    Ini("LogFileName=" + absolute + "\nSingleFile=true");
    var policy = OptiScalerSessionLogPath.Load(build);
    Check(policy.Path == absolute, "absolute old build path");
    Check(policy.FindCurrent(start, now) == null, "missing configured file");
    Log(absolute, start.AddSeconds(-10));
    Check(policy.FindCurrent(start, now) == null, "old build stale rejected");
    Log(absolute, start.AddSeconds(10));
    Check(policy.FindCurrent(start, now) == absolute, "old build current accepted");
    Ini("LogFileName=logs/custom.log\nSingleFile=true");
    policy = OptiScalerSessionLogPath.Load(build);
    Check(policy.Path == Path.Combine(build, "logs", "custom.log"), "relative build path");
    Ini(""); policy = OptiScalerSessionLogPath.Load(build);
    Check(policy.Path == Path.Combine(build, "OptiScaler.log") && !policy.Multiple, "default");
    Log(policy.Path, start.AddSeconds(-5)); Check(policy.FindCurrent(start, now) == null, "stale default rejected");
    Log(policy.Path, start.AddSeconds(1)); Check(policy.FindCurrent(start, now) == policy.Path, "current default");
    Ini("LogFileName=logs/custom.log\nSingleFile=false"); policy = OptiScalerSessionLogPath.Load(build);
    Check(policy.FindCurrent(start, now) == null, "missing auto file");
    string dir = Path.Combine(build, "logs");
    Log(Path.Combine(dir, "custom_111.log"), start.AddSeconds(-10));
    Check(policy.FindCurrent(start, now) == null, "only stale ticks");
    Log(Path.Combine(dir, "custom_222.log"), start.AddSeconds(10));
    string latest = Path.Combine(dir, "custom_333.log"); Log(latest, start.AddSeconds(20));
    Log(Path.Combine(dir, "custom_fake.log"), start.AddSeconds(30));
    Log(Path.Combine(dir, "other_444.log"), start.AddSeconds(30));
    Log(Path.Combine(dir, "custom_555.log"), now.AddMinutes(1));
    Check(policy.FindCurrent(start, now) == latest, "latest current numeric suffix only");
    Ini("SingleFile=false"); policy = OptiScalerSessionLogPath.Load(build);
    string defaultTicks = Path.Combine(build, "OptiScaler_777.log"); Log(defaultTicks, start.AddSeconds(10));
    Check(policy.FindCurrent(start, now) == defaultTicks, "default ticks");
    File.Delete(Path.Combine(build, "OptiScaler.ini"));
    Check(!OptiScalerSessionLogPath.Load(build).Multiple, "missing ini defaults");
    Console.WriteLine($"PASS {checks} log path checks");
} finally { Directory.Delete(root, true); }
''')
    subprocess.run([str(root / '.dotnet10/dotnet.exe'), 'restore', str(work / 'Tests.csproj'), '--configfile', str(work / 'NuGet.Config'), '--verbosity', 'quiet'], cwd=root, check=True)
    subprocess.run([str(root / '.dotnet10/dotnet.exe'), 'run', '--no-restore', '--project', str(work / 'Tests.csproj'), '--verbosity', 'quiet'], cwd=root, check=True)
