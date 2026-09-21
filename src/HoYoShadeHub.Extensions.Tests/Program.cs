
using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.I18n;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System.IO.Compression;

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

Console.WriteLine("== 10. 内置目录 ==");
var builtin = ExtensionCatalogService.LoadBuiltin();
Check(builtin.Extensions.Length >= 6, $"内置目录有 {builtin.Extensions.Length} 个条目");
Check(builtin.Extensions.All(e => e.IsValid), "所有内置条目都通过 IsValid 校验");
Check(builtin.Extensions.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == builtin.Extensions.Length, "id 无重复");
string[] expectedIds = ["renodx.hkrpg", "renodx.dlss5", "renodx.dlss5.superanus", "renodx.dlss.sf", "renodx.ue.doffix", "dlss5.neural.interposer", "hoyoshade.presets"];
Check(expectedIds.All(id => builtin.Extensions.Any(e => e.Id == id)), "七款插件条目齐全（含 thx114/hoyodlss5 的 Neural Interposer）");

// 「只管理插件」：全局插件页只列会装 addon 的扩展包，滤镜/预设那条不该出现
Check(builtin.Extensions.First(e => e.Id == "hoyoshade.presets").Rules.All(r => !r.To.Contains("Addons")),
    "官方预设合集不装 addon —— 所以全局插件页不会列它");

var dlss5 = builtin.Extensions.First(e => e.Id == "renodx.dlss5");
var dlssSf = builtin.Extensions.First(e => e.Id == "renodx.dlss.sf");
var ueDof = builtin.Extensions.First(e => e.Id == "renodx.ue.doffix");
var hkrpg = builtin.Extensions.First(e => e.Id == "renodx.hkrpg");

// 同一个仓库里多个插件族必须靠 tagPattern 区分开
Check(dlss5.Source.Repository == dlssSf.Source.Repository, "rhi-repo 里的多个插件族来自同一仓库");
Check(!System.Text.RegularExpressions.Regex.IsMatch("renodx-dlss-SF-26.0917.1436", dlss5.Source.TagPattern!), "renodx-dlss5- 的 tagPattern 不会误吞 renodx-dlss-SF-");
Check(GlobMatcher.IsMatch(dlssSf.Source.AssetPattern!, "renodx-dlss_SF_26.0917.1436.zip"), "renodx-dlss-SF 资产名匹配");
Check(GlobMatcher.IsMatch(ueDof.Source.AssetPattern!, "renodx-universal_ue-dof-fix.addon64"), "ue-dof-fix 资产名匹配（是裸 addon 不是 zip）");
Check(!string.IsNullOrWhiteSpace(hkrpg.Source.AssetName), "renodx.hkrpg 用 assetName 走 atom 快路径（避开 renodx 仓库的巨大 JSON）");

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
    foreach (var entry in builtin.Extensions.Where(e => e.Source.Type == ExtensionSourceType.GithubRelease))
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

        string optiFile = Path.Combine(catalogDir, "optiscaler.json");
        List<OptiScalerSource>? remoteSources = OptiScalerCatalog.LoadFile(optiFile);
        Check(remoteSources is not null && remoteSources.Count > 0, $"[远端] optiscaler.json 读得到（{remoteSources?.Count ?? 0} 条）");

        foreach (OptiScalerSource remoteSource in OptiScalerCatalog.MergeWithBuiltin(remoteSources))
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
var serviceA = new GamePluginService(entryA, ShadeHostLocator.FromUserDataFolder(userDataFolder)!, cachePath15);
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

string plainDlss = "renodx-dlss(9.17.12).addon64";

// 用户反馈过：文件名里常常没有 ShortFuse 分支信息（装的就是 renodx-dlss.addon64），
// 于是 hook 下拉框被灰掉 —— 现在只要是 renodx-dlss* 就允许改
Check(AddonFileInfo.Parse(plainDlss)!.IsHookPointCapable, "普通 renodx-dlss 也算（文件名分不出 SF 分支）");
Check(AddonFileInfo.Parse("renodx-dlss.addon64")!.IsHookPointCapable, "没有版本号/分支的 renodx-dlss.addon64 也算");
Check(AddonFileInfo.Parse("renodx-dlss(ShortFuse_9.11.6).addon64")!.IsHookPointCapable, "renodx-dlss(ShortFuse) 算");
Check(AddonFileInfo.Parse("renodx-dlss-SF(9.17.14).addon64")!.IsHookPointCapable, "renodx-dlss-SF 也算");
Check(AddonFileInfo.Parse("renodx-dlss5-super-anus(1.0.8.18).addon64")!.IsHookPointCapable, "super-anus 算");
Check(AddonFileInfo.Parse("dlss5-bridge.addon64")!.IsHookPointCapable == false, "dlss5-bridge 不算");

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
Console.WriteLine("== 21. DLSS5 的 dll 依赖 + LoadFromDllMain 自动勾 ==");
Check(!DlssDllRequirements.IsDlss5(["hdr", "tonemap"]), "非 dlss5 标签不算 dlss5 插件");
Check(DlssDllRequirements.IsDlss5(["dlss5", "neural"]), "带 dlss5 标签就算");

AddonDllStatus noDll = AddonDllChecker.CheckFiles(["renodx-dlss5-super-anus.addon64"], ["dlss5"]);
Check(noDll.Severity == 2 && noDll.Summary.Contains("nvngx_dlssnr.dll"), $"没有 nvngx_dlssnr.dll → 标红（{noDll.Summary}）");

AddonDllStatus noSl = AddonDllChecker.CheckFiles(["a.addon64", "nvngx_dlssnr.dll"], ["dlss5"]);
Check(noSl.Severity == 1 && noSl.Summary.Contains("sl.interposer.dll"), $"有 dlssnr 但没 streamline → 标黄（{noSl.Summary}）");

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
string normalFile = "renodx-dlss(9.17.12).addon64";
dlssService.SetAddonEnabled(normalFile, true);
dlssService.SetAddonEnabled(superAnusFile, false);   // 先关掉，把 DllMain 那串清一下
dlssService.SetAddonEnabled(superAnusFile, true);    // 再打开 → 应该自动进 LoadFromDllMain
Check(dlssService.GetAddons().First(a => a.FileName == superAnusFile).LoadFromDllMain,
    "打开 dlss5 插件时自动勾上 LoadFromDllMain");
Check(!dlssService.GetAddons().First(a => a.FileName == normalFile).LoadFromDllMain,
    "非 dlss5 插件不会被动到 LoadFromDllMain");
Check(dlssService.GetAddons().First(a => a.FileName == superAnusFile).IsDlss5, "这条被认成 dlss5 插件");

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
Check(iniText.Contains(@".\reshade-shaders\Shaders\**"), "相对路径那条原样留着（它跟着 DLL 走，本来就是对的）");
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

Console.WriteLine("-- 插件汉化（白名单原地替换 + 备份 / 还原）--");
AddonI18nDocument builtinTable = AddonLocalizer.LoadBuiltin();
Check(builtinTable.Tables.Count >= 3, $"内置翻译表至少 3 个插件族，实际 {builtinTable.Tables.Count}");
AddonI18nTable? dlssTable = AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss5-super-anus(1.0.8.18).addon64");
Check(dlssTable?.Slug == "renodx-dlss5", $"renodx-dlss5-super-anus 选到 renodx-dlss5 那张表（实际 {dlssTable?.Slug}）");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "renodx-dlss(9.17.12).addon64")?.Slug == "renodx-dlss", "renodx-dlss 不会错选到 dlss5 那张表");
Check(AddonLocalizer.SelectTable(builtinTable.Tables, "some-other-thing.addon64") is null, "别的插件没有表就不汉化");

// 造一个「假 DLL」：尾部放几条 NUL 结尾的英文，加上一条超长中文的对照
string fakeDll = Path.Combine(root, "fake-addon.addon64");
byte[] fakeBytes = System.Text.Encoding.UTF8.GetBytes(string.Join((char)0, ["junk", "Off", "Reset", "Ultra Performance", "Too Long Sentence Here"]));
File.WriteAllBytes(fakeDll, fakeBytes);
string backupDir = Path.Combine(root, ".hysx", AddonLocalizer.BackupFolderName);

AddonI18nTable testTable = AddonLocalizer.LoadBuiltin().Tables.First(t => t.Slug == "renodx-dlss");
AddonLocalizeResult localize = AddonLocalizer.Apply(fakeDll, testTable, backupDir);
// 注意：Reset(5 字节) 放不下「重置」(6 字节) → 会被算进「放不下跳过」，这正是打法 A 的边界
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
Check(AddonLocalizer.PatchCodeImmediates(codeBytes, upsSource, upsTarget, upsImmediates) == 1, "立即数被替换");
Check(System.Text.Encoding.UTF8.GetString(codeBytes, 2, 8) == "放大" + new string(' ', 2),
    $"立即数换成「放大」并补空格（实际 '{System.Text.Encoding.UTF8.GetString(codeBytes, 2, 8)}'）");
Check(codeBytes[10] == 0x48 && codeBytes[11] == 0x89, "立即数后面的指令字节没被动");

// 长串的尾巴也是立即数：头从 .rdata 读、尾是 mov rsi, "Strength"（这就是「中文头 + 英文尾」的来源）
byte[] tailBytes =
[
    0x48, 0xBE, (byte)'S', (byte)'t', (byte)'r', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h',
    0x48, 0x89, 0x70, 0x1E,
];
byte[] skinSource = System.Text.Encoding.UTF8.GetBytes("Skin Structure Strength");
byte[] skinTarget = System.Text.Encoding.UTF8.GetBytes("皮肤结构强度");
List<(int Position, int Size)> tailImmediates = [.. AddonLocalizer.EnumerateImmediates(tailBytes, [(0, tailBytes.Length)])];
AddonLocalizer.PatchCodeImmediates(tailBytes, skinSource, skinTarget, tailImmediates);
Check(System.Text.Encoding.UTF8.GetString(tailBytes, 2, 8) == "度" + new string(' ', 5),
    $"尾巴立即数换成目标的对应段（实际 '{System.Text.Encoding.UTF8.GetString(tailBytes, 2, 8)}'）");

// 尾巴立即数带补零（mov rdx, "Mode\0\0\0\0"）：只换到串尾那几个字节，补零不能动。
// 注意尾巴匹配现在要求「前面 96 字节内已经有这条串改过的段」，所以这里带上头一段一起测。
byte[] paddedBytes =
[
    0x48, 0xB8, (byte)'O', (byte)'p', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'s', (byte)' ',
    0x48, 0xBA, (byte)'M', (byte)'o', (byte)'d', (byte)'e', 0x00, 0x00, 0x00, 0x00,
];
byte[] optSource = System.Text.Encoding.UTF8.GetBytes("Options Mode");
byte[] optTarget = System.Text.Encoding.UTF8.GetBytes("选项模式");
List<(int Position, int Size)> paddedImmediates = [.. AddonLocalizer.EnumerateImmediates(paddedBytes, [(0, paddedBytes.Length)])];
AddonLocalizer.PatchCodeImmediates(paddedBytes, optSource, optTarget, paddedImmediates);
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
Check(paddedTail, $"带补零的尾巴立即数换成目标的 [8..12)（实际 '{System.Text.Encoding.UTF8.GetString(paddedBytes, 12, 8)}'）");
Check(paddedBytes[16] == 0 && paddedBytes[19] == 0, "补零（NUL 结尾）没被动");

// 没有「根」的尾巴匹配要拒绝：孤零零一个 "Mode" 常量不该被当成 Options Mode 的尾巴
byte[] lonelyBytes = [0x48, 0xBA, (byte)'M', (byte)'o', (byte)'d', (byte)'e', 0x00, 0x00, 0x00, 0x00];
List<(int Position, int Size)> lonelyImmediates = [.. AddonLocalizer.EnumerateImmediates(lonelyBytes, [(0, lonelyBytes.Length)])];
int lonely = AddonLocalizer.PatchCodeImmediates(lonelyBytes, optSource, optTarget, lonelyImmediates);
Check(lonely == 0, $"没有根的尾巴匹配要拒绝（实际改了 {lonely} 处）");

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

// 端到端：把真文件里《Pass Count》那 26 个字节原样搬过来，看头一段和尾巴都换掉没有
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

// 尾巴一个字节一个字节 store（C6 45 xx）：只认紧接着已改段的那种，且要能连着往下走
byte[] byteStoreBytes =
[
    0x48, 0xB8, (byte)'P', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'C', (byte)'o', (byte)'u',
    0xC6, 0x45, 0x10, (byte)'n',
    0xC6, 0x45, 0x12, (byte)'t',
];
byte[] passSource = System.Text.Encoding.UTF8.GetBytes("Pass Count");
byte[] passTarget = System.Text.Encoding.UTF8.GetBytes("通道数");
List<(int Position, int Size)> byteStoreImmediates = [.. AddonLocalizer.EnumerateImmediates(byteStoreBytes, [(0, byteStoreBytes.Length)])];
AddonLocalizer.PatchCodeImmediates(byteStoreBytes, passSource, passTarget, byteStoreImmediates);

// 单字节 store 链是故意不做的：只看「字节值对不对得上」会认到别的串的 store，偏移错一位就把
// 中文切成半个 UTF-8（界面上显示「?握采」那种怪字）。所以这里断言：那两个单字节 store 不许被动。
Check(byteStoreBytes[13] == (byte)'n' && byteStoreBytes[17] == (byte)'t',
    $"单字节 store 不许乱动（实际 {byteStoreBytes[13]} {byteStoreBytes[17]}）");

// 单字节尾巴：靠**位移**精确接上（mov [rsi+0x80], rax 之后的 mov byte [rsi+0x88], 'u'）
byte[] dispBytes =
[
    0x48, 0xB8, (byte)'U', (byte)'s', (byte)'e', (byte)' ', (byte)'E', (byte)'x', (byte)'p', (byte)'o',
    0x48, 0x89, 0x86, 0x80, 0x00, 0x00, 0x00,
    0xC6, 0x86, 0x88, 0x00, 0x00, 0x00, (byte)'s',   // "Use Expo|sure..." 的第 9 个字符
];
byte[] useSource = System.Text.Encoding.UTF8.GetBytes("Use Exposure Value");
byte[] useTarget = System.Text.Encoding.UTF8.GetBytes("使用曝光值");
List<(int Position, int Size, int Disp, bool HasDisp)> dispImmediates = [(2, 8, 0x80, true), (23, 1, 0x88, true)];
int dispPatched = AddonLocalizer.PatchCodeImmediatesWithDisp(dispBytes, useSource, useTarget, dispImmediates);
Check(dispPatched == 2, $"头一段 + 单字节尾巴都要换（实际 {dispPatched} 处）");
Check(dispBytes[23] == useTarget[8], $"单字节尾巴按位移接上（实际 0x{dispBytes[23]:X2}，期望 0x{useTarget[8]:X2}）");

// 回归：'ter Mask' 只能被别的串匹配到 3 个字节（"Upsample Filter" 的 "ter"）时不许动它 ——
// 之前就是这么把「角色遮罩」写成了「角色＋9C＋空格＋Mask」。
byte[] mixBytes = [0x48, 0xB8, (byte)'t', (byte)'e', (byte)'r', (byte)' ', (byte)'M', (byte)'a', (byte)'s', (byte)'k'];
List<(int Position, int Size)> mixImmediates = [.. AddonLocalizer.EnumerateImmediates(mixBytes, [(0, mixBytes.Length)])];
int weak = AddonLocalizer.PatchCodeImmediates(mixBytes, System.Text.Encoding.UTF8.GetBytes("Upsample Filter"), System.Text.Encoding.UTF8.GetBytes("上采样滤镜"), mixImmediates);
Check(weak == 0, $"少于 4 字节的尾巴匹配要被拒绝（实际改了 {weak} 处）");

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
AddonLocalizer.PatchCodeImmediates(twoChunk, skinMaskSource, skinMaskTarget, twoChunkImmediates);
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

Check(OptiScalerCatalog.Builtin.Count == 3, "内置 3 个 OptiScaler 来源");
Check(OptiScalerCatalog.Builtin.Any(s => s.Id == "dlssnr-amd"), "第三个来源换成了 DLSS NR on AMD");
Check(OptiScalerCatalog.Builtin.All(s => s.Id != "multipass-mfg"), "404 的那个来源删掉了");
Check(OptiScalerCatalog.Builtin.Select(s => s.Id).Distinct().Count() == 3, "来源 id 不重复（要当目录名用）");
Check(OptiScalerCatalog.Builtin.All(s => s.Repository.Contains('/')), "每个来源都是 owner/repo");
Check(OptiScalerLibrary.Sanitize("a/b:c") == "a_b_c", "版本号里的非法字符会被换掉");
Check(OptiScalerCatalog.Find("neurotic")?.Repository == "MagicalPrincessUnicorn/NeuRotic-an-OptiScaler-DLSSNR-fork", "按 id 找得到来源");

Console.WriteLine();
Console.WriteLine($"========== PASS {_passed} / FAIL {_failed} ==========");
try { Directory.Delete(root, true); } catch { }
return _failed == 0 ? 0 : 1;