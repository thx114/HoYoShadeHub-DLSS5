
using HoYoShadeHub.Core;
using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Conditions;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.I18n;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System.IO.Compression;
using System.Text.Json;

// Controlled worker for lifecycle regression; no real XXMI/game is touched.
if (args.Length > 0 && args[0] == "owned-process-test-worker")
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

// 临时诊断入口：overlay-install <zip> <HoYoShade 根> <OptiScaler 根> <模块根> <缓存根> [临时目录]
// 拿真包往一份「假装的安装目录」上盖一遍，验证识别 / 落位 / 归档都对。
// overlay-noshade（或 HoYoShade 根传 "-"）= 模拟「本机还没装 HoYoShade」的全新便携包。
if (args.Length >= 6 && (args[0] == "overlay-install" || args[0] == "overlay-noshade"))
{
    if (args.Length >= 7)
    {
        TemporaryFolder.Override = args[6];
    }

    bool noShade = args[0] == "overlay-noshade" || args[2] == "-";
    string probeShade = noShade ? string.Empty : args[2];
    string probeAddons = probeShade.Length == 0 ? string.Empty : Path.Combine(probeShade, "reshade-shaders", "Addons");

    Console.WriteLine("kind    = " + LocalPackageInstaller.DetectKind(args[1]));
    Console.WriteLine("shade   = " + (probeShade.Length == 0 ? "(没装 HoYoShade)" : probeShade));

    try
    {
        LocalPackageInstallResult probeResult = await new LocalPackageInstaller(
            args[3], probeAddons, args[4], null, probeShade.Length == 0 ? null : probeShade, null, args[5])
            .InstallAsync(args[1]);

        Console.WriteLine("summary = " + probeResult.Summary);
        Console.WriteLine("target  = " + probeResult.TargetPath);
        foreach (string line in probeResult.Details)
        {
            Console.WriteLine("detail  = " + line);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
    }

    return 0;
}

// 临时诊断入口：`-- localize-dump <addon 路径>`
// 用真正的 AddonLocalizer 打一遍，然后把所有含中文的字符串打出来 —— 验证输出干不干净
if (args.Length >= 2 && args[0] == "localize-dump")
{
    string dumpDir = Path.Combine(@"D:\CODE\HoyoDLSS5\build", "i18n-dump");
    Directory.CreateDirectory(dumpDir);
    string dumpWork = Path.Combine(dumpDir, Path.GetFileName(args[1]));
    File.Copy(args[1], dumpWork, overwrite: true);

    AddonI18nTable dumpTable = AddonLocalizer.LoadBuiltin().Tables.First(t => t.Slug == "renodx-dlss");
    AddonLocalizeResult dumpResult = AddonLocalizer.Apply(dumpWork, dumpTable, Path.Combine(dumpDir, "bak"));
    Console.WriteLine(dumpResult.Message);

    byte[] dumpOrig = File.ReadAllBytes(args[1]);
    byte[] dumpBytes = File.ReadAllBytes(dumpWork);
    int dumpIndex = 0;
    int dumpCount = 0;
    while (dumpIndex < dumpBytes.Length)
    {
        if (dumpBytes[dumpIndex] == 0) { dumpIndex++; continue; }
        int dumpStart = dumpIndex;
        while (dumpIndex < dumpBytes.Length && dumpBytes[dumpIndex] != 0) { dumpIndex++; }
        int dumpLength = dumpIndex - dumpStart;
        if (dumpLength is < 2 or > 200) { continue; }

        int dumpOrigEnd = dumpStart;
        while (dumpOrigEnd < dumpOrig.Length && dumpOrig[dumpOrigEnd] != 0) { dumpOrigEnd++; }
        string dumpOrigText = System.Text.Encoding.UTF8.GetString(dumpOrig, dumpStart, dumpOrigEnd - dumpStart);
        string dumpNewText = System.Text.Encoding.UTF8.GetString(dumpBytes, dumpStart, dumpLength);
        if (dumpOrigText == dumpNewText) { continue; }

        string dumpHex = string.Join(' ', dumpBytes.Skip(dumpStart).Take(dumpLength + 2).Select(x => x.ToString("X2")));
        Console.WriteLine($"  @{dumpStart}  '{dumpOrigText}'");
        Console.WriteLine($"        -> '{dumpNewText}'");
        Console.WriteLine($"        hex: {dumpHex}");
        dumpCount++;
    }

    Console.WriteLine($"改动 {dumpCount} 条");

    // 诊断：中文立即数后面还紧跟着「带字母的立即数」= 没换完的尾巴
    List<(int Position, int Size)> allImmediates = AddonLocalizer.EnumerateImmediates(dumpBytes);
    Console.WriteLine($"立即数总数 {allImmediates.Count}");

    // 临时诊断：Pass Count 那一片的立即数认出来了没有
    foreach ((int position, int size) in allImmediates)
    {
        if (position >= 130990 && position <= 131040)
        {
            Console.WriteLine($"  立即数 @{position} size={size} bytes={BitConverter.ToString(dumpBytes, position, size)}");
        }
    }

    int leftover = 0;

    for (int i = 0; i < allImmediates.Count; i++)
    {
        (int firstPosition, int firstSize) = allImmediates[i];
        bool hasCjk = false;

        for (int k = 0; k < firstSize && firstPosition + k < dumpBytes.Length; k++)
        {
            if (dumpBytes[firstPosition + k] >= 0x80)
            {
                hasCjk = true;
                break;
            }
        }

        if (!hasCjk)
        {
            continue;
        }

        for (int j = i + 1; j < allImmediates.Count; j++)
        {
            (int position, int size) = allImmediates[j];

            if (position - (firstPosition + firstSize) > 32)
            {
                break;
            }

            int letters = 0;

            for (int k = 0; k < size; k++)
            {
                byte value = dumpBytes[position + k];

                if ((value >= (byte)'A' && value <= (byte)'Z') || (value >= (byte)'a' && value <= (byte)'z'))
                {
                    letters++;
                }
            }

            if (letters >= 2)
            {
                Console.WriteLine($"  尾巴没换: @{position} size={size} '{System.Text.Encoding.UTF8.GetString(dumpBytes, position, size)}'");
                leftover++;
            }
        }
    }

    Console.WriteLine($"疑似没换完的尾巴 {leftover} 处");
    return 0;
}

int _passed = 0;
int _failed = 0;

void Check(bool condition, string message)
{
    if (condition)
    {
        _passed++;
        Console.WriteLine($"  [PASS] {message}");
    }
    else
    {
        _failed++;
        Console.WriteLine($"  [FAIL] {message}");
    }
}

string root = Path.Combine(Path.GetTempPath(), "hysx-e2e-" + Guid.NewGuid().ToString("N")[..8]);
// 复刻 Hub 的真实布局：HoYoShade 全局装在 <用户数据目录>\HoYoShade，而不是 <游戏目录>\HoYoShade
string userDataFolder = Path.Combine(root, "data");
string shadeRoot = Path.Combine(userDataFolder, "HoYoShade");
Directory.CreateDirectory(Path.Combine(shadeRoot, "reshade-shaders", "Shaders"));
Directory.CreateDirectory(Path.Combine(shadeRoot, "reshade-shaders", "Textures"));
Directory.CreateDirectory(Path.Combine(shadeRoot, "reshade-shaders", "Addons"));
Directory.CreateDirectory(Path.Combine(shadeRoot, "Presets"));

File.WriteAllText(Path.Combine(shadeRoot, "ReShade64.dll"), "fake-reshade");
File.WriteAllLines(Path.Combine(shadeRoot, "ReShade.ini"), [
    "[GENERAL]",
    @"EffectSearchPaths=.\reshade-shaders\Shaders\**",
    @"TextureSearchPaths=.\reshade-shaders\Textures\**",
    "",
    "[INPUT]",
    "KeyOverlay=36,0,0,0",
]);

// 用户自己放的文件，卸载时必须原样保留
File.WriteAllText(Path.Combine(shadeRoot, "reshade-shaders", "Shaders", "UserOwn.fx"), "user shader");
File.WriteAllText(Path.Combine(shadeRoot, "Presets", "MyOwnPreset.ini"), "user preset");
// 会被扩展覆盖的既有文件，应该先进备份
File.WriteAllText(Path.Combine(shadeRoot, "reshade-shaders", "Shaders", "Bar.fx"), "ORIGINAL - should be backed up");

Console.WriteLine("== 1. 构造一个假的扩展包 (.zip: addon + shader + preset) ==");
string packagePath = Path.Combine(root, "FakeExt.zip");
string staging = Path.Combine(root, "staging");
Directory.CreateDirectory(Path.Combine(staging, "shaders"));
Directory.CreateDirectory(Path.Combine(staging, "presets"));
File.WriteAllText(Path.Combine(staging, "Foo.addon64"), "fake addon dll");
File.WriteAllText(Path.Combine(staging, "shaders", "Bar.fx"), "shader from extension");
File.WriteAllText(Path.Combine(staging, "presets", "Baz.ini"), "preset from extension");
File.WriteAllText(Path.Combine(staging, "README.md"), "should not be installed");
ZipFile.CreateFromDirectory(staging, packagePath);
Check(File.Exists(packagePath), "测试包已生成");

var manifest = new ExtensionManifest
{
    Id = "test.fake",
    Name = "测试扩展",
    Version = "1.0.0",
    Hosts = ["HoYoShade"],
    Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = packagePath },
    Rules =
    [
        new ExtensionFileRule { Match = "*.addon64", To = "reshade-shaders/Addons", Flatten = true },
        new ExtensionFileRule { Match = "shaders/*.fx", To = "reshade-shaders/Shaders", Flatten = true },
        new ExtensionFileRule { Match = "presets/**", To = "Presets", Flatten = true },
    ],
};

Console.WriteLine("== 2. 定位宿主 ==");
Check(ShadeHostLocator.FromUserDataFolder(userDataFolder) is not null, "从 <用户数据目录> 定位到 HoYoShade（Hub 的真实布局）");
Check(ShadeHostLocator.GetDefaultRoot(userDataFolder) == shadeRoot, @"默认根目录 = <用户数据目录>\HoYoShade");
Check(ShadeHostLocator.FromShadeRoot(shadeRoot) is not null, "从 HoYoShade 根目录直接定位");
Check(ShadeHostLocator.FromUserDataFolder(null) is null, "用户数据目录为空时返回 null 而不是抛异常");
ShadeHost host = ShadeHostLocator.FromUserDataFolder(userDataFolder)!;
Check(host.Exists, "检测到 ReShade64.dll");

var manager = new ExtensionManagerService(host);

Console.WriteLine("== 3. 安装 ==");
ExtensionInstallResult install = await manager.InstallAsync(manifest);
Check(File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Addons", "Foo.addon64")), "addon 落到 reshade-shaders/Addons");
Check(File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Shaders", "Bar.fx")), "shader 落到 reshade-shaders/Shaders");
Check(File.Exists(Path.Combine(shadeRoot, "Presets", "Baz.ini")), "preset 落到 Presets");
Check(!File.Exists(Path.Combine(shadeRoot, "README.md")), "没有规则匹配的 README.md 没有被安装");
Check(install.InstalledFiles.Count == 3, $"账本记录 3 个文件（实际 {install.InstalledFiles.Count}）");
Check(File.Exists(Path.Combine(shadeRoot, ".hysx", "installed.json")), "账本文件已写入");
Check(install.OverwrittenFiles.Contains("reshade-shaders/Shaders/Bar.fx"), "识别出覆盖了既有文件 Bar.fx");
Check(install.BackupDirectory is not null && Directory.Exists(install.BackupDirectory), "覆盖前已备份");
if (install.BackupDirectory is not null)
{
    string backup = Path.Combine(install.BackupDirectory, "reshade-shaders", "Shaders", "Bar.fx");
    Check(File.Exists(backup) && File.ReadAllText(backup).StartsWith("ORIGINAL"), "备份内容正确");
}

Console.WriteLine("== 4. ReShade.ini 的 [ADDON] AddonPath ==");
string ini = File.ReadAllText(Path.Combine(shadeRoot, "ReShade.ini"));
Check(install.ReShadeIniUpdated, "安装 addon 时写入了 ReShade.ini");
Check(ini.Contains("[ADDON]"), "ini 里出现 [ADDON] 段");
Check(ini.Contains(@"AddonPath=.\reshade-shaders\Addons"), "AddonPath 指向 reshade-shaders/Addons");
Check(ini.Contains("KeyOverlay=36,0,0,0"), "原有 [INPUT] 内容未被破坏");
Check(ReShadeIniService.HasUsableAddonPath(host), "复检 AddonPath 可用");
Check(!ReShadeIniService.EnsureAddonPath(host), "重复调用不会重复写文件");

Console.WriteLine("== 5. 状态视图 ==");
var statuses = await manager.GetStatusAsync();
Check(statuses.Any(s => s.Manifest.Id == "test.fake"), "账本里的扩展出现在状态列表");
Check((await manager.GetInstalledAsync()).Count == 1, "GetInstalled 返回 1 条");

Console.WriteLine("== 6. 冲突保护 ==");
var conflicting = new ExtensionManifest
{
    Id = "test.conflict",
    Name = "冲突扩展",
    Version = "1.0.0",
    Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = packagePath },
    Rules = [new ExtensionFileRule { Match = "*.addon64", To = "reshade-shaders/Addons", Flatten = true }],
};
bool rejected = false;
try
{
    await manager.InstallAsync(conflicting);
}
catch (InvalidOperationException ex)
{
    rejected = ex.Message.Contains("占用");
}
Check(rejected, "同一文件被别的扩展占用时拒绝安装");

var mutuallyExclusive = new ExtensionManifest
{
    Id = "test.rival",
    Name = "互斥扩展",
    Version = "1.0.0",
    Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = packagePath },
    ConflictsWith = ["test.fake"],
    Rules = [new ExtensionFileRule { Match = "presets/**", To = "Presets", Flatten = true }],
};
bool conflictRejected = false;
try
{
    await manager.InstallAsync(mutuallyExclusive);
}
catch (InvalidOperationException ex)
{
    conflictRejected = ex.Message.Contains("互斥");
}
Check(conflictRejected, "conflictsWith 命中的扩展被拒绝安装");

Console.WriteLine("== 7. 卸载 ==");
ExtensionUninstallResult uninstall = await manager.UninstallAsync("test.fake");
Check(!File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Addons", "Foo.addon64")), "addon 已删除");
Check(File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Shaders", "UserOwn.fx")), "用户自己的 UserOwn.fx 保留");
Check(File.Exists(Path.Combine(shadeRoot, "Presets", "MyOwnPreset.ini")), "用户自己的预设保留");
Check(uninstall.DeletedFiles.Count == 2, $"删除了 2 个（实际 {uninstall.DeletedFiles.Count}）");
Check(uninstall.KeptOverwrittenFiles.Contains("reshade-shaders/Shaders/Bar.fx"), "被覆盖过的 Bar.fx 不删只摘账本");
Check(File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Shaders", "Bar.fx")), "Bar.fx 仍在原地");
Check((await manager.GetInstalledAsync()).Count == 0, "账本已清空");

Console.WriteLine("== 8. 手改过的文件不删 ==");
await manager.InstallAsync(manifest);
File.WriteAllText(Path.Combine(shadeRoot, "reshade-shaders", "Addons", "Foo.addon64"), "user modified this");
ExtensionUninstallResult uninstall2 = await manager.UninstallAsync("test.fake");
Check(uninstall2.SkippedModifiedFiles.Contains("reshade-shaders/Addons/Foo.addon64"), "哈希对不上的文件被跳过");
Check(File.Exists(Path.Combine(shadeRoot, "reshade-shaders", "Addons", "Foo.addon64")), "被改过的文件确实保留了下来");

Console.WriteLine("== 9. Glob / 前缀剥离 ==");
Check(GlobMatcher.IsMatch("**/*.addon64", "a/b/c/x.addon64"), "** 跨层级");
Check(!GlobMatcher.IsMatch("*.addon64", "a/x.addon64"), "* 不跨层级");
Check(GlobMatcher.IsMatch("shaders/*.fx", "shaders/Bar.fx"), "单层匹配");
string stripped = ExtensionInstaller.StripGlobPrefix("Repo-main/HSR/aaa/bbb.ini", "Repo-main/HSR/**");
Check(stripped == "aaa/bbb.ini", $"剥离字面前缀（实际 {stripped}）");

Console.WriteLine("== 10. 远端插件目录（仓库根 catalog/plugins.json）==");
// 目录的唯一出处是仓库根 catalog/（嵌入式内置表 2026-09-30 移除）。从测试运行目录往上找仓库根。
static string? FindRepoCatalogDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "catalog", "plugins.json")))
        {
            return Path.Combine(dir.FullName, "catalog");
        }

        dir = dir.Parent;
    }

    return null;
}

string? repoCatalogDir = FindRepoCatalogDir();
Check(repoCatalogDir is not null, "从测试运行目录往上找得到仓库根的 catalog/（找不到的话后面的目录断言都会红）");
var builtin = ExtensionCatalogService.LoadFile(Path.Combine(repoCatalogDir ?? "", "plugins.json"))
              ?? new ExtensionCatalogDocument();
var liveEntries = builtin.Extensions.Where(e => !e.Removed).ToArray();
Check(liveEntries.Length >= 6, $"插件目录有 {liveEntries.Length} 个有效条目");
Check(liveEntries.All(e => e.IsValid), "所有条目都通过 IsValid 校验");
Check(liveEntries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == liveEntries.Length, "id 无重复");
// renodx.hkrpg（RenoDX 星铁）按用户要求删掉了 —— 只允许以墓碑（removed: true）形式存在
string[] expectedIds = ["renodx.dlss5", "renodx.dlss5.superanus", "gitc.uplift", "renodx.dlss.sf", "renodx.ue.doffix", "dlss5.neural.interposer", "dlss5.bridge", "hoyoshade.presets"];
Check(expectedIds.All(id => liveEntries.Any(e => e.Id == id)), "插件条目齐全（含 thx114/hoyodlss5 的 Neural Interposer）");
Check(builtin.Extensions.All(e => e.Id != "renodx.hkrpg" || e.Removed), "renodx.hkrpg（RenoDX 星铁）已按用户要求删除（墓碑不算活条目）");

// 「只管理插件」：全局插件页只列会装 addon 的扩展包，滤镜/预设那条不该出现
Check(liveEntries.First(e => e.Id == "hoyoshade.presets").Rules.All(r => !r.To.Contains("Addons")),
    "官方预设合集不装 addon —— 所以全局插件页不会列它");

var dlss5 = liveEntries.First(e => e.Id == "renodx.dlss5");
var dlssSf = liveEntries.First(e => e.Id == "renodx.dlss.sf");
var ueDof = liveEntries.First(e => e.Id == "renodx.ue.doffix");

// 同一个仓库里多个插件族必须靠 tagPattern 区分开
Check(dlss5.Source.Repository == dlssSf.Source.Repository, "rhi-repo 里的多个插件族来自同一仓库");
Check(!System.Text.RegularExpressions.Regex.IsMatch("renodx-dlss-SF-26.0917.1436", dlss5.Source.TagPattern!), "renodx-dlss5- 的 tagPattern 不会误吞 renodx-dlss-SF-");
Check(GlobMatcher.IsMatch(dlssSf.Source.AssetPattern!, "renodx-dlss_SF_26.0917.1436.zip"), "renodx-dlss-SF 资产名匹配");
Check(GlobMatcher.IsMatch(ueDof.Source.AssetPattern!, "renodx-universal_ue-dof-fix.addon64"), "ue-dof-fix 资产名匹配（是裸 addon 不是 zip）");
Check(!string.IsNullOrWhiteSpace(builtin.Extensions.First(e => e.Id == "dlss5.bridge").Source.AssetName),
    "dlss5.bridge 用 assetName 走 atom 快路径（避开 renodx 仓库的巨大 JSON）");

// 互斥必须成对声明，否则用户先装哪个决定能不能装另一个
foreach (var entry in builtin.Extensions.Where(e => e.ConflictsWith is { Length: > 0 }))
{
    foreach (string other in entry.ConflictsWith!)
    {
        var peer = builtin.Extensions.FirstOrDefault(e => e.Id == other);
        Check(peer is not null && (peer.ConflictsWith?.Contains(entry.Id) ?? false),
            $"{entry.Id} 与 {other} 的互斥声明成对");
    }
}

// GITC Uplift（ghostinthecamera/GITC-Uplift）：落位形状必须跟上游压缩包一致 ——
// 压缩包的 Addons 目录整个照搬（addon64 + helper64.exe + GITC-Uplift 文件夹），
// Shaders 里的 .fx 铺进 Shaders，README / LICENSE 不装。
var uplift = liveEntries.First(e => e.Id == "gitc.uplift");
Check(uplift.Tags?.Contains("dlss5") == true, "gitc.uplift 带 dlss5 标签（NR 判定 + dll 需求都靠它）");
Check(uplift.ConflictsWith?.Contains("renodx.dlss5") == true,
    "gitc.uplift 与 RenoDX DLSS5 互斥（上游明确要求先删掉 renodx-dlss*）");
Check(ExtensionAddonMatcher.MatchExtensionId(liveEntries, "gitc-uplift.addon64") == "gitc.uplift",
    "自己手动塞进 Addons 的 gitc-uplift.addon64 也会被这个条目认领");

// 条件表平时由 RemoteCatalogService 从远端（首跑用随包种子）喂进来，这里直接读仓库那份
string upliftConditionsPath = Path.Combine(repoCatalogDir ?? "", "conditions.json");
Check(File.Exists(upliftConditionsPath), "仓库里有 catalog/conditions.json");
AddonConditions.LoadJson(File.ReadAllText(upliftConditionsPath));
Check(AddonConditions.MatchAddon("gitc-uplift") is { Dlss5: true, HookPointCapable: false, RenoDxDlss5: false },
    "conditions：gitc-uplift 算 DLSS5 类，但不是 RenoDX 那套（hook 点控件不该出现）");
Check(AddonNameResolver.GetKnownInternalName("gitc-uplift") == "GITC Uplift",
    "addon 卡片用内部名 GITC Uplift 显示");
AddonConditions.Load(null);   // 还原成内置默认，别影响后面的用例

string upliftStaging = Path.Combine(root, "gitc-uplift-payload");
Directory.CreateDirectory(Path.Combine(upliftStaging, "Addons", "GITC-Uplift"));
Directory.CreateDirectory(Path.Combine(upliftStaging, "Shaders"));
File.WriteAllText(Path.Combine(upliftStaging, "Addons", "gitc-uplift.addon64"), "fake addon");
File.WriteAllText(Path.Combine(upliftStaging, "Addons", "gitc-uplift.addon32"), "fake addon 32");
File.WriteAllText(Path.Combine(upliftStaging, "Addons", "gitc-uplift-helper64.exe"), "fake helper");
File.WriteAllText(Path.Combine(upliftStaging, "Addons", "GITC-Uplift", "Put nvngx_dlssnr.dll here.txt"), "nvngx 放这儿");
File.WriteAllText(Path.Combine(upliftStaging, "Shaders", "Uplift.fx"), "shader");
File.WriteAllText(Path.Combine(upliftStaging, "Shaders", "UpliftMask.fx"), "shader");
File.WriteAllText(Path.Combine(upliftStaging, "README.md"), "不进安装");
File.WriteAllText(Path.Combine(upliftStaging, "LICENSE"), "不进安装");
File.WriteAllText(Path.Combine(upliftStaging, "THIRD_PARTY_NOTICES.md"), "不进安装");

var upliftPayload = new ResolvedExtensionPayload { PayloadRoot = upliftStaging, ResolvedTag = "v1.1.7" };
var upliftPlan = ExtensionInstaller.BuildPlan(upliftPayload, uplift);
var upliftDest = upliftPlan.ToDictionary(p => p.SourceFile, p => p.RelativePath, StringComparer.OrdinalIgnoreCase);
Check(upliftDest.TryGetValue("Addons/gitc-uplift.addon64", out string? upliftAddonDest)
      && upliftAddonDest == "reshade-shaders/Addons/gitc-uplift.addon64",
    $"addon64 → Addons（实际 {upliftAddonDest ?? "无"}）");
Check(upliftDest.TryGetValue("Addons/gitc-uplift-helper64.exe", out string? upliftHelperDest)
      && upliftHelperDest == "reshade-shaders/Addons/gitc-uplift-helper64.exe",
    "helper64.exe 和 addon 并排（上游要求同一版本一起放）");
Check(upliftDest.TryGetValue("Addons/GITC-Uplift/Put nvngx_dlssnr.dll here.txt", out string? upliftNrDest)
      && upliftNrDest == "reshade-shaders/Addons/GITC-Uplift/Put nvngx_dlssnr.dll here.txt",
    "GITC-Uplift 文件夹连层级一起保留（nvngx_dlssnr.dll 要放这儿）");
Check(upliftDest.TryGetValue("Shaders/Uplift.fx", out string? upliftFxDest)
      && upliftFxDest == "reshade-shaders/Shaders/Uplift.fx",
    $"Uplift.fx → reshade-shaders/Shaders（实际 {upliftFxDest ?? "无"}）");
Check(!upliftDest.ContainsKey("Addons/gitc-uplift.addon32"),
    "addon32 不装：HoYoShade 只有 64 位宿主（ReShade64.dll），32 位那份用不上");
Check(upliftDest.Count == 5, $"只落这 5 个文件，README / LICENSE / THIRD_PARTY_NOTICES 不装（实际 {upliftDest.Count}）");

var diag = manager.Diagnose();
Check(diag.AddonPathConfigured, "体检：AddonPath 已配置");
Console.WriteLine();
Console.WriteLine("== 10. 版本下拉的排序（GitHub 按创建时间列，我们按发布时间排）==");
// 真实样本：RankFTW/rhi-repo 的 releases 页前四条。卡片顺序 = 创建顺序，发布时间是乱序的（09-19 / 09-18 / 09-20 / 09-21）。
string cardsHtml =
    "<a href=\"/RankFTW/rhi-repo/releases/tag/renodx-dlss5-6.5.3\">RenoDX DLSS5 6.5.3</a>"
    + "<relative-time class=\"no-wrap\" datetime=\"2026-09-19T17:09:53Z\"></relative-time>"
    + "<a href=\"/RankFTW/rhi-repo/releases/tag/renodx-dlss5-6.4.1\">6.4.1</a>"
    + "<relative-time class=\"no-wrap\" datetime=\"2026-09-18T22:45:36Z\"></relative-time>"
    + "<a href=\"/RankFTW/rhi-repo/releases/tag/renodx-dlss-SF-26.0919.2025\">SF</a>"
    + "<relative-time datetime=\"2026-09-10 23:50:46 UTC\"></relative-time>"   // commit 时间那种写法，不能当成发布时间
    + "<relative-time class=\"no-wrap\" datetime=\"2026-09-20T12:24:35Z\"></relative-time>"
    + "<a href=\"/RankFTW/rhi-repo/releases/tag/DLSS-Enabler-4.10.0.7\">Enabler</a>"
    + "<relative-time class=\"no-wrap\" datetime=\"2026-09-21T05:02:09Z\"></relative-time>";

var cards = GithubReleaseResolver.ExtractReleaseCards(cardsHtml);
Check(cards.Count == 4, $"抓到 4 条 release 卡片（实际 {cards.Count}）");
Check(cards[0].Tag == "renodx-dlss5-6.5.3" && cards[0].Published == DateTimeOffset.Parse("2026-09-19T17:09:53Z"),
    $"卡片顺序还是页面的顺序，第一条带上了发布时间（{cards[0].Tag} / {cards[0].Published:u}）");
Check(cards[2].Tag == "renodx-dlss-SF-26.0919.2025" && cards[2].Published == DateTimeOffset.Parse("2026-09-20T12:24:35Z"),
    "commit 时间那种 datetime 不会被当成发布时间，卡片里第一个 ISO 时间才是");

List<ExtensionVersion> reordered = GithubReleaseResolver.SortByPublishedDescending(cards.Select(c => new ExtensionVersion(c.Tag, c.Published)));
Check(reordered.Select(v => v.Tag).SequenceEqual(["DLSS-Enabler-4.10.0.7", "renodx-dlss-SF-26.0919.2025", "renodx-dlss5-6.5.3", "renodx-dlss5-6.4.1"]),
    "重排后按发布时间新 → 旧：" + string.Join(" > ", reordered.Select(v => v.Tag)));

List<ExtensionVersion> noTime = GithubReleaseResolver.SortByPublishedDescending(
    [new ExtensionVersion("b"), new ExtensionVersion("a", DateTimeOffset.Parse("2026-09-01T00:00:00Z")), new ExtensionVersion("c")]);
Check(noTime[0].Tag == "a" && noTime[1].Tag == "b" && noTime[2].Tag == "c",
    "没拿到时间的垫底，且保持原来的相对顺序：" + string.Join(" > ", noTime.Select(v => v.Tag)));

// atom：<updated> 在 tag 链接**前面**，跨 entry 的正则会把它配到下一条的时间上
string atomXml =
    "<feed><entry>"
    + "<id>tag:github.com,2008:Repository/1172082676/renodx-dlss5-6.5.3</id>"
    + "<updated>2026-09-19T17:09:53Z</updated>"
    + "<link rel=\"alternate\" type=\"text/html\" href=\"https://github.com/RankFTW/rhi-repo/releases/tag/renodx-dlss5-6.5.3\"/>"
    + "<title>RenoDX DLSS5 6.5.3</title>"
    + "</entry><entry>"
    + "<id>tag:github.com,2008:Repository/1172082676/DLSS-Enabler-4.10.0.7</id>"
    + "<updated>2026-09-21T05:02:09Z</updated>"
    + "<link rel=\"alternate\" type=\"text/html\" href=\"https://github.com/RankFTW/rhi-repo/releases/tag/DLSS-Enabler-4.10.0.7\"/>"
    + "</entry></feed>";

List<ExtensionVersion> atomVersions = GithubReleaseResolver.ParseAtom(atomXml);
Check(atomVersions.Count == 2, $"atom 解析出 2 条（实际 {atomVersions.Count}）");
Check(atomVersions[0].Tag == "renodx-dlss5-6.5.3" && atomVersions[0].Published == DateTimeOffset.Parse("2026-09-19T17:09:53Z"),
    $"atom 每条 tag 配的是自己那条的时间，不是下一条的（{atomVersions[0].Tag} / {atomVersions[0].Published:u}）");
Check(atomVersions[1].Tag == "DLSS-Enabler-4.10.0.7" && atomVersions[1].Published == DateTimeOffset.Parse("2026-09-21T05:02:09Z"),
    $"atom 第二条同理（{atomVersions[1].Tag} / {atomVersions[1].Published:u}）");
Check(GithubReleaseResolver.SortByPublishedDescending(atomVersions)[0].Tag == "DLSS-Enabler-4.10.0.7",
    "atom 的顺序不是时间序，重排后才把 09-21 那条放到最前（atom 里它排第二）");


// 目录里的 tagPattern / assetPattern 是人工写的，必须真的连一次 GitHub 才知道对不对。
// 默认不跑，加 --online 才跑。
if (args.Contains("--online"))
{
    Console.WriteLine();
    Console.WriteLine("== 10.1 联网：dll 组件清单 + 真下载一个包（--online）==");
    DllCatalog catalog = await DllComponentCatalog.LoadAsync();
    Check(!catalog.IsEmpty, $"拉到 dll 组件清单（{catalog.Components.Sum(c => c.Value.Count)} 条，错误：{catalog.Error ?? "无"}）");
    Check(catalog.Of("dlssnr").Count >= 4, $"dlssnr 有 {catalog.Of("dlssnr").Count} 个变体（清单里 2 个 + 补的 RTX40 / SF）");
    Check(catalog.Of("streamline").Count > 0 && catalog.Of("dlss").Count > 0, "streamline / dlss 都拿到了");
    Check(catalog.Of("dlssnr")[0].NormalizedVersion.Length > 0, $"版本可归一化：{catalog.Of("dlssnr")[0].Version} → {catalog.Of("dlssnr")[0].NormalizedVersion}");

    DllComponent? smallest = catalog.Of("streamline").FirstOrDefault();
    if (smallest is not null)
    {
        string dllTestDir = Path.Combine(root, "dll-install-test");
        Directory.CreateDirectory(dllTestDir);

        DllInstallResult dllInstall = await DllInstaller.InstallAsync(dllTestDir, smallest);
        Check(dllInstall.Ok, $"真下载并解压了 streamline {smallest.Version}（{dllInstall.Error ?? "ok"}）");
        Check(dllInstall.Installed.Any(n => n.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)),
            $"解出来的是 sl.*.dll：{string.Join(", ", dllInstall.Installed.Take(4))}…（共 {dllInstall.Installed.Count} 个）");

        List<InstalledDll> scanned2 = DllInstaller.Scan(dllTestDir);
        Check(DllInstaller.GetInstalledVersion(scanned2, DllComponentCatalog.FamilyOf("streamline")!) is not null,
            "装完之后能读回版本（PE 版本资源）");
    }

    Console.WriteLine();
    Console.WriteLine("== 11. 联网校验内置条目的 source（--online）==");
    var resolver = new GithubReleaseResolver();
    // 墓碑（removed: true，比如 renodx.hkrpg）只有 id、没有 source，别拿去解析
    foreach (var entry in builtin.Extensions.Where(e => !e.Removed && e.Source.Type == ExtensionSourceType.GithubRelease))
    {
        try
        {
            GithubArtifact? artifact = await resolver.ResolveAsync(entry.Source, default);
            if (artifact is null)
            {
                Check(false, $"{entry.Id}: 没找到匹配 tagPattern({entry.Source.TagPattern}) 的 release");
                continue;
            }

            Check(true, $"{entry.Id}: tag={artifact.Tag} asset={artifact.AssetName} ({Math.Round(artifact.Size / 1024d, 1)} KB)");

            // 非压缩包来源时，规则里的 match 必须能命中资产名，否则装不下去
            if (!ExtensionPackageFetcher.IsArchive(artifact.AssetName))
            {
                Check(entry.Rules.Any(r => GlobMatcher.IsMatch(r.Match, artifact.AssetName)),
                    $"{entry.Id}: 非压缩包资产 {artifact.AssetName} 能被规则 match 命中");
            }
        }
        catch (Exception ex)
        {
            Check(false, $"{entry.Id}: {ex.GetType().Name} {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("== 11.1 联网：版本列表按发布时间倒序、第一条 = 「装最新」解析出来的 tag（--online）==");
    foreach (string id in new[] { "renodx.dlss5", "renodx.dlss.sf" })
    {
        var target = builtin.Extensions.First(e => e.Id == id);
        try
        {
            List<ExtensionVersion> list = await resolver.ListVersionsAsync(target.Source, 30);
            DateTimeOffset? prev = null;
            bool descending = true;
            int timed = 0;
            foreach (ExtensionVersion v in list)
            {
                if (v.Published is null)
                {
                    continue;
                }

                timed++;
                if (prev is not null && v.Published > prev)
                {
                    descending = false;
                }

                prev = v.Published;
            }

            Check(list.Count > 0 && descending,
                $"{id}: {list.Count} 个版本、{timed} 个带时间，时间是倒序的（第一条 {list[0].Tag} / {list[0].Published:u}）");

            GithubArtifact? artifact = await resolver.ResolveAsync(target.Source, default);
            Check(artifact is not null && string.Equals(artifact.Tag, list[0].Tag, StringComparison.OrdinalIgnoreCase),
                $"{id}: 「装最新」解析出 {artifact?.Tag}，和下拉第一条 {list[0].Tag} 一致");
        }
        catch (Exception ex)
        {
            Check(false, $"{id}: {ex.GetType().Name} {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("== 11.3 联网：Veritas 版本列表（published_at 排序，不是 atom 的 updated_at）==");
    try
    {
        var veritasSource = new ExtensionSource
        {
            Type = ExtensionSourceType.GithubRelease,
            Repository = "hessiser/veritas",
            TagPattern = @"^\d",
        };

        List<ExtensionVersion> veritas = await resolver.ListVersionsAsync(veritasSource, 20);
        Check(veritas.Count > 0, $"veritas 读到 {veritas.Count} 个版本（第一条 {veritas.FirstOrDefault()?.Tag}）");

        List<Version> parsed = [.. veritas
            .Select(v => Version.TryParse(v.Tag, out Version? parsedTag) ? parsedTag : null)
            .Where(v => v is not null)
            .Select(v => v!)];

        bool versionDesc = true;
        for (int i = 1; i < parsed.Count; i++)
        {
            if (parsed[i - 1] < parsed[i]) { versionDesc = false; break; }
        }

        Check(parsed.Count == veritas.Count && versionDesc,
            $"版本是「新→旧」版本倒序（第一条 {veritas[0].Tag} / 最后一条 {veritas[^1].Tag}）");

        ExtensionVersion? v48 = veritas.FirstOrDefault(v => v.Tag == "0.2.48");
        Check(v48?.Published is { } p48 && p48.UtcDateTime.ToString("yyyy-MM-dd") == "2026-05-02",
            $"0.2.48 用的是 published_at（2026-05-02），不是 atom 的 updated_at（2026-06-05）：{v48?.Published:u}");
    }
    catch (Exception ex)
    {
        Check(false, $"veritas 版本列表：{ex.GetType().Name} {ex.Message}");
    }
    Console.WriteLine("== 11.2 联网：远端目录（catalog/）逐条解析（--online + 环境变量 HYSX_CATALOG_DIR）==");
    string? catalogDir = Environment.GetEnvironmentVariable("HYSX_CATALOG_DIR");
    if (string.IsNullOrWhiteSpace(catalogDir))
    {
        Console.WriteLine("  （设 HYSX_CATALOG_DIR=<仓库>/catalog 才会跑这一段）");
    }
    else
    {
        string pluginsFile = Path.Combine(catalogDir, "plugins.json");
        if (File.Exists(pluginsFile))
        {
            ExtensionCatalogDocument? remoteDoc = System.Text.Json.JsonSerializer.Deserialize<ExtensionCatalogDocument>(
                File.ReadAllText(pluginsFile),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            foreach (ExtensionManifest remoteEntry in remoteDoc?.Extensions ?? [])
            {
                // 墓碑（removed: true）只是个占位，没有 source / rules，不算不完整
                if (remoteEntry.Removed)
                {
                    Check(!string.IsNullOrWhiteSpace(remoteEntry.Id), "[远端] 墓碑条目带 id");
                    continue;
                }

                Check(remoteEntry.IsValid, $"[远端] {remoteEntry.Id} 清单完整（id / source / rules）");
                if (!remoteEntry.IsValid || remoteEntry.Source.Type != ExtensionSourceType.GithubRelease)
                {
                    continue;
                }

                try
                {
                    GithubArtifact? remoteArtifact = await resolver.ResolveAsync(remoteEntry.Source, default);
                    if (remoteArtifact is null)
                    {
                        Check(false, $"[远端] {remoteEntry.Id} 没解析到 release（tagPattern={remoteEntry.Source.TagPattern}）");
                        continue;
                    }

                    Check(true, $"[远端] {remoteEntry.Id}: tag={remoteArtifact.Tag} asset={remoteArtifact.AssetName}");

                    if (!ExtensionPackageFetcher.IsArchive(remoteArtifact.AssetName))
                    {
                        Check(remoteEntry.Rules.Any(r => GlobMatcher.IsMatch(r.Match, remoteArtifact.AssetName)),
                            $"[远端] {remoteEntry.Id} 规则能命中资产 {remoteArtifact.AssetName}");
                    }
                }
                catch (Exception ex)
                {
                    Check(false, $"[远端] {remoteEntry.Id}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        string modulesFile = Path.Combine(catalogDir, "modules.json");
        if (File.Exists(modulesFile))
        {
            using JsonDocument modulesDoc = JsonDocument.Parse(File.ReadAllText(modulesFile));
            JsonElement modules = modulesDoc.RootElement.GetProperty("modules");
            Check(modules.GetArrayLength() > 0, $"[远端] modules.json 有 {modules.GetArrayLength()} 个模块");
            using var moduleClient = HoYoShadeHub.Extensions.Networking.HysxHttp.CreateClient(timeout: TimeSpan.FromSeconds(30));

            foreach (JsonElement module in modules.EnumerateArray())
            {
                string id = module.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
                string repo = module.TryGetProperty("repository", out JsonElement repoElement) ? repoElement.GetString() ?? "" : "";
                bool removed = module.TryGetProperty("removed", out JsonElement removedElement) && removedElement.GetBoolean();
                Check(removed || (!string.IsNullOrWhiteSpace(id) && repo.Contains('/')),
                    $"[远端] 模块 {id} 是 owner/repo（或墓碑）");

                if (removed || string.IsNullOrWhiteSpace(repo))
                {
                    continue;
                }

                // 仓库树直链型（directFiles）：文件得真的在那儿
                if (module.TryGetProperty("directFiles", out JsonElement files) && files.GetArrayLength() > 0)
                {
                    string branch = module.TryGetProperty("branch", out JsonElement branchElement)
                        ? branchElement.GetString() ?? "main"
                        : "main";

                    foreach (JsonElement file in files.EnumerateArray())
                    {
                        string name = file.GetString() ?? "";
                        string url = HoYoShadeHub.Extensions.Networking.HysxHttp.Apply(
                            $"https://raw.githubusercontent.com/{repo}/{branch}/{name}");

                        try
                        {
                            using var response = await moduleClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            long length = response.Content.Headers.ContentLength ?? -1;
                            Check(response.IsSuccessStatusCode && length != 0,
                                $"[远端] 模块 {id} 的 {name} 能下（{(response.IsSuccessStatusCode ? Math.Round(length / 1024d / 1024d, 1) + " MB" : response.StatusCode.ToString())}）");
                        }
                        catch (Exception ex)
                        {
                            Check(false, $"[远端] 模块 {id} 的 {name}: {ex.GetType().Name} {ex.Message}");
                        }
                    }
                }
            }
        }

        string optiFile = Path.Combine(catalogDir, "optiscaler.json");
        List<OptiScalerSource>? remoteSources = OptiScalerCatalog.LoadFile(optiFile);
        Check(remoteSources is not null && remoteSources.Count > 0, $"[远端] optiscaler.json 读得到（{remoteSources?.Count ?? 0} 条）");

        foreach (OptiScalerSource remoteSource in OptiScalerCatalog.Normalize(remoteSources))
        {
            try
            {
                GithubArtifact? remoteArtifact = await resolver.ResolveAsync(remoteSource.ToExtensionSource(), default);
                Check(remoteArtifact is not null, $"[远端] OptiScaler {remoteSource.Id}: tag={remoteArtifact?.Tag} asset={remoteArtifact?.AssetName}");
            }
            catch (Exception ex)
            {
                Check(false, $"[远端] OptiScaler {remoteSource.Id}: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine("== 12. 每个游戏的 ReShade.ini ==");
// 内容按用户给的真实样本（ZenlessZoneZero）裁剪
string profilePath = Path.Combine(root, "game", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
File.WriteAllText(profilePath, string.Join("\r\n", [
    "# This is the ReShade configuration that generated by HoYoShade V3.",
    "",
    "[ADDON]",
    @"AddonPath=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons\",
    "DisabledAddons=RenoDX DLSS_A@renodx-dlss5-super-anus(1.0.8.18).addon64,RenoDX DLSS@renodx-dlss(9.17.12).addon64",
    "LoadFromDllMain=renodx-dlss(9.17.12).addon64,,renodx-dlss5-super-anus(1.0.8.18).addon64",
    "",
    // 真实样本里这个键在 [RENODX-DLSS]（addon 自己的配置段），不在 [ADDON]
    "[RENODX-DLSS]",
    "DirectNeuralRenderingHookPoint=4",
    "DLSSQualityMode=0",
    "",
    "[GENERAL]",
    @"EffectSearchPaths=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Shaders\**",
    "PerformanceMode=0",
    "",
    "[STYLE]",
    "Alpha=1.000000",
    "FontSize=17",
    "[OVERLAY]",
    "ShowFPS=2",
]) + "\r\n");

var profile = ReShadeProfile.Load(profilePath);
Check(profile.AddonPath == @"D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons", "AddonPath 去掉尾部反斜杠");

var disabled = profile.GetDisabledAddons();
Check(disabled.Count == 2, $"DisabledAddons 解析出 2 条（实际 {disabled.Count}）");
Check(disabled[0].DisplayName == "RenoDX DLSS_A" && disabled[0].FileName == "renodx-dlss5-super-anus(1.0.8.18).addon64", "第一条 Name@File 正确");
Check(profile.IsDisabled("renodx-dlss(9.17.12).addon64"), "IsDisabled 按文件名判断");

var dllMain = profile.GetLoadFromDllMain();
Check(dllMain is { Count: 3 }, $"LoadFromDllMain 解析出 3 个槽（实际 {dllMain?.Count}）");
Check(dllMain is not null && dllMain[1] == "" && dllMain[0] == "renodx-dlss(9.17.12).addon64", "中间空槽位被保留，没有被过滤掉");

Check(profile.GetHookPoint() == 4, "HookPoint 读到 4");

profile.EnableAddon("renodx-dlss(9.17.12).addon64");
var afterEnable = profile.GetDisabledAddons();
Check(afterEnable.Count == 1 && afterEnable[0].FileName.Contains("super-anus"), "只启用了指定那一个，另一个不受影响");

profile.DisableAddon("dlss5-bridge.addon64", "RenoDX DLSS Bridge");
Check(profile.IsDisabled("dlss5-bridge.addon64"), "禁用新插件");
Check(profile.GetDisabledAddons().First(e => e.FileName == "dlss5-bridge.addon64").DisplayName == "RenoDX DLSS Bridge", "带上显示名一起写进 ini");

profile.SetHookPoint(0);
Check(profile.GetHookPoint() == 0, "HookPoint 写成 0（off）");
profile.Save();

// 往返：不相关的节和键必须原样活着
string reloadedText = File.ReadAllText(profilePath);
Check(reloadedText.Contains("[STYLE]") && reloadedText.Contains("Alpha=1.000000"), "往返后 [STYLE] 段完好");
Check(reloadedText.Contains("ShowFPS=2"), "往返后 [OVERLAY] 段完好");
Check(reloadedText.Contains(@"EffectSearchPaths=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Shaders\**"), "往返后 EffectSearchPaths 完好");
int hookIdx = reloadedText.IndexOf("[RENODX-DLSS]", StringComparison.Ordinal);
int hookEnd = reloadedText.IndexOf("\n[", hookIdx + 1, StringComparison.Ordinal);
string hookSectionText = hookEnd < 0 ? reloadedText[hookIdx..] : reloadedText[hookIdx..hookEnd];
Check(hookSectionText.Contains("DirectNeuralRenderingHookPoint=0"), "0 写在 [RENODX-DLSS] 段里（addon 真正读的位置）");
Check(hookSectionText.Contains("DLSSQualityMode=0"), "[RENODX-DLSS] 里别的键没被动");

int addonIdx = reloadedText.IndexOf("[ADDON]", StringComparison.Ordinal);
int addonEnd = reloadedText.IndexOf("\n[", addonIdx + 1, StringComparison.Ordinal);
Check(!reloadedText[addonIdx..addonEnd].Contains("DirectNeuralRenderingHookPoint"), "[ADDON] 里不该有这个键（用户反馈的那个 bug 就是写这儿了）");

var reloaded = ReShadeProfile.Load(profilePath);
Check(reloaded.GetHookPoint() == 0, "重新加载后 HookPoint 仍是 0");
Check(reloaded.GetLoadFromDllMain() is { Count: 3 }, "重新加载后 LoadFromDllMain 槽位数不变");

Console.WriteLine("-- DX11Source（RenoDX DLSS5 呈现模式）--");
Check(profile.GetDx11Source() is null, "默认没有 DX11Source");
profile.SetDx11SourceNative(true);
Check(profile.IsDx11SourceNative(), "写 DX11Source=native 后判定为 native");
profile.Save();
string dx11Text = File.ReadAllText(profilePath);
int dx11SecIdx = dx11Text.IndexOf("[RenoDX.DLSS5]", StringComparison.Ordinal);
int dx11SecEnd = dx11Text.IndexOf("\n[", dx11SecIdx + 1, StringComparison.Ordinal);
string dx11Section = dx11SecEnd < 0 ? dx11Text[dx11SecIdx..] : dx11Text[dx11SecIdx..dx11SecEnd];
Check(dx11Section.Contains("DX11Source=native"), "native 落在 [RenoDX.DLSS5] 段（DLSS5 addon 真正读的位置）");
Check(dx11Text.IndexOf("DX11Source", StringComparison.Ordinal) == dx11Text.LastIndexOf("DX11Source", StringComparison.Ordinal), "全文件只有一处 DX11Source（没往 [ADDON] 里乱写）");
Check(ReShadeProfile.Load(profilePath).IsDx11SourceNative(), "重载后 DX11Source 还是 native");
profile.SetDx11SourceNative(false);
Check(profile.GetDx11Source() is null, "关掉 = 删键（跟随插件默认）");
profile.Save();
Check(!File.ReadAllText(profilePath).Contains("DX11Source"), "关掉后文件里不再有 DX11Source");

Console.WriteLine("-- EnableHooks（无 / 1 / 2 三态）--");
profile.SetEnableHooks(0);
profile.Save();
Check(profile.GetEnableHooksMode() == 0 && profile.GetEnableHooks() is null, "无 = 删掉 EnableHooks 键");
profile.SetEnableHooks(1);
profile.Save();
Check(ReShadeProfile.Load(profilePath).GetEnableHooksMode() == 1, "EnableHooks=1 保存并重载");
profile.SetEnableHooks(2);
profile.Save();
Check(ReShadeProfile.Load(profilePath).GetEnableHooksMode() == 2, "EnableHooks=2 保存并重载");
string enableHooksText = File.ReadAllText(profilePath);
int enableHooksSectionStart = enableHooksText.IndexOf("[RenoDX.DLSS5]", StringComparison.Ordinal);
int enableHooksSectionEnd = enableHooksText.IndexOf("\n[", enableHooksSectionStart + 1, StringComparison.Ordinal);
string enableHooksSection = enableHooksSectionEnd < 0 ? enableHooksText[enableHooksSectionStart..] : enableHooksText[enableHooksSectionStart..enableHooksSectionEnd];
Check(enableHooksSection.Contains("EnableHooks=2"), "2 写在 [RenoDX.DLSS5] 段");
Check(enableHooksText.IndexOf("EnableHooks", StringComparison.Ordinal) == enableHooksText.LastIndexOf("EnableHooks", StringComparison.Ordinal), "全文件只有一处 EnableHooks");
profile.SetEnableHooks(0);
profile.Save();
Check(ReShadeProfile.Load(profilePath).GetEnableHooksMode() == 0 && !File.ReadAllText(profilePath).Contains("EnableHooks"), "重新选择无后文件不再含 EnableHooks");

Console.WriteLine("-- 旧版本误写在 [ADDON] 的那份要能读、并自动搬家 --");
string legacyPath = Path.Combine(root, "legacy", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
File.WriteAllLines(legacyPath, [
    "[ADDON]",
    @"AddonPath=.\reshade-shaders\Addons",
    "DirectNeuralRenderingHookPoint=3",
    "",
    "[GENERAL]",
    "PerformanceMode=0",
]);
var legacy = ReShadeProfile.Load(legacyPath);
Check(legacy.GetHookPoint() == 3, "误写在 [ADDON] 的 HookPoint 仍能读出来（兼容）");
legacy.SetHookPoint(2);
legacy.Save();
string legacyText = File.ReadAllText(legacyPath);
Check(legacyText.Contains("[RENODX-DLSS]") && legacyText.Contains("DirectNeuralRenderingHookPoint=2"), "写的时候落到 [RENODX-DLSS]");
Check(legacyText.IndexOf("DirectNeuralRenderingHookPoint", StringComparison.Ordinal) == legacyText.LastIndexOf("DirectNeuralRenderingHookPoint", StringComparison.Ordinal), "[ADDON] 里那份被顺手删掉（全文件只剩一处）");
Check(ReShadeProfile.Load(legacyPath).GetHookPoint() == 2, "搬家后再读还是 2");

Console.WriteLine("-- 从文件名解析版本 --");
var a1 = AddonFileInfo.Parse("renodx-dlss(9.17.12).addon64");
Check(a1 is { Slug: "renodx-dlss", Version: "9.17.12", Branch: null, IsRenamedDisabled: false }, "renodx-dlss(9.17.12).addon64");

var a2 = AddonFileInfo.Parse("renodx-dlss(ShortFuse_9.11.6).addon64x");
Check(a2 is { Slug: "renodx-dlss", Version: "9.11.6", Branch: "ShortFuse", IsRenamedDisabled: true }, "renodx-dlss(ShortFuse_9.11.6).addon64x 拆出分支 + 识别重命名禁用");

var a3 = AddonFileInfo.Parse("renodx-dlss5-super-anus(1.0.8.18).addon64");
Check(a3 is { Slug: "renodx-dlss5-super-anus", Version: "1.0.8.18" }, "super-anus 的 slug 和版本");

var a4 = AddonFileInfo.Parse("dlss5-bridge.addon64");
Check(a4 is { Slug: "dlss5-bridge", Version: null }, "没有括号 → 版本为 null 而不是崩");

// thx114/hoyodlss5 发布的资产是「名字.版本.addon64」这种写法（实测）
var a5 = AddonFileInfo.Parse("renodx-neural-interposer-nvngx.dll.23.1.0RC1.addon64");
Check(a5 is { Slug: "renodx-neural-interposer-nvngx.dll", Version: "23.1.0RC1", IsRenamedDisabled: false },
    $"点号版本也能解析：slug={a5?.Slug} ver={a5?.Version}");
Check(AddonFileInfo.Parse("renodx-neural-interposer-nvngx.dll.addon64") is { Version: null }, "没有版本段的就不硬编");
Check(AddonFileInfo.Parse("dlss5-bridge.addon64") is { Version: null }, "dlss5-bridge 依然没有文件名版本（靠 PE 兜底）");

Check(AddonFileInfo.Parse("nvngx_dlss.dll") is null, "配套 dll 不被当成插件");
Check(AddonFileInfo.Parse("1.txt") is null, "备注 txt 不被当成插件");
Check(AddonFileInfo.Parse("该插件有内存泄漏问题，重启游戏恢复帧率") is null, "空文件当备注的也不被当成插件");

Directory.CreateDirectory(Path.Combine(root, "fakeaddons"));
foreach (string n in new[] { "renodx-dlss(9.17.12).addon64", "renodx-dlss(ShortFuse_9.11.6).addon64x", "nvngx_dlss.dll", "1.txt", "DLSS 插件包 1.3.2.zip" })
{
    File.WriteAllText(Path.Combine(root, "fakeaddons", n), "x");
}
var scanned = AddonFileInfo.ScanDirectory(Path.Combine(root, "fakeaddons"));
Check(scanned.Count == 2, $"扫目录只挑出 2 个 addon（实际 {scanned.Count}）");

Console.WriteLine();
Console.WriteLine("== 13. addon 内部名的解析策略 ==");
Check(AddonNameResolver.Resolve(null, "renodx-dlss(9.17.12).addon64", learnedName: "RenoDX DLSS", knownName: "X", fallback: "Y") == "RenoDX DLSS",
    "优先级：学到的名字最可信（ReShade 自己写出来的）");
Check(AddonNameResolver.Resolve(null, "renodx-dlss(9.17.12).addon64", knownName: "RenoDX DLSS") == "RenoDX DLSS",
    "没有学到的就用已知映射");
Check(AddonNameResolver.Resolve(null, "renodx-dlss(9.17.12).addon64") == "renodx-dlss",
    "都没有就退回 slug，保证 @ 前面永远有东西（没有 @ 的条目 ReShade 永远匹配不上）");

// 名字缓存自愈
string cachePath = Path.Combine(root, "addon-names.json");
var cache = new AddonNameCache();
int learnedCount = cache.LearnFrom(profile);
Check(learnedCount >= 1, $"从 ini 里学到 {learnedCount} 个名字");
cache.Save(cachePath);
var cache2 = AddonNameCache.Load(cachePath);
Check(cache2.Get("dlss5-bridge.addon64") == "RenoDX DLSS Bridge", "缓存往返后名字还在");

// 拿你机器上真实装的 addon 实测
string realAddons = @"D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons";
if (Directory.Exists(realAddons))
{
    Console.WriteLine("-- 对真实 DLL 实测 --");

    string superAnus = Path.Combine(realAddons, "renodx-dlss5-super-anus(1.0.8.18).addon64");
    if (File.Exists(superAnus))
    {
        string? n = AddonNameResolver.FromVersionResource(superAnus);
        Check(n == "RenoDX DLSS_A", $"super-anus 的 FileDescription = '{n}'（ini 里记的正是 RenoDX DLSS_A）");
    }

    string renodx = Path.Combine(realAddons, "renodx-dlss(9.17.12).addon64");
    if (File.Exists(renodx))
    {
        string? n = AddonNameResolver.FromVersionResource(renodx);
        Check(n is null, $"renodx-dlss 的 FileDescription 是空的（所以版本资源不能当唯一手段），实际 '{n}'");

        // 版本资源拿不到，试试二进制里找候选名
        string? matched = AddonNameResolver.MatchAgainstStrings(renodx, ["RenoDX DLSS", "RenoDX", "DLSS"]);
        Check(matched == "RenoDX DLSS", $"二进制里能匹配到候选名 '{matched}'（这才是 renodx-dlss 的兜底手段）");

        string resolved = AddonNameResolver.Resolve(renodx, "renodx-dlss(9.17.12).addon64", knownName: null, fallback: null);
        Check(resolved == "renodx-dlss", $"只给路径时退回 slug = '{resolved}'（不影响禁用，只影响显示）");

        string auto = AddonNameResolver.Resolve(renodx, "renodx-dlss(9.17.12).addon64", candidateNames: ["RenoDX DLSS", "DLSS"]);
        Check(auto == "RenoDX DLSS", $"给候选名后自动解析出 '{auto}'（版本资源为空时的自动兜底）");
    }

    string bridge = Path.Combine(realAddons, "dlss5-bridge.addon64");
    if (File.Exists(bridge))
    {
        Check(AddonNameResolver.FromVersionResource(bridge) == "DLSS 5 Bridge", "dlss5-bridge 的 FileDescription");
    }

    // 真实目录扫描：应该只挑出 addon，把 dll/zip/txt/空备注文件全滤掉
    var realScan = AddonFileInfo.ScanDirectory(realAddons);
    Check(realScan.All(a => a.FileName.Contains(".addon")), $"真实目录扫出 {realScan.Count} 个 addon，且全都是 addon 文件");
    // 这条依赖用户真实目录里此刻**恰好**有一个被重命名禁用的 addon（全局关掉的那个），
    // 用户随时会把它打开 —— 没有就跳过，不要因此把整个自测判失败。
    if (realScan.Any(a => a.IsRenamedDisabled))
    {
        Check(true, "扫出了被重命名禁用的那个（.addon64x）");
    }
    else
    {
        Console.WriteLine("  [SKIP] 真实目录里当前没有被重命名禁用的 addon（这条用例靠 §17 的造数据覆盖）");
    }
    Check(realScan.All(a => !a.FileName.EndsWith(".dll")), "配套的 nvngx_*.dll / sl.*.dll 没有被误认成插件");
}
else
{
    Console.WriteLine($"  [SKIP] 找不到 {realAddons}，跳过真实 DLL 实测");
}

Console.WriteLine();
Console.WriteLine("== 14. 游戏条目 / 发现 / 持久化 ==");
// 两个「游戏目录」，各自一份 ReShade.ini（AddonPath 指向同一个全局插件目录）
string gameA = Path.Combine(root, "Genshin Impact Game");
string gameB = Path.Combine(root, "Star Rail Game");
Directory.CreateDirectory(gameA);
Directory.CreateDirectory(gameB);
// 这几个游戏自己的插件目录（跟上面扩展安装用的那个分开，免得互相干扰）
string addonsDir = Path.Combine(root, "game-addons");
Directory.CreateDirectory(addonsDir);

foreach (string dir in new[] { gameA, gameB })
{
    File.WriteAllText(Path.Combine(dir, "ReShade.ini"), string.Join("\r\n", [
        "[ADDON]",
        "AddonPath=" + addonsDir + "\\",
        "DisabledAddons=",
        "",
        "[GENERAL]",
        "PerformanceMode=0",
        "",
        "[STYLE]",
        "FontSize=17",
    ]) + "\r\n");
}

// 插件真身：3 个 addon + 1 个被重命名禁用的 + 1 个无关 dll
foreach (string n in new[] { "renodx-dlss(9.17.12).addon64", "renodx-dlss5-super-anus(1.0.8.18).addon64", "dlss5-bridge.addon64", "nvngx_dlss.dll" })
{
    File.WriteAllText(Path.Combine(addonsDir, n), "x");
}

File.WriteAllText(Path.Combine(gameA, "YuanShen.exe"), "fake");
File.WriteAllText(Path.Combine(gameA, "GenshinImpact.exe"), "fake");
File.WriteAllText(Path.Combine(gameB, "StarRail.exe"), "fake");

var customExe = Path.Combine(root, "wegame", "蓝色星原", "PetitPlanet.exe");
Directory.CreateDirectory(Path.GetDirectoryName(customExe)!);
File.WriteAllText(customExe, "fake");
File.WriteAllText(Path.Combine(Path.GetDirectoryName(customExe)!, "ReShade.ini"), "[ADDON]\r\nAddonPath=" + addonsDir + "\\\r\n");

string storePath = Path.Combine(root, ".hysx", "games.json");
Check(GameEntryStore.GetDefaultPath(userDataFolder) == Path.Combine(userDataFolder, ".hysx", "games.json"), @"默认落盘位置 = <用户数据目录>\.hysx\games.json");

var store = GameEntryStore.Load(storePath);
var discovery = new GameDiscoveryService(store) { StorePath = storePath };

var knownA = new KnownGameCandidate("hk4e_cn", "原神（国服）", gameA, "YuanShen.exe");
var knownB = new KnownGameCandidate("hkrpg_cn", "崩坏：星穹铁道", gameB, "StarRail.exe");
var knownMissing = new KnownGameCandidate("nap_cn", "绝区零", Path.Combine(root, "not-installed"), "ZenlessZoneZero.exe");
// 同目录的两个区服（国服 / B 服共用一个安装目录）→ 必须去重成一条
var knownDup = new KnownGameCandidate("hk4e_bilibili", "原神（B 服）", gameA, "YuanShen.exe");

List<GameEntry> found = discovery.DiscoverAll([knownA, knownB, knownMissing, knownDup]);
Check(found.Count == 2, $"发现 2 个游戏（目录不存在的跳过、同一 ini 的去重），实际 {found.Count}");

GameEntry entryA = found.First(e => e.Biz?.Value == "hk4e_cn");
Check(entryA.Id == "biz:hk4e_cn", $"条目 id 跟着 GameBiz = {entryA.Id}");
Check(entryA.GameDirectory == gameA, "GameDirectory 从 exe 推出来");
Check(entryA.ProcessName == "YuanShen.exe", "ProcessName 就是 inject.exe 要的参数");
Check(entryA.ReShadeIniPath == Path.Combine(gameA, "ReShade.ini"), "ReShadeIniPath = <游戏目录>\\ReShade.ini");
Check(entryA.HasReShadeIni, "HasReShadeIni 为真");

// 多个 exe：优先用 Hub 给的那一个（不瞎猜）
Check(GameDiscoveryService.PickMainExe(gameA, "YuanShen.exe") == Path.Combine(gameA, "YuanShen.exe"), "多 exe 时用 Hub 报的进程名");
string? guessed = GameDiscoveryService.PickMainExe(gameA, null);
Check(guessed is not null && KnownProcessNames.IsKnownProcess(guessed), $"没有 hub 名字时退回内置进程名表里认识的那个（实际 {Path.GetFileName(guessed ?? "null")}）");
Check(GameDiscoveryService.EnumerateExeCandidates(gameA).Count == 2, "列出目录里的 2 个候选 exe");
Check(GameDiscoveryService.PickMainExe(Path.Combine(root, "not-installed"), null) is null, "目录不存在返回 null 而不是抛异常");

Console.WriteLine("-- 手动添加自定义游戏 --");
AddCustomResult added = discovery.AddCustom(customExe);
Check(added.Added && added.Entry is not null, "选一个 exe 就能加进来");
Check(added.Entry!.Id.StartsWith("exe:"), $"自定义条目 id 跟着 exe 路径 = {added.Entry.Id}");
Check(added.Entry.ProcessName == "PetitPlanet.exe", "自定义条目的进程名 = 文件名");
Check(added.Entry.HasReShadeIni, "同目录有 ReShade.ini → 直接可以管插件（比 Hub 现有「自定义注入」多拿到的信息）");
Check(added.Entry.DisplayName == "Petit Planet", $"认得出的进程名反查成中文名 = {added.Entry.DisplayName}");

AddCustomResult again = discovery.AddCustom(customExe);
Check(!again.Added && again.Error is not null, "重复添加会被挡住：" + again.Error);
AddCustomResult badFile = discovery.AddCustom(Path.Combine(root, "nope.exe"));
Check(!badFile.Added && badFile.Error is not null, "文件不存在时给出错误：" + badFile.Error);
AddCustomResult notExe = discovery.AddCustom(profilePath);
Check(!notExe.Added && notExe.Error is not null, "不是 exe 时给出错误：" + notExe.Error);

Console.WriteLine("-- 没有 ReShade.ini 的自定义游戏也要能加（只管启动/注入）--");
string bareExe = Path.Combine(root, "bare", "NexusAnima.exe");
Directory.CreateDirectory(Path.GetDirectoryName(bareExe)!);
File.WriteAllText(bareExe, "fake");
AddCustomResult bare = discovery.AddCustom(bareExe);
Check(bare.Added && bare.Entry!.ReShadeIniPath is not null && !bare.Entry.HasReShadeIni,
    "允许添加，只是 HasReShadeIni 为假（插件页显示「该游戏没有 ReShade.ini」）");

Console.WriteLine("-- 持久化 --");
entryA.UseInjectMode = true;
store.Capture(entryA);
store.Save(storePath);
Check(File.Exists(storePath), "games.json 已写入");

var store2 = GameEntryStore.Load(storePath);
var discovery2 = new GameDiscoveryService(store2) { StorePath = storePath };
List<GameEntry> found2 = discovery2.DiscoverAll([knownA, knownB]);
Check(found2.Count == 4, $"重新发现：2 个已知 + 2 个自定义（一个带 ini 一个不带）= {found2.Count}");
Check(found2.First(e => e.Id == "biz:hk4e_cn").UseInjectMode, "注入模式开关跟着 id 活过了重启");
Check(!found2.First(e => e.Id == "biz:hkrpg_cn").UseInjectMode, "别的游戏不受影响（只影响单个游戏）");
Check(discovery2.RemoveCustom(bare.Entry!.Id), "自定义条目可以删掉");
Check(new GameEntryStore().Games.Count == 0, "空 store 不会炸");

Console.WriteLine();
Console.WriteLine("== 15. 每个游戏独立的插件开关 ==");
var cachePath15 = Path.Combine(root, ".hysx", "addon-names.json");
// 这里故意**不提供任何 dlss5 标签**：真机上用户手装的 renodx-dlss.addon64 就没有扩展目录记录，
// 得靠文件名的兜底判据把它认成 DLSS5 一类（否则「从 DllMain 加载」对它整条消失）。
var serviceA = new GamePluginService(
    entryA,
    ShadeHostLocator.FromUserDataFolder(userDataFolder)!,
    cachePath15,
    null,
    addonFileName => new[] { "hdr" });
GameEntry entryB = found2.First(e => e.Id == "biz:hkrpg_cn");
var serviceB = new GamePluginService(entryB, ShadeHostLocator.FromUserDataFolder(userDataFolder)!, cachePath15);

List<GameAddonState> addonsA = serviceA.GetAddons();
Check(addonsA.Count == 3, $"该游戏的插件目录里认出 3 个 addon（dll 被滤掉），实际 {addonsA.Count}");
Check(serviceA.AddonDirectory == addonsDir, "AddonPath 取的是这个游戏 ini 里写的那个目录");
Check(serviceA.HasReShadeIni, "这个游戏有 ReShade.ini");
Check(addonsA.First(a => a.Slug == "renodx-dlss").DisplayName == "RenoDX DLSS", "显示名用内部注册名（已知映射）");
Check(addonsA.First(a => a.Slug == "renodx-dlss5-super-anus").DisplayName == "RenoDX DLSS_A", "super-anus 的显示名");
Check(addonsA.First(a => a.Slug == "dlss5-bridge").DisplayName == "DLSS 5 Bridge", "dlss5-bridge 的显示名");
Check(addonsA.All(a => a.Enabled), "初始全是启用状态（DisabledAddons 为空）");

string dlssFile = "renodx-dlss(9.17.12).addon64";
Check(serviceA.SetAddonEnabled(dlssFile, false), "在 A 游戏里关掉一个插件");
Check(!serviceA.GetAddons().First(a => a.FileName == dlssFile).Enabled, "A 游戏里读回来是关的");

string iniA = File.ReadAllText(Path.Combine(gameA, "ReShade.ini"));
Check(iniA.Contains("RenoDX DLSS@renodx-dlss(9.17.12).addon64"), "@ 存在且前面带内部名（没有 @ 的条目 ReShade 永远匹配不上）");
Check(serviceB.GetAddons().All(a => a.Enabled), "B 游戏完全不受影响（验收点：同一个插件 A 关 B 开）");
Check(!File.ReadAllText(Path.Combine(gameB, "ReShade.ini")).Contains("DisabledAddons=RenoDX"), "B 的 ini 里没有被写上");

Check(serviceB.SetAddonEnabled(dlssFile, false), "在 B 游戏里也关掉它");
Check(!serviceB.GetAddons().First(a => a.FileName == dlssFile).Enabled, "B 游戏里读回来是关的");
Check(serviceA.GetAddons().First(a => a.FileName == dlssFile).Enabled == false, "A 仍然是关的");
Check(serviceA.SetAddonEnabled(dlssFile, true), "再把 A 打开");
Check(serviceA.GetAddons().First(a => a.FileName == dlssFile).Enabled, "A 打开了");
Check(File.ReadAllText(Path.Combine(gameB, "ReShade.ini")).Contains("RenoDX DLSS@renodx-dlss(9.17.12).addon64"), "B 还是关着（两份 ini 各自正确）");

Console.WriteLine("-- LoadFromDllMain 的空槽位 --");
string superFile = "renodx-dlss5-super-anus(1.0.8.18).addon64";
Check(serviceA.SetLoadFromDllMain(dlssFile, true), "把 dlss 勾进 LoadFromDllMain");
Check(serviceA.SetLoadFromDllMain(superFile, true), "把 super-anus 也勾进去");
Check(serviceA.GetAddons().Count(a => a.LoadFromDllMain) == 2, "两个都读回来是勾上的");
Check(serviceA.SetLoadFromDllMain(dlssFile, false), "取消勾选 dlss");
string rawSlots = File.ReadAllLines(Path.Combine(gameA, "ReShade.ini")).First(l => l.StartsWith("LoadFromDllMain="));
Check(rawSlots.Count(c => c == ',') == 1, $"取消后留的是空槽位而不是把整串删掉：{rawSlots}");
Check(serviceA.GetAddons().First(a => a.FileName == dlssFile).LoadFromDllMain == false, "取消后读回来是没勾的");
Check(serviceA.SetLoadFromDllMain(superFile, false), "super-anus 也取消 → 剩两个空槽位");

Console.WriteLine("-- DirectNeuralRenderingHookPoint --");
Check(serviceA.CanEditHookPoint(), "装了 super-anus → 允许改 hook 点");
Check(serviceA.GetHookPoint() == 0, "键不存在时读出来是 0（= off）");
Check(serviceA.SetHookPoint(3), "写 hook 点 = 3");
Check(serviceA.GetHookPoint() == 3, "读回来还是 3");
Check(File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("DirectNeuralRenderingHookPoint=3"), "1-4 原样写数字，不做别名");
Check(serviceA.SetHookPoint(0), "写 0");
Check(File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("DirectNeuralRenderingHookPoint=0"), "0 是写键不是删键");
Check(File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("DirectNeuralRenderingHookStage=0"), "ShortFuse 那版的 DirectNeuralRenderingHookStage 也一起写（用户实测的键名）");
Check(File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("[STYLE]"), "往返后 [STYLE] 段还在");

Console.WriteLine("-- DX11Source（服务层）--");
Check(!serviceA.IsDx11SourceNative(), "默认不是 native");
Check(serviceA.SetDx11SourceNative(true), "写 DX11Source=native");
Check(serviceA.IsDx11SourceNative(), "读回来是 native");
string dx11Raw = File.ReadAllText(Path.Combine(gameA, "ReShade.ini"));
Check(dx11Raw.Contains("DX11Source=native"), "native 落到盘上");
Check(dx11Raw.IndexOf("DX11Source", StringComparison.Ordinal) == dx11Raw.LastIndexOf("DX11Source", StringComparison.Ordinal), "只有一处（没写进 [ADDON]）");
Check(serviceA.SetDx11SourceNative(false), "关掉 DX11Source");
Check(!File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("DX11Source"), "关掉后盘上没了");

Console.WriteLine("-- EnableHooks（服务层）--");
Check(serviceA.SetEnableHooks(1) && serviceA.GetEnableHooksMode() == 1, "服务层写入并读回 EnableHooks=1");
Check(serviceA.SetEnableHooks(2) && serviceA.GetEnableHooksMode() == 2, "服务层写入并读回 EnableHooks=2");
Check(File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("EnableHooks=2"), "服务层将 2 保存到游戏 ini");
Check(serviceA.SetEnableHooks(0) && serviceA.GetEnableHooksMode() == 0, "服务层选择无后读回 0");
Check(!File.ReadAllText(Path.Combine(gameA, "ReShade.ini")).Contains("EnableHooks"), "服务层选择无后删除键");
Check(!serviceA.SetEnableHooks(3), "服务层拒绝不支持的 EnableHooks 值");

string plainDlss = "renodx-dlss(9.17.12).addon64";

// 用户反馈过：文件名里常常没有 ShortFuse 分支信息（装的就是 renodx-dlss.addon64），
// 于是 hook 下拉框被灰掉 —— 现在只要是 renodx-dlss* 就允许改
Check(AddonFileInfo.Parse(plainDlss)!.IsHookPointCapable, "普通 renodx-dlss 也算（文件名分不出 SF 分支）");
Check(AddonFileInfo.Parse("renodx-dlss.addon64")!.IsHookPointCapable, "没有版本号/分支的 renodx-dlss.addon64 也算");
Check(AddonFileInfo.Parse("renodx-dlss(ShortFuse_9.11.6).addon64")!.IsHookPointCapable, "renodx-dlss(ShortFuse) 算");
Check(AddonFileInfo.Parse("renodx-dlss-SF(9.17.14).addon64")!.IsHookPointCapable, "renodx-dlss-SF 也算");
Check(AddonFileInfo.Parse("renodx-dlss5-super-anus(1.0.8.18).addon64")!.IsHookPointCapable, "super-anus 算");
Check(AddonFileInfo.Parse("dlss5-bridge.addon64")!.IsHookPointCapable == false, "dlss5-bridge 不算");

// 文件名兜底判据（tags 没有 dlss5 时靠它）：renodx-dlss 整族都算 DLSS5 一类
Check(AddonFileInfo.Parse("renodx-dlss(9.17.12).addon64")!.IsDlss5ByName, "renodx-dlss(9.17.12) 按文件名算 DLSS5 一类");
Check(AddonFileInfo.Parse("renodx-dlss.addon64")!.IsDlss5ByName, "没有版本号/分支的 renodx-dlss.addon64 也算");
Check(AddonFileInfo.Parse("renodx-dlss-SF(9.17.14).addon64")!.IsDlss5ByName, "renodx-dlss-SF 也算");
Check(!AddonFileInfo.Parse("renodx-universal_ue-dof-fix.addon64")!.IsDlss5ByName, "无关插件不算 DLSS5 一类");

// 只装了无关插件的游戏：hook 点还是不许改
string onlyPlain = Path.Combine(root, "only-plain", "StarRail.exe");
Directory.CreateDirectory(Path.GetDirectoryName(onlyPlain)!);
File.WriteAllText(onlyPlain, "fake");
string onlyPlainAddons = Path.Combine(root, "only-plain-addons");
Directory.CreateDirectory(onlyPlainAddons);
File.WriteAllText(Path.Combine(onlyPlainAddons, "dlss5-bridge.addon64"), "x");
File.WriteAllText(Path.Combine(Path.GetDirectoryName(onlyPlain)!, "ReShade.ini"), "[ADDON]\r\nAddonPath=" + onlyPlainAddons + "\\\r\nDisabledAddons=\r\n");
AddCustomResult onlyPlainEntry = discovery.AddCustom(onlyPlain);
var servicePlain = new GamePluginService(onlyPlainEntry.Entry!, null, null);
Check(!servicePlain.CanEditHookPoint(), "只装 dlss5-bridge → hook 点不可改");
Check(!servicePlain.SetHookPoint(4), "不可改的时候写不进去");
Check(!File.ReadAllText(Path.Combine(Path.GetDirectoryName(onlyPlain)!, "ReShade.ini")).Contains("DirectNeuralRenderingHookPoint"), "盘上确实没写");
Check(!servicePlain.SetDx11SourceNative(true), "不可改的时候 DX11Source 也写不进去");
Check(!File.ReadAllText(Path.Combine(Path.GetDirectoryName(onlyPlain)!, "ReShade.ini")).Contains("DX11Source"), "盘上确实没写 DX11Source");
Check(!servicePlain.SetEnableHooks(2), "非 RenoDX DLSS5 插件不能写 EnableHooks=2");

Console.WriteLine("-- 启用 RenoDX DLSS5 时自动补写 DX11Source=native --");
Check(!GamePluginService.IsRenoDxDlss5Addon("renodx-dlss5-super-anus(1.0.8.18).addon64"), "super-anus 不是主插件（另一款实现，段名不同）");
Check(!GamePluginService.IsRenoDxDlss5Addon("renodx-dlss(9.17.12).addon64"), "renodx-dlss（DLSS 版）不是主插件");
Check(!GamePluginService.IsRenoDxDlss5Addon(null), "null 不是");
string autoExe = Path.Combine(root, "auto-dx11", "YuanShen.exe");
Directory.CreateDirectory(Path.GetDirectoryName(autoExe)!);
File.WriteAllText(autoExe, "fake");
string autoAddons = Path.Combine(root, "auto-dx11-addons");
Directory.CreateDirectory(autoAddons);
File.WriteAllText(Path.Combine(autoAddons, "renodx-dlss5(1.0.8.18).addon64"), "x");
File.WriteAllText(Path.Combine(Path.GetDirectoryName(autoExe)!, "ReShade.ini"), "[ADDON]\r\nAddonPath=" + autoAddons + "\\\r\n");
AddCustomResult autoEntry = discovery.AddCustom(autoExe);
var serviceAuto = new GamePluginService(autoEntry.Entry!, null, null);
Check(GamePluginService.IsRenoDxDlss5Addon("renodx-dlss5(1.0.8.18).addon64"), "主插件判定：renodx-dlss5( ... ) 算");
Check(GamePluginService.IsRenoDxDlss5Addon("renodx-dlss5.addon64"), "主插件判定：renodx-dlss5.addon64 算");
Check(serviceAuto.SetAddonEnabled("renodx-dlss5(1.0.8.18).addon64", true), "启用 RenoDX DLSS5");
string autoIni = File.ReadAllText(Path.Combine(Path.GetDirectoryName(autoExe)!, "ReShade.ini"));
Check(autoIni.Contains("DX11Source=native"), "启用时自动补写 DX11Source=native（进游戏插件不再弹提示）");
Check(!serviceAuto.GetAddons().First(a => a.Slug == "renodx-dlss5").LoadFromDllMain, "启用 DLSS5 不会强制改动独立的 LoadFromDllMain");

Console.WriteLine("-- DLSS5 Feed 的 cfg（create_delay / warmup_rebuild 做成可调）--");
string feedDir = Path.Combine(root, "feed-cfg");
Directory.CreateDirectory(feedDir);
Check(Dlss5FeedConfig.Load(null) is null, "没有插件目录时不构造（界面上那条就不显示）");
Check(Dlss5FeedConfig.Load(feedDir) is { FileExists: false, CreateDelay: 60, WarmupRebuild: 180 },
    "cfg 不存在时用出厂默认 60 / 180");

string feedCfgPath = Path.Combine(feedDir, Dlss5FeedConfig.FileName);
File.WriteAllText(feedCfgPath, "; DLSS5 Feed\r\nenabled=1\r\nmode=2\r\ncreate_delay=60\r\nwarmup_rebuild=180\r\ngpu_timeout_ms=2000\r\n\r\n");
Dlss5FeedConfig? feedCfg = Dlss5FeedConfig.Load(feedDir);
Check(feedCfg is { FileExists: true, CreateDelay: 60, WarmupRebuild: 180 },
    $"读回出厂值（实际 {feedCfg?.CreateDelay}/{feedCfg?.WarmupRebuild}）");
Check(feedCfg!.Save(0, 12), "把两个键改成 0 / 12");
string feedText = File.ReadAllText(feedCfgPath);
Check(feedText.Contains("create_delay=0") && feedText.Contains("warmup_rebuild=12"), "两个键都落到盘上");
Check(feedText.Contains("; DLSS5 Feed") && feedText.Contains("enabled=1") && feedText.Contains("gpu_timeout_ms=2000"),
    "注释和别的键一个不丢（只认键值行）");
Check(feedText.IndexOf("mode=2", StringComparison.Ordinal) < feedText.IndexOf("create_delay=0", StringComparison.Ordinal),
    "顺序没被重排（mode 仍在 create_delay 前）");
Check(!File.ReadAllBytes(feedCfgPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "不写 BOM（addon 按窄字符读）");
Check(Dlss5FeedConfig.Load(feedDir) is { CreateDelay: 0, WarmupRebuild: 12 }, "重新读回来是新值");
Check(feedCfg.Save(99_999, -5), "超范围也照写（内部夹住）");
Check(feedCfg.CreateDelay == Dlss5FeedConfig.MaxFrames && feedCfg.WarmupRebuild == 0, "夹到 0..3600");
Check(Dlss5FeedConfig.ResolvePath(feedDir) == feedCfgPath, "ResolvePath 拼的就是那份 cfg");

File.WriteAllText(feedCfgPath, "enabled=1\r\nmode=2\r\n");
Check(Dlss5FeedConfig.Load(feedDir) is { CreateDelay: 60, WarmupRebuild: 180 }, "键缺失时读出来还是默认值");
Check(feedCfg.Save(7, 8), "缺键也能写");
string feedText2 = File.ReadAllText(feedCfgPath);
Check(feedText2.Contains("create_delay=7") && feedText2.Contains("warmup_rebuild=8"), "缺的键补到根节末尾");
Check(feedText2.Contains("enabled=1") && feedText2.Contains("mode=2"), "原有的键还在");

Console.WriteLine("-- RenoDX DLSS5 的 hook 点（NRHookPoint）+ RenoDX DLSS 也能进 LoadFromDllMain --");
string dlss5Exe = Path.Combine(root, "dlss5-hook", "YuanShen.exe");
Directory.CreateDirectory(Path.GetDirectoryName(dlss5Exe)!);
File.WriteAllText(dlss5Exe, "fake");
string dlss5Addons = Path.Combine(root, "dlss5-hook-addons");
Directory.CreateDirectory(dlss5Addons);
File.WriteAllText(Path.Combine(dlss5Addons, "renodx-dlss5.addon64"), "x");
File.WriteAllText(Path.Combine(dlss5Addons, "renodx-dlss.addon64"), "x");
File.WriteAllText(Path.Combine(Path.GetDirectoryName(dlss5Exe)!, "ReShade.ini"),
    "[ADDON]\r\nAddonPath=" + dlss5Addons + "\\\r\nDisabledAddons=\r\n\r\n[RenoDX.DLSS5]\r\nConfigVersion=7\r\n\r\n[RENODX-DLSS]\r\nDirectNeuralRenderingHookPoint=0\r\n");
AddCustomResult dlss5HookEntry = discovery.AddCustom(dlss5Exe);
var serviceDlss5Hook = new GamePluginService(dlss5HookEntry.Entry!, null, null);
Check(serviceDlss5Hook.CanEditDlss5HookPoint(), "装了 renodx-dlss5 → 允许改 NRHookPoint");
Check(serviceDlss5Hook.GetDlss5HookPoint() == 0, "键不存在时读出来是 0（和 DLSS 那个同口径）");
Check(serviceDlss5Hook.SetDlss5HookPoint(2), "写 NRHookPoint = 2");
Check(serviceDlss5Hook.GetDlss5HookPoint() == 2, "读回来还是 2");
string dlss5HookIni = File.ReadAllText(Path.Combine(Path.GetDirectoryName(dlss5Exe)!, "ReShade.ini"));
Check(dlss5HookIni.Contains("NRHookPoint=2"), "写在 [RenoDX.DLSS5] 段里");
Check(dlss5HookIni.Contains("ConfigVersion=7") && dlss5HookIni.Contains("DirectNeuralRenderingHookPoint=0"),
    "同段/别的段的其他键一个不动");
Check(!GamePluginService.IsRenoDxDlss5Addon("renodx-dlss.addon64"), "renodx-dlss 不是 DLSS5 主插件（各写各的键）");

Check(serviceDlss5Hook.NeedsLoadFromDllMain("renodx-dlss.addon64"), "RenoDX DLSS 属于「该从 DllMain 加载」那一族（用户第 6 条）");
Check(serviceDlss5Hook.SetAddonEnabled("renodx-dlss.addon64", true), "启用 RenoDX DLSS");
Check(!serviceDlss5Hook.IsLoadFromDllMain("renodx-dlss.addon64"), "启用插件不会强制改动独立的 LoadFromDllMain");
Check(serviceDlss5Hook.SetLoadFromDllMain("renodx-dlss.addon64", true), "用户可以手动勾选这个选项");
Check(serviceDlss5Hook.IsLoadFromDllMain("renodx-dlss.addon64"), "手动勾选后读回来是已勾选");
Check(serviceDlss5Hook.SetLoadFromDllMain("renodx-dlss.addon64", false), "用户也能手动取消这个勾");
Check(!serviceDlss5Hook.IsLoadFromDllMain("renodx-dlss.addon64"), "取消后读回来是没勾的");

Console.WriteLine("-- 没有 ReShade.ini 的游戏 --");
var serviceNone = new GamePluginService(bare.Entry!, ShadeHostLocator.FromUserDataFolder(userDataFolder)!);
Check(!serviceNone.HasReShadeIni, "没有 ini → HasReShadeIni 为假（界面走空状态）");
Check(serviceNone.Profile is null, "Profile 为 null");
Check(!serviceNone.SetAddonEnabled(plainDlss, false), "没有 ini 时写不进去，也不会抛异常");

Console.WriteLine();
Console.WriteLine("== 16. INIBuild ==");
string iniBuildPath = Path.Combine(shadeRoot, "LauncherResource", "INIBuild.exe");
Check(!ReShadeIniBuilder.IsAvailable(ShadeHostLocator.FromUserDataFolder(userDataFolder)!), "没装 LauncherResource 时 IsAvailable 为假");
IniBuildResult noExe = await ReShadeIniBuilder.RunAsync(ShadeHostLocator.FromUserDataFolder(userDataFolder)!);
Check(!noExe.Ok && noExe.FailureReason is not null, "找不到 INIBuild.exe 时给出原因而不是抛异常：" + noExe.FailureReason);

// 目标 ini 已经在了就什么都不做（不白跑）
IniBuildResult? skipped = await ReShadeIniBuilder.EnsureAsync(ShadeHostLocator.FromUserDataFolder(userDataFolder)!, Path.Combine(gameA, "ReShade.ini"));
Check(skipped is null, "游戏 ini 已经存在 → EnsureAsync 直接返回 null，不动用户的配置");

// 真 exe 实测：仓库里的沙盒 HoYoShade 带一份真的 INIBuild.exe
string sandboxIniBuild = new[]
{
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "smoke", "data", "HoYoShade", "LauncherResource", "INIBuild.exe")),
    Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build", "smoke", "data", "HoYoShade", "LauncherResource", "INIBuild.exe")),
}.FirstOrDefault(File.Exists) ?? string.Empty;
if (File.Exists(sandboxIniBuild))
{
    Console.WriteLine("-- 对真的 INIBuild.exe 实测（在副本里跑）--");
    string iniRoot = Path.Combine(root, "inibuild-host");
    Directory.CreateDirectory(Path.Combine(iniRoot, "LauncherResource"));
    Directory.CreateDirectory(Path.Combine(iniRoot, "reshade-shaders", "Addons"));
    File.Copy(sandboxIniBuild, Path.Combine(iniRoot, "LauncherResource", "INIBuild.exe"));
    File.Copy(Path.Combine(Path.GetDirectoryName(sandboxIniBuild)!, "AddonWhitelist.txt"), Path.Combine(iniRoot, "LauncherResource", "AddonWhitelist.txt"), overwrite: true);
    File.WriteAllText(Path.Combine(iniRoot, "ReShade64.dll"), "fake");
    File.WriteAllText(Path.Combine(iniRoot, "reshade-shaders", "Addons", "renodx-dlss5-super-anus(1.0.8.18).addon64"), "x");
    File.WriteAllText(Path.Combine(iniRoot, "reshade-shaders", "Addons", "renodx-dlss(9.17.12).addon64"), "x");

    var iniHost = new ShadeHost(iniRoot);
    Check(ReShadeIniBuilder.IsAvailable(iniHost), "副本里能定位到 INIBuild.exe");

    IniBuildResult build = await ReShadeIniBuilder.RunAsync(iniHost);
    Check(build.Ok, "INIBuild 跑通了" + (build.FailureReason is null ? "" : "：" + build.FailureReason));
    Check(File.Exists(iniHost.ReShadeIniPath), "生成了模板 ReShade.ini");

    string generated = File.ReadAllText(iniHost.ReShadeIniPath);
    Check(generated.Contains(iniRoot), "写出来的是绝对路径（所以每个游戏那份是复制过去的）");
    Check(generated.Contains("renodx-dlss5-super-anus(1.0.8.18).addon64"), "DisabledAddons 里列出了目录里的 addon（新装的默认关）");
    Check(generated.Contains("@"), "列出来的条目带 @（不带 @ 的话 ReShade 永远匹配不上）");
    Check(ReShadeProfile.Load(iniHost.ReShadeIniPath).GetDisabledAddons().Count == 2, "两条都能被我们的解析器读懂");

    // 复制的模板 = 游戏那份的起点：跑完 Ensure 之后游戏 ini 就有了
    string gameC = Path.Combine(root, "new-game");
    Directory.CreateDirectory(gameC);
    File.Copy(iniHost.ReShadeIniPath, Path.Combine(gameC, "ReShade.ini"));
    IniBuildResult? ensured = await ReShadeIniBuilder.EnsureAsync(iniHost, Path.Combine(gameC, "ReShade.ini"));
    Check(ensured is null, "EnsureAsync：游戏 ini 在就不再跑 INIBuild");
}
else
{
    Console.WriteLine($"  [SKIP] 找不到 {sandboxIniBuild}，跳过真 INIBuild 实测");
}

Console.WriteLine();
Console.WriteLine("== 17. 全局禁用（重命名 .addon64 ↔ .addon64x）==");
Check(AddonFileSwitcher.DisabledNameOf("renodx-dlss(9.17.12).addon64") == "renodx-dlss(9.17.12).addon64x", "禁用 = 加 x");
Check(AddonFileSwitcher.EnabledNameOf("renodx-dlss(9.17.12).addon64x") == "renodx-dlss(9.17.12).addon64", "启用 = 去掉 x");
Check(AddonFileSwitcher.DisabledNameOf("a.addon64x") == "a.addon64x", "已经是禁用状态就不要再加一个 x");
Check(AddonFileSwitcher.EnabledNameOf("a.addon64") == "a.addon64", "已经是启用状态就不要再剥一层");
Check(AddonFileSwitcher.DisabledNameOf("x.addon32") == "x.addon32x", "32 位同理");

string globalDir = Path.Combine(root, "global-addons");
Directory.CreateDirectory(globalDir);
string globalAddon = Path.Combine(globalDir, "dlss5-bridge.addon64");
File.WriteAllText(globalAddon, "x");

AddonToggleResult off = AddonFileSwitcher.SetEnabled(globalAddon, false);
Check(off.Ok && off.Path is not null && File.Exists(off.Path), "全局禁用：文件被改名了");
Check(!File.Exists(globalAddon), "原来的名字不在了");
Check(AddonFileInfo.Parse(Path.GetFileName(off.Path!))!.IsRenamedDisabled, "扫目录时能认出「全局已禁用」");

AddonToggleResult back = AddonFileSwitcher.SetEnabled(off.Path!, true);
Check(back.Ok && File.Exists(globalAddon), "再启用回来（名字还原）");
AddonToggleResult noop = AddonFileSwitcher.SetEnabled(globalAddon, true);
Check(noop.Ok && noop.Path == globalAddon, "已经是启用状态时是空操作");
Check(!AddonFileSwitcher.SetEnabled(Path.Combine(globalDir, "nope.addon64"), false).Ok, "文件不在时给出错误而不是抛异常");

Console.WriteLine();
Console.WriteLine("== 18. 自定义游戏的合成 biz（顶部游戏列表要用）==");
string customExe2 = Path.Combine(root, "custom2", "WeGameGame.exe");
Directory.CreateDirectory(Path.GetDirectoryName(customExe2)!);
File.WriteAllText(customExe2, "fake");
AddCustomResult custom2 = discovery.AddCustom(customExe2);
GameEntry entry2 = custom2.Entry!;

Check(entry2.CustomBizValue.StartsWith("custom_"), $"合成 biz = {entry2.CustomBizValue}");
Check(entry2.CustomBizValue.Length == "custom_".Length + 12, "短 id 是 12 位");
Check(entry2.CustomBizValue == custom2.Entry!.CustomBizValue, "同一个条目每次算出来都一样（稳定）");
Check(!entry2.CustomBiz.IsKnown(), "合成 biz 对 Hub 来说是“不认识”的 —— 不会拿去请求 HoYoPlay");
Check(entry2.ShortId.All(char.IsAsciiHexDigitLower), "短 id 全是十六进制小写字符（可以直接当文件名）");

var entry2b = new GameEntry(entry2.Id, "换个名字");
Check(entry2b.CustomBizValue == entry2.CustomBizValue, "合成 biz 只跟 Id 走，显示名改了也不变");

Console.WriteLine();
Console.WriteLine("== 23. dll 组件版本排序（不能按字符串排）==");
Check(DllVersion.Compare("310.9.1", "310.10.0") < 0, "310.9.1 < 310.10.0（按字符串排会反）");
Check(DllVersion.Compare("2.9.0.0", "2.14.1.0") < 0, "2.9.0.0 < 2.14.1.0");
Check(DllVersion.Compare("310.7.128", "310.7.129") < 0, "310.7.128 < 310.7.129");
Check(DllVersion.Compare("310.8.0", "310.8.0-RTX40") < 0, "同数字段时 310.8.0 < 310.8.0-RTX40");
Check(DllVersion.Compare("310.8", "310.8.SF") < 0, "310.8 < 310.8.SF");
Check(DllVersion.Compare("310.8.SF", "310.8.SF-v2") < 0, "310.8.SF < 310.8.SF-v2");
Check(DllVersion.Compare("v1.4.13-pre7", "v1.4.13-pre8") < 0, "带 v 前缀和 pre 后缀也能比");
Check(DllVersion.Compare("310.9.1", "310.9.1") == 0, "相等");

// 用清单里真实的那些数字排一遍（模拟界面下拉框）
List<string> dlssVersions = ["310.9.1", "310.10.0", "310.9.0", "310.7.129", "310.7.128", "310.5.3"];
List<string> sorted = [.. dlssVersions.OrderByDescending(v => v, DllVersionComparer.Instance)];
Check(sorted[0] == "310.10.0" && sorted[1] == "310.9.1" && sorted[2] == "310.9.0",
    $"dlss 版本倒序：{string.Join(" > ", sorted)}");

List<string> nrVersions = ["310.8.0", "310.8.SF-v2", "310.8.0-RTX40", "310.8.SF"];
List<string> nrSorted = [.. nrVersions.OrderByDescending(v => v, DllVersionComparer.Instance)];
Check(nrSorted[0] == "310.8.SF-v2", $"dlssnr 版本倒序：{string.Join(" > ", nrSorted)}");

Console.WriteLine();
Console.WriteLine("== 22. 定位游戏时「往里找一层」==");
string pickRoot = Path.Combine(root, "launcher-root");
Directory.CreateDirectory(Path.Combine(pickRoot, "games", "Genshin Impact Game"));
Directory.CreateDirectory(Path.Combine(pickRoot, "games", "Star Rail Game"));
Directory.CreateDirectory(Path.Combine(pickRoot, "games", "ZenlessZoneZero Game"));
Directory.CreateDirectory(Path.Combine(pickRoot, "AntiCheatExpert"));
Directory.CreateDirectory(Path.Combine(pickRoot, "logs"));
File.WriteAllText(Path.Combine(pickRoot, "games", "Genshin Impact Game", "YuanShen.exe"), "x");
File.WriteAllText(Path.Combine(pickRoot, "games", "Star Rail Game", "StarRail.exe"), "x");
File.WriteAllText(Path.Combine(pickRoot, "AntiCheatExpert", "YuanShen.exe"), "x");   // 噪声目录里也有同名 exe

// 用户直接选了启动器根目录（真正的游戏在 games\<游戏名> Game\）
GameFolderSearchResult nested = GameFolderLocator.Locate(pickRoot, "YuanShen.exe");
Check(nested.Found && nested.WasNested, $"在根目录里往里找到了：{nested.Directory}");
Check(nested.Directory == Path.Combine(pickRoot, "games", "Genshin Impact Game"),
    "挑的是名字像游戏的那个（games\\Genshin Impact Game），不是 AntiCheatExpert");
Check(nested.Depth == 2, $"深度 = {nested.Depth}（根 → games → 游戏目录）");
Check(nested.ScannedDirectories < 20, $"只看了 {nested.ScannedDirectories} 个目录（不做全盘遍历）");

// 选对了就直接认
GameFolderSearchResult direct = GameFolderLocator.Locate(Path.Combine(pickRoot, "games", "Star Rail Game"), "StarRail.exe");
Check(direct.Found && !direct.WasNested && direct.Depth == 0, "选对目录时深度 0，不用往里找");

// 找不到就老实返回 null（调用方照旧报错）
Check(!GameFolderLocator.Locate(pickRoot, "Nope.exe").Found, "没有这个 exe 就返回找不到");
Check(!GameFolderLocator.Locate(Path.Combine(root, "not-exist"), "YuanShen.exe").Found, "目录不存在也不炸");

// 再往下超过两层就不再往下了
Directory.CreateDirectory(Path.Combine(pickRoot, "a", "b", "c", "deep"));
File.WriteAllText(Path.Combine(pickRoot, "a", "b", "c", "deep", "DeepGame.exe"), "x");
Check(!GameFolderLocator.Locate(pickRoot, "DeepGame.exe").Found, "超过 2 层就不找了（不做全盘遍历）");

Console.WriteLine();
Console.WriteLine("== 21. DLSS5 的 dll 依赖 + LoadFromDllMain 独立设置 ==");
Check(!DlssDllRequirements.IsDlss5(["hdr", "tonemap"]), "非 dlss5 标签不算 dlss5 插件");
Check(DlssDllRequirements.IsDlss5(["dlss5", "neural"]), "带 dlss5 标签就算");

AddonDllStatus noDll = AddonDllChecker.CheckFiles(["renodx-dlss5-super-anus.addon64"], ["dlss5"]);
Check(noDll.Severity == 2 && noDll.Summary.Contains("nvngx_dlssnr.dll"), $"没有 nvngx_dlssnr.dll → 标红（{noDll.Summary}）");

AddonDllStatus noSl = AddonDllChecker.CheckFiles(["a.addon64", "nvngx_dlssnr.dll"], ["dlss5"]);
Check(noSl.Severity == 2 && noSl.Summary.Contains("sl.interposer.dll"), $"有 dlssnr 但没 streamline → 也标红（{noSl.Summary}）");

AddonDllStatus allDll = AddonDllChecker.CheckFiles(["nvngx_dlssnr.dll", "sl.interposer.dll", "sl.dlss_nr.dll"], ["dlss5"]);
Check(allDll.IsOk && allDll.Severity == 0, "三件套齐 → 没问题");
Check(AddonDllChecker.CheckFiles(["a.addon64"], ["hdr"]).IsOk, "不是 dlss5 的插件不检查 dll");

Check(DllVersion.Normalize("310,8,0,0") == "310.8", "PE 读出来的 310,8,0,0 归一化成 310.8");
Check(DllVersion.IsSame("310,8,0,0", "310.8.0"), "归一化之后能和清单里的 310.8.0 对上");
Check(DllInstaller.Matches("sl.dlss_nr.dll", DllComponentCatalog.FamilyOf("streamline")!), "sl.*.dll 都算 streamline");
Check(!DllInstaller.Matches("nvngx_dlssnr.dll", DllComponentCatalog.FamilyOf("streamline")!), "nvngx_dlssnr.dll 不算 streamline");

// 自动勾 LoadFromDllMain（只有 dlss5 类插件）
var dlssService = new GamePluginService(
    entryA,
    ShadeHostLocator.FromUserDataFolder(userDataFolder)!,
    null,
    null,
    addonFileName => addonFileName.Contains("super-anus", StringComparison.OrdinalIgnoreCase) ? ["dlss5"] : ["hdr"]);

string superAnusFile = "renodx-dlss5-super-anus(1.0.8.18).addon64";
string renoDlssFile = "renodx-dlss(9.17.12).addon64";
dlssService.SetAddonEnabled(superAnusFile, false);   // 禁用会移除 DllMain 槽位
dlssService.SetAddonEnabled(superAnusFile, true);    // 重新启用不覆盖独立设置
Check(!dlssService.GetAddons().First(a => a.FileName == superAnusFile).LoadFromDllMain,
    "重新启用 dlss5 插件不会自动勾上 LoadFromDllMain");
Check(dlssService.GetAddons().First(a => a.FileName == superAnusFile).IsDlss5, "这条被认成 dlss5 插件");

// 真机最常见的那种：文件名没有 dlss5、扩展目录也没给 dlss5 标签的 renodx-dlss。
// 它同样属于 RenoDX DLSS 家族，但 LoadFromDllMain 仍由用户独立控制。
dlssService.SetAddonEnabled(renoDlssFile, false);
dlssService.SetAddonEnabled(renoDlssFile, true);
Check(dlssService.GetAddons().First(a => a.FileName == renoDlssFile).IsDlss5,
    "renodx-dlss 按文件名兜底认成 dlss5 一类");
Check(!dlssService.GetAddons().First(a => a.FileName == renoDlssFile).LoadFromDllMain,
    "重新启用 renodx-dlss 时不会自动勾上 LoadFromDllMain");
Check(!dlssService.SetLoadFromDllMain("hdr-tonemap.addon64", true),
    "真正无关的插件仍然不许勾进 LoadFromDllMain");

// 真机目录实测（有就读，没有就 SKIP）
string realAddonsDir2 = @"D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons";
if (Directory.Exists(realAddonsDir2))
{
    List<InstalledDll> installedDlls = DllInstaller.Scan(realAddonsDir2);
    InstalledDll? nr = installedDlls.FirstOrDefault(d => string.Equals(d.FileName, "nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase));
    Check(nr is not null, $"真机目录扫到 nvngx_dlssnr.dll（版本 {nr?.Version ?? "无"}，{nr?.SizeText}）");

    AddonDllStatus realStatus = AddonDllChecker.Check(realAddonsDir2, ["dlss5"]);
    Check(!realStatus.HasMissingRequired, $"真机目录里 dlss5 的必需 dll 是齐的（{realStatus.Summary}）");

    string? slVersion = DllInstaller.GetInstalledVersion(installedDlls, DllComponentCatalog.FamilyOf("streamline")!);
    Check(!string.IsNullOrWhiteSpace(slVersion), $"从 sl.*.dll 里读出 streamline 版本 = {slVersion}");
    Check(installedDlls.Count >= 10, $"真机 addons 目录里扫到 {installedDlls.Count} 个 dll");
}
else
{
    Console.WriteLine("  [SKIP] 找不到真实 addons 目录，跳过 dll 实测");
}

Console.WriteLine();
Console.WriteLine("== 20. 全局插件页按文件名认领插件（不是 Hub 装的也能认出来）==");
// 用用户机器上真实的那几个文件名（改过名的也算）
List<AddonFileInfo> realishFiles = new[] {
    "renodx-dlss5-super-anus(1.0.8.18).addon64x",
    "renodx-dlss(9.17.12).addon64x",
    "renodx-dlss(ShortFuse_9.17.14).addon64x",
    "renodx-neural-interposer-nvngx.dll (23.1.0 RC1).addon64",
    "dlss5-bridge.addon64x",
    "renodx-dlss5.addon64",
}.Select(AddonFileInfo.Parse).Where(f => f is not null).Select(f => f!).ToList();

Dictionary<string, List<AddonFileInfo>> owned = ExtensionAddonMatcher.Match(builtin.Extensions, realishFiles);
Check(owned.TryGetValue("renodx.dlss5.superanus", out var superAnusFiles) && superAnusFiles.Count == 1
      && superAnusFiles[0].FileName.StartsWith("renodx-dlss5-super-anus"),
    "super-anus 的文件被它自己认领（没被 renodx.dlss5 抢走）");
Check(!owned.TryGetValue("renodx.dlss5", out var dlss5Files) || dlss5Files.All(f => !f.FileName.Contains("super-anus")),
    "renodx.dlss5 不会把 super-anus 的文件算成自己的");
Check(owned.TryGetValue("renodx.dlss.sf", out var sfFiles) && sfFiles.Any(f => f.FileName.Contains("ShortFuse")),
    "ShortFuse 的文件归 renodx.dlss.sf");
Check(owned.TryGetValue("dlss5.bridge", out var bridgeFiles) && bridgeFiles.Any(f => f.FileName.StartsWith("dlss5-bridge")),
    "dlss5-bridge 归 dlss5.bridge");
Check(owned.TryGetValue("dlss5.neural.interposer", out var interposerFiles) && interposerFiles.Any(f => f.FileName.Contains("interposer")),
    "RenoDX Neural Interposer 归 dlss5.neural.interposer");
int claimedTotal = owned.Values.Sum(v => v.Count);
Check(claimedTotal == owned.Values.SelectMany(v => v).Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    $"一个文件只会被一个条目认领（共 {claimedTotal} 个）");

Console.WriteLine();
Console.WriteLine("== 19. addon 版本号：文件名优先，PE 兜底 ==");
Check(AddonVersionResolver.Resolve(null, "1.0.8.18") == "1.0.8.18", "文件名括号里有版本就直接用它");
Check(AddonVersionResolver.Resolve(null, null) is null, "没路径没版本 → null");
Check(AddonVersionResolver.Resolve(Path.Combine(root, "nope.addon64"), null) is null, "文件不在 → null（不抛异常）");

string realAddonDir = @"D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons";
string? realBridge = Directory.Exists(realAddonDir)
    ? Directory.EnumerateFiles(realAddonDir, "dlss5-bridge.addon64*").FirstOrDefault()
    : null;
if (realBridge is not null)
{
    string? bridgeVersion = AddonVersionResolver.Resolve(realBridge, AddonFileInfo.Parse(Path.GetFileName(realBridge))?.Version);
    Check(!string.IsNullOrWhiteSpace(bridgeVersion), $"dlss5-bridge 名字里没版本，从 PE 读到了 {bridgeVersion}");
}
else
{
    Console.WriteLine("  [SKIP] 找不到真实的 dlss5-bridge.addon64，跳过 PE 版本实测");
}

string? realRenodx = Directory.Exists(realAddonDir)
    ? Directory.EnumerateFiles(realAddonDir, "renodx-dlss(*.addon64*").FirstOrDefault()
    : null;
if (realRenodx is not null)
{
    // renodx-dlss 的 PE 版本字段里塞的是时间戳，不能被当成版本号
    string? peOnly = AddonVersionResolver.Resolve(realRenodx, null);
    Check(peOnly is null, $"时间戳式的 PE 版本被过滤掉了（实际 {peOnly ?? "null"}）");
}
else
{
    Console.WriteLine("  [SKIP] 找不到真实的 renodx-dlss，跳过时间戳过滤实测");
}

Console.WriteLine();
Console.WriteLine("== 24. ReShade.ini 的绝对路径对回当前 HoYoShade ==");

// 场景：游戏目录里的 ini 是**别的** HoYoShade 生成的（换过启动器 / 换过用户数据目录），
// 里面全是旧根目录的绝对路径 —— 运行时 ReShade 就会去旧目录找插件和 dll（用户反馈的那个红标）
string oldShadeRoot = Path.Combine(root, "old-data", "HoYoShade");
string gameIniPath = Path.Combine(root, "game", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(gameIniPath)!);
File.WriteAllLines(gameIniPath, [
    "[ADDON]",
    @"AddonPath=" + oldShadeRoot + @"\reshade-shaders\Addons\",
    "DisabledAddons=RenoDX DLSS@renodx-dlss5-super-anus.addon64",
    "",
    "[GENERAL]",
    @"EffectSearchPaths=" + oldShadeRoot + @"\reshade-shaders\Shaders\**,.\reshade-shaders\Shaders\**",
    @"TextureSearchPaths=" + oldShadeRoot + @"\reshade-shaders\Textures\**",
    @"PresetPath=" + oldShadeRoot + @"\Presets\Mod OFF.ini",
    @"IntermediateCachePath=C:\Users\me\AppData\Local\Temp\ReShade",
    "",
    "[STYLE]",
    @"Font=" + oldShadeRoot + @"\InjectResource\Fonts\MiSans-Bold.ttf",
    "",
    "[SCREENSHOT]",
    @"SavePath=" + oldShadeRoot + @"\ScreenShot\",
    "",
    "[RENODX-DLSS]",
    "DirectNeuralRenderingHookPoint=4",
]);

ShadePathAlignResult aligned = ShadePathAligner.Align(gameIniPath, shadeRoot);
Check(aligned.Changed, $"旧根目录被识别出来了（{aligned.PreviousRoot}）");
Check(aligned.ChangedKeys.Count == 6, $"改了 6 个键：{string.Join("、", aligned.ChangedKeys)}");

ReShadeProfile alignedProfile = ReShadeProfile.Load(gameIniPath);
Check(alignedProfile.AddonPath == Path.Combine(shadeRoot, "reshade-shaders", "Addons"),
    $"AddonPath 指回当前 HoYoShade：{alignedProfile.AddonPath}");
Check(alignedProfile.ResolveAddonDirectory() == Path.Combine(shadeRoot, "reshade-shaders", "Addons"),
    "插件页算红黄标用的目录跟着一起对了");

string iniText = File.ReadAllText(gameIniPath);
Check(iniText.Contains(shadeRoot + @"\reshade-shaders\Shaders\**"), "EffectSearchPaths 换成了当前根");
// 游戏目录 ini 里的相对路径按 ini 所在目录解析 = 游戏目录下，那里没有 reshade-shaders ——
// 必须改写到当前根下（以前原样留着 → 提示条报不一致、按钮却说「不用改」）
Check(!iniText.Contains(@".\reshade-shaders\Shaders\**"), "游戏 ini 里的相对搜索路径不再原样留着");
Check(iniText.IndexOf("reshade-shaders\\Shaders\\**", StringComparison.OrdinalIgnoreCase) >= 0
      && iniText.Split(shadeRoot + @"\reshade-shaders\Shaders\**").Length - 1 == 1,
    "相对那条被改写到当前根，且和绝对那条去重成一条");
Check(iniText.Contains("DirectNeuralRenderingHookPoint=4"), "[RENODX-DLSS] 里调好的参数没被动");
Check(iniText.Contains("DisabledAddons=RenoDX DLSS@renodx-dlss5-super-anus.addon64"), "每个游戏的插件开关没被动");
Check(iniText.Contains(@"IntermediateCachePath=C:\Users\me\AppData\Local\Temp\ReShade"), "跟 HoYoShade 无关的路径没被动");

ShadePathAlignResult alignAgain = ShadePathAligner.Align(gameIniPath, shadeRoot);
Check(!alignAgain.Changed, "再跑一次什么都不改（幂等）");

// 认不出来 / 不该动的
Check(ShadePathAligner.RootOf(@".\reshade-shaders\Addons") is null, "相对路径不反推根目录");
Check(ShadePathAligner.RootOf(@"D:\MyAddons\Stuff") is null, "不带 HoYoShade 特征目录的自定义路径不动");
Check(ShadePathAligner.RootOf(@"D:\Games\MyPresets\p.ini") is null, "「MyPresets」这种子串不算 Presets 段");
Check(ShadePathAligner.RootOf(shadeRoot + @"\Presets\Mod OFF.ini") == shadeRoot, "按 Presets 段能反推出根目录");
Check(ShadePathAligner.RootOf(shadeRoot + @"\reshade-shaders\Addons\") == shadeRoot, "按 reshade-shaders 段能反推出根目录（末尾分隔符不算）");

string untouched = ShadePathAligner.AlignValue(@"D:\MyAddons\Stuff;D:\x", shadeRoot);
Check(untouched == @"D:\MyAddons\Stuff;D:\x", "分号写法我们认不准，原样返回");

string? missingIni = ShadePathAligner.Align(Path.Combine(root, "nope", "ReShade.ini"), shadeRoot).Changed
    ? "changed" : null;
Check(missingIni is null, "ini 不存在 → 什么都不做、不抛异常");

// 第三方整合包（群友的 Seri 案例）：reshade-shaders 形状但根不叫 HoYoShade ——
// 一样要被「指回」当前 HoYoShade（以前直接跳过 → 指回无效、红标消不掉），重复条目顺手去重
string seriRoot = @"C:\ProgramData\ReShade Addons\Seri";
string seriPath = seriRoot + @"\reshade-shaders\Shaders\**";
Check(ShadePathAligner.RootOf(seriPath) == seriRoot, "第三方包的根也能被反推出来");

string seriIni = Path.Combine(root, "serigame", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(seriIni)!);
File.WriteAllLines(seriIni, [
    "[ADDON]",
    @"AddonPath=" + shadeRoot + @"\reshade-shaders\Addons",
    "",
    "[GENERAL]",
    @"EffectSearchPaths=" + shadeRoot + @"\reshade-shaders\Shaders\**," + shadeRoot + @"\reshade-shaders\Shaders\**," + seriPath,
    @"TextureSearchPaths=" + shadeRoot + @"\reshade-shaders\Textures\**",
]);

ShadePathAlignResult seriAligned = ShadePathAligner.Align(seriIni, shadeRoot);
Check(seriAligned.ChangedKeys.Contains("[GENERAL] EffectSearchPaths"), "第三方路径被指回 + 重复条目被去重：" + string.Join("、", seriAligned.ChangedKeys));
string afterAlign = File.ReadAllText(seriIni);
Check(!afterAlign.Contains("ProgramData"), "Seri 的路径被改写成当前根了");
Check(afterAlign.Split(shadeRoot + @"\reshade-shaders\Shaders\**").Length - 1 == 1, "当前根的搜索路径只剩一条（去重生效）");
Check(!ShadePathAligner.Align(seriIni, shadeRoot).Changed, "修完再对齐是幂等的");
try { Directory.Delete(Path.GetDirectoryName(seriIni)!, true); } catch { }

// 用户截图那个 case：完整包把绝对路径改写成相对，模板复制到游戏目录后 AddonPath 变成相对路径，
// 按 ini 所在目录解析落到游戏目录\reshade-shaders\Addons（不存在）——
// 提示条报「指向游戏目录」，对齐器却跳过相对路径 → 点「指回」弹「不用改」，死锁
string relativeIni = Path.Combine(root, "relative-game", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(relativeIni)!);
File.WriteAllLines(relativeIni, [
    "[ADDON]",
    @"AddonPath=.\reshade-shaders\Addons",
]);

ShadePathAlignResult relativeAligned = ShadePathAligner.Align(relativeIni, shadeRoot);
Check(relativeAligned.ChangedKeys.Contains("[ADDON] AddonPath"), "游戏 ini 的相对 AddonPath 被指回当前根");
Check(ReShadeProfile.Load(relativeIni).ResolveAddonDirectory() == Path.Combine(shadeRoot, "reshade-shaders", "Addons"),
    "改完后插件页算出来的目录就是当前 HoYoShade");
Check(!ShadePathAligner.Align(relativeIni, shadeRoot).Changed, "改完再对齐是幂等的");
try { Directory.Delete(Path.GetDirectoryName(relativeIni)!, true); } catch { }

// 反过来：宿主根目录里那份 ini 的相对路径跟着 DLL 走，永远是对的 —— 不动
string hostIni = Path.Combine(shadeRoot, "ReShade.ini");
Directory.CreateDirectory(shadeRoot);
string hostIniBackup = File.Exists(hostIni) ? File.ReadAllText(hostIni) : string.Empty;
File.WriteAllLines(hostIni, [
    "[ADDON]",
    @"AddonPath=.\reshade-shaders\Addons",
    "",
    "[GENERAL]",
    @"EffectSearchPaths=.\reshade-shaders\Shaders\**",
]);
Check(!ShadePathAligner.Align(hostIni, shadeRoot).Changed, "宿主 ini 里的相对路径不动");
if (hostIniBackup.Length > 0) { File.WriteAllText(hostIni, hostIniBackup); } else { File.Delete(hostIni); }

Console.WriteLine("== 25. OptiScaler 本地库（下载 / 单选 / 删除）==");
string optiRoot = Path.Combine(userDataFolder, "OptiScaler");
var library = new OptiScalerLibrary(optiRoot);
Check(library.List().Count == 0, "库里还什么都没有的时候是空表");
Check(library.GetSelected() is null, "没有选中项");
Check(library.SelectedDllPath is null, "没启用时没有可注入的 dll");

// 造两个「已下载」的构建（= 解压后的目录 + build.json）
string buildA = library.DirectoryFor("wilsjo2", "v0.8.6");
Directory.CreateDirectory(Path.Combine(buildA, "OptiScaler"));
File.WriteAllText(Path.Combine(buildA, "OptiScaler", "OptiScaler.dll"), "fake dll");
File.WriteAllText(Path.Combine(buildA, "OptiScaler.ini"), "fake ini");
File.WriteAllText(Path.Combine(buildA, OptiScalerLibrary.BuildManifestName),
    @"{""sourceId"":""wilsjo2"",""version"":""v0.8.6"",""assetName"":""OptiScaler-NR-v0.8.6.zip""}");

string buildB = library.DirectoryFor("neurotic", "alpha-0.9.6");
Directory.CreateDirectory(buildB);
File.WriteAllText(Path.Combine(buildB, "OptiScaler.dll"), "fake dll");
File.WriteAllText(Path.Combine(buildB, OptiScalerLibrary.BuildManifestName),
    @"{""sourceId"":""neurotic"",""version"":""alpha-0.9.6""}");

List<OptiScalerBuild> builds = library.List();
Check(builds.Count == 2, $"扫到 2 个构建（实际 {builds.Count}）");
Check(builds.All(b => b.DllPath is not null), "两边的 OptiScaler.dll 都找到了（放在子目录里的那个也算）");
Check(builds.All(b => b.SizeBytes > 0), "构建目录大小算出来了（界面显示用）");

library.Select("wilsjo2/v0.8.6");
Check(library.GetSelected()?.Id == "wilsjo2/v0.8.6", "选中 wilsjo2/v0.8.6");
library.Select("neurotic/alpha-0.9.6");
Check(library.GetSelected()?.Id == "neurotic/alpha-0.9.6", "换一个 —— 单选：同一时间只有一个启用");
Check(File.ReadAllText(Path.Combine(optiRoot, OptiScalerLibrary.StateFileName)).Contains("neurotic/alpha-0.9.6"),
    "选择写进了 state.json");
Check(library.SelectedDllPath == Path.Combine(buildB, "OptiScaler.dll"), "注入用的就是选中的那个 dll");

bool selectMissing = false;
try { library.Select("nope/v1"); } catch (InvalidOperationException) { selectMissing = true; }
Check(selectMissing, "选一个不存在的构建会抛错，而不是把 state 写坏");

Check(library.Delete("neurotic/alpha-0.9.6"), "删掉当前选中的那个构建");
Check(library.GetSelected() is null, "选中的被删了 → 回到「未启用」（不会指向空目录）");
Check(library.List().Count == 1, "另一个构建还在");
Check(!Directory.Exists(Path.Combine(optiRoot, "neurotic")), "来源目录空了会被一起收掉");

Check(!library.Delete("nope/v1"), "删不存在的构建返回 false，而不是炸");

Console.WriteLine("-- 安装程序来源：version.dll 也要能认成注入目标 --");
string proxyBuild = library.DirectoryFor("dlssnr-amd", "v0.3.1");
Directory.CreateDirectory(proxyBuild);
File.WriteAllText(Path.Combine(proxyBuild, "version.dll"), "fake");
File.WriteAllText(Path.Combine(proxyBuild, "nvngx.dll"), "fake runtime");
File.WriteAllText(Path.Combine(proxyBuild, OptiScalerLibrary.BuildManifestName),
    @"{""sourceId"":""dlssnr-amd"",""version"":""v0.3.1"",""assetName"":""dlssnr_on_amd_setup.exe""}");
Check(OptiScalerLibrary.FindDll(proxyBuild) is { } p1 && Path.GetFileName(p1) == "version.dll",
    "代理名 version.dll 认成注入目标（目录里还有 nvngx.dll 也不会认错）");
Check(OptiScalerLibrary.ProxyDllNames.Contains("dxgi.dll") && OptiScalerLibrary.ProxyDllNames[0] == "version.dll",
    "代理名表里 version.dll 排第一（DLSS NR on AMD 默认装这个）");

string bothDir = library.DirectoryFor("dlssnr-amd", "v0.3.2");
Directory.CreateDirectory(bothDir);
File.WriteAllText(Path.Combine(bothDir, "version.dll"), "fake");
File.WriteAllText(Path.Combine(bothDir, "OptiScaler.dll"), "fake");
Check(Path.GetFileName(OptiScalerLibrary.FindDll(bothDir)!) == "OptiScaler.dll", "正主 OptiScaler.dll 优先于代理名");

Console.WriteLine("-- OptiScaler 主 DLL 归一化（dlss-unlocked 的 dxgi.dll → OptiScaler.dll）--");
string normalizeDir = library.DirectoryFor("dlss-unlocked", "NR-v0.9.10");
Directory.CreateDirectory(Path.Combine(normalizeDir, "OptiScaler"));
File.WriteAllText(Path.Combine(normalizeDir, "OptiScaler", "dxgi.dll"), "fake real body");
File.WriteAllText(Path.Combine(normalizeDir, "OptiScaler.ini"), "fake ini");
Check(Path.GetFileName(OptiScalerLibrary.FindDll(normalizeDir)!) == "dxgi.dll", "归一化前正身识别为 dxgi.dll");
Check(OptiScalerLibrary.NormalizePrimaryDll(normalizeDir), "dxgi.dll 改名成 OptiScaler.dll");
Check(!File.Exists(Path.Combine(normalizeDir, "OptiScaler", "dxgi.dll")), "旧的 dxgi.dll 已不存在");
Check(Path.GetFileName(OptiScalerLibrary.FindDll(normalizeDir)!) == "OptiScaler.dll", "归一化后注入目标是 OptiScaler.dll");
Check(!OptiScalerLibrary.NormalizePrimaryDll(normalizeDir), "已经叫 OptiScaler.dll 时不重复操作");

Console.WriteLine("-- OptiScaler 的 nvngx_dlssnr.dll（各分支手册都要求放在包旁边）--");
string nrdllAddonsDir = Path.Combine(root, "fake-addons");
Directory.CreateDirectory(nrdllAddonsDir);
File.WriteAllText(Path.Combine(nrdllAddonsDir, OptiScalerRuntime.NeuralRuntimeFileName), "nr-runtime-payload");
string nrdllBuildDir = library.DirectoryFor("wilsjo2", "v0.8.9");
Directory.CreateDirectory(nrdllBuildDir);
File.WriteAllText(Path.Combine(nrdllBuildDir, "OptiScaler.dll"), "fake");

Check(!OptiScalerRuntime.HasNrdll(nrdllBuildDir), "一开始构建目录里没有运行时");

NrdllPlaceResult nrdllPlaced = OptiScalerRuntime.EnsureNrdll(nrdllBuildDir, nrdllAddonsDir);
Check(nrdllPlaced.Status == NrdllPlaceStatus.Copied && nrdllPlaced.Ok, "从插件目录复制过来：" + nrdllPlaced.Message);
Check(OptiScalerRuntime.HasNrdll(nrdllBuildDir), "复制完构建目录里就有了");
Check(File.ReadAllText(Path.Combine(nrdllBuildDir, OptiScalerRuntime.NeuralRuntimeFileName)) == "nr-runtime-payload", "内容一致");

NrdllPlaceResult nrdllAgain = OptiScalerRuntime.EnsureNrdll(nrdllBuildDir, nrdllAddonsDir);
Check(nrdllAgain.Status == NrdllPlaceStatus.AlreadyPresent, "已有同样大小的一份就不重复拷（别乱覆盖用户自己换的版本）");

File.WriteAllText(Path.Combine(nrdllBuildDir, OptiScalerRuntime.NeuralRuntimeFileName), "a-much-longer-user-supplied-runtime");
NrdllPlaceResult nrdllFixedUp = OptiScalerRuntime.EnsureNrdll(nrdllBuildDir, nrdllAddonsDir);
Check(nrdllFixedUp.Status == NrdllPlaceStatus.Copied && File.ReadAllText(Path.Combine(nrdllBuildDir, OptiScalerRuntime.NeuralRuntimeFileName)) == "nr-runtime-payload",
    "目录里那份大小不对时会被插件目录里的覆盖回去");

string nrdllNoSourceBuild = library.DirectoryFor("neurotic", "alpha-9.9");
Directory.CreateDirectory(nrdllNoSourceBuild);
File.WriteAllText(Path.Combine(nrdllNoSourceBuild, "OptiScaler.dll"), "fake");
NrdllPlaceResult nrdllMissing = OptiScalerRuntime.EnsureNrdll(nrdllNoSourceBuild, Path.Combine(root, "nope"));
Check(nrdllMissing.Status == NrdllPlaceStatus.NotFound && !nrdllMissing.Ok, "找不到源头时明确报 NotFound（界面据此提示去 DLL 配置装一个）");

Console.WriteLine("-- 原神 FSR 桥（模块的 DLL/ini + 补齐）--");
Check(OptiScalerRuntime.FsrBridgeDllName == "Dx11FsrBridge.dll" && OptiScalerRuntime.FsrBridgeIniName == "Dx11FsrBridge.ini",
    "桥的两个文件名固定（模块 dllHint 与补齐逻辑都按它们来）");

string bridgeDir = Path.Combine(root, "bridge-module");
Directory.CreateDirectory(bridgeDir);
Check(!OptiScalerRuntime.HasFsrBridge(bridgeDir), "一开始目录里没有桥");

Check(OptiScalerRuntime.EnsureFsrBridgeIni(bridgeDir), "ini 缺失时补一份");
string bridgeIni = Path.Combine(bridgeDir, OptiScalerRuntime.FsrBridgeIniName);
Check(File.Exists(bridgeIni), "ini 真的写出来了");
string bridgeIniText = File.ReadAllText(bridgeIni);
Check(bridgeIniText.Contains("[Dx11FsrBridge]"), "补齐的 ini 用了桥认的段名");
Check(bridgeIniText.Contains("EnableFsr2GetProcAddressShim=1"), "补上了关键的那个垫片开关（OptiScaler 就靠它看见 FSR2）");
Check(bridgeIniText.All(c => c < 128), "ini 是纯 ASCII（桥按窄字符读，不赌中文注释的编码）");

File.WriteAllText(bridgeIni, "user-tuned-ini");
Check(!OptiScalerRuntime.EnsureFsrBridgeIni(bridgeDir), "已经有一份就不动（用户可能照 CXP 那份调过 RVA）");
Check(File.ReadAllText(bridgeIni) == "user-tuned-ini", "内容原样保留，没被覆盖");

Check(!OptiScalerRuntime.EnsureFsrBridgeIni(Path.Combine(root, "no-such-bridge-dir")), "目录不存在时返回 false，不炸");

string bridgeWithDll = Path.Combine(root, "bridge-module-2");
Directory.CreateDirectory(bridgeWithDll);
File.WriteAllText(Path.Combine(bridgeWithDll, OptiScalerRuntime.FsrBridgeDllName), "fake");
Check(OptiScalerRuntime.HasFsrBridge(bridgeWithDll), "DLL 在就算有桥（ini 只是覆盖项，桥自带代码默认值）");

// 2026-10-07 用户实测：1.4.3.1 的模块下载器把桥的 Dx11FsrBridge.dll 改名成了 OptiScaler.dll
//（OptiScalerLibrary.NormalizePrimaryDll 对所有 Release 模块一律归一化），之后所有「按文件名
// 找桥」的地方都认不出来 —— 模块明明装好了却一直报「FSR Bridge 缺失」。
Console.WriteLine("-- 桥被旧下载器改名成 OptiScaler.dll（装了也要认得出来）--");
string hostPe = Environment.ProcessPath
    ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
    ?? throw new InvalidOperationException("拿不到测试宿主自己的 PE 路径");

string renamedBridge = Path.Combine(root, "bridge-renamed", "genshin-fsr-bridge", "v2.3.4-fg-20261006");
Directory.CreateDirectory(renamedBridge);
File.Copy(hostPe, Path.Combine(renamedBridge, FsrBridgePayload.LegacyAliasDllName), overwrite: true);
File.WriteAllText(Path.Combine(renamedBridge, FsrBridgePayload.IniName), OptiScalerRuntime.FsrBridgeIniTemplate);

Check(!File.Exists(Path.Combine(renamedBridge, FsrBridgePayload.DllName)), "复现现场：目录里只有 OptiScaler.dll，没有 Dx11FsrBridge.dll");
Check(FsrBridgePayload.LooksLikeBridgeDirectory(renamedBridge), "带桥 ini 的目录仍然认得出来（判据不认光秃秃的 OptiScaler.dll）");
string? renamedFound = FsrBridgePayload.FindDll(renamedBridge);
Check(renamedFound is not null && Path.GetFileName(renamedFound) == FsrBridgePayload.LegacyAliasDllName, "别名那份被当成桥要注入的 DLL");
Check(HostBridgeVersion() == "2.3.4.0", $"测试宿主自己的版本号就是 2.3.4.0（实际 {HostBridgeVersion()}）");
Check(BridgeAccepted(renamedFound!), "2.3.4.0 的桥过版本门（这就是用户机上那份的版本）");
Check(OptiScalerRuntime.HasFsrBridge(renamedBridge), "HasFsrBridge：改名之后也算有桥");

string? renamedBack = FsrBridgePayload.NormalizeDll(renamedBridge);
Check(renamedBack is not null && Path.GetFileName(renamedBack) == FsrBridgePayload.DllName, "归位：OptiScaler.dll → Dx11FsrBridge.dll");
Check(File.Exists(Path.Combine(renamedBridge, FsrBridgePayload.DllName)) && !File.Exists(Path.Combine(renamedBridge, "OptiScaler.dll")), "归位之后目录里就是正身名");
Check(Path.GetFileName(FsrBridgePayload.NormalizeDll(renamedBridge)!) == FsrBridgePayload.DllName, "再归位一次是幂等的（不报错、不覆盖）");

string canonicalBridge = Path.Combine(root, "bridge-canonical");
Directory.CreateDirectory(canonicalBridge);
File.WriteAllText(Path.Combine(canonicalBridge, FsrBridgePayload.DllName), "canonical");
Check(Path.GetFileName(FsrBridgePayload.FindDll(canonicalBridge)!) == FsrBridgePayload.DllName, "正身名优先（原来的行为不变）");
Check(FsrBridgePayload.NormalizeDll(canonicalBridge) is not null && !File.Exists(Path.Combine(canonicalBridge, "OptiScaler.dll")), "正身名已在时归位是空操作");
File.WriteAllText(Path.Combine(canonicalBridge, FsrBridgePayload.LegacyAliasDllName), "alias");
Check(Path.GetFileName(FsrBridgePayload.FindDll(canonicalBridge)!) == FsrBridgePayload.DllName
      && File.Exists(Path.Combine(canonicalBridge, "OptiScaler.dll")), "两个名字都在时用正身名，别名原样留着");

string optiOnlyDir = Path.Combine(root, "opti-only-build", "dlss-unlocked", "NR-v0.9.10");
Directory.CreateDirectory(optiOnlyDir);
File.WriteAllText(Path.Combine(optiOnlyDir, "OptiScaler.dll"), "opti");
File.WriteAllText(Path.Combine(optiOnlyDir, "OptiScaler.ini"), "opti-ini");
Check(!FsrBridgePayload.LooksLikeBridgeDirectory(optiOnlyDir), "纯 OptiScaler 构建目录不算桥");
Check(FsrBridgePayload.NormalizeDll(optiOnlyDir) is null && File.Exists(Path.Combine(optiOnlyDir, "OptiScaler.dll")), "不把真正的 OptiScaler.dll 改名成桥");
Check(!OptiScalerRuntime.HasFsrBridge(optiOnlyDir), "HasFsrBridge 也不会把纯 OptiScaler 目录当成桥");

Check(!BridgeCompatibility.IsSupported(new Version(2, 2, 9, 0)), "2.2.9.0 的旧桥仍然被拒");
Check(!BridgeCompatibility.IsSupported(new Version(2, 3, 0, 0)), "2.3.0 仍然被拒（要 >= 2.3.1）");
Check(BridgeCompatibility.IsSupported(new Version(2, 3, 4, 0)), "2.3.4.0 放行");

string fakeBridgeDir = Path.Combine(root, "bridge-fake-dll");
Directory.CreateDirectory(fakeBridgeDir);
File.WriteAllText(Path.Combine(fakeBridgeDir, FsrBridgePayload.LegacyAliasDllName), "not-a-real-pe");
Check(!BridgeAccepted(Path.Combine(fakeBridgeDir, FsrBridgePayload.LegacyAliasDllName)), "0.0.0.0 的假 DLL 过不了版本门（不会被拿去注入）");

// 桥的 autoload 清单：跟着「启用OptiScaler」走 —— 开了写回去、关了撤走（先留一份备份）
string autoloadRoot = Path.Combine(root, "bridge-autoload");
string autoloadBridge = Path.Combine(autoloadRoot, "payload", "Bridge");
string autoloadOpti = Path.Combine(autoloadRoot, "payload", "OptiScaler");
Directory.CreateDirectory(autoloadBridge);
Directory.CreateDirectory(autoloadOpti);
string autoloadDll = Path.Combine(autoloadOpti, "OptiScaler.dll");
File.WriteAllText(autoloadDll, "fake");
string autoloadFile = Path.Combine(autoloadBridge, OptiScalerRuntime.FsrBridgeAutoloadName);
Check(OptiScalerRuntime.ReadFsrBridgeAutoload(autoloadBridge) is null, "一开始桥目录里没有 autoload 清单");

string? autoloadRelative = OptiScalerRuntime.WriteFsrBridgeAutoload(autoloadBridge, autoloadDll);
Check(autoloadRelative is not null && !Path.IsPathRooted(autoloadRelative),
    $"同一个包里的 OptiScaler 写相对路径（实际 {autoloadRelative}）");
Check(autoloadRelative == Path.Combine("..", "OptiScaler", "OptiScaler.dll"),
    "相对路径形如 ..\\OptiScaler\\OptiScaler.dll，整包拷给别人还能用");
byte[] autoloadBytes = File.ReadAllBytes(autoloadFile);
Check(!(autoloadBytes.Length >= 3 && autoloadBytes[0] == 0xEF && autoloadBytes[1] == 0xBB && autoloadBytes[2] == 0xBF),
    "清单不带 BOM（免得读的人把 BOM 当成路径的第一个字符）");

string? autoloadFar = OptiScalerRuntime.WriteFsrBridgeAutoload(autoloadBridge, Path.Combine(root, "far-away", "OptiScaler.dll"));
Check(autoloadFar is not null && Path.IsPathRooted(autoloadFar), "爬到两层以上才退回绝对路径（那种布局本来就搬不走）");

OptiScalerRuntime.WriteFsrBridgeAutoload(autoloadBridge, autoloadDll);
Check(OptiScalerRuntime.RemoveFsrBridgeAutoload(autoloadBridge, out string? autoloadBackup)
      && autoloadBackup is not null && File.Exists(autoloadBackup),
    "关掉 OptiScaler 时撤走清单，并留了一份备份");
Check(!File.Exists(autoloadFile), "清单本体已经不在桥目录里（桥下次启动不会再把它拉回来）");
Check(!OptiScalerRuntime.RemoveFsrBridgeAutoload(autoloadBridge, out _), "再撤一次没东西可撤，返回 false");

File.WriteAllText(autoloadBackup!, "user-own-copy");
OptiScalerRuntime.WriteFsrBridgeAutoload(autoloadBridge, autoloadDll);
OptiScalerRuntime.RemoveFsrBridgeAutoload(autoloadBridge, out _);
Check(File.ReadAllText(autoloadBackup!) == "user-own-copy", "已经有一份备份就不再覆盖（用户自己放的东西留着）");

Check(OptiScalerRuntime.WriteFsrBridgeAutoload(autoloadBridge, autoloadDll) is not null
      && OptiScalerRuntime.ReadFsrBridgeAutoload(autoloadBridge)?.Trim() == Path.Combine("..", "OptiScaler", "OptiScaler.dll"),
    "重新打开 OptiScaler 又写回去（关了再开能回到原样）");
Check(OptiScalerRuntime.WriteFsrBridgeAutoload(Path.Combine(root, "no-such-dir-xyz"), autoloadDll) is null,
    "目录不存在时返回 null（不炸，启动器会走外部注入兜底）");
Check(!OptiScalerRuntime.RemoveFsrBridgeAutoload(null, out _), "目录传空也不炸");

Console.WriteLine("-- 插件汉化（整条覆盖式原地替换 + 备份 / 还原）--");
AddonI18nDocument builtinTable = AddonLocalizer.LoadBuiltin();
// 2026-09-28：内置表只保留 renodx-dlss 一张 —— renodx-dlss5 / dlss5-bridge 自带多语言，不再打补丁
Check(builtinTable.Tables.Count == 1 && builtinTable.Tables[0].Slug == "renodx-dlss", $"内置翻译表只剩 renodx-dlss 一张（实际 {builtinTable.Tables.Count} 张）");
AddonI18nTable? noDlss5Table = AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss5-super-anus(1.0.8.18).addon64");
Check(noDlss5Table is null, "renodx-dlss5 自带多语言，没有表、不再汉化");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss(9.17.12).addon64")?.Slug == "renodx-dlss", "renodx-dlss 能选到自己那张表");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss_SF_1.0.0.addon64")?.Slug == "renodx-dlss", "renodx-dlss_SF（下划线分隔）也算 renodx-dlss");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss.addon64")?.Slug == "renodx-dlss", "裸 renodx-dlss 精确匹配");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "some-other-thing.addon64") is null, "别的插件没有表就不汉化");
Check(AddonLocalizer.AlgorithmVersion >= 2, $"补丁算法有版本号，旧补丁过的文件才知道要还原重打（实际 v{AddonLocalizer.AlgorithmVersion}）");

// 造一个「假 DLL」：尾部放几条 NUL 结尾的英文，加上一条超长中文的对照
string fakeDll = Path.Combine(root, "fake-addon.addon64");
byte[] fakeBytes = System.Text.Encoding.UTF8.GetBytes(string.Join((char)0, ["junk", "Off", "Reset", "Ultra Performance", "Too Long Sentence Here"]));
File.WriteAllBytes(fakeDll, fakeBytes);
string backupDir = Path.Combine(root, ".hysx", AddonLocalizer.BackupFolderName);

AddonI18nTable testTable = AddonLocalizer.LoadBuiltin().Tables.First(t => t.Slug == "renodx-dlss");
AddonLocalizeResult localize = AddonLocalizer.Apply(fakeDll, testTable, backupDir);
// 注意：Reset(5 字节) 放不下「重置」(6 字节) → 会被算进「放不下跳过」，这正是原地替换的边界
Check(localize.Applied >= 2, $"Off 和 Ultra Performance 被替换了，实际 {localize.Applied} 条：{localize.Message}");
Check(localize.SkippedTooLong >= 1, $"中文放不下的被跳过并计数（实际 {localize.SkippedTooLong} 条）");
Check(localize.BackupPath is not null && File.Exists(localize.BackupPath!), "动手前备份了原文件");

byte[] patched = File.ReadAllBytes(fakeDll);
string patchedText = System.Text.Encoding.UTF8.GetString(patched);
Check(patchedText.Contains("关"), "DLL 里出现中文（Off → 关）");
Check(patched.Length == fakeBytes.Length, "原地替换不改变文件大小（右边补空格）");
Check(patchedText.Contains("Too Long Sentence Here"), "放不下的英文原样留着");

AddonLocalizeResult restore = AddonLocalizer.Restore(fakeDll, backupDir);
Check(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(fakeDll)) == System.Text.Encoding.UTF8.GetString(fakeBytes), "还原回原版（一个字节不差）");
Check(AddonLocalizer.BackupPathOf(fakeDll, backupDir) is not null, "备份还在，可以再打");

// 回归：needle 是更长字符串的「后半截」时不许命中（空格也算字符串内部）。
// 旧规则的左边界判断只排除 0x21-0x7E，空格漏网 → "Open Neural Rendering Debug" 会被
// "Neural Rendering Debug" 命中，替换成 "Open 神经渲染调试"。
byte[] contextBytes = System.Text.Encoding.UTF8.GetBytes(string.Join((char)0, ["junk", "Open Neural Rendering Debug", "Skin Structure Strength", ""]));   // 末尾空串 = 收尾 NUL
Check(AddonLocalizer.FindLiteral(contextBytes, System.Text.Encoding.UTF8.GetBytes("Neural Rendering Debug")).Count == 0,
    "不命中更长字符串的后半截（Open Neural Rendering Debug 不该被改）");
Check(AddonLocalizer.FindLiteral(contextBytes, System.Text.Encoding.UTF8.GetBytes("Open Neural Rendering Debug")).Count == 1,
    "整条字符串照样能命中");
Check(AddonLocalizer.FindLiteral(contextBytes, System.Text.Encoding.UTF8.GetBytes("Skin Structure Strength")).Count == 1,
    "紧跟 NUL 的完整字符串能命中");

// 代码立即数：短标签根本不是字符串，编译器写成 mov rax, imm64（实测 48 B8 "Upscaled"），
// 只改 .rdata 的话界面永远英文。
byte[] codeBytes =
[
    0x48, 0xB8, (byte)'U', (byte)'p', (byte)'s', (byte)'c', (byte)'a', (byte)'l', (byte)'e', (byte)'d',
    0x48, 0x89, 0x85, 0x38, 0x07, 0x00, 0x00,
];
byte[] upsSource = System.Text.Encoding.UTF8.GetBytes("Upscaled");
byte[] upsTarget = System.Text.Encoding.UTF8.GetBytes("放大");
List<(int Position, int Size)> upsImmediates = [.. AddonLocalizer.EnumerateImmediates(codeBytes, [(0, codeBytes.Length)])];
Check(upsImmediates.Count == 1 && upsImmediates[0].Position == 2 && upsImmediates[0].Size == 8,
    $"认得出 mov rax, imm64 里的字符串立即数（实际 {upsImmediates.Count} 个）");
Check(AddonLocalizer.HasCodeImmediate(codeBytes, upsSource, upsImmediates), "立即数确实等于 Upscaled");
Check(AddonLocalizer.PatchCodeImmediates(codeBytes, upsSource, upsTarget, upsImmediates) == 1, "整条都在这一个立即数里 → 换掉");
Check(System.Text.Encoding.UTF8.GetString(codeBytes, 2, 8) == "放大" + new string(' ', 2),
    $"立即数换成「放大」并补空格（实际 '{System.Text.Encoding.UTF8.GetString(codeBytes, 2, 8)}'）");
Check(codeBytes[10] == 0x48 && codeBytes[11] == 0x89, "立即数后面的指令字节没被动");

// ★ 新算法的核心：一条英文的每个字节都要被立即数盖住，否则**整条不写**。
// 老版本「能写多少写多少」，于是出现「中文头 + 英文尾」——用户截图里的「结构强度 sity」「缩ling」。
byte[] tailOnly =
[
    0x48, 0xBE, (byte)'S', (byte)'t', (byte)'r', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h',
    0x48, 0x89, 0x70, 0x1E,
];
byte[] skinSource = System.Text.Encoding.UTF8.GetBytes("Skin Structure Strength");
byte[] skinTarget = System.Text.Encoding.UTF8.GetBytes("皮肤结构强度");
List<(int Position, int Size)> tailImmediates = [.. AddonLocalizer.EnumerateImmediates(tailOnly, [(0, tailOnly.Length)])];
Check(AddonLocalizer.PatchCodeImmediates(tailOnly, skinSource, skinTarget, tailImmediates) == 0,
    "只有尾巴、没有头（盖不满）→ 一个字节都不写");
Check(System.Text.Encoding.UTF8.GetString(tailOnly, 2, 8) == "Strength",
    $"英文原样留着（实际 '{System.Text.Encoding.UTF8.GetString(tailOnly, 2, 8)}'）");

// 头 + 尾凑齐（12 字节全覆盖）→ 两段都换成目标串的对应段
byte[] paddedBytes =
[
    0x48, 0xB8, (byte)'O', (byte)'p', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'s', (byte)' ',
    0x48, 0xBA, (byte)'M', (byte)'o', (byte)'d', (byte)'e', 0x00, 0x00, 0x00, 0x00,
];
byte[] optSource = System.Text.Encoding.UTF8.GetBytes("Options Mode");
byte[] optTarget = System.Text.Encoding.UTF8.GetBytes("选项模式");
List<(int Position, int Size)> paddedImmediates = [.. AddonLocalizer.EnumerateImmediates(paddedBytes, [(0, paddedBytes.Length)])];
Check(AddonLocalizer.PatchCodeImmediates(paddedBytes, optSource, optTarget, paddedImmediates) == 2,
    "头 8 字节 + 尾 4 字节凑齐了 → 两段都换");
bool paddedHead = true;
bool paddedTail = true;
for (int k = 0; k < 8; k++)
{
    if (paddedBytes[2 + k] != optTarget[k])
    {
        paddedHead = false;
    }
}

for (int k = 0; k < 4; k++)
{
    if (paddedBytes[12 + k] != optTarget[8 + k])
    {
        paddedTail = false;
    }
}

Check(paddedHead, "头一段立即数换成目标的 [0..8)");
Check(paddedTail, $"尾巴立即数换成目标的 [8..12)（实际 '{System.Text.Encoding.UTF8.GetString(paddedBytes, 12, 8)}'）");
Check(paddedBytes[16] == 0 && paddedBytes[19] == 0, "补零（NUL 结尾）没被动");

// 孤立一个 "Mode" 常量：它是 Options Mode 的尾巴，但盖不满 → 不许动
byte[] lonelyBytes = [0x48, 0xBA, (byte)'M', (byte)'o', (byte)'d', (byte)'e', 0x00, 0x00, 0x00, 0x00];
List<(int Position, int Size)> lonelyImmediates = [.. AddonLocalizer.EnumerateImmediates(lonelyBytes, [(0, lonelyBytes.Length)])];
int lonely = AddonLocalizer.PatchCodeImmediates(lonelyBytes, optSource, optTarget, lonelyImmediates);
Check(lonely == 0, $"盖不满的尾巴匹配要拒绝（实际改了 {lonely} 处）");

// 真实指令：《Pass Count》的尾巴是 mov word ptr [rdx+0x88], "nt"
byte[] wordStore = [0x66, 0xC7, 0x82, 0x88, 0x00, 0x00, 0x00, (byte)'n', (byte)'t'];
List<(int Position, int Size)> wordImmediates = [.. AddonLocalizer.EnumerateImmediates(wordStore, [(0, wordStore.Length)])];
Check(wordImmediates.Count == 1 && wordImmediates[0].Position == 7 && wordImmediates[0].Size == 2,
    $"认得出 66 C7 82 disp32 imm16（实际 {wordImmediates.Count} 个，{(wordImmediates.Count > 0 ? $"pos={wordImmediates[0].Position} size={wordImmediates[0].Size}" : "无")}）");

// 真实指令：《Options Mode》的尾巴是 C7 81 disp32 "Mode"
byte[] dwordStore = [0xC7, 0x81, 0x88, 0x00, 0x00, 0x00, (byte)'M', (byte)'o', (byte)'d', (byte)'e'];
List<(int Position, int Size)> dwordImmediates = [.. AddonLocalizer.EnumerateImmediates(dwordStore, [(0, dwordStore.Length)])];
Check(dwordImmediates.Count == 1 && dwordImmediates[0].Position == 6 && dwordImmediates[0].Size == 4,
    $"认得出 C7 81 disp32 imm32（实际 {dwordImmediates.Count} 个）");

// 端到端：把真文件里《Pass Count》那 26 个字节原样搬过来，头 8 字节 + imm16 尾巴要一起换掉
byte[] passReal =
[
    0x48, 0xB8, (byte)'P', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'C', (byte)'o', (byte)'u',
    0x48, 0x89, 0x82, 0x80, 0x00, 0x00, 0x00,
    0x66, 0xC7, 0x82, 0x88, 0x00, 0x00, 0x00, (byte)'n', (byte)'t',
];
byte[] passCountSource = System.Text.Encoding.UTF8.GetBytes("Pass Count");
byte[] passCountTarget = System.Text.Encoding.UTF8.GetBytes("通道数");
List<(int Position, int Size)> passRealImmediates = [.. AddonLocalizer.EnumerateImmediates(passReal, [(0, passReal.Length)])];
int passChanged = AddonLocalizer.PatchCodeImmediates(passReal, passCountSource, passCountTarget, passRealImmediates);
Check(passChanged == 2, $"头一段 + 尾巴都要换（实际 {passChanged} 处，共 {passRealImmediates.Count} 个立即数）");
Check(passReal[24] == passCountTarget[8] && passReal[25] == 0x20,
    $"imm16 尾巴换成目标的 [8..10)（实际 {passReal[24]:X2} {passReal[25]:X2}）");
Check(System.Text.Encoding.UTF8.GetString(passReal, 2, 8) == "通道" + System.Text.Encoding.UTF8.GetString(passCountTarget, 6, 2),
    "头一段换掉「Pass Cou」且没串位");

// 重叠片段（真实文件里的 "Scaling " + "g Domain" = "Scaling Domain"）：盖满就写，重叠处写的是同一批字节
byte[] overlapBytes =
[
    0x48, 0xB8, (byte)'S', (byte)'c', (byte)'a', (byte)'l', (byte)'i', (byte)'n', (byte)'g', (byte)' ',
    0x48, 0xB8, (byte)'g', (byte)' ', (byte)'D', (byte)'o', (byte)'m', (byte)'a', (byte)'i', (byte)'n',
];
byte[] scalingSource = System.Text.Encoding.UTF8.GetBytes("Scaling Domain");
byte[] scalingTarget = System.Text.Encoding.UTF8.GetBytes("缩放空间");
List<(int Position, int Size)> overlapImmediates = [.. AddonLocalizer.EnumerateImmediates(overlapBytes, [(0, overlapBytes.Length)])];
int overlapChanged = AddonLocalizer.PatchCodeImmediates(overlapBytes, scalingSource, scalingTarget, overlapImmediates);
Check(overlapChanged == 2, $"两段都要换（实际 {overlapChanged} 处）");
// 运行时会拼成 [段1 8 字节][段2 从偏移 6 起 8 字节]，重叠的 2 字节写的是同一批字节
string assembled = System.Text.Encoding.UTF8.GetString(overlapBytes, 2, 6) + System.Text.Encoding.UTF8.GetString(overlapBytes, 12, 8);
Check(assembled.TrimEnd(' ') == "缩放空间",
    $"两段拼起来正好是「缩放空间」（实际 '{assembled}'）");

// 单字节 store 链不算覆盖（一个字节的匹配太容易认到别的串）：整条不写
byte[] byteStoreBytes =
[
    0x48, 0xB8, (byte)'P', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'C', (byte)'o', (byte)'u',
    0xC6, 0x45, 0x10, (byte)'n',
    0xC6, 0x45, 0x12, (byte)'t',
];
List<(int Position, int Size)> byteStoreImmediates = [.. AddonLocalizer.EnumerateImmediates(byteStoreBytes, [(0, byteStoreBytes.Length)])];
AddonLocalizer.PatchCodeImmediates(byteStoreBytes, passCountSource, passCountTarget, byteStoreImmediates);
Check(byteStoreBytes[13] == (byte)'n' && byteStoreBytes[17] == (byte)'t',
    $"单字节 store 不许乱动（实际 {byteStoreBytes[13]} {byteStoreBytes[17]}）");

// 《Use Exposure Value》实测就是「8 字节头 + 单字节 store」，盖不满 → 整条保持英文。
// 老版本硬写会得到「使用曝光值 ue」这种尾巴（用户截图里的怪字之一）。
byte[] dispBytes =
[
    0x48, 0xB8, (byte)'U', (byte)'s', (byte)'e', (byte)' ', (byte)'E', (byte)'x', (byte)'p', (byte)'o',
    0x48, 0x89, 0x86, 0x80, 0x00, 0x00, 0x00,
    0xC6, 0x86, 0x88, 0x00, 0x00, 0x00, (byte)'s',
];
byte[] useSource = System.Text.Encoding.UTF8.GetBytes("Use Exposure Value");
byte[] useTarget = System.Text.Encoding.UTF8.GetBytes("使用曝光值");
List<(int Position, int Size, int Disp, bool HasDisp)> dispImmediates = [(2, 8, 0x80, true), (23, 1, 0x88, true)];
int dispPatched = AddonLocalizer.PatchCodeImmediatesWithDisp(dispBytes, useSource, useTarget, dispImmediates);
Check(dispPatched == 0, $"单字节尾巴凑不满 → 整条不写（实际改了 {dispPatched} 处）");
Check(dispBytes[23] == (byte)'s' && System.Text.Encoding.UTF8.GetString(dispBytes, 2, 8) == "Use Expo",
    "英文原样留着（宁可留英文，也不写半个中文）");

// 回归：'ter Mask' 单独一个立即数（它是 "Character Mask" 的第 6..14 字节）盖不满 → 不许动
byte[] mixBytes = [0x48, 0xB8, (byte)'t', (byte)'e', (byte)'r', (byte)' ', (byte)'M', (byte)'a', (byte)'s', (byte)'k'];
List<(int Position, int Size)> mixImmediates = [.. AddonLocalizer.EnumerateImmediates(mixBytes, [(0, mixBytes.Length)])];
int weak = AddonLocalizer.PatchCodeImmediates(mixBytes, System.Text.Encoding.UTF8.GetBytes("Upsample Filter"), System.Text.Encoding.UTF8.GetBytes("上采样滤镜"), mixImmediates);
Check(weak == 0, $"只盖住中间一段的匹配要被拒绝（实际改了 {weak} 处）");

// 两段重叠的立即数合起来要正好是「角色遮罩」
byte[] twoChunk =
[
    0x48, 0xB8, (byte)'C', (byte)'h', (byte)'a', (byte)'r', (byte)'a', (byte)'c', (byte)'t', (byte)'e',
    0x49, 0x89, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00,
    0x48, 0xB8, (byte)'t', (byte)'e', (byte)'r', (byte)' ', (byte)'M', (byte)'a', (byte)'s', (byte)'k',
];
byte[] skinMaskSource = System.Text.Encoding.UTF8.GetBytes("Character Mask");
byte[] skinMaskTarget = System.Text.Encoding.UTF8.GetBytes("角色遮罩");
List<(int Position, int Size)> twoChunkImmediates = [.. AddonLocalizer.EnumerateImmediates(twoChunk, [(0, twoChunk.Length)])];
Check(twoChunkImmediates.Count == 2, $"两段立即数都要认得出来（实际 {twoChunkImmediates.Count} 个）");
Check(AddonLocalizer.PatchCodeImmediates(twoChunk, skinMaskSource, skinMaskTarget, twoChunkImmediates) == 2, "两段都换");
bool chunksOk = true;
for (int k = 0; k < 8; k++)
{
    if (twoChunk[2 + k] != skinMaskTarget[k])
    {
        chunksOk = false;
    }

    if (twoChunk[20 + k] != (6 + k < skinMaskTarget.Length ? skinMaskTarget[6 + k] : (byte)0x20))
    {
        chunksOk = false;
    }
}

Check(chunksOk, $"两段重叠的立即数合起来正好是「角色遮罩」（实际 '{System.Text.Encoding.UTF8.GetString(twoChunk, 2, 8)}' + '{System.Text.Encoding.UTF8.GetString(twoChunk, 20, 8)}'）");

// 位移闸（真实调用永远开着）：片段必须带 store 位移、且「位移 - 偏移」一致才敢写
byte[] dispChain =
[
    0x48, 0xB8, (byte)'P', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'C', (byte)'o', (byte)'u',
    0x48, 0x89, 0x82, 0x80, 0x00, 0x00, 0x00,
    0x66, 0xC7, 0x82, 0x88, 0x00, 0x00, 0x00, (byte)'n', (byte)'t',
];
List<(int Position, int Size, int Disp, bool HasDisp)> dispChainImmediates = [(2, 8, 0x80, true), (24, 2, 0x88, true)];
Check(AddonLocalizer.PatchCodeImmediatesWithDisp(dispChain, passCountSource, passCountTarget, dispChainImmediates) == 2,
    "位移连续（0x80/0x88 对应偏移 0/8，差 0）→ 两段都换");

byte[] dispBroken =
[
    0x48, 0xB8, (byte)'O', (byte)'p', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'s', (byte)' ',
    0x48, 0xBA, (byte)'M', (byte)'o', (byte)'d', (byte)'e', 0x00, 0x00, 0x00, 0x00,
];
// 实测真文件里《Options Mode》的尾巴位移是 -129（另一个缓冲/局部量）：写下去就是「中文头 + 英文尾」
List<(int Position, int Size, int Disp, bool HasDisp)> dispBrokenImmediates = [(2, 8, 0x80, true), (12, 8, 0x80 + 8 - 129, true)];
Check(AddonLocalizer.PatchCodeImmediatesWithDisp(dispBroken, optSource, optTarget, dispBrokenImmediates) == 0,
    "位移对不上（其实不是同一个缓冲）→ 整条不写");
Check(dispBroken[9] == (byte)' ' && dispBroken[12] == (byte)'M' && dispBroken[15] == (byte)'e', "英文原样留着，一个字节没动");

// 片段超出串尾的那几个字节必须是补零：不是补零就说明它其实是别的串/别的常量（实测有 "Of" + "ff..." 的假链）
byte[] nonNulTail =
[
    0x48, 0xB8, (byte)'O', (byte)'f', (byte)'f', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?',
];
byte[] offSource = System.Text.Encoding.UTF8.GetBytes("Off");
byte[] offTarget = System.Text.Encoding.UTF8.GetBytes("关");
List<(int Position, int Size)> nonNulImmediates = [.. AddonLocalizer.EnumerateImmediates(nonNulTail, [(0, nonNulTail.Length)])];
Check(AddonLocalizer.PatchCodeImmediates(nonNulTail, offSource, offTarget, nonNulImmediates) == 0,
    "片段后面不是补零（其实是别的常量）→ 不许写");

byte[] nulTail =
[
    0x48, 0xB8, (byte)'O', (byte)'f', (byte)'f', 0x00, 0x00, 0x00, 0x00, 0x00,
];
List<(int Position, int Size)> nulImmediates = [.. AddonLocalizer.EnumerateImmediates(nulTail, [(0, nulTail.Length)])];
Check(AddonLocalizer.PatchCodeImmediates(nulTail, offSource, offTarget, nulImmediates) == 1, "补零结尾的整条立即数能换");
Check(nulTail[2] == offTarget[0] && nulTail[4] == offTarget[2] && nulTail[5] == 0x00, "换成「关」且补零没被动（NUL 仍是 NUL）");


// ★ v3：运行时**覆盖写尾巴**。有些标签的头是 .rdata 字面量（编译器用 16 字节 SSE 拷贝搬过去），
// 尾巴却是紧接着一条短立即数——实测 Overall Intensity 是 mov word [rbx+0x10], "y\0"，
// Structure Intensity 是 mov dword [rbx+0xF], "sity"。运行时它们会再写一遍尾巴、把我们补的空格
// 盖回英文，界面就是「总体强度 y」「结构强度 sity」。所以头汉化了，尾巴也必须按同一套偏移写掉。
byte[] tailBytes2 = new byte[48];
byte[] overallSource = System.Text.Encoding.UTF8.GetBytes("Overall Intensity");
byte[] overallTarget = System.Text.Encoding.UTF8.GetBytes("总体强度");
Array.Copy(overallSource, 0, tailBytes2, 0, overallSource.Length);
byte[] tailStore = [0x66, 0xC7, 0x82, 0x10, 0x00, 0x00, 0x00, (byte)'y', 0x00];
Array.Copy(tailStore, 0, tailBytes2, 38, tailStore.Length);
int tailWrote = AddonLocalizer.PatchCodeImmediatesWithDisp(tailBytes2, overallSource, overallTarget, [(45, 2, 0x10, true)]);
Check(tailWrote == 1, $"尾巴覆盖写要写掉那个立即数（实际 {tailWrote} 处）");
Check(System.Text.Encoding.UTF8.GetString(tailBytes2, 0, 17) == "Overall Intensity",
    "字面量那头归 Apply 处理（立即数这一趟不碰它）");
Check(tailBytes2[45] == 0x20 && tailBytes2[46] == 0x00, $"尾巴那个 y 补成空格、NUL 不动（实际 {tailBytes2[45]:X2} {tailBytes2[46]:X2}）");

// 1 字节 + NUL 的尾巴也算「盖满」：单独 mov byte [x+8], '1' 不行，mov word [x+8], "1\0" 行
byte[] presetChain =
[
    0x48, 0xB8, (byte)'P', (byte)'r', (byte)'e', (byte)'s', (byte)'e', (byte)'t', (byte)' ', (byte)'#',
    0x66, 0xC7, 0x82, 0x08, 0x00, 0x00, 0x00, (byte)'1', 0x00,
];
byte[] presetSource = System.Text.Encoding.UTF8.GetBytes("Preset #1");
byte[] presetTarget = System.Text.Encoding.UTF8.GetBytes("预设 #1");
List<(int Position, int Size)> presetImmediates = [.. AddonLocalizer.EnumerateImmediates(presetChain, [(0, presetChain.Length)])];
Check(AddonLocalizer.PatchCodeImmediates(presetChain, presetSource, presetTarget, presetImmediates) == 2,
    "头 8 字节 + 1 字节尾巴（带 NUL）也凑满 → 两段都换");
Check(presetChain[17] == presetTarget[8] && presetChain[18] == 0x00, "1 字节尾巴换成目标的最后一字节，NUL 没被动");

// ★ 基线分组：同一个短常量在别的函数里还有副本、基线各不相同（实测 "Mode" 有 6 份）。
//   老写法抓到窗口 8 那两份里地址靠前的那份（基线 0 != 128）就判「位移不连续 → 整条不写」，
//   Options Mode 在界面上一直是英文就栽在这儿；现在按「盖住第 0 字节的片段」的基线逐个试。
byte[] optChainBuf = new byte[64];
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Options "), 0, optChainBuf, 0, 8);
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Mode"), 0, optChainBuf, 16, 4);   // 诱饵：基线 0
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Mode"), 0, optChainBuf, 32, 4);   // 同一条缓冲：基线 128
byte[] optTargetBytes = System.Text.Encoding.UTF8.GetBytes("选项模式");
int optChainHits = AddonLocalizer.PatchCodeImmediatesForTest(
    optChainBuf,
    [new AddonI18nEntry { En = "Options Mode", Zh = "选项模式" }],
    [(16, 4, 8, true), (0, 8, 128, true), (32, 4, 136, true)]);
Check(optChainHits >= 2, $"基线不同的一堆副本里还是能挑出正确那条链（实际 {optChainHits} 段）");
Check(optChainBuf.Take(8).SequenceEqual(optTargetBytes.Take(8)), "链头换成目标的 [0..8)");
Check(optChainBuf.Skip(32).Take(4).SequenceEqual(optTargetBytes.Skip(8).Take(4)), "真尾巴（基线 128）换成目标的 [8..12)");

// ★ 共享常量：「Mode」同时是 Model A/B/C 的头，写成中文界面就是「????A」「?式l」。
//   guard 条目（只当判据、不翻译）也要参与这个判断。
byte[] guardBuf = new byte[64];
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Options "), 0, guardBuf, 0, 8);
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Mode"), 0, guardBuf, 16, 4);   // Options Mode 的真尾巴
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Mode"), 0, guardBuf, 32, 4);   // Model A 的头（基线 2096）
AddonLocalizer.PatchCodeImmediatesForTest(
    guardBuf,
    [
        new AddonI18nEntry { En = "Options Mode", Zh = "选项模式" },
        new AddonI18nEntry { En = "Model A", Zh = "模型A", Guard = true },
    ],
    [(0, 8, 128, true), (16, 4, 136, true), (32, 4, 2104, true)]);
Check(System.Text.Encoding.UTF8.GetString(guardBuf, 32, 4) == "Mode",
    "共享常量：Model A 的头不能被当成 Options Mode 的尾巴写掉");

// ★ 尾巴片段的归属："sity" 有两份 —— disp 15 那份是 Structure Intensity 的、disp 16 那份是
//   Local Tone Intensity 的（v3 那版「全写」让先写的 Local Tone 把两份都占成自己的字节，界面上坏的
//   正是 Structure 那行）。现在先比「是不是别的条目也能当尾巴用」，都被共用才比 |disp - 窗口|，
//   而且一条串只写自己那一份。
byte[] ownBuf = new byte[160];
Array.Copy(System.Text.Encoding.UTF8.GetBytes("sity"), 0, ownBuf, 8, 4);    // disp 15 → Structure 的
Array.Copy(System.Text.Encoding.UTF8.GetBytes("sity"), 0, ownBuf, 24, 4);   // disp 16 → Local Tone 的
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Structure Intensity"), 0, ownBuf, 40, 19);
Array.Copy(System.Text.Encoding.UTF8.GetBytes("Local Tone Intensity"), 0, ownBuf, 70, 20);
AddonLocalizer.PatchCodeImmediatesForTest(
    ownBuf,
    [
        new AddonI18nEntry { En = "Structure Intensity", Zh = "结构强度" },
        new AddonI18nEntry { En = "Local Tone Intensity", Zh = "局部色调强度" },
    ],
    [(8, 4, 15, true), (24, 4, 16, true)]);
Check(ownBuf[8] == 0x20 && ownBuf[9] == 0x20 && ownBuf[10] == 0x20 && ownBuf[11] == 0x20,
    $"Structure 那份（disp 15）按自己的映射补空格（实际 {ownBuf[8]:X2} {ownBuf[9]:X2} {ownBuf[10]:X2} {ownBuf[11]:X2}）");
byte[] localToneTarget = System.Text.Encoding.UTF8.GetBytes("局部色调强度");
Check(ownBuf[24] == localToneTarget[16] && ownBuf[25] == localToneTarget[17] && ownBuf[26] == 0x20 && ownBuf[27] == 0x20,
    $"Local Tone 那份（disp 16）写自己的「度」，没被 Structure 那份盖掉（实际 {ownBuf[24]:X2} {ownBuf[25]:X2} {ownBuf[26]:X2} {ownBuf[27]:X2}）");
Console.WriteLine("-- 下载器的纯逻辑（不联网）--");
string[] assets = ["OptiScaler-NR-v0.8.3.zip", "OptiScaler-NR-v0.8.3-rtx40-mfg.zip", "OptiScaler-NR-v0.8.3-SHA256SUMS.txt", "NeuRotic-Patch.zip"];
Check(OptiScalerDownloader.IsUsableAsset(assets[0]) && !OptiScalerDownloader.IsUsableAsset(assets[2]),
    "只认 .zip / .exe，sha256 清单不算可安装包");
Check(OptiScalerDownloader.PickAsset(assets) == "OptiScaler-NR-v0.8.3.zip", "自动挑的时候优先非补丁、名字最短的");
Check(OptiScalerDownloader.LooksLikePatch("NeuRotic-Patch.zip"), "patch 包能被认出来");

// 只有安装程序的来源（DLSS NR on AMD）：认得出 exe，且优先挑 zip
string[] installerOnly = ["dlssnr_on_amd_setup.exe"];
string[] bothKinds = ["dlssnr_on_amd_setup.exe", "OptiScaler-NR-v0.8.6.zip"];
Check(OptiScalerDownloader.IsUsableAsset(installerOnly[0]), "安装程序（.exe）也算可安装包");
Check(OptiScalerDownloader.IsInstaller(installerOnly[0]) && !OptiScalerDownloader.IsInstaller(bothKinds[1]),
    "IsInstaller 只对 .exe 为真");
Check(OptiScalerDownloader.PickAsset(installerOnly) == "dlssnr_on_amd_setup.exe", "只有 exe 时挑它");
Check(OptiScalerDownloader.PickAsset(bothKinds) == "OptiScaler-NR-v0.8.6.zip", "有 zip 就不要顺手去跑人家的安装程序");

// 目录来源（catalog/optiscaler.json）：wilsjo2 / neurotic / mfg-ada（本 fork）等
List<OptiScalerSource> optiCatalog = repoCatalogDir is null
    ? []
    : OptiScalerCatalog.Normalize(OptiScalerCatalog.LoadFile(Path.Combine(repoCatalogDir, "optiscaler.json")));
Check(optiCatalog.Count >= 4, $"目录里有 {optiCatalog.Count} 个 OptiScaler 来源");
Check(optiCatalog.All(s => s.Id != "dlssnr-amd"), "DLSS NR on AMD 不再挂在 OptiScaler 来源里");
Check(optiCatalog.All(s => s.Id != "multipass-mfg"), "404 的那个来源删掉了");
Check(optiCatalog.Select(s => s.Id).Distinct().Count() == optiCatalog.Count, "来源 id 不重复（要当目录名用）");
Check(optiCatalog.All(s => s.Tags is { Length: > 0 }), "每个来源都带 tags（卡片上要显示）");
Check(optiCatalog.All(s => s.Repository.Contains('/')), "每个来源都是 owner/repo");
Check(OptiScalerLibrary.Sanitize("a/b:c") == "a_b_c", "版本号里的非法字符会被换掉");
Check(OptiScalerCatalog.Find(optiCatalog, "neurotic")?.Repository == "MagicalPrincessUnicorn/NeuRotic-an-OptiScaler-DLSSNR-fork", "按 id 找得到来源");


Console.WriteLine();
Console.WriteLine();
Console.WriteLine("== 27. DLSS5 Feed：预设里的效果开关（含根键 ini）==");

string presetDir = Path.Combine(root, "Presets");
Directory.CreateDirectory(presetDir);

string presetPath = Path.Combine(presetDir, "Mod OFF.ini");
File.WriteAllLines(presetPath, [
    "Techniques=MartysMods_MXAO@MartysMods_MXAO.fx",
    "TechniqueSorting=MartysMods_MXAO@MartysMods_MXAO.fx,DPX@DPX.fx",
    @"PreprocessorDefinitions=RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0,fLUT_TextureName=""DarkNRich.png""",
    "",
    "[MartysMods_MXAO.fx]",
    "MXAO_TWO_LAYER=1",
]);

string feedIni = Path.Combine(root, "feedgame", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(feedIni)!);
File.WriteAllLines(feedIni, [
    "[ADDON]",
    "DisabledAddons=",
    "",
    "[GENERAL]",
    "PresetPath=" + presetPath,
]);

ReShadeProfile feedProfile = ReShadeProfile.Load(feedIni);
Check(feedProfile.ResolvePresetPath() == presetPath, "PresetPath 解析出来了");

Check(ReShadePresetEditor.IsFeedAddon("dlss5-feed.addon64"), "dlss5-feed.addon64 认成 Feed 插件");
Check(!ReShadePresetEditor.IsFeedAddon("renodx-dlss5.addon64"), "别的插件不会误判");

string TechniquesLine() => File.ReadAllLines(presetPath).First(l => l.StartsWith("Techniques="));
string DefinitionsLine() => File.ReadAllLines(presetPath).FirstOrDefault(l => l.StartsWith("PreprocessorDefinitions=")) ?? string.Empty;

Check(ReShadePresetEditor.TrySetEnabled(feedProfile, true, out string? feedError), $"打开 Feed 的预设开关（{feedError}）");
Check(TechniquesLine() == "Techniques=MartysMods_MXAO@MartysMods_MXAO.fx,Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx",
    "Kernel 排在 Feed 前面、原有的 technique 保留：" + TechniquesLine());
Check(DefinitionsLine().Contains("DLSS5_MV_PROVIDER=3"), "写进了 DLSS5_MV_PROVIDER=3");
Check(DefinitionsLine().Contains("DarkNRich.png"), "别的预处理器定义原样保留");
Check(File.ReadAllText(presetPath).Contains("[MartysMods_MXAO.fx]"), "节还在");
Check(File.ReadAllText(presetPath).Contains("MXAO_TWO_LAYER=1"), "节里的参数没被动");
Check(ReShadePresetEditor.IsEnabled(feedProfile), "读回来是开着的");

Check(ReShadePresetEditor.TrySetEnabled(feedProfile, true, out _), "再开一次不报错");
Check(TechniquesLine() == "Techniques=MartysMods_MXAO@MartysMods_MXAO.fx,Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx",
    "幂等：不会重复加 technique");

Check(ReShadePresetEditor.TrySetEnabled(feedProfile, false, out _), "关掉 Feed 的预设开关");
Check(TechniquesLine() == "Techniques=MartysMods_MXAO@MartysMods_MXAO.fx", "只剩原来的 technique：" + TechniquesLine());
Check(!DefinitionsLine().Contains("DLSS5_MV_PROVIDER"), "预处理器定义也摘掉了");
Check(File.ReadAllText(presetPath).Contains("TechniqueSorting=MartysMods_MXAO@MartysMods_MXAO.fx,DPX@DPX.fx,Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx"),
    "TechniqueSorting 是全集清单，故意留着");
Check(!ReShadePresetEditor.IsEnabled(feedProfile), "读回来是关着的");

// 根键本来不存在时：插在第一个节头之前
string barePath = Path.Combine(presetDir, "bare.ini");
File.WriteAllLines(barePath, ["[Some.fx]", "A=1"]);
string bareIni = Path.Combine(root, "baregame", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(bareIni)!);
File.WriteAllLines(bareIni, ["[GENERAL]", "PresetPath=" + barePath]);
Check(ReShadePresetEditor.TrySetEnabled(ReShadeProfile.Load(bareIni), true, out _), "空预设也能开");
string[] bareLines = File.ReadAllLines(barePath);
Check(bareLines.Length == 5
      && bareLines[0].StartsWith("Techniques=")
      && bareLines[1].StartsWith("TechniqueSorting=")
      && bareLines[2] == "PreprocessorDefinitions=DLSS5_MV_PROVIDER=3"
      && bareLines[3] == "[Some.fx]",
    "根键插在第一个节头之前：" + string.Join(" | ", bareLines));

// 目录条目（仓库根 catalog/plugins.json，见上面 section 10）
ExtensionManifest? feedManifest = builtin.Extensions.FirstOrDefault(e => e.Id == "dlss5.feed" && !e.Removed);
Check(feedManifest is not null, "内置目录有 dlss5.feed");
Check(feedManifest!.Requires is ["lumenitefx"], "dlss5.feed 声明依赖 lumenitefx");
Check(feedManifest.Rules.Any(r => r.To.Contains("Addons", StringComparison.OrdinalIgnoreCase))
      && feedManifest.Rules.Any(r => r.To.Contains("Shaders", StringComparison.OrdinalIgnoreCase)),
    "既装 addon 也装 DLSS5_Feed.fx");
Check(feedManifest.Tags?.Contains("dlss5") == true, "带 dlss5 标签（自动进 LoadFromDllMain）");

ExtensionManifest? lumManifest = builtin.Extensions.FirstOrDefault(e => e.Id == "lumenitefx" && !e.Removed);
Check(lumManifest is not null, "目录里有 lumenitefx");
Check(lumManifest!.Rules.All(r => !r.To.Contains("Addons", StringComparison.OrdinalIgnoreCase)),
    "lumenitefx 不是插件（不进「全局插件」列表）");

AddonFileInfo? feedAddon = AddonFileInfo.Parse("dlss5-feed.addon64");
Check(feedAddon is not null
      && ExtensionAddonMatcher.Match([feedManifest], [feedAddon!]).ContainsKey("dlss5.feed"),
    "dlss5-feed.addon64 能被目录认领");

// 兼容性检测第 12 项报「没写 AddonPath」之后点的就是对齐修复 —— 必须真的补上
Directory.CreateDirectory(Path.Combine(shadeRoot, "reshade-shaders", "Addons"));
string missIni = Path.Combine(root, "missaddon", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(missIni)!);
File.WriteAllLines(missIni, [
    "[ADDON]",
    "DisabledAddons=",
    "",
    "[GENERAL]",
    @"EffectSearchPaths=" + shadeRoot + @"\reshade-shaders\Shaders\**",
]);
ShadePathAlignResult addonFix = ShadePathAligner.Align(missIni, shadeRoot);
Check(addonFix.ChangedKeys.Contains("[ADDON] AddonPath"), "缺 AddonPath 会被补上：" + string.Join("、", addonFix.ChangedKeys));
string? fixedAddonPath = ReShadeProfile.Load(missIni).AddonPath;
Check(fixedAddonPath is not null && Path.IsPathFullyQualified(fixedAddonPath) && Directory.Exists(fixedAddonPath),
    "补的是**绝对**路径（第 12 项会拿它 Directory.Exists）：" + fixedAddonPath);
Check(!ShadePathAligner.Align(missIni, shadeRoot).Changed, "补完再对齐是幂等的");

// 指到别的 HoYoShade 的搜索路径要能被反推出来（第 12 项靠它报「指到别的 HoYoShade」）
string otherRoot = Path.Combine(root, "other", "HoYoShade");
Check(ShadePathAligner.RootOf(otherRoot + @"\reshade-shaders\Shaders\**") == otherRoot,
    "别的 HoYoShade 的搜索路径能反推出根");
try { Directory.Delete(Path.Combine(root, "missaddon"), true); } catch { }
foreach (string entryDir in new[] { Path.Combine(root, "feedgame"), Path.Combine(root, "baregame") })
{
    try { Directory.Delete(entryDir, true); } catch { }
}

Console.WriteLine("== 28. 版本归档 + 每游戏插件包 + 迁移规划（需求 1/2/3/4/G） ==");

string cacheRoot = Path.Combine(root, "cache");
string gameKey = "hk4e_cn";
Check(GameAddonPack.SanitizeGameKey("hk4e_cn") == "hk4e_cn", "游戏 key 正常");
Check(GameAddonPack.SanitizeGameKey("a/b:c*d") == "a_b_c_d", "非法字符替换成 _：" + GameAddonPack.SanitizeGameKey("a/b:c*d"));
Check(GameAddonPack.SanitizeGameKey("   ") == "unknown", "空 key 退到 unknown");
Check(GameAddonPack.AddonDirectory(cacheRoot, gameKey) == Path.Combine(cacheRoot, "games", gameKey, "Addons"),
    "每游戏插件包目录映射");

var versionStore = new AddonVersionStore(cacheRoot);
Check(versionStore.RootPath == Path.Combine(cacheRoot, "plugins"), "归档根 = cache\\plugins");
Check(versionStore.DirectoryFor("dlss5.neural.interposer", "1.1.5")
      == Path.Combine(cacheRoot, "plugins", "dlss5.neural.interposer", "1.1.5"), "归档目录映射");

// 造一个假的宿主 + 已装记录
string archiveHostRoot = Path.Combine(root, "archive-host");
Directory.CreateDirectory(Path.Combine(archiveHostRoot, "reshade-shaders", "Addons"));
Directory.CreateDirectory(Path.Combine(archiveHostRoot, "reshade-shaders", "Shaders"));
File.WriteAllText(Path.Combine(archiveHostRoot, "ReShade64.dll"), "fake");
var archiveHost = new ShadeHost(archiveHostRoot);

string archivedAddon = Path.Combine(archiveHost.AddonsPath, "renodx-dlss.addon64");
File.WriteAllText(archivedAddon, "v1-addon");
File.WriteAllText(Path.Combine(archiveHostRoot, "reshade-shaders", "Shaders", "foo.fx"), "v1-shader");

var archiveRecord = new InstalledExtension
{
    Id = "renodx.dlss",
    Name = "RenoDX DLSS",
    Version = "1.0",
    ResolvedTag = "v1.0",
    Files =
    {
        new InstalledExtension.InstalledExtensionFile { Path = "reshade-shaders/Addons/renodx-dlss.addon64" },
        new InstalledExtension.InstalledExtensionFile { Path = "reshade-shaders/Shaders/foo.fx" },
    },
};

AddonArchiveResult archive1 = versionStore.Archive(archiveHost, archiveRecord);
Check(archive1.Archived == 2 && versionStore.Has("renodx.dlss", "v1.0"), "归档当前安装的文件：" + archive1.Archived);
Check(versionStore.AddonFiles("renodx.dlss", "v1.0").Count == 1, "归档里只挑 Addons 下的文件");
Check(versionStore.Archive(archiveHost, archiveRecord).Archived == 0, "归档幂等（第二次全跳过）");

// 换一份内容（模拟安装器用 tmp+Move 替换，生成新的 inode）
File.Delete(archivedAddon);
File.WriteAllText(archivedAddon, "v2-addon");
archiveRecord.ResolvedTag = "v1.1";
versionStore.Archive(archiveHost, archiveRecord);
Check(versionStore.InstalledTags("renodx.dlss").Count == 2, "同一个插件两个版本共存");

string archivedV1 = Path.Combine(versionStore.DirectoryFor("renodx.dlss", "v1.0"), "reshade-shaders", "Addons", "renodx-dlss.addon64");
Check(File.ReadAllText(archivedV1) == "v1-addon", "老版本的归档没被新版本覆盖（硬链接 + inode 替换）");

// 规划插件包：共享目录里的全部文件 + 选版本的那个来自归档
File.WriteAllText(Path.Combine(archiveHost.AddonsPath, "other.addon64"), "other");
var selections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["renodx.dlss"] = "v1.0" };
List<AddonPackEntry> plan = GameAddonPack.Plan(archiveHost.AddonsPath, selections, (id, tag) => versionStore.AddonFiles(id, tag));
Check(plan.Count == 2, "包计划包含共享目录的全部文件：" + plan.Count);
AddonPackEntry pickedEntry = plan.First(e => e.FileName == "renodx-dlss.addon64");
Check(pickedEntry.ExtensionId == "renodx.dlss"
      && pickedEntry.SourcePath.Contains(Path.Combine("plugins", "renodx.dlss", "v1.0"), StringComparison.OrdinalIgnoreCase),
    "选过版本的文件来自归档：" + pickedEntry.SourcePath);
Check(!GameAddonPack.Plan(archiveHost.AddonsPath, new Dictionary<string, string>(), (id, tag) => versionStore.AddonFiles(id, tag))
        .Any(e => e.ExtensionId is not null),
    "没选版本时全部用共享目录的文件（保持今天的行为）");

string packAddons = GameAddonPack.AddonDirectory(cacheRoot, gameKey);
GameAddonPack.Sync(packAddons, plan, selections.Select(kv => (kv.Key, kv.Value)));
Check(File.Exists(Path.Combine(packAddons, "renodx-dlss.addon64")) && File.Exists(Path.Combine(packAddons, "other.addon64")),
    "包目录里有全部文件");
Check(File.ReadAllText(Path.Combine(packAddons, "renodx-dlss.addon64")) == "v1-addon", "包里用的是归档那一版");
Check(File.Exists(Path.Combine(packAddons, GameAddonPack.MarkerFileName)), "包目录带 pack.json 标记");
Check(GameAddonPack.IsPackDirectory(packAddons), "IsPackDirectory 认标记");
Check(!GameAddonPack.IsPackDirectory(archiveHost.AddonsPath), "普通共享目录不算插件包");

// 不再需要的旧行会被清掉
File.Delete(Path.Combine(archiveHost.AddonsPath, "other.addon64"));
List<AddonPackEntry> plan2 = GameAddonPack.Plan(archiveHost.AddonsPath, selections, (id, tag) => versionStore.AddonFiles(id, tag));
GameAddonPack.Sync(packAddons, plan2, selections.Select(kv => (kv.Key, kv.Value)));
Check(!File.Exists(Path.Combine(packAddons, "other.addon64")), "不再需要的旧行被清掉");

// ShadePathAligner 不能把每游戏插件包改回 HoYoShade
string packIni = Path.Combine(root, "packgame", "ReShade.ini");
Directory.CreateDirectory(Path.GetDirectoryName(packIni)!);
File.WriteAllLines(packIni, [
    "[ADDON]",
    "AddonPath=" + packAddons,
    "DisabledAddons=",
]);
ShadePathAlignResult packAlign = ShadePathAligner.Align(packIni, shadeRoot);
Check(!packAlign.ChangedKeys.Contains("[ADDON] AddonPath"), "Align 跳过每游戏插件包：" + string.Join("、", packAlign.ChangedKeys));
Check(ReShadeProfile.Load(packIni).AddonPath == packAddons, "包路径原样保留");
Check(!ShadePathAligner.Align(packIni, shadeRoot).Changed, "认包路径的那个 ini 对齐是幂等的");

// 删除单个版本
Check(versionStore.DeleteVersion("renodx.dlss", "v1.1"), "删掉一个归档版本");
Check(versionStore.InstalledTags("renodx.dlss").Count == 1, "只剩一个版本");
Check(!File.Exists(versionStore.DirectoryFor("renodx.dlss", "v1.1")), "被删版本的目录真的没了");

// 迁移规划
Check(CacheMigrationPlanner.NeedsMigration(false, true, false, false, false, false, false), "装过插件但没归档 -> 需要引导");
Check(!CacheMigrationPlanner.NeedsMigration(true, true, false, true, false, true, false), "已迁移过 -> 不引导");
Check(!CacheMigrationPlanner.NeedsMigration(false, false, false, false, false, false, false), "全新安装 -> 不引导");
// OptiScaler 不参与迁移：它自己的库原地多版本共存，老 OptiScaler 还在也不该弹引导
Check(!CacheMigrationPlanner.NeedsMigration(false, false, false, true, false, false, false), "老 OptiScaler 还在也不引导（不迁移）");

string legacyOpti = Path.Combine(root, "legacy-opti");
string cacheOpti = Path.Combine(cacheRoot, "optiscaler");
Directory.CreateDirectory(Path.Combine(legacyOpti, "src", "v1"));
Check(CacheMigrationPlanner.PlanMoves(legacyOpti, null, cacheOpti, null).Count == 0, "OptiScaler 不规划搬迁");

// 模块要迁移
string legacyModules = Path.Combine(root, "legacy-modules");
string cacheModules = Path.Combine(cacheRoot, "modules");
Directory.CreateDirectory(Path.Combine(legacyModules, "veritas"));
List<LegacyStoreMove> plannedMoves = CacheMigrationPlanner.PlanMoves(null, legacyModules, null, cacheModules);
Check(plannedMoves.Count == 1 && plannedMoves[0].Source == legacyModules && plannedMoves[0].Target == cacheModules && !plannedMoves[0].TargetExists,
    "规划出模块搬迁");
Directory.CreateDirectory(cacheModules);
Check(CacheMigrationPlanner.PlanMoves(null, legacyModules, null, cacheModules)[0].TargetExists, "目标已存在要标记，不能覆盖");
Check(CacheMigrationPlanner.PlanMoves(null, Path.Combine(root, "nope"), null, cacheModules).Count == 0, "源不存在就不规划");

Console.WriteLine("== 29. addon→扩展归属 + 插件包体检 + dll 版本归档 ==");

// addon 文件名 → 扩展 id（每游戏插件卡片的版本下拉靠它把文件对到归档）
Check(ExtensionAddonMatcher.MatchExtensionId([feedManifest], "dlss5-feed.addon64") == "dlss5.feed",
    "addon 文件名能对到扩展 id");
Check(ExtensionAddonMatcher.MatchExtensionId([feedManifest], "definitely-not-an-addon.txt") is null,
    "认不出来的文件名返回 null");

// pack.json 里的版本选择要能读回来（兼容性检测第 20 项靠它报「这个游戏在用哪一版」）
AddonPackEntry[] planFinal = [.. plan2];
GameAddonPack.Sync(packAddons, planFinal, selections.Select(kv => (kv.Key, kv.Value)));
Dictionary<string, string> packSelections = GameAddonPack.ReadSelections(packAddons);
Check(packSelections.Count == 1 && packSelections.TryGetValue("renodx.dlss", out string? pickedTag) && pickedTag == "v1.0",
    "pack.json 的版本选择能读回来：" + string.Join(",", packSelections.Select(kv => kv.Key + "=" + kv.Value)));
Check(GameAddonPack.ReadSelections(archiveHost.AddonsPath).Count == 0, "不是包目录就没有选择记录");

// 体检：同步状态下应该全对得上
AddonPackAudit auditOk = GameAddonPackAudit.Inspect(cacheRoot, gameKey, archiveHost.AddonsPath);
Check(auditOk.IsPack, "体检认得这是一个插件包");
Check(auditOk.Ok,
    "同步状态下体检通过（缺文件 " + string.Join(",", auditOk.MissingFiles)
    + " / 过期 " + string.Join(",", auditOk.StaleFiles)
    + " / 多余 " + string.Join(",", auditOk.ExtraFiles) + "）");
Check(!GameAddonPackAudit.Inspect(cacheRoot, "no-such-game", archiveHost.AddonsPath).IsPack, "没包目录 → IsPack=false");

// 共享目录新增一个文件 → 包里缺它
File.WriteAllText(Path.Combine(archiveHost.AddonsPath, "late.addon64"), "late");
Check(GameAddonPackAudit.Inspect(cacheRoot, gameKey, archiveHost.AddonsPath).MissingFiles.Contains("late.addon64"),
    "共享目录新增文件 → 包里缺文件被查出来");
File.Delete(Path.Combine(archiveHost.AddonsPath, "late.addon64"));

// 包里的文件被换成别的内容（硬链接断了）→ 报「过期」
// 硬链接是同一个 inode —— 「链接不在了」模拟成「删掉包里这份、重新写一个新文件」，
// 源（归档）那份保持不动，大小就不一样了
string packLinkedFile = Path.Combine(packAddons, "renodx-dlss.addon64");
File.Delete(packLinkedFile);
File.WriteAllText(packLinkedFile, "a brand new file that is not linked to the archive");
Check(GameAddonPackAudit.Inspect(cacheRoot, gameKey, archiveHost.AddonsPath).StaleFiles.Contains("renodx-dlss.addon64"),
    "包内容和源对不上（硬链接断了）→ 报过期");

// 归档版本被删掉 → 报「选中的版本没了」
versionStore.DeleteVersion("renodx.dlss", "v1.0");
Check(GameAddonPackAudit.Inspect(cacheRoot, gameKey, archiveHost.AddonsPath).MissingArchiveVersions.Contains("renodx.dlss@v1.0"),
    "归档版本被删 → 体检报缺失");

// dll 运行时归档：<CacheRoot>\dlls\<family>\<version>\
string dllCache = Path.Combine(root, "dllcache");
var dllStore = new DllVersionStore(dllCache);
Check(dllStore.RootPath == Path.Combine(dllCache, "dlls"), "dll 归档根 = cache\\dlls");
Check(dllStore.DirectoryFor("dlssnr", "310.8.0") == Path.Combine(dllCache, "dlls", "dlssnr", "310.8.0"),
    "dll 归档目录映射");

string dllStaging = Path.Combine(root, "dllstage");
Directory.CreateDirectory(dllStaging);
string nrdllPath = Path.Combine(dllStaging, "nvngx_dlssnr.dll");
File.WriteAllText(nrdllPath, "nr-310.8");
Check(dllStore.Archive("dlssnr", "310.8.0", [nrdllPath]) == 1, "归档一个 dll 版本");
Check(dllStore.Has("dlssnr", "310.8.0")
      && File.Exists(Path.Combine(dllStore.DirectoryFor("dlssnr", "310.8.0"), "nvngx_dlssnr.dll")),
    "归档里有那个 dll");
Check(dllStore.Archive("dlssnr", "310.8.0", [nrdllPath]) == 0, "dll 归档幂等（第二次跳过）");
Check(dllStore.Archive("dlssnr", "310.8.0", [Path.Combine(dllStaging, "missing.dll")]) == 0,
    "源不存在不报错也不计数（安装不能被归档拖累）");
Check(dllStore.Archive("dlssnr", "  ", [nrdllPath]) == 0, "版本为空不归档");
Check(dllStore.InstalledVersions("dlssnr").Count == 1, "归档版本列表：1 个");
Check(dllStore.DeleteVersion("dlssnr", "310.8.0") && !dllStore.Has("dlssnr", "310.8.0"), "删掉 dll 归档版本");

// 「盘上这份是哪个变体」：Lecram 的 PE 是 310.8.3.0，数字段对不上清单里的 310.8.0，
// 只靠版本号会认成 310.8.0（用户反馈：下的明明是 Lecram，识别成 50 系）
{
    DllFamily nrFamily = DllComponentCatalog.FamilyOf("dlssnr")!;
    var lecramOnDisk = new InstalledDll("nvngx_dlssnr.dll", 123456, "310.8.3.0");

    Check(DllVariantResolver.Identify(null, nrFamily, lecramOnDisk, "310.8.Lecram", 123456).Variant == "310.8.Lecram",
        "记账 + 字节数一致 → 认得出是 Lecram（不会因为 PE 是 310.8.3.0 回退成 310.8.0）");
    Check(!DllVariantResolver.Identify(null, nrFamily, lecramOnDisk, "310.8.Lecram", 999).Known,
        "字节数变了、数字段又对不上 → 老实说认不出来");
    var officialOnDisk = new InstalledDll("nvngx_dlssnr.dll", 55555, "310.8.0.0");
    Check(DllVariantResolver.Identify(null, nrFamily, officialOnDisk, "310.8.0", 999).Variant == "310.8.0",
        "字节数变了但数字段对得上 → 继续信记账");
    Check(!DllVariantResolver.Identify(null, nrFamily, lecramOnDisk, "310.8.0", 999).Known,
        "记账说 310.8.0、盘上 PE 却是 310.8.3.0（对不上）→ 不信记账，老实说认不出来");
    Check(!DllVariantResolver.Identify(null, nrFamily, lecramOnDisk, null, 0).Known,
        "没记账 → 认不出来（不再瞎猜成 310.8.0）");
    Check(DllVariantResolver.Identify(null, nrFamily, null, "310.8.0", 1).Variant is null,
        "没装 → 没有变体");

    // 没记账、但归档里有同名同大小的 → 反查得出（现有 Lecram 用户走这条就能修好）
    string probeCache = Path.Combine(root, "dllcache-probe");
    var probeStore = new DllVersionStore(probeCache);
    string probeStage = Path.Combine(root, "dllstage-probe");
    Directory.CreateDirectory(probeStage);
    string lecramPath = Path.Combine(probeStage, "nvngx_dlssnr.dll");
    File.WriteAllText(lecramPath, new string('x', 777));
    Check(probeStore.Archive("dlssnr", "310.8.Lecram", [lecramPath]) == 1, "探针：归档 Lecram");
    var onDisk = new InstalledDll("nvngx_dlssnr.dll", 777, "310.8.3.0");
    Check(DllVariantResolver.Identify(probeStore, nrFamily, onDisk, null, 0).Variant == "310.8.Lecram",
        "没记账、但归档里有同名同大小 → 认得出是 Lecram");
}

Console.WriteLine("== 30. 插件自己登记加载方式的检测（第 7 条） ==");
Check(AddonSelfRegistrationDetector.Detect(System.Text.Encoding.ASCII.GetBytes("xx LoadFromDllMain yy")),
    "ASCII 的 LoadFromDllMain 认成自登记");
Check(AddonSelfRegistrationDetector.Detect(System.Text.Encoding.Unicode.GetBytes("xx WritePrivateProfileString yy")),
    "UTF-16 的 WritePrivateProfileString 认成自登记（写 ini）");
Check(!AddonSelfRegistrationDetector.Detect(System.Text.Encoding.ASCII.GetBytes("normal addon, only GetPrivateProfileString and DisabledAddons")),
    "只读 ini / 只提 DisabledAddons 的不算自登记");
Check(!AddonSelfRegistrationDetector.Detect((byte[]?)null), "null 不算自登记");
Check(!AddonSelfRegistrationDetector.Detect([]), "空数组不算自登记");
if (Directory.Exists(realAddons))
{
    foreach (string probe in Directory.EnumerateFiles(realAddons, "*.addon64*").Take(3))
    {
        Console.WriteLine("    [探测] " + Path.GetFileName(probe) + " -> 自登记=" + AddonSelfRegistrationDetector.Detect(probe));
    }
}
Console.WriteLine("== 31. 便携版「只认自己目录树」的作用域判断 ==");
// 便携根目录**自己**必须算在树内 —— 老实现写的是 path.StartsWith(root + '\\')，
// 根目录自己不满足这个前缀，于是便携版永远找不到用户数据目录 → DB 不初始化 →
// playtime 子进程报 no such table: PlayTimeItem。
Check(PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", @"D:\APPS\HoYoShadeHub"),
    "便携根目录自己算在树内（老实现这里是 false，就是它引起的）");
Check(PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", @"D:\APPS\HoYoShadeHub\cache"),
    "子目录算在树内");
Check(PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub\", @"D:\APPS\HoYoShadeHub\cache\games"),
    "根目录带结尾分隔符也认");
Check(PortableDataFolderScope.IsInside(@"d:\apps\hoyoshadehub", @"D:\APPS\HoYoShadeHub\Cache"),
    "大小写不敏感");
Check(!PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", @"D:\APPS\HoYoShadeHub-new"),
    "同前缀的隔壁目录不算（这条就是那个过滤器的目的）");
Check(!PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", @"D:\APPS"),
    "上级目录不算");
Check(!PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", @"C:\Users\thx11\AppData\Local\HoYoShadeHub"),
    "别的盘 / 别人家的数据目录不算");
Check(!PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", null), "null 路径不算");
Check(!PortableDataFolderScope.IsInside(null, @"D:\APPS\HoYoShadeHub\cache"), "null 根不算");
Check(!PortableDataFolderScope.IsInside(@"D:\APPS\HoYoShadeHub", "  "), "空白路径不算");

Console.WriteLine("== 31. 按 addonPatterns 删同族文件 + 插件包副本清理（第 8 条） ==");
string patRoot = Path.Combine(root, "pattern-clean");
Directory.CreateDirectory(patRoot);
foreach (string n in new[] { "foo.addon64", "foo(1.0).addon64x", "foo-extra.addon64x", "foobar.addon64", "bar.addon64" })
{
    File.WriteAllText(Path.Combine(patRoot, n), "x");
}

// 模式照扩展目录的写法：带版本的 .addon64*、带括号的、带短横线的变体都覆盖；foobar 不匹配
string[] fooPatterns = ["foo.addon64*", "foo(*", "foo-*"];
AddonPatternCleanResult patResult = AddonPatternCleaner.DeleteMatching(patRoot, fooPatterns);
Check(patResult.DeletedCount == 3, "按模式删掉 3 个同族文件：" + string.Join(",", patResult.Deleted));
Check(!File.Exists(Path.Combine(patRoot, "foo.addon64"))
      && !File.Exists(Path.Combine(patRoot, "foo(1.0).addon64x"))
      && !File.Exists(Path.Combine(patRoot, "foo-extra.addon64x")), "三种变体都删掉了");
Check(File.Exists(Path.Combine(patRoot, "foobar.addon64")) && File.Exists(Path.Combine(patRoot, "bar.addon64")),
    "不匹配模式的文件不动（foobar 没被前缀误删，证明是 GlobMatcher 不是前缀比较）");
Check(AddonPatternCleaner.DeleteMatching(patRoot, fooPatterns, ["foo.addon64"]).DeletedCount == 0,
    "alreadyDeleted 里给过的名字不重复计");
Check(AddonPatternCleaner.DeleteMatching(Path.Combine(root, "no-such-dir"), fooPatterns).DeletedCount == 0,
    "目录不存在返回空结果");

// 8b：共享目录里删掉后，重拼插件包会把包目录里那份（硬链接副本）清掉
string packRoot = Path.Combine(root, "pattern-pack");
string sharedForPack = Path.Combine(root, "pattern-shared");
Directory.CreateDirectory(sharedForPack);
File.WriteAllText(Path.Combine(sharedForPack, "foo.addon64"), "aaa");
List<AddonPackEntry> packPlan = GameAddonPack.Plan(sharedForPack, null, null);
Check(packPlan.Count == 1, "包计划 1 个文件");
GameAddonPack.Sync(packRoot, packPlan, null);
Check(File.Exists(Path.Combine(packRoot, "foo.addon64")) && File.Exists(Path.Combine(packRoot, GameAddonPack.MarkerFileName)),
    "包里先有一份副本 + pack.json");
File.Delete(Path.Combine(sharedForPack, "foo.addon64"));
GameAddonPack.Sync(packRoot, GameAddonPack.Plan(sharedForPack, null, null), null);
Check(!File.Exists(Path.Combine(packRoot, "foo.addon64")), "共享文件删掉后重拼，包里的副本被清掉");
Console.WriteLine("== 32. RTX HDR 和显示器 HDR 的搭配判定（第 4 条） ==");
Check(RtxHdrCoexistence.Evaluate(false, true) == RtxHdrVerdict.NotEnabled, "RTX HDR 没开 → 不用管（就算系统 HDR 开着）");
Check(RtxHdrCoexistence.Evaluate(false, false) == RtxHdrVerdict.NotEnabled, "RTX HDR 没开、系统 HDR 也没开 → 不用管");
Check(RtxHdrCoexistence.Evaluate(true, false) == RtxHdrVerdict.EnabledWithoutSystemHdr,
    "RTX HDR 开着但显示器 HDR 没开 → 判定为「失效」（用户反馈的这条）");
Check(RtxHdrCoexistence.Evaluate(true, true) == RtxHdrVerdict.EnabledWithSystemHdr, "开着且系统 HDR 也开着 → 生效，提醒叠加");
Check(RtxHdrCoexistence.Evaluate(true, null) == RtxHdrVerdict.EnabledWithUnknownSystemHdr, "开着但读不到显示器 HDR → 只说读不到");
Check(RtxHdrCoexistence.Evaluate(null, true) == RtxHdrVerdict.UnknownToggleWithSystemHdr,
    "驱动里读不到 RTX HDR、系统 HDR 开着 → 提醒去 NVIDIA app 确认");
Check(RtxHdrCoexistence.Evaluate(null, false) == RtxHdrVerdict.UnknownToggleWithoutSystemHdr,
    "驱动里读不到 RTX HDR、系统 HDR 也关着 → 放心");
Check(RtxHdrCoexistence.Evaluate(null, null) == RtxHdrVerdict.UnknownToggleWithoutSystemHdr,
    "两边都读不到 → 不报问题（绝不猜一个值吓人）");
Check(RtxHdrCoexistence.Evaluate(true, false) != RtxHdrVerdict.EnabledWithSystemHdr,
    "失效判定不会被当成「生效」");


Console.WriteLine("== 33. 「真便携」标记 / 环境变量开关 ==");
string portableRoot = Path.Combine(root, "portable-root");
Directory.CreateDirectory(portableRoot);
Check(!PortableDataFolderScope.HasPortableMarker(portableRoot), "没有 .portable 标记 → 不认");
Check(!PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "没标记没环境变量 → 默认不是真便携（老用户缓存不搬家）");
File.WriteAllText(Path.Combine(portableRoot, PortableDataFolderScope.PortableMarkerFileName), "");
Check(PortableDataFolderScope.HasPortableMarker(portableRoot), "放了 .portable 就认");
Check(PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "放了 .portable → 真便携开着");
Check(!PortableDataFolderScope.HasPortableMarker(Path.Combine(root, "no-such-portable")), "目录不存在 → false（不抛）");
Check(!PortableDataFolderScope.HasPortableMarker(null), "null 根 → false");
Check(!PortableDataFolderScope.HasPortableMarker("  "), "空白根 → false");
Check(!PortableDataFolderScope.IsPortableLocalEnabled(null), "null 根 + 没环境变量 → false");
File.Delete(Path.Combine(portableRoot, PortableDataFolderScope.PortableMarkerFileName));
Check(!PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "标记删掉 → 回到默认（关）");
Environment.SetEnvironmentVariable(PortableDataFolderScope.PortableLocalEnvironmentVariable, "1");
Check(PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量 =1 → 真便携开着（没标记文件也认）");
Environment.SetEnvironmentVariable(PortableDataFolderScope.PortableLocalEnvironmentVariable, "TRUE");
Check(PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量 =TRUE → 认（大小写不敏感）");
Environment.SetEnvironmentVariable(PortableDataFolderScope.PortableLocalEnvironmentVariable, "0");
Check(!PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量 =0 → 不算真值");
Environment.SetEnvironmentVariable(PortableDataFolderScope.PortableLocalEnvironmentVariable, "banana");
Check(!PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量 =banana → 不算真值");
File.WriteAllText(Path.Combine(portableRoot, PortableDataFolderScope.PortableMarkerFileName), "");
Check(PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量 =0 但标记在 → 还是真便携（标记兜底）");
Environment.SetEnvironmentVariable(PortableDataFolderScope.PortableLocalEnvironmentVariable, null);
Check(PortableDataFolderScope.IsPortableLocalEnabled(portableRoot), "环境变量清掉、标记还在 → 仍真便携");


Console.WriteLine("== 34. 临时目录统一入口（真便携 → 便携目录内；否则就是 %TEMP%） ==");
string? savedTempOverride = TemporaryFolder.Override;
try
{
    TemporaryFolder.Override = null;
    Check(TemporaryFolder.Path.TrimEnd(Path.DirectorySeparatorChar)
          == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "没设 Override 时就是系统 %TEMP%（行为没变）");

    string localTemp = Path.Combine(root, "portable-temp");
    TemporaryFolder.Override = localTemp;
    string resolvedTemp = TemporaryFolder.Path;
    Check(Directory.Exists(localTemp), "设了 Override，取 Path 时自动把目录建出来");
    Check(resolvedTemp.StartsWith(localTemp, StringComparison.OrdinalIgnoreCase), "Path 指到便携目录里");
    Check(resolvedTemp.EndsWith(Path.DirectorySeparatorChar), "结尾带分隔符（Path.Combine 才拼得对）");
    Check(Path.Combine(TemporaryFolder.Path, "x.zip").StartsWith(localTemp, StringComparison.OrdinalIgnoreCase),
        "Path.Combine 拼出来的文件落在便携目录里，不会跑到 %TEMP%");

    TemporaryFolder.Override = "   ";
    Check(TemporaryFolder.Path.TrimEnd(Path.DirectorySeparatorChar)
          == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "传空白 → 回到系统 %TEMP%（不把空白当目录名）");

    TemporaryFolder.Override = null;
    Check(TemporaryFolder.Path.TrimEnd(Path.DirectorySeparatorChar)
          == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "传 null → 回到系统 %TEMP%");
}
finally
{
    TemporaryFolder.Override = savedTempOverride;
}
Check(TemporaryFolder.Override == savedTempOverride, "测试完把 Override 还原");


Console.WriteLine("== 35. 「一键覆盖包」的识别与安装（本地安装按钮） ==");

// 识别：带整棵 HoYoShade / OptiScaler 的包是覆盖包，不能落到 OptiScaler / addon 分支去
Check(LocalPackageInstaller.DetectKindFromEntries(
        ["HoYoShade/ReShade64.dll", "HoYoShade/reshade-shaders/Addons/x.addon64", "OptiScaler/state.json"])
      == LocalPackageKind.Overlay, "带 HoYoShade 框架 + OptiScaler/state.json → 认成覆盖包");
Check(LocalPackageInstaller.DetectKindFromEntries(["HoYoShade/ReShade64.dll", "HoYoShade/ReShade.ini"])
      == LocalPackageKind.Overlay, "只有 HoYoShade 框架也算覆盖包");
Check(LocalPackageInstaller.DetectKindFromEntries(["OptiScaler/state.json", "OptiScaler/mfg-ada/v1/OptiScaler.dll"])
      == LocalPackageKind.Overlay, "只有 OptiScaler 库（带 state.json）也算覆盖包");
Check(LocalPackageInstaller.DetectKindFromEntries(["ReShade64.dll", "reshade-shaders/Addons/x.addon64"])
      == LocalPackageKind.Overlay, "内容直接铺在根上（没套 HoYoShade 目录）也认");
Check(LocalPackageInstaller.DetectKindFromEntries(["OptiScaler.dll", "OptiScaler.ini"])
      == LocalPackageKind.OptiScaler, "OptiScaler 官方 release 包不受影响");
Check(LocalPackageInstaller.DetectKindFromEntries(["foo.addon64"]) == LocalPackageKind.Addon,
    "单个 addon 不受影响");
Check(LocalPackageInstaller.DetectKindFromEntries(["module/version.dll"]) == LocalPackageKind.Module,
    "模块包不受影响");
Check(LocalPackageInstaller.DetectKindFromEntries(
        ["version.ini", "app-1.3.9.1/HoYoShadeHub.exe", "HoYoShade/ReShade64.dll"])
      == LocalPackageKind.AppPackage, "启动器完整包不会被当成覆盖包（否则整包解进 OptiScaler 库）");

// 安装：铺一份「用户现有的」HoYoShade，再拿覆盖包盖上去
string overlayWork = Path.Combine(root, "overlay");
string overlayShade = Path.Combine(overlayWork, "HoYoShade");
string overlayOpti = Path.Combine(overlayWork, "OptiScaler");
string overlayModules = Path.Combine(overlayWork, "Modules");
string overlayAddons = Path.Combine(overlayShade, "reshade-shaders", "Addons");
string overlayCache = Path.Combine(overlayWork, "cache", "cache-root");
Directory.CreateDirectory(overlayAddons);
Directory.CreateDirectory(Path.Combine(overlayShade, ".hysx"));
File.WriteAllText(Path.Combine(overlayShade, "ReShade64.dll"), "old");
File.WriteAllText(Path.Combine(overlayShade, "keep.txt"), "keep");
File.WriteAllText(Path.Combine(overlayAddons, "foo.addon64"), "old-addon");
File.WriteAllText(Path.Combine(overlayShade, ".hysx", "installed.json"), "MINE");

string overlayZip = Path.Combine(overlayWork, "星穹铁道6倍覆盖包_1.1.zip");
using (ZipArchive zip = ZipFile.Open(overlayZip, ZipArchiveMode.Create))
{
    // 打包工具多套了一层同名目录 —— 安装器要能钻进去
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/HoYoShade/ReShade64.dll", "new");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/HoYoShade/reshade-shaders/Addons/foo.addon64", "new-addon");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/HoYoShade/.hysx/installed.json", "PACKED");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/OptiScaler/state.json", @"{ ""selected"": ""mfg-ada/v1"" }");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/OptiScaler/mfg-ada/v1/OptiScaler.dll", "dll");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/OptiScaler/mfg-ada/v1/build.json",
        @"{ ""sourceId"": ""mfg-ada"", ""version"": ""v1"" }");
    WriteZipText(zip, "星穹铁道6倍覆盖包_1.1/OptiScaler/presets/40-x6.ini", "preset");
}

Check(LocalPackageInstaller.DetectKind(overlayZip) == LocalPackageKind.Overlay, "真 zip 文件也认成覆盖包");

LocalPackageInstallResult overlayResult = await new LocalPackageInstaller(
    overlayOpti, overlayAddons, overlayModules, null, overlayShade, null, overlayCache).InstallAsync(overlayZip);

Check(overlayResult.Kind == LocalPackageKind.Overlay, "装完报的类型是覆盖包");
Check(overlayResult.Summary.StartsWith("[覆盖包]"), "状态栏文案带「覆盖包」：" + overlayResult.Summary);
Check(File.ReadAllText(Path.Combine(overlayShade, "ReShade64.dll")) == "new", "HoYoShade 框架文件被盖成包里的版本");
Check(File.ReadAllText(Path.Combine(overlayAddons, "foo.addon64")) == "new-addon", "addons 目录里的插件被盖新");
Check(File.Exists(Path.Combine(overlayShade, "keep.txt")), "包里没有的文件不动（覆盖不是清空重来）");
Check(File.ReadAllText(Path.Combine(overlayShade, ".hysx", "installed.json")) == "MINE",
    "包里的 .hysx 不盖掉用户账本");
Check(File.ReadAllText(Path.Combine(overlayOpti, "state.json")).Contains("mfg-ada/v1"),
    "OptiScaler 库连 state.json 一起盖过去");
Check(File.Exists(Path.Combine(overlayOpti, "mfg-ada", "v1", "OptiScaler.dll")), "OptiScaler 构建目录落位");
Check(File.Exists(Path.Combine(overlayOpti, "presets", "40-x6.ini")), "OptiScaler 预设也进去");
Check(new OptiScalerLibrary(overlayOpti).GetSelected()?.Id == "mfg-ada/v1",
    "盖完启动器就认得出「当前启用」的是哪个构建");
Check(!File.Exists(Path.Combine(overlayShade, "ReShade64.dll.hysx-new")), "临时 .hysx-new 文件没留下");

// 归档：覆盖包里带着运行时 dll，装完要能进 <CacheRoot>/dlls/<family>/<version>/
string sampleDll = Path.Combine(AppContext.BaseDirectory, "HoYoShadeHub.Extensions.dll");
if (File.Exists(sampleDll))
{
    File.Copy(sampleDll, Path.Combine(overlayAddons, "nvngx_dlssnr.dll"), overwrite: true);

    LocalPackageInstallResult archived = await new LocalPackageInstaller(
        overlayOpti, overlayAddons, overlayModules, null, overlayShade, null, overlayCache).InstallAsync(overlayZip);

    var dllArchive = new DllVersionStore(overlayCache);
    Check(dllArchive.ListVersions("dlssnr").Count == 1,
        "覆盖包装完，盘上这份运行时 dll 被归档（DLL 页才看得出是哪个版本）");
    Check(archived.Summary.Contains("归档"), "状态栏里有归档结果：" + archived.Summary);
}


Console.WriteLine("== 36. 覆盖包清单 filelist.json：识别 / 落位 / dll 版本按清单归档 ==");

Check(OverlayManifest.Parse("""{"hysxOverlay":1,"name":"x"}""")?.DisplayName == "x", "清单能解析出名字");
Check(OverlayManifest.Parse("""{"schema":1}""")?.IsValid == true, "schema 写法也认");
Check(OverlayManifest.Parse("""{"name":"x"}""") is null, "没写格式版本 → 不当成覆盖包清单");
Check(OverlayManifest.Parse("not json") is null, "坏 json 不抛、当没有清单");
Check(OverlayManifest.Parse(null) is null, "空内容 → null");
Check(OverlayManifest.Load(Path.Combine(root, "no-such-dir")) is null, "目录不存在 → null");
Check(LocalPackageInstaller.DetectKindFromEntries(["filelist.json", "随便/一个.bin"]) == LocalPackageKind.Overlay,
    "顶层有 filelist.json 就是覆盖包（不用靠目录结构猜）");
Check(LocalPackageInstaller.DetectKindFromEntries(["壳/filelist.json", "壳/x.bin"]) == LocalPackageKind.Overlay,
    "多套了一层壳也认得出清单");
Check(LocalPackageInstaller.DetectKindFromEntries(["filelist.json", "version.ini", "app-1.3.9.1/HoYoShadeHub.exe"])
      == LocalPackageKind.AppPackage, "启动器本体包优先于覆盖包");

// 按清单装：目录名随便起，落位 / dll 版本 / OptiScaler 选择全听清单的
string mfWork = Path.Combine(root, "overlay-manifest");
string mfShade = Path.Combine(mfWork, "HoYoShade");
string mfOpti = Path.Combine(mfWork, "OptiScaler");
string mfModules = Path.Combine(mfWork, "Modules");
string mfAddons = Path.Combine(mfShade, "reshade-shaders", "Addons");
string mfCache = Path.Combine(mfWork, "cache-root");
Directory.CreateDirectory(mfAddons);

string mfZip = Path.Combine(mfWork, "打包好的覆盖包.zip");
string mfManifest = """
{
  "hysxOverlay": 1,
  "name": "星穹铁道 6 倍覆盖包",
  "version": "1.1",
  "game": "hkrpg",
  "targets": [
    { "from": "framework", "to": "shade" },
    { "from": "opti-lib", "to": "optiscaler" },
    { "from": "docs", "to": "skip" }
  ],
  "dlls": [ { "family": "dlssnr", "file": "nvngx_dlssnr.dll", "version": "310.8.Lecram" } ],
  "optiscaler": { "sourceId": "mfg-ada", "version": "mfg-ada-0.1.5" },
  "addons": [ { "file": "foo.addon64", "name": "DLSS 5 Feed" } ],
  "files": [ { "path": "framework/ReShade64.dll", "size": 3 } ]
}
""";
using (ZipArchive zip = ZipFile.Open(mfZip, ZipArchiveMode.Create))
{
    WriteZipText(zip, "filelist.json", mfManifest);
    WriteZipText(zip, "framework/ReShade64.dll", "new");
    WriteZipText(zip, "framework/reshade-shaders/Addons/nvngx_dlssnr.dll", "not-a-pe-file");
    WriteZipText(zip, "framework/reshade-shaders/Addons/foo.addon64", "addon");
    WriteZipText(zip, "opti-lib/mfg-ada/mfg-ada-0.1.5/OptiScaler.dll", "dll");
    WriteZipText(zip, "docs/README.txt", "should not be installed");
}

LocalPackageInstallResult mfResult = await new LocalPackageInstaller(
    mfOpti, mfAddons, mfModules, null, mfShade, null, mfCache).InstallAsync(mfZip);

Check(mfResult.Kind == LocalPackageKind.Overlay, "带清单的包按覆盖包装");
Check(mfResult.DisplayName == "星穹铁道 6 倍覆盖包", "状态栏名字取清单里的：" + mfResult.DisplayName);
Check(File.Exists(Path.Combine(mfShade, "ReShade64.dll")), "清单里 framework → shade 落位（目录名包自己起）");
Check(File.Exists(Path.Combine(mfAddons, "foo.addon64")), "插件也落到 Addons");
Check(File.Exists(Path.Combine(mfOpti, "mfg-ada", "mfg-ada-0.1.5", "OptiScaler.dll")), "opti-lib → OptiScaler 库落位");
Check(File.Exists(Path.Combine(mfOpti, "mfg-ada", "mfg-ada-0.1.5", "build.json")),
    "包里没写 build.json，清单声明了也会补上（否则库里看不见这个构建）");
Check(!Directory.Exists(Path.Combine(mfShade, "docs")) && !File.Exists(Path.Combine(mfShade, "README.txt")),
    "to=skip 的目录不装");
Check(new DllVersionStore(mfCache).Has("dlssnr", "310.8.Lecram"),
    "PE 读不出来的 dll 也照清单写的版本归档（310.8.Lecram）");
Check(new OptiScalerLibrary(mfOpti).GetSelected()?.Id == "mfg-ada/mfg-ada-0.1.5",
    "清单没关 select → 装完就选中这个构建");
Check(mfResult.Summary.Contains("按清单安装"), "状态栏说明是按清单装的：" + mfResult.Summary);

// 全新便携包（还没装 HoYoShade）里先导覆盖包：必须明确报错，不能默默只装 OptiScaler 一半
// （用户实测：这样导完 DLL 页一直「缺必需文件」，因为框架那半根本没落）
string noShadeWork = Path.Combine(root, "overlay-no-shade");
string noShadeOpti = Path.Combine(noShadeWork, "OptiScaler");
string noShadeModules = Path.Combine(noShadeWork, "Modules");
string noShadeCache = Path.Combine(noShadeWork, "cache-root");
Directory.CreateDirectory(noShadeOpti);
Directory.CreateDirectory(noShadeModules);

string? noShadeError = null;
try
{
    await new LocalPackageInstaller(noShadeOpti, string.Empty, noShadeModules, null, null, null, noShadeCache)
        .InstallAsync(mfZip);
}
catch (Exception ex)
{
    noShadeError = ex.Message;
}

Check(noShadeError is not null, "没装 HoYoShade 时导带框架的覆盖包 → 报错，不再默默只装一半");
Check(noShadeError?.Contains("HoYoShade") == true, "报错说清是缺 HoYoShade：" + noShadeError);
Check(!Directory.Exists(Path.Combine(noShadeOpti, "mfg-ada")), "报错就整包不落（不会有 OptiScaler 半拉的残留）");

// 老包（没清单）靠目录结构认，同样不能只装一半
string legacyZip = Path.Combine(noShadeWork, "老包没有清单.zip");
using (ZipArchive zip = ZipFile.Open(legacyZip, ZipArchiveMode.Create))
{
    WriteZipText(zip, "HoYoShade/ReShade64.dll", "old");
    WriteZipText(zip, "OptiScaler/state.json", @"{ ""selected"": ""mfg-ada/v1"" }");
}

string? legacyError = null;
try
{
    await new LocalPackageInstaller(noShadeOpti, string.Empty, noShadeModules, null, null, null, noShadeCache)
        .InstallAsync(legacyZip);
}
catch (Exception ex)
{
    legacyError = ex.Message;
}

Check(legacyError?.Contains("HoYoShade") == true, "老包（没清单）同样报错：" + legacyError);

// 反例：包里只有 OptiScaler 那一半 → 本机没装 HoYoShade 也该照装
string optiOnlyZip = Path.Combine(noShadeWork, "只有 opti 的包.zip");
using (ZipArchive zip = ZipFile.Open(optiOnlyZip, ZipArchiveMode.Create))
{
    WriteZipText(zip, "filelist.json",
        """{"hysxOverlay":1,"name":"只有 OptiScaler 的包","targets":[{"from":"opti-lib","to":"optiscaler"}]}""");
    WriteZipText(zip, "opti-lib/mfg-ada/mfg-ada-0.1.5/OptiScaler.dll", "dll");
}

LocalPackageInstallResult optiOnly = await new LocalPackageInstaller(
    noShadeOpti, string.Empty, noShadeModules, null, null, null, noShadeCache).InstallAsync(optiOnlyZip);
Check(optiOnly.Kind == LocalPackageKind.Overlay
      && File.Exists(Path.Combine(noShadeOpti, "mfg-ada", "mfg-ada-0.1.5", "OptiScaler.dll")),
    "包里没有 HoYoShade 那一半 → 没装框架也照样装 OptiScaler 那半");


Console.WriteLine("== FG-only minimal INI runtime path regression ==");
string minimalBuild = Path.Combine(root, "中文 launcher", "mfg-ada-0.1.6");
Directory.CreateDirectory(minimalBuild);
string minimalIni = Path.Combine(minimalBuild, "OptiScaler.ini");
string expectedPathLine = "OptiDllPath = " + Path.Combine(minimalBuild, "OptiScaler");
foreach (string input in new[]
{
    "[FrameGen]\nEnabled=false\n",
    "[Libraries]\nNvngxPath=auto\n[FrameGen]\nEnabled=false\n",
    "[Libraries]\nOptiDllPath=auto\n[FrameGen]\nEnabled=false\n",
    "[libraries]\noptidllpath=old-path\n[FrameGen]\nEnabled=false\n",
    ""
})
{
    File.WriteAllText(minimalIni, input);
    Check(OptiScalerRuntime.EnsureConfigDllPath(minimalBuild), "minimal INI path repaired");
    string once = File.ReadAllText(minimalIni);
    Check(once.Contains(expectedPathLine), "path pinned to build, including Unicode path");
    Check(!input.Contains("Enabled=false") || once.Contains("Enabled=false"), "FG setting preserved");
    Check(!input.Contains("NvngxPath=auto") || once.Contains("NvngxPath=auto"), "other library setting preserved");
    Check(OptiScalerRuntime.EnsureConfigDllPath(minimalBuild) && File.ReadAllText(minimalIni) == once,
        "path repair is idempotent");
}
// Exercise the actual release version gate and Genshin profile migration.
string? releaseDll = Environment.GetEnvironmentVariable("HYS_RELEASE_OPTI_DLL");
if (!string.IsNullOrEmpty(releaseDll))
{
    string nativeBuild = Path.Combine(root, "genshin-native-profile");
    Directory.CreateDirectory(nativeBuild);
    File.Copy(releaseDll, Path.Combine(nativeBuild, "OptiScaler.dll"));
    string nativeIni = Path.Combine(nativeBuild, "OptiScaler.ini");
    File.WriteAllText(nativeIni, "[DLSS]\r\nNativeScreenSpaceGuides = false\r\nNativeScreenSpaceGuides=auto\r\nPreset=1\r\n[OptiFG]\r\nResourceFlip=true\r\n");
    Check(OptiScalerRuntime.EnsureGenshinNativeGuides(nativeBuild), "原神新版本 profile 修正启用");
    string repaired = File.ReadAllText(nativeIni);
    Check(repaired.Contains("NativeScreenSpaceGuides=true") && !repaired.Contains("NativeScreenSpaceGuides = false")
        && repaired.Contains("Preset=1") && repaired.Contains("ResourceFlip=true"), "原神重复键清理且其他设置保留");
    Check(OptiScalerRuntime.EnsureGenshinNativeGuides(nativeBuild) && File.ReadAllText(nativeIni) == repaired,
        "原神 guide 配置重复启动幂等");
    string profileDir = Path.Combine(nativeBuild, "profiles");
    Directory.CreateDirectory(profileDir);
    string earlyProfile = Path.Combine(profileDir, "hk4e_cn.ini");
    File.WriteAllText(earlyProfile, "[DLSS]\nNativeScreenSpaceGuides=false\n[Libraries]\nOptiDllPath=auto\n[FrameGen]\nFGOutput=DLSSG\n");
    Check(OptiScalerRuntime.PrepareGenshinEarlyConfiguration(nativeBuild, "hk4e_cn"), "首次早期注入前准备原神配置");
    string beforeLoad = File.ReadAllText(nativeIni);
    Check(beforeLoad.Contains("NativeScreenSpaceGuides=true")
        && beforeLoad.Contains("OptiDllPath = " + Path.Combine(nativeBuild, "OptiScaler"))
        && beforeLoad.Contains("FGOutput=DLSSG"), "LoadLibrary前已修正库路径且保留FG配置");
    File.WriteAllText(earlyProfile, File.ReadAllText(earlyProfile).Replace("OptiDllPath=auto", "OptiDllPath=Z:\\previous-machine"));
    Check(OptiScalerRuntime.PrepareGenshinEarlyConfiguration(nativeBuild, "hk4e_cn")
        && !File.ReadAllText(nativeIni).Contains("previous-machine"), "换目录后早期profile路径重新固定");
    File.Delete(nativeIni);
    Check(!OptiScalerRuntime.EnsureGenshinNativeGuides(nativeBuild), "不生成缺失的游戏配置");
}
File.Delete(minimalIni);
Check(!OptiScalerRuntime.EnsureConfigDllPath(minimalBuild), "missing INI not fabricated");
Check(!OptiScalerRuntime.EnsureConfigDllPath(Path.Combine(root, "absent-build")), "missing build not fabricated");

Console.WriteLine("== 启动前引导：游戏 ReShade.ini 缺失 / 教程标记 / 第二个 runtime 的 ReShade2.ini ==");
string bootHostRoot = Path.Combine(root, "bootstrap-host");
Directory.CreateDirectory(Path.Combine(bootHostRoot, "reshade-shaders", "Addons"));
File.WriteAllText(Path.Combine(bootHostRoot, "ReShade64.dll"), "fake");
File.WriteAllText(Path.Combine(bootHostRoot, "ReShade.ini"),
    "[ADDON]\r\nAddonPath=" + Path.Combine(bootHostRoot, "reshade-shaders", "Addons") + "\\\r\n\r\n[OVERLAY]\r\nTutorialProgress=0\r\n");
var bootHost = new ShadeHost(bootHostRoot);

// ① ini 缺失：从模板复制 + 教程标 4 + 预生成 ReShade2.ini
string bootGameDir = Path.Combine(root, "bootstrap-game");
Directory.CreateDirectory(bootGameDir);
File.WriteAllText(Path.Combine(bootGameDir, "StarRail.exe"), "fake");
var bootEntry = new GameEntry("test:bootstrap", "引导测试") { ExePath = Path.Combine(bootGameDir, "StarRail.exe") };

GameIniBootstrapResult boot1 = GameIniBootstrap.Ensure(bootEntry, bootHost);
Check(boot1.CreatedFromTemplate && boot1.TutorialMarkedDone && boot1.CreatedSecondary,
    "缺失时：复制模板 + 教程标完成 + 预生成 ReShade2.ini");
Check(File.Exists(Path.Combine(bootGameDir, "ReShade.ini")), "主 ini 已创建");
Check(ReShadeProfile.Load(bootEntry.ReShadeIniPath!).GetValue("OVERLAY", "TutorialProgress") == "4", "主 ini 教程标记 = 4");
Check(File.Exists(Path.Combine(bootGameDir, "ReShade2.ini")), "ReShade2.ini 已预生成");
Check(ReShadeProfile.Load(Path.Combine(bootGameDir, "ReShade2.ini")).GetValue("OVERLAY", "TutorialProgress") == "4",
    "ReShade2.ini 带上教程标记");

// ② 再跑一遍：什么都不动（幂等）
GameIniBootstrapResult boot2 = GameIniBootstrap.Ensure(bootEntry, bootHost);
Check(!boot2.ChangedAnything && !boot2.Failed, "第二次 Ensure 什么都不改");

// ③ ReShade2.ini 被用户删了 → 重新预生成；主 ini 里的用户改动不被抹掉
File.Delete(Path.Combine(bootGameDir, "ReShade2.ini"));
ReShadeProfile bootProfile3 = ReShadeProfile.Load(bootEntry.ReShadeIniPath!);
bootProfile3.SetValue("GENERAL", "PresetPath", "X:\\custom.ini");
bootProfile3.Save();
GameIniBootstrapResult boot3 = GameIniBootstrap.Ensure(bootEntry, bootHost);
Check(boot3.CreatedSecondary && !boot3.CreatedFromTemplate, "只补 ReShade2.ini");
Check(ReShadeProfile.Load(bootEntry.ReShadeIniPath!).GetValue("GENERAL", "PresetPath") == "X:\\custom.ini",
    "主 ini 用户配置不被覆盖");

// ④ 没有宿主（host = null）时：已有 ini 仍收拾教程标记，缺 ini 则报 MissingTemplate
GameIniBootstrapResult boot4 = GameIniBootstrap.Ensure(bootEntry, null);
Check(!boot4.Failed && !boot4.CreatedFromTemplate, "host = null 也安全");
var orphanDir = Path.Combine(root, "bootstrap-orphan");
Directory.CreateDirectory(orphanDir);
File.WriteAllText(Path.Combine(orphanDir, "StarRail.exe"), "fake");
var orphanEntry = new GameEntry("test:orphan", "无宿主") { ExePath = Path.Combine(orphanDir, "StarRail.exe") };
GameIniBootstrapResult boot5 = GameIniBootstrap.Ensure(orphanEntry, null);
Check(boot5.MissingTemplate && !File.Exists(orphanEntry.ReShadeIniPath!), "没宿主且没 ini → MissingTemplate，不硬造");

// ⑤ 崩铁双 runtime 案：ReShade2.ini 停留在旧副本（旧宿主路径 + 旧插件开关）→ 启动时被主 ini 镜像覆盖；
//    [INPUT] / [OVERLAY] 里的 runtime 自己的状态保留
string staleSecondary = Path.Combine(bootGameDir, "ReShade2.ini");
ReShadeProfile staleProfile = ReShadeProfile.Load(staleSecondary);
staleProfile.SetValue("ADDON", "AddonPath", "Z:\\old-host\\reshade-shaders\\Addons\\");
staleProfile.SetValue("ADDON", "DisabledAddons", "Old Plugin@old.addon64");
staleProfile.SetValue("INPUT", "KeyOverlay", "113,0,0,0");
staleProfile.SetValue("OVERLAY", "WindowX", "123");
staleProfile.Save();
GameIniBootstrapResult boot6 = GameIniBootstrap.Ensure(bootEntry, bootHost);
ReShadeProfile fixedSecondary = ReShadeProfile.Load(staleSecondary);
Check(boot6.SyncedSecondary && !boot6.CreatedSecondary, "识别为同步已有 ReShade2.ini（不是新建）");
Check(string.IsNullOrEmpty(fixedSecondary.GetValue("ADDON", "AddonPath")),
    "ReShade2.ini 的 AddonPath 保持为空，不重复加载 Present 插件");
string? mainDisabled = ReShadeProfile.Load(bootEntry.ReShadeIniPath!).GetValue("ADDON", "DisabledAddons");
Check(fixedSecondary.GetValue("ADDON", "DisabledAddons") == mainDisabled,
    "ReShade2.ini 的 DisabledAddons 跟主 ini 一致（主没有则副本的死键也被摘掉）");
Check(fixedSecondary.GetValue("INPUT", "KeyOverlay") == "113,0,0,0", "第二 runtime 自己的热键保留");
Check(fixedSecondary.GetValue("OVERLAY", "WindowX") == "123", "第二 runtime 自己的覆盖层位置保留");
Check(fixedSecondary.GetValue("OVERLAY", "TutorialProgress") == "4", "教程标记仍然 = 4");
GameIniBootstrapResult boot7 = GameIniBootstrap.Ensure(bootEntry, bootHost);
Check(!boot7.SyncedSecondary && !boot7.ChangedAnything, "同步幂等：再跑一遍什么都不写");

Console.WriteLine("== DLSS5 预设切换器：分享码编解码 + 预设库 ==");
// 跨实现测试向量：Python 独立实现（zlib.crc32 + 自定义字母表 base64）算的 flags=0 裸路径
byte[]? vec1 = Dlss5PresetShareCode.Decode("D5P1AB0AAAATBwEhW1JFTk9EWC1ETFNTNV0KTlJIb29rUG9pbnQ9MQo");
Check(vec1 is not null && System.Text.Encoding.UTF8.GetString(vec1!) == "[RENODX-DLSS5]\nNRHookPoint=1\n",
    "解码 Python 向量（裸路径 flags=0）");
Check(Dlss5PresetShareCode.Decode("D5P1AAUAAACCidH3SGVsbG8") is { } v2
      && System.Text.Encoding.UTF8.GetString(v2) == "Hello", "解码第二个 Python 向量");

// 往返：随机（不可压 → flags=0）/ 重复 INI 文本（可压 → flags=1）/ 边界尺寸
var rng = new Random(42);
byte[] randomPayload = new byte[5000];
rng.NextBytes(randomPayload);
Check(Dlss5PresetShareCode.Decode(Dlss5PresetShareCode.Encode(randomPayload)!)!.SequenceEqual(randomPayload),
    "随机负载往返");
var repetitive = new System.Text.StringBuilder();
for (int i = 0; i < 400; i++)
{
    repetitive.Append("[RENODX-DLSS5]\nNRHookPoint=1\nDX11Source=native\nEnableHooks=2\n");
}

byte[] repetitiveBytes = System.Text.Encoding.UTF8.GetBytes(repetitive.ToString());
string repetitiveCode = Dlss5PresetShareCode.Encode(repetitiveBytes)!;
Check(Dlss5PresetShareCode.Decode(repetitiveCode)!.SequenceEqual(repetitiveBytes), "可压缩负载往返");
Check(repetitiveCode.Length < repetitiveBytes.Length * 4 / 3 / 2, "可压缩负载真的走了压缩（码长明显小于原文 base64）");
byte[] boundary = new byte[Dlss5PresetShareCode.MaxPayload];
rng.NextBytes(boundary);
Check(Dlss5PresetShareCode.Decode(Dlss5PresetShareCode.Encode(boundary)!)!.SequenceEqual(boundary), "256 KiB 边界往返");
Check(Dlss5PresetShareCode.Encode(new byte[Dlss5PresetShareCode.MaxPayload + 1]) is null, "超 256 KiB 拒编码");
Check(Dlss5PresetShareCode.Encode([]) is null, "空内容拒编码");

// CRC 校验：改一个字符必须解码失败（不是静默出坏数据）
char[] corrupt = repetitiveCode.ToCharArray();
corrupt[corrupt.Length - 3] = corrupt[corrupt.Length - 3] == 'A' ? 'B' : 'A';
Check(Dlss5PresetShareCode.Decode(new string(corrupt)) is null, "负载被篡改 → CRC 拦下");
Check(Dlss5PresetShareCode.Decode("not-a-code") is null && Dlss5PresetShareCode.Decode("D5P1!!!") is null,
    "非分享码拒解码");
Check(Dlss5PresetShareCode.Decode("  \r\nD5P1AAUAAACCidH3SGVsbG8  ") is { } v3
      && System.Text.Encoding.UTF8.GetString(v3) == "Hello", "前导空白容忍");

// 预设库：扫描（Addons 顶层 + DLSS5-Presets 递归）+ 导入分享码 / 导入文件
string presetAddons = Path.Combine(root, "preset-addons");
Directory.CreateDirectory(Path.Combine(presetAddons, "DLSS5-Presets", "by-author"));
File.WriteAllText(Path.Combine(presetAddons, "beside-addon.ini"), "[A]\nB=1\n");
File.WriteAllText(Path.Combine(presetAddons, "DLSS5-Presets", "gi.ini"), "[G]\nH=1\n");
File.WriteAllText(Path.Combine(presetAddons, "DLSS5-Presets", "by-author", "nested.ini"), "[N]\nM=1\n");
File.WriteAllText(Path.Combine(presetAddons, "ignore.txt"), "x");
List<Dlss5PresetFile> presets = Dlss5PresetLibrary.Scan(presetAddons);
Check(presets.Count == 3 && presets.Any(p => p.Name == "beside-addon.ini")
      && presets.Any(p => p.Name == "gi.ini") && presets.Any(p => p.Name == "nested.ini"),
    "扫描到顶层 + DLSS5-Presets 递归，txt 不算");
Check(Dlss5PresetLibrary.Scan(Path.Combine(root, "no-such-dir")).Count == 0, "目录不存在 → 空列表");

string? imported = Dlss5PresetLibrary.ImportShareCode(
    presetAddons, "D5P1AB0AAAATBwEhW1JFTk9EWC1ETFNTNV0KTlJIb29rUG9pbnQ9MQo", null);
Check(imported is not null && File.Exists(imported!) && Path.GetFileName(imported!).StartsWith("Shared-"),
    "导入分享码 → DLSS5-Presets 下 Shared-XXXXXXXX.ini");
string? imported2 = Dlss5PresetLibrary.ImportShareCode(
    presetAddons, "D5P1AB0AAAATBwEhW1JFTk9EWC1ETFNTNV0KTlJIb29rUG9pbnQ9MQo", "我的预设");
Check(imported2 is not null && Path.GetFileName(imported2!) == "我的预设.ini", "指定文件名导入");
string? imported3 = Dlss5PresetLibrary.ImportShareCode(
    presetAddons, "D5P1AB0AAAATBwEhW1JFTk9EWC1ETFNTNV0KTlJIb29rUG9pbnQ9MQo", "我的预设");
Check(imported3 is not null && Path.GetFileName(imported3!) == "我的预设 (2).ini", "重名自动加后缀");
Check(Dlss5PresetLibrary.ImportShareCode(presetAddons, "garbage", null) is null, "坏码拒导入");

string? importedFile = Dlss5PresetLibrary.ImportFile(presetAddons, Path.Combine(presetAddons, "beside-addon.ini"));
Check(importedFile is not null && File.Exists(importedFile!) && importedFile!.EndsWith(".ini"),
    "导入 txt/ini 文件复制进 DLSS5-Presets");
Check(System.Text.Encoding.UTF8.GetString(Dlss5PresetLibrary.ReadContent(importedFile!)!) == "[A]\nB=1\n",
    "读回内容一致");
File.WriteAllBytes(Path.Combine(presetAddons, "utf16.ini"),
    [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("[A]\r\nB=1\r\n")]);
Check(Dlss5PresetLibrary.ReadContent(Path.Combine(presetAddons, "utf16.ini")) is null, "UTF-16 预设拒读（和游戏内一致）");

// ===== 覆盖包用户内容：ini_config.json / auto.json / presets 同步 / 文件覆盖 =====
string ucPackRoot = Path.Combine(root, "pack");
Directory.CreateDirectory(Path.Combine(ucPackRoot, "presets", "sub"));
File.WriteAllText(Path.Combine(ucPackRoot, "presets", "a.ini"), "[A]\nB=1\n");
File.WriteAllText(Path.Combine(ucPackRoot, "presets", "sub", "b.ini"), "[C]\nD=2\n");
Check(GameAddonPackUserContent.HasAny(ucPackRoot), "presets\\ 算用户内容");

string ucPackAddons = Path.Combine(ucPackRoot, "Addons");
Directory.CreateDirectory(ucPackAddons);
File.WriteAllText(Path.Combine(ucPackAddons, GameAddonPack.MarkerFileName), "{\"schema\":1}");
Check(GameAddonPackUserContent.PackRootOfAddonDirectory(ucPackAddons) == Path.GetFullPath(ucPackRoot),
    "包根 = Addons 上一级");
Check(GameAddonPackUserContent.PackRootOfAddonDirectory(presetAddons) is null, "非包目录 → 无包根");

int syncedPresets = GameAddonPackUserContent.SyncPresets(ucPackRoot, ucPackAddons);
Check(syncedPresets == 2 && File.Exists(Path.Combine(ucPackAddons, "DLSS5-Presets", "a.ini"))
      && File.Exists(Path.Combine(ucPackAddons, "DLSS5-Presets", "sub", "b.ini")),
    "presets\\ 递归复制进 DLSS5-Presets");
Check(GameAddonPackUserContent.SyncPresets(ucPackRoot, ucPackAddons) == 0, "重复同步幂等（内容一致不重写）");
File.WriteAllText(Path.Combine(ucPackRoot, "presets", "a.ini"), "[A]\nB=2\n");
Check(GameAddonPackUserContent.SyncPresets(ucPackRoot, ucPackAddons) == 1, "内容变了会更新");
Check(File.ReadAllText(Path.Combine(ucPackAddons, "DLSS5-Presets", "a.ini")) == "[A]\nB=2\n",
    "更新后的预设内容一致");

Directory.CreateDirectory(Path.Combine(ucPackRoot, "game_files", "sub"));
File.WriteAllText(Path.Combine(ucPackRoot, "game_files", "x.dll"), "dll");
File.WriteAllText(Path.Combine(ucPackRoot, "game_files", "sub", "y.txt"), "y");
string overlayTarget = Path.Combine(root, "overlay-target");
int overlaid = GameAddonPackUserContent.OverlayFiles(ucPackRoot, GameAddonPackUserContent.GameFilesFolderName, overlayTarget);
Check(overlaid == 2 && File.ReadAllText(Path.Combine(overlayTarget, "sub", "y.txt")) == "y",
    "game_files\\ 按相对结构覆盖到目标目录");

// ini_config.json：set 写入 + remove 摘除 + 幂等
File.WriteAllText(Path.Combine(ucPackRoot, GameAddonPackUserContent.IniConfigFileName), """
{
  "set": {
    "GENERAL": { "PresetPath": "C:\\p.ini", "NoEffectCache": "1" },
    "RenoDX.DLSS5": { "NRHookPoint": "1" }
  },
  "remove": { "ADDON": ["DeadKey"] }
}
""");
GamePackIniConfig? iniConfig = GamePackIniConfig.Load(ucPackRoot);
Check(iniConfig is not null && iniConfig.Set.Count == 2 && iniConfig.Set["GENERAL"].Count == 2
      && iniConfig.Remove["ADDON"].Count == 1, "ini_config.json 解析");

string gameIni = Path.Combine(root, "game.ini");
File.WriteAllText(gameIni, "[GENERAL]\nPresetPath=C:\\old.ini\n[ADDON]\nDeadKey=1\n");
Check(iniConfig!.Apply(gameIni), "首次 apply 有写入");
string applied = File.ReadAllText(gameIni);
Check(applied.Contains("PresetPath=C:\\p.ini") && applied.Contains("NoEffectCache=1")
      && applied.Contains("NRHookPoint=1") && !applied.Contains("DeadKey"),
    "set 写入 + remove 摘除都生效");
Check(!iniConfig.Apply(gameIni), "重复 apply 幂等");

File.WriteAllText(Path.Combine(ucPackRoot, GameAddonPackUserContent.IniConfigFileName), "{ broken json");
Check(GamePackIniConfig.Load(ucPackRoot) is null, "坏 ini_config.json 当没有配置");
File.WriteAllText(Path.Combine(ucPackRoot, GameAddonPackUserContent.IniConfigFileName), "{}");
Check(GamePackIniConfig.Load(ucPackRoot) is null, "空配置当没有");

// auto.json：动作 / runOnLaunch / 步骤参数
File.WriteAllText(Path.Combine(ucPackRoot, GameAddonPackUserContent.AutoActionFileName), """
{
  "actions": [
    {
      "name": "一键 NR",
      "runOnLaunch": true,
      "steps": [
        { "action": "set_addons", "enabled": false, "files": ["a.addon64", "b.addon64"] },
        { "action": "apply_preset", "name": "NR.ini" },
        { "action": "set_dx12", "enabled": true }
      ]
    },
    { "name": "空动作", "steps": [] },
    { "name": "坏步骤", "steps": [ { "nope": 1 }, { "action": "set_opt", "enabled": false } ] }
  ]
}
""");
List<PackAutoAction> actions = PackAutoActionFile.Load(ucPackRoot);
Check(actions.Count == 2, "空动作被丢掉、坏步骤被跳过");
Check(actions[0].Name == "一键 NR" && actions[0].RunOnLaunch && actions[0].Steps.Count == 3,
    "动作名 / runOnLaunch / 步骤数");
Check(actions[0].Steps[0].GetBool("enabled") == false
      && actions[0].Steps[0].GetStringList("files").SequenceEqual(["a.addon64", "b.addon64"]),
    "步骤参数按名取值");
Check(actions[0].Steps[1].GetString("name") == "NR.ini", "字符串参数");
Check(actions[1].Steps.Count == 1 && actions[1].Steps[0].Action == "set_opt",
    "坏步骤跳过、有效步骤保留");
Check(!actions[1].RunOnLaunch, "runOnLaunch 默认 false");
Check(PackAutoActionFile.Load(Path.Combine(root, "no-such-pack")).Count == 0, "无 auto.json → 空");
Check(PackAutoActionFile.Parse("""{"actions":[{"name":"A","steps":[{"action":"set_dx12","enabled":true},{"action":"launch_game"}]}]}""") is { Count: 1 } pa && pa[0].Name == "A" && pa[0].Steps.Count == 2 && pa[0].Steps[1].Action == "launch_game", "Parse：标准 actions 形态");
Check(PackAutoActionFile.Parse("""[{"steps":[{"action":"set_opt","enabled":true}]},{"steps":[{"action":"clear_game_ini"}]}]""") is { Count: 2 } bareArr && bareArr[0].Steps[0].GetBool("enabled") == true, "Parse：裸数组形态");
Check(PackAutoActionFile.Parse("""{"steps":[{"action":"set_dx12","enabled":true}]}""") is { Count: 1 } single && single[0].Steps.Count == 1 && single[0].Name.Length > 0, "Parse：单 steps 匿名动作");
Check(PackAutoActionFile.Parse("not json").Count == 0 && PackAutoActionFile.Parse("{}").Count == 0, "Parse：坏 JSON / 空对象 → 空");
Check(PackAutoActionFile.Parse("""{"steps":[{"action":"wait","seconds":1.5}]}""") is { Count: 1 } waitAction
      && waitAction[0].Steps[0].GetNumber("seconds") == 1.5, "Parse：wait 秒参数");

// ==================== 远端条件表（conditions.json）兼容性 ====================
// 断言：不传任何远端文档时，各处行为必须与硬编码时代逐字节一致；喂远端文档后按文档走。
{
    AddonConditions.Load(null);   // 确保从干净状态开始

    // --- 默认表（无远端）---
    AddonConditionMatch super = AddonConditions.MatchAddon("renodx-dlss5-super-anus");
    Check(super is { Dlss5: true, LoadFromDllMain: true, HookPointCapable: true, RenoDxDlss5: false, Feed: false },
        "conditions：super-anus = DLSS5+DllMain+HookPoint，但不抢主插件名分");

    AddonConditionMatch main = AddonConditions.MatchAddon("renodx-dlss5");
    Check(main is { Dlss5: true, LoadFromDllMain: true, HookPointCapable: true, RenoDxDlss5: true },
        "conditions：renodx-dlss5 主插件 RenoDxDlss5=true");

    AddonConditionMatch oldReno = AddonConditions.MatchAddon("renodx-dlss");
    Check(oldReno is { Dlss5: true, LoadFromDllMain: true, HookPointCapable: true, RenoDxDlss5: false },
        "conditions：renodx-dlss 老版 DLSS 非主插件");

    AddonConditionMatch dlss5Match = AddonConditions.MatchAddon("dlss5-bridge");
    Check(dlss5Match is { Dlss5: true, LoadFromDllMain: true, HookPointCapable: false, RenoDxDlss5: false },
        "conditions：dlss5 前缀 DllMain=true、HookPoint=false");

    AddonConditionMatch feed = AddonConditions.MatchAddon("dlss5-feed");
    Check(feed is { Dlss5: true, LoadFromDllMain: true, HookPointCapable: false, RenoDxDlss5: false, Feed: true },
        "conditions：dlss5-feed Feed=true，且按旧 Contains 语义仍属 DLSS5 类（DllMain 加载）");

    AddonConditionMatch none = AddonConditions.MatchAddon("some-other-addon");
    Check(none is { Dlss5: false, LoadFromDllMain: false, HookPointCapable: false, RenoDxDlss5: false, Feed: false },
        "conditions：未知 slug 全 false");

    // --- dll 需求默认 ---
    var (req5, rec5) = AddonConditions.DllRequirementsOf("dlss5");
    Check(req5.Count == 2
          && req5[0].Files.SequenceEqual(new[] { "nvngx_dlssnr.dll" })
          && req5[1].Files.SequenceEqual(new[] { "sl.interposer.dll", "sl.dlss_nr.dll" })
          && rec5.Count == 0,
        "conditions：dlss5 默认必需 2 组（nr + interposer/dlss_nr）");
    var (reqX, _) = AddonConditions.DllRequirementsOf("other");
    Check(reqX.Count == 0, "conditions：非 dlss5 tag 默认无条件");

    // --- DllComponentCatalog 默认 ---
    Check(DllComponentCatalog.Families.Count == 5
          && DllComponentCatalog.Families.Select(f => f.Id).SequenceEqual(new[] { "dlssnr", "streamline", "dlss", "dlssd", "dlssg" })
          && DllComponentCatalog.Families[1].PreferredVersion == "2.14.0.0",
        "conditions：dllFamilies 默认 5 族、streamline 首选 2.14.0.0");
    Check(AddonConditions.Current is null, "conditions：无远端时 Current=null（全部内置默认）");

    // --- OptiScaler / Upscaler 运行时默认 ---
    Check(OptiScalerRuntime.StreamlineFileNames.Length == 7
          && OptiScalerRuntime.StreamlineFileNames.Contains("sl.interposer.dll")
          && OptiScalerRuntime.StreamlineFileNames.Contains("sl.pcl.dll"),
        "conditions：streamlineFiles 默认 7 个且含 interposer/pcl");
    Check(GamePluginService.UpscalerRuntimeDlls.SequenceEqual(new[] { "nvngx.dll", "libxess.dll" }),
        "conditions：upscalerRuntimeDlls 默认 nvngx+libxess");

    // --- 内部注册名默认 ---
    Check(AddonNameResolver.GetKnownInternalName("renodx-dlss5-super-anus") == "RenoDX DLSS_A"
          && AddonNameResolver.GetKnownInternalName("renodx-dlss") == "RenoDX DLSS"
          && AddonNameResolver.GetKnownInternalName("dlss5-bridge") == "DLSS 5 Bridge"
          && AddonNameResolver.GetKnownInternalName("nope") is null,
        "conditions：internalNames 默认 3 条映射");

    // --- Feed 预设默认 ---
    Check(ReShadePresetEditor.FeedTechniques.Count == 2
          && ReShadePresetEditor.FeedTechniques[0].StartsWith("Lumenite_Kernel@")
          && ReShadePresetEditor.IsFeedAddon("dlss5-feed.addon64")
          && !ReShadePresetEditor.IsFeedAddon("dlss5-bridge.addon64"),
        "conditions：feedAddon 默认 technique 顺序 + slug 识别");
    Check(ReShadePresetEditor.MotionVectorProviderName == "DLSS5_MV_PROVIDER"
          && ReShadePresetEditor.MotionVectorProviderValue == "3",
        "conditions：MV provider 默认 DLSS5_MV_PROVIDER=3");

    // --- 远端文档：覆盖 + 兜底语义 ---
    AddonConditions.LoadJson("""
    {
      "schema": 1,
      "addonConditions": [
        { "slugPrefix": "my-new-addon", "dlss5": true, "loadFromDllMain": true }
      ],
      "dllRequirements": [
        { "tag": "dlss5", "required": [["nvngx_dlssnr.dll"], ["sl.interposer.dll", "sl.dlss_nr.dll", "sl.pcl.dll"]] }
      ],
      "optiscaler": { "streamlineFiles": ["a.dll", "b.dll"], "upscalerRuntimeDlls": ["x.dll"] },
      "internalNames": { "my-new-addon": "My New Addon" },
      "feedAddon": { "slugPrefix": "my-feed", "techniques": ["P@p.fx", "F@f.fx"], "motionVectorProviderName": "MV", "motionVectorProviderValue": "9" }
    }
    """);

    Check(AddonConditions.MatchAddon("my-new-addon") is { Dlss5: true, LoadFromDllMain: true },
        "conditions：远端表新增 slug 生效");
    Check(AddonConditions.MatchAddon("dlss5-bridge") is { Dlss5: true, LoadFromDllMain: true },
        "conditions：远端表没配全的 slug 回落默认表");
    Check(AddonConditions.MatchAddon("renodx-dlss5") is { RenoDxDlss5: true },
        "conditions：远端表没配 renodx 时默认表补 RenoDxDlss5");

    var (req5r, _) = AddonConditions.DllRequirementsOf("dlss5");
    Check(req5r.Count == 2 && req5r[1].Files.Length == 3 && req5r[1].Files.Contains("sl.pcl.dll"),
        "conditions：远端 dllRequirements 整组替换");
    Check(OptiScalerRuntime.StreamlineFileNames.SequenceEqual(new[] { "a.dll", "b.dll" }),
        "conditions：远端 streamlineFiles 覆盖");
    Check(GamePluginService.UpscalerRuntimeDlls.SequenceEqual(new[] { "x.dll" }),
        "conditions：远端 upscalerRuntimeDlls 覆盖");
    Check(AddonNameResolver.GetKnownInternalName("my-new-addon") == "My New Addon"
          && AddonNameResolver.GetKnownInternalName("renodx-dlss") == "RenoDX DLSS",
        "conditions：远端 internalNames 合并而不是替换");
    Check(ReShadePresetEditor.IsFeedAddon("my-feed.addon64") && !ReShadePresetEditor.IsFeedAddon("dlss5-feed.addon64")
          && ReShadePresetEditor.MotionVectorProviderName == "MV" && ReShadePresetEditor.MotionVectorProviderValue == "9"
          && ReShadePresetEditor.FeedTechniques.SequenceEqual(new[] { "P@p.fx", "F@f.fx" }),
        "conditions：远端 feedAddon 全字段覆盖");

    // 坏 JSON / 清空 → 全部回落默认
    AddonConditions.LoadJson("not json");
    Check(AddonConditions.Current is null && AddonConditions.MatchAddon("dlss5-bridge").Dlss5,
        "conditions：坏 JSON 当没有，回落默认");
    AddonConditions.Load(null);

    // DllComponentCatalog 远端族替换
    AddonConditions.LoadJson("""{"dllFamilies":[{"id":"dlssnr","displayName":"NR","filePattern":"nvngx_dlssnr.dll","level":"required","note":"n"}]}""");
    Check(DllComponentCatalog.Families.Count == 1 && DllComponentCatalog.Families[0].Level == DllRequirementLevel.Required,
        "conditions：远端 dllFamilies 整表替换（含 level 映射）");
    AddonConditions.Load(null);
    Check(DllComponentCatalog.Families.Count == 5, "conditions：清掉远端后 Families 回落默认 5 族");

    // --- 仓库里那份真实的 catalog/conditions.json：语法 + 关键语义 ---
    string repoConditionsPath = Path.Combine(repoCatalogDir ?? "", "conditions.json");
    if (File.Exists(repoConditionsPath))
    {
        AddonConditions.LoadJson(File.ReadAllText(repoConditionsPath));
        Check(AddonConditions.Current is not null, "conditions：仓库 conditions.json 能被解析");
        Check(AddonConditions.MatchAddon("renodx-dlss5-super-anus") is { RenoDxDlss5: false, Dlss5: true, HookPointCapable: true },
            "conditions：仓库表 super-anus 明确不算主插件（前缀重叠顺序正确）");
        Check(AddonConditions.MatchAddon("renodx-dlss5") is { RenoDxDlss5: true },
            "conditions：仓库表 renodx-dlss5 主插件判定保留");
        Check(AddonConditions.MatchAddon("dlss5-feed") is { Feed: true },
            "conditions：仓库表 feed 判定保留");
        var (reqR, _) = AddonConditions.DllRequirementsOf("dlss5");
        Check(reqR.Count == 2 && reqR[1].Files.Contains("sl.interposer.dll"),
            "conditions：仓库表 dlss5 必需组完整");
        AddonConditions.Load(null);
    }
    else
    {
        Check(false, "conditions：仓库 catalog/conditions.json 存在");
    }
}

Console.WriteLine("== Known MiHoYo exe routing and installed Bridge compatibility ==");
string knownRoot = Path.Combine(root, "known-game-routing");
Directory.CreateDirectory(knownRoot);
string knownExe = Path.Combine(knownRoot, "StarRail.exe");
File.WriteAllText(knownExe, "fake executable");
Check(KnownGameSelection.Resolve(knownExe, []) == new GameBiz(GameBiz.hkrpg_cn), "StarRail exe routes to built-in game");
File.WriteAllText(Path.Combine(knownRoot, "config.ini"), "[General]\nchannel=14\n");
Check(KnownGameSelection.Resolve(knownExe, []) == new GameBiz(GameBiz.hkrpg_bilibili), "Bilibili config uses built-in B server scheme");
File.WriteAllText(Path.Combine(knownRoot, "config.ini"), "[General]\ngame_biz=hkrpg_global\n");
Check(KnownGameSelection.Resolve(knownExe, []) == new GameBiz(GameBiz.hkrpg_global), "Explicit valid region wins over filename fallback");
Check(KnownGameSelection.Resolve(knownExe, [new KnownGameCandidate(GameBiz.hkrpg_bilibili, "B server", knownRoot, "StarRail.exe")])
    == new GameBiz(GameBiz.hkrpg_bilibili), "Existing exact install keeps original launcher scheme");
string unknownExe = Path.Combine(knownRoot, "MyGame.exe");
File.WriteAllText(unknownExe, "unknown executable");
Check(KnownGameSelection.Resolve(unknownExe, []) is null, "Unknown exe remains a custom game even beside MiHoYo config");
File.WriteAllText(Path.Combine(knownRoot, "config.ini"), "[General]\ngame_biz=hk4e_cn\n");
Check(KnownGameSelection.Resolve(knownExe, []) == new GameBiz(GameBiz.hkrpg_cn), "Mismatched game family config rejected");
Check(BridgeCompatibility.IsSupported(new Version(2,3,2,0)), "Overlay Bridge2.3.2 accepted");
Check(BridgeCompatibility.IsSupported(new Version(2,3,1,0)), "Previous Bridge2.3.1 retained");
Check(!BridgeCompatibility.IsSupported(new Version(2,2,0,0)) && !BridgeCompatibility.IsSupported(null), "Unsupported old/missing Bridge rejected");

Console.WriteLine("== Current full portable layout does not trigger legacy upgrade guidance ==");
string portableLayoutRoot = Path.Combine(root, "full-portable-layout");
Directory.CreateDirectory(portableLayoutRoot);
File.WriteAllText(Path.Combine(portableLayoutRoot, "HoYoShadeHub.exe"), "stub");
File.WriteAllText(Path.Combine(portableLayoutRoot, ".portable"), "");
Check(!CacheMigrationPlanner.HasSupportedPortableLayout(portableLayoutRoot), "Unmarked legacy layout still detected normally");
string layoutMarker = Path.Combine(portableLayoutRoot, CacheMigrationPlanner.PortableLayoutFileName);
File.WriteAllText(layoutMarker, "{\"schema\":1,\"layout\":\"full-portable-v1\",\"moduleRoot\":\"Modules\",\"optiscalerRoot\":\"OptiScaler\",\"cacheRoot\":\"cache\"}");
Check(CacheMigrationPlanner.HasSupportedPortableLayout(portableLayoutRoot), "Current supported full package layout recognized");
Check(!CacheMigrationPlanner.NeedsMigration(false, true, false, true, false, true, false, true),
    "Fresh full package does not ask to upgrade root modules or bundled plugins");
Check(CacheMigrationPlanner.NeedsMigration(false, false, false, false, false, true, false),
    "Actual unmarked legacy Modules migration remains available");
File.WriteAllText(layoutMarker, "{\"schema\":99}");
Check(!CacheMigrationPlanner.HasSupportedPortableLayout(portableLayoutRoot), "Unknown layout schema cannot silence migration");
File.WriteAllText(layoutMarker, "invalid json");
Check(!CacheMigrationPlanner.HasSupportedPortableLayout(portableLayoutRoot), "Corrupt layout marker safely ignored");

Console.WriteLine("== XXMI graphics prerequisite identity ==");
string requestedGimi = Path.Combine(root, "XXMI", "GIMI", "d3d11.dll");
Check(GraphicsModulePrerequisite.Matches(requestedGimi, "d3d11.dll", requestedGimi), "Actual GIMI graphics DLL accepted");
Check(!GraphicsModulePrerequisite.Matches(requestedGimi, "d3d11.dll", Path.Combine(root, "Windows", "System32", "d3d11.dll")),
    "System d3d11 with same basename does not mark XXMI ready");
Check(GraphicsModulePrerequisite.Matches("ReShade64.dll", "ReShade64.dll", Path.Combine(root, "ReShade64.dll")),
    "Existing basename-based ReShade order unchanged");
Check(!GraphicsModulePrerequisite.Matches(requestedGimi, "d3d11.dll", ""), "Unknown module path does not mark GIMI ready");

Console.WriteLine("== Genshin manual XXMI uses ordinary launcher routing ==");
Check(!GenshinLaunchRouting.UseEarlyGraphicsLaunch(false, false, true), "Manual XXMI bypasses immediate graphics batch");
Check(GenshinLaunchRouting.UseEarlyGraphicsLaunch(false, false, false), "Without XXMI existing early graphics path retained");
Check(!GenshinLaunchRouting.UseEarlyGraphicsLaunch(true, false, false), "Injection-only mode not changed into direct start");
Check(!GenshinLaunchRouting.UseEarlyGraphicsLaunch(false, true, false), "Starward launch remains separate");

Console.WriteLine("== XXMI new launch enum does not corrupt old start-method enum ==");
Check(XxmiLaunchConfiguration.PreserveLegacyStartMethod("Direct") == "OPTION_REMOVED", "Invalid Direct trial value repaired");
Check(XxmiLaunchConfiguration.PreserveLegacyStartMethod("DIRECT") == "OPTION_REMOVED", "Invalid uppercase DIRECT repaired");
Check(XxmiLaunchConfiguration.PreserveLegacyStartMethod("OPTION_REMOVED") == "OPTION_REMOVED", "Current deprecated placeholder retained");
Check(XxmiLaunchConfiguration.PreserveLegacyStartMethod("MANUAL") == "MANUAL", "Existing accepted MANUAL retained");
Check(XxmiLaunchConfiguration.PreserveLegacyStartMethod("") == "OPTION_REMOVED", "Missing obsolete key uses current placeholder");

Console.WriteLine("== Pre-launch process cleanup is bounded to supplied instances ==");
using (var worker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
{
    FileName = Environment.ProcessPath!, Arguments = "owned-process-test-worker",
    UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
}))
{
    Check(worker is not null, "Controlled process started");
    if (worker is not null)
    {
        var cleanup = await OwnedProcessShutdown.CloseAsync([worker], TimeSpan.FromMilliseconds(100));
        Check(cleanup.Success && worker.HasExited && cleanup.Closed == 1, "Supplied headless process retired before configuration write");
        var closedAgain = await OwnedProcessShutdown.CloseAsync([worker], TimeSpan.FromMilliseconds(100));
        Check(closedAgain.Success && closedAgain.Forced == 0, "Already exited process is not killed again");
    }
}
var noInstances = await OwnedProcessShutdown.CloseAsync([], TimeSpan.FromMilliseconds(100));
Check(noInstances.Success && noInstances.Closed == 0, "No XXMI instances proceeds immediately");

Check(XxmiLaunchModes.Parse("manual") == XxmiLaunchMode.Manual, "Manual launch selection parsed");
Check(XxmiLaunchModes.Parse("OFFICIAL") == XxmiLaunchMode.Official, "Official launch selection parsed");
Check(XxmiLaunchModes.Parse(null) == XxmiLaunchMode.Official, "Existing users keep current official default");
Check(XxmiLaunchModes.Parse("unknown") == XxmiLaunchMode.Official, "Invalid saved mode safely uses current default");

Console.WriteLine($"========== PASS {_passed} / FAIL {_failed} ==========");
try { Directory.Delete(root, true); } catch { }
static void WriteZipText(ZipArchive zip, string entryName, string text)
{
    ZipArchiveEntry entry = zip.CreateEntry(entryName);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(text);
}

static string HostBridgeVersion()
{
    string path = Environment.ProcessPath
        ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
        ?? string.Empty;
    return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
}

// 和 ModuleRegistry.IsAcceptedModuleDll 同一把尺：桥只收 2.3.1+。本测试宿主只引用 Extensions
// 那个程序集（拿不到 ModuleRegistry），所以这里照抄那道门，顺便证明 2.2.x 还是会被拒。
static bool BridgeAccepted(string path)
{
    try
    {
        System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        return BridgeCompatibility.IsSupported(
            new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart));
    }
    catch
    {
        return false;
    }
}

return _failed == 0 ? 0 : 1;
