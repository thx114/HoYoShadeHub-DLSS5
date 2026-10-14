using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.ReShade;
namespace HoYoShadeHub.Extensions.Tests;
internal static class AddonFreshnessTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Hoyo-addon-freshness-" + Guid.NewGuid().ToString("N"));
        int failures = 0;
        void Check(bool value, string name) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}"); if (!value) failures++; }
        Directory.CreateDirectory(root);
        try
        {
            var host = new ShadeHost(Path.Combine(root, "host"));
            Directory.CreateDirectory(host.AddonsPath);
            string source = Path.Combine(host.AddonsPath, "FsrBridgeDepthAddon.addon64");
            File.WriteAllText(source, "old-build");
            var store = new AddonVersionStore(Path.Combine(root, "cache"));
            var record = new InstalledExtension { Id = "test.fsr", ResolvedTag = "test-v1" };
            record.Files.Add(new InstalledExtension.InstalledExtensionFile { Path = "reshade-shaders/Addons/FsrBridgeDepthAddon.addon64" });
            store.Archive(host, record);
            string archived = Path.Combine(store.DirectoryFor(record.Id, record.ResolvedTag), "reshade-shaders", "Addons", "FsrBridgeDepthAddon.addon64");
            // Replace inode, preserving size; target is deliberately newer.
            File.Delete(source);
            File.WriteAllText(source, "new-build");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(archived, DateTime.UtcNow);
            Check(HysxFileLink.SizeEquals(source, archived), "reproduction: equal size hides different contents");
            Check(!HysxFileLink.IsUpToDate(source, archived), "production hash rejects newer stale addon");
            Check(store.Archive(host, record).Archived == 1 && File.ReadAllText(archived) == "new-build", "archive same tag refreshes changed addon bytes");
            Check(store.Archive(host, record).Skipped == 1, "archive unchanged bytes remains idempotent");
            var plan = GameAddonPack.Plan(host.AddonsPath, new Dictionary<string, string>(), (id, tag) => store.AddonFiles(id, tag));
            string pack = Path.Combine(root, "pack");
            GameAddonPack.Sync(pack, plan, Array.Empty<(string, string)>());
            string cached = Path.Combine(pack, "FsrBridgeDepthAddon.addon64");
            File.Delete(cached); File.WriteAllText(cached, "old-build");
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddHours(1));
            GameAddonPack.Sync(pack, plan, Array.Empty<(string, string)>());
            Check(File.ReadAllText(cached) == "new-build", "game cache refresh rejects newer stale same-size addon");
            string config = Path.Combine(host.AddonsPath, "FsrBridgeDepthAddon.ini"), target = Path.Combine(root, "FsrBridgeDepthAddon.ini");
            File.WriteAllText(config, "Dx11ReShadeOnly=1"); File.WriteAllText(target, "Dx11ReShadeOnly=0");
            Check(!HysxFileLink.IsUpToDate(config, target), "same-size addon config must compare bytes");
            string build = Path.Combine(root, "opti"); Directory.CreateDirectory(build);
            string main = Path.Combine(build, "OptiScaler.ini"); File.WriteAllText(main, "[HoYoShade]\nDx11ReShadeOnly=true\n");
            Check(OptiScalerProfiles.Activate(build, "hk4e_cn"), "owned profile created");
            string profile = OptiScalerProfiles.ProfilePath(build, "hk4e_cn");
            File.WriteAllText(profile, "[HoYoShade]\nDx11ReShadeOnly=false\n");
            Check(OptiScalerProfiles.Activate(build, "hk4e_cn") && File.ReadAllText(main).Contains("Dx11ReShadeOnly=false"), "profile false replays to main without enabling new route");
            File.WriteAllText(profile, "[HoYoShade]\nDx11ReShadeOnly=false\nRocketMode=true\n");
            Check(OptiScalerProfiles.Activate(build, "hk4e_cn"), "old per-game profile activates before route enforcement");
            Check(OptiScalerRuntime.EnsureStarRailDx11InputRoute(build, "hk4e_cn"), "StarRail DX11 input route enforced after profile activation");
            Check(File.ReadAllText(main).Contains("Dx11ReShadeOnly = true") && File.ReadAllText(profile).Contains("Dx11ReShadeOnly = true"), "enforced DX11 route persists in runtime ini and game profile");
            Check(File.ReadAllText(main).Contains("RocketMode=true") && File.ReadAllText(profile).Contains("RocketMode=true"), "route enforcement preserves unrelated profile keys");

            // ===== 覆盖包「一次性」：包指纹没变就不重铺，用户后改的设置要留住 =====
            string shared2 = Path.Combine(root, "shared-oneshot");
            Directory.CreateDirectory(shared2);
            File.WriteAllText(Path.Combine(shared2, "a.addon64"), "v1");
            File.WriteAllText(Path.Combine(shared2, "FsrBridgeDepthAddon.ini"), "PreDlssEffects=1");
            List<AddonPackEntry> oneShotPlan = GameAddonPack.Plan(shared2, new Dictionary<string, string>(), (id, tag) => (IReadOnlyList<string>)[]);
            string oneShotPack = Path.Combine(root, "pack-oneshot");
            string identity1 = GameAddonPack.ComputeIdentity("hk4e_cn", oneShotPlan);
            GameAddonPack.Sync(oneShotPack, oneShotPlan, null, identity1);
            Check(GameAddonPack.ReadIdentity(oneShotPack) == identity1, "包标记记下包指纹");

            string userIni = Path.Combine(oneShotPack, "FsrBridgeDepthAddon.ini");
            File.WriteAllText(userIni, "PreDlssEffects=0");                          // 用户在游戏里改了插件设置
            File.WriteAllText(Path.Combine(oneShotPack, "user-own.txt"), "keep-me"); // 用户自己放的文件
            File.Delete(Path.Combine(oneShotPack, "a.addon64"));                     // 顺手模拟缺行
            GameAddonPack.Sync(oneShotPack, oneShotPlan, null, identity1);           // 下一次启动
            Check(File.ReadAllText(userIni) == "PreDlssEffects=0", "包没变：用户改的插件设置不被刷回");
            Check(File.Exists(Path.Combine(oneShotPack, "user-own.txt")), "包没变：用户自己放的文件保留");
            Check(File.ReadAllText(Path.Combine(oneShotPack, "a.addon64")) == "v1", "包没变：缺的行仍然会补回来");

            // 升级 / 降级：源变了 → 指纹变 → 包赢一次（重铺 + 清计划外的旧行）
            File.WriteAllText(Path.Combine(shared2, "a.addon64"), "v2-new");
            File.WriteAllText(Path.Combine(shared2, "FsrBridgeDepthAddon.ini"), "PreDlssEffects=1\r\nWriteBackBeforeNR=1\r\n");
            List<AddonPackEntry> upgradedPlan = GameAddonPack.Plan(shared2, new Dictionary<string, string>(), (id, tag) => (IReadOnlyList<string>)[]);
            string identity2 = GameAddonPack.ComputeIdentity("hk4e_cn", upgradedPlan);
            Check(identity2 != identity1, "源变了包指纹就变");
            GameAddonPack.Sync(oneShotPack, upgradedPlan, null, identity2);
            Check(File.ReadAllText(userIni).Contains("WriteBackBeforeNR=1"), "升级后包里的插件配置覆盖一次");
            Check(File.ReadAllText(Path.Combine(oneShotPack, "a.addon64")) == "v2-new", "升级后包文件重铺");
            Check(!File.Exists(Path.Combine(oneShotPack, "user-own.txt")), "升级时清掉计划外的旧行");
            Check(GameAddonPack.ReadIdentity(oneShotPack) == identity2, "包标记更新指纹");

            // ===== once 记账 + 包栅栏：升级/降级保留所有 once =====
            string fenceDir = Path.Combine(root, "fence");
            Directory.CreateDirectory(fenceDir);
            Check(PackOnceLedger.ApplyFence(fenceDir, "packA"), "新包第一次对齐栅栏");
            Check(!PackOnceLedger.Has(fenceDir, "step1"), "没记账的步骤要执行");
            PackOnceLedger.Mark(fenceDir, "step1");
            Check(PackOnceLedger.Has(fenceDir, "step1"), "记账后同一版本跳过");
            Check(!PackOnceLedger.ApplyFence(fenceDir, "packA"), "同一个包不清账");
            Check(PackOnceLedger.Has(fenceDir, "step1"), "同一个包仍然跳过");
            Check(PackOnceLedger.ApplyFence(fenceDir, "packB"), "包升级/降级只更新诊断栅栏");
            Check(PackOnceLedger.Has(fenceDir, "step1"), "升级后 once 记账仍然有效");
            PackOnceLedger.Mark(fenceDir, "step1");
            File.WriteAllText(Path.Combine(fenceDir, PackOnceLedger.FileName), "{\"deadbeef\":true}"); // 旧格式平铺 map
            Check(PackOnceLedger.ApplyFence(fenceDir, "packB"), "旧格式账本第一次对齐只更新栅栏");
            Check(PackOnceLedger.Has(fenceDir, "deadbeef"), "旧格式 flat 记账被保留且仍有效");

            // ===== 控制器配置解析：启动器必须和插件读同一个文件 =====
            string controllerDir = Path.Combine(root, "controller");
            Directory.CreateDirectory(controllerDir);
            File.WriteAllText(Path.Combine(controllerDir, "FsrBridgeDepthAddon.ini"), "PreDlssEffects=1");
            Check(GameAddonPack.ResolveControllerIni(controllerDir)?.EndsWith("FsrBridgeDepthAddon.ini", StringComparison.OrdinalIgnoreCase) == true,
                "只有基名配置时读基名");
            string scopedConfig = Path.Combine(controllerDir, "FsrBridgeDepthAddon.hk4e.ini");
            File.WriteAllText(scopedConfig, "PreDlssEffects=0");
            Check(GameAddonPack.ResolveControllerIni(controllerDir)?.EndsWith("FsrBridgeDepthAddon.hk4e.ini", StringComparison.OrdinalIgnoreCase) == true,
                "有游戏专属配置时读专属（与插件 resolve_controller_ini 一致）");
            Check(GameAddonPack.ResolveControllerIni(Path.Combine(root, "no-such-dir")) is null, "目录不存在返回 null");

            // 同族配置一律按内容比：专属那份写成同长度不同内容 + 更新的 mtime，也必须算过期
            string scopedTwin = Path.Combine(root, "scoped-twin.ini");
            File.WriteAllText(scopedTwin, "PreDlssEffects=1");
            File.SetLastWriteTimeUtc(scopedTwin, DateTime.UtcNow.AddHours(1));
            Check(!HysxFileLink.IsUpToDate(scopedConfig, scopedTwin), "游戏专属配置同大小不同内容也算过期");
        }
        finally { Directory.Delete(root, true); }
        return failures == 0 ? 0 : 1;
    }
}
