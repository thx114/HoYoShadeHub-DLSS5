using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>插件矩阵里的一格：某个游戏里的某个 addon</summary>
public sealed class GameAddonState
{
    public required AddonFileInfo File { get; init; }

    /// <summary>界面上显示的名字（addon 自己注册的内部名，猜不准就退到 slug）</summary>
    public required string DisplayName { get; init; }

    /// <summary>该游戏里是不是开着的（= 不在这个游戏的 DisabledAddons 里）</summary>
    public required bool Enabled { get; init; }

    /// <summary>在该游戏的 LoadFromDllMain 里</summary>
    public required bool LoadFromDllMain { get; init; }

    /// <summary>改了权限才能允许编辑 hook 点的插件（super-anus / renodx-dlss(ShortFuse)）</summary>
    public required bool IsHookPointCapable { get; init; }

    /// <summary>DLSS5 那一类插件（目录里 tags 带 dlss5）</summary>
    public required bool IsDlss5 { get; init; }

    /// <summary>它需要的 dll 齐不齐（缺必需的标红、缺推荐的标黄）</summary>
    public required AddonDllStatus DllStatus { get; init; }

    /// <summary>它要的运行时 dll 是不是也躺在**游戏目录**里（interposer 那条路径需要）</summary>
    public bool InGameDirectory { get; init; }

    public string FileName => File.FileName;

    public string? Slug => File.Slug;

    /// <summary>文件名括号里的版本优先，没有就退回 PE 版本资源（见 <see cref="AddonVersionResolver"/>）</summary>
    public required string? Version { get; init; }

    public string? Branch => File.Branch;

    /// <summary>文件被改名成 .addon64x —— 这是**全局**禁用，跟本游戏的开关无关</summary>
    public bool GloballyDisabled => File.IsRenamedDisabled;

    /// <summary>这个游戏的开关改了也没用（文件全局都加载不了）</summary>
    public bool CanToggle => !GloballyDisabled;
}

/// <summary>
/// 一个游戏的插件开关。
///
/// 层次（docs/GAMES-AND-INJECT.md §3）：
/// <list type="bullet">
/// <item>插件**文件**是全局一份 —— 在 HoYoShade 的 <c>reshade-shaders\Addons</c>；</item>
/// <item>启用/禁用是**每个游戏一份** —— 写各游戏 <c>ReShade.ini</c> 的 <c>[ADDON] DisabledAddons</c>。</item>
/// </list>
/// 所以这里所有写操作都落在**这个游戏**的 ini 上，插件文件一个字节都不动。
/// </summary>
public sealed class GamePluginService
{
    private readonly string? _nameCachePath;
    private readonly List<string> _candidateNames;
    private readonly AddonNameCache _nameCache;

    /// <summary>addon 文件名 → 这个插件的 tags（来自扩展目录，用来判断要不要 DLSS5 那套 dll）</summary>
    private readonly Func<string, IEnumerable<string>?>? _tagsOfAddon;

    /// <summary>扩展账本缓存：addon 文件名 → hub 装的时候那个版本 tag</summary>
    private Dictionary<string, string>? _ledgerVersions;

    public GamePluginService(
        GameEntry game,
        ShadeHost? host,
        string? nameCachePath = null,
        IEnumerable<string>? candidateNames = null,
        Func<string, IEnumerable<string>?>? tagsOfAddon = null)
    {
        _tagsOfAddon = tagsOfAddon;
        Game = game;
        Host = host;
        _nameCachePath = nameCachePath;
        _candidateNames = [.. candidateNames ?? []];
        _nameCache = string.IsNullOrWhiteSpace(nameCachePath) ? new AddonNameCache() : AddonNameCache.Load(nameCachePath!);
        Reload();
    }

    public GameEntry Game { get; }

    public ShadeHost? Host { get; }

    /// <summary>这个游戏读 ReShade.ini 的地方（= exe 旁边）</summary>
    public string? ReShadeIniPath => Game.ReShadeIniPath;

    /// <summary>
    /// 账本里这个 addon 文件对应的版本（&lt;HoYoShade&gt;\.hysx\installed.json 的
    /// <c>resolvedTag</c>，没有就用 <c>version</c>）。读不到就返回 null。
    /// </summary>
    private string? VersionFromLedger(AddonFileInfo file)
    {
        try
        {
            Dictionary<string, string> map = LedgerVersions();

            if (map.TryGetValue(file.FileName, out string? version))
            {
                return version;
            }

            // 全局禁用是改名（.addon64 ↔ .addon64x），账本里记的是原名
            string normalized = Path.GetFileNameWithoutExtension(file.FileName) + ".addon64";
            return map.TryGetValue(normalized, out string? byNormalized) ? byNormalized : null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> LedgerVersions()
    {
        if (_ledgerVersions is not null)
        {
            return _ledgerVersions;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? path = Host?.LedgerPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                InstalledExtensionLedger? ledger = JsonSerializer.Deserialize<InstalledExtensionLedger>(
                    File.ReadAllText(path), InstalledExtensionLedger.JsonOptions);

                foreach (InstalledExtension extension in ledger?.Extensions ?? [])
                {
                    string? version = string.IsNullOrWhiteSpace(extension.ResolvedTag) ? extension.Version : extension.ResolvedTag;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    foreach (InstalledExtension.InstalledExtensionFile installed in extension.Files)
                    {
                        string name = Path.GetFileName(installed.Path);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            map[name] = version!.Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // 账本读不出来不影响别的功能
        }

        _ledgerVersions = map;
        return map;
    }

    public bool HasReShadeIni => Profile is not null;

    /// <summary>该游戏的 ini；没有 ini（或读坏了）时为 null</summary>
    public ReShadeProfile? Profile { get; private set; }

    /// <summary>ini 存在但读不了时的原因</summary>
    public string? ProfileError { get; private set; }

    /// <summary>插件真身所在目录：优先用这个游戏 ini 里的 AddonPath，兜底全局宿主</summary>
    public string? AddonDirectory { get; private set; }

    /// <summary>重新读盘</summary>
    public void Reload()
    {
        Profile = null;
        ProfileError = null;

        if (ReShadeIniPath is { } iniPath && File.Exists(iniPath))
        {
            try
            {
                Profile = ReShadeProfile.Load(iniPath);
                LearnNames();
            }
            catch (Exception ex)
            {
                ProfileError = ex.Message;
                Profile = null;
            }
        }

        AddonDirectory = Profile?.ResolveAddonDirectory()
                         ?? (Host is null ? null : Host.AddonsPath);
    }

    /// <summary>把 ini 里学到的 addon 内部名记下来（下次给别的游戏写时就写对了）</summary>
    public int LearnNames()
    {
        if (Profile is null)
        {
            return 0;
        }

        int learned = _nameCache.LearnFrom(Profile);
        if (learned > 0 && !string.IsNullOrWhiteSpace(_nameCachePath))
        {
            _nameCache.Save(_nameCachePath!);
        }

        return learned;
    }

    #region 读

    /// <summary>该游戏插件目录里的所有 addon + 它们在这个游戏里的开关状态</summary>
    public List<GameAddonState> GetAddons()
    {
        if (AddonDirectory is null)
        {
            return [];
        }

        List<AddonFileInfo> files = AddonFileInfo.ScanDirectory(AddonDirectory);
        var result = new List<GameAddonState>(files.Count);

        // 运行时 dll 可能在**两个**目录里：插件目录（ReShade 扫的）和游戏目录
        // （neural interposer 要求 nvngx_dlssnr.dll 躺在游戏 exe 旁边）。只查插件目录会误报「缺 dll」。
        List<string?> dllFileNames = CollectRuntimeFileNames(AddonDirectory);

        foreach (AddonFileInfo file in files)
        {
            result.Add(new GameAddonState
            {
                File = file,
                DisplayName = ResolveName(file),
                // 版本要从**盘上真实那个文件**读：全局禁用是改名（.addon64 → .addon64x），
                // 按 file.FileName 拼路径会找不到文件，于是「版本未知」（用户报过：全局页有版本、每游戏页没有）
                // 文件名和 PE 都读不出来时，退回**扩展账本**里 hub 装的时候那个版本 tag
                // （有些 addon 文件不带版本、PE 里写的又是时间戳，只有账本知道装的是哪一版）
                Version = AddonVersionResolver.Resolve(ResolveActualPath(file), file.Version)
                          ?? VersionFromLedger(file),
                Enabled = Profile?.IsDisabled(file.FileName) != true,
                LoadFromDllMain = Profile?.IsLoadFromDllMain(file.FileName) == true,
                IsHookPointCapable = file.IsHookPointCapable,
                IsDlss5 = IsDlss5Addon(file.FileName),
                DllStatus = AddonDllChecker.CheckFiles(dllFileNames!, TagsOf(file.FileName)),
                InGameDirectory = HasRuntimeFileInGameDirectory(file, dllFileNames),
            });
        }

        return result;
    }

    /// <summary>插件目录 + 游戏目录里所有文件名（给「缺 dll」检查用）</summary>
    private static List<string?> CollectRuntimeFileNames(string? addonsDirectory)
    {
        var names = new List<string?>();

        void AddDirectory(string? directory)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    names.AddRange(Directory.EnumerateFiles(directory).Select(Path.GetFileName));
                }
            }
            catch
            {
                // 目录读不了就当没有
            }
        }

        AddDirectory(addonsDirectory);
        return names;
    }

    /// <summary>盘上真实那个文件（被全局禁用时是 <c>.addon64x</c>）</summary>
    private string? ResolveActualPath(AddonFileInfo file)
    {
        if (AddonDirectory is null)
        {
            return null;
        }

        string path = Path.Combine(AddonDirectory, file.FileName);
        if (File.Exists(path))
        {
            return path;
        }

        string disabled = Path.Combine(AddonDirectory, AddonFileSwitcher.DisabledNameOf(file.FileName));
        return File.Exists(disabled) ? disabled : path;
    }

    /// <summary>这条 addon 要的运行时 dll 是不是躺在游戏目录里（interposer 需要）</summary>
    private bool HasRuntimeFileInGameDirectory(AddonFileInfo file, List<string?> addonDirectoryFiles)
    {
        if (Game.GameDirectory is not { } gameDirectory || !Directory.Exists(gameDirectory))
        {
            return false;
        }

        IReadOnlyList<DllRequirement> requirements = DlssDllRequirements.For(TagsOf(file.FileName));
        if (requirements.Count == 0)
        {
            return false;
        }

        try
        {
            var names = new HashSet<string>(
                Directory.EnumerateFiles(gameDirectory).Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);
            return requirements.SelectMany(r => r.Files).All(names.Contains);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>显示名：学到的 &gt; 已知映射 &gt; PE 版本资源 &gt; 二进制候选名 &gt; slug</summary>
    public string ResolveName(AddonFileInfo file) => AddonNameResolver.Resolve(
        addonFilePath: AddonDirectory is null ? null : Path.Combine(AddonDirectory, file.FileName),
        fileName: file.FileName,
        learnedName: _nameCache.Get(file.FileName),
        knownName: AddonNameResolver.GetKnownInternalName(file.Slug),
        candidateNames: _candidateNames,
        fallback: file.Slug);

    /// <summary>这个 addon 文件的 tags（扩展目录里配的）；认不出来就当没有</summary>
    public IEnumerable<string>? TagsOf(string addonFileName)
    {
        try
        {
            return _tagsOfAddon?.Invoke(addonFileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是不是 DLSS5 那一类插件</summary>
    public bool IsDlss5Addon(string addonFileName) => DlssDllRequirements.IsDlss5(TagsOf(addonFileName));

    /// <summary>
    /// 把「开着但没勾 LoadFromDllMain」的 DLSS5 插件补上。
    /// 用户要求：DLSS5 类插件只要启用了就默认从 DllMain 加载。
    /// </summary>
    /// <returns>补了几个</returns>
    public int SyncDlss5LoadFromDllMain()
    {
        if (Profile is null)
        {
            return 0;
        }

        int added = 0;

        foreach (AddonFileInfo file in AddonFileInfo.ScanDirectory(AddonDirectory ?? string.Empty))
        {
            if (file.IsRenamedDisabled || !IsDlss5Addon(file.FileName))
            {
                continue;
            }

            if (Profile.IsDisabled(file.FileName) || Profile.IsLoadFromDllMain(file.FileName))
            {
                continue;
            }

            Profile.AddLoadFromDllMain(file.FileName);
            added++;
        }

        if (added > 0)
        {
            Profile.Save();
        }

        return added;
    }

    /// <summary>hook 点能不能改：装了 super-anus 或 renodx-dlss(ShortFuse) 才行</summary>
    public bool CanEditHookPoint(IEnumerable<GameAddonState>? addons = null) =>
        (addons ?? GetAddons()).Any(a => a.IsHookPointCapable);

    /// <summary>键不存在也返回 0（= off）</summary>
    public int GetHookPoint() => Profile?.GetHookPoint() ?? 0;

    #endregion

    #region 写

    /// <summary>
    /// 按游戏开关一个插件（写/删该游戏 ini 的 DisabledAddons）。
    /// 打开的是 DLSS5 类插件时，顺带把它加进 <c>LoadFromDllMain</c>
    /// （用户要求：DLSS5 插件只要启用就默认从 DllMain 加载）。
    /// 打开神经插帧器时还要把 <c>nvngx_dlssnr.dll</c> 复制到游戏目录（见 <see cref="EnsureInterposerDlls"/>）。
    /// </summary>
    public bool SetAddonEnabled(string addonFileName, bool enabled)
    {
        if (Profile is null || string.IsNullOrWhiteSpace(addonFileName))
        {
            return false;
        }

        if (enabled)
        {
            Profile.EnableAddon(addonFileName);

            if (IsDlss5Addon(addonFileName))
            {
                Profile.AddLoadFromDllMain(addonFileName);
            }

            if (IsNeuralInterposer(addonFileName))
            {
                EnsureInterposerDlls();
            }
        }
        else
        {
            // 禁用要写 `名字@文件名`：**@ 必须存在**，名字只是显示用（猜错不影响是否生效）
            Profile.DisableAddon(addonFileName, ResolveNameFor(addonFileName));

            // 禁用之后就不该再从 DllMain 加载它（用户要求：暂时去掉；重新启用会再加回来）
            Profile.RemoveLoadFromDllMain(addonFileName);
        }

        Profile.Save();
        return true;
    }

    private string ResolveNameFor(string addonFileName)
    {
        AddonFileInfo info = AddonFileInfo.Parse(addonFileName) ?? new AddonFileInfo
        {
            FileName = addonFileName,
            Slug = Path.GetFileNameWithoutExtension(addonFileName),
            IsAddon = true,
        };

        return ResolveName(info);
    }

    /// <summary>写 LoadFromDllMain（**空槽位保留**，不能整串重排）</summary>
    public bool SetLoadFromDllMain(string addonFileName, bool value)
    {
        if (Profile is null || string.IsNullOrWhiteSpace(addonFileName))
        {
            return false;
        }

        if (value)
        {
            Profile.AddLoadFromDllMain(addonFileName);
        }
        else
        {
            Profile.RemoveLoadFromDllMain(addonFileName);
        }

        Profile.Save();
        return true;
    }

    #region 游戏目录里要放的运行时 dll

    /// <summary>神经插帧器的 slug —— 它要从**游戏目录**加载 <c>nvngx_dlssnr.dll</c></summary>
    public const string NeuralInterposerSlug = "renodx-neural-interposer-nvngx";

    /// <summary>interposer 要的运行时 dll（从插件目录复制到游戏 exe 旁边）</summary>
    public static readonly string[] InterposerDlls = ["nvngx_dlssnr.dll"];

    /// <summary>这个 addon 是不是那个神经插帧器</summary>
    public static bool IsNeuralInterposer(string? addonFileName)
    {
        if (string.IsNullOrWhiteSpace(addonFileName))
        {
            return false;
        }

        string? slug = AddonFileInfo.Parse(addonFileName)?.Slug ?? Path.GetFileNameWithoutExtension(addonFileName);
        return slug is not null
               && slug.StartsWith(NeuralInterposerSlug, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 interposer 要的 dll 从插件目录复制到**游戏目录**（用户要求：
    /// 「renodx-neural-interposer-nvngx.dll 是需求把 nrdll 放在游戏目录，他如果启用就把文件复制过去」）。
    /// 已经有一份一样大的就不重复拷（游戏目录里那份可能被玩家自己替换过，别乱覆盖）。
    /// </summary>
    /// <returns>这次复制过去的文件名</returns>
    public List<string> EnsureInterposerDlls()
    {
        var copied = new List<string>();

        try
        {
            if (AddonDirectory is null || Game.GameDirectory is not { } gameDirectory
                || !Directory.Exists(gameDirectory))
            {
                return copied;
            }

            foreach (string dll in InterposerDlls)
            {
                string source = Path.Combine(AddonDirectory, dll);
                if (!File.Exists(source))
                {
                    continue;
                }

                string target = Path.Combine(gameDirectory, dll);
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
                {
                    continue;
                }

                File.Copy(source, target, overwrite: true);
                copied.Add(dll);
            }
        }
        catch
        {
            // 复制失败不该让开关操作失败
        }

        return copied;
    }

    #endregion


    /// <summary>写 hook 点；0 = off（写 0，不删键）</summary>
    public bool SetHookPoint(int value)
    {
        if (Profile is null || !CanEditHookPoint())
        {
            return false;
        }

        Profile.SetHookPoint(value);
        Profile.Save();
        return true;
    }

    #endregion
}
