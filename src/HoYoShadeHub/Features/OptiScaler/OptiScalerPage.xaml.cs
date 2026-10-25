using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.OptiScaler;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.Features.ViewHost;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.OptiScaler;

/// <summary>
/// OptiScaler 页里的一张「来源」卡片：这个来源在本地装了哪几版，
/// 用卡片里的「切换版本」下拉给这个游戏选一版（用户要求第 2 条：不要再套娃）。
/// </summary>
public sealed partial class OptiScalerBuildItemViewModel : ObservableObject
{
    private readonly Dictionary<string, OptiScalerBuild> _buildsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labelToId = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressBuildChange;

    /// <summary>来源 id（配置的分支限制靠它判断）</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>来源显示名（目录里配的名字，没有就用 id）</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>来源标签（hdr / dlss5 …）</summary>
    public string TagsText { get; init; } = string.Empty;

    /// <summary>
    /// 卡片标题：来源名 + 版本。
    /// 现在**一个构建一张卡片**（用户要的「多个 opt 单选、点卡片就换」），
    /// 所以同一个来源的多版必须靠版本号区分。
    /// </summary>
    public string Title
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(SourceName) ? SourceId : SourceName;
            string? version = CurrentBuild?.Version;
            return string.IsNullOrWhiteSpace(version) ? name : $"{name}  ·  {version}";
        }
    }

    /// <summary>「切换版本」下拉的选项（这个来源装在本地的版本，新 → 旧）</summary>
    public ObservableCollection<string> BuildOptions { get; } = [];

    [ObservableProperty]
    private string? selectedBuildOption;

    /// <summary>这个游戏现在用的就是这个来源的某一版 → 卡片标「使用中」</summary>
    private bool _isInUse;
    public bool IsInUse
    {
        get => _isInUse;
        set
        {
            if (SetProperty(ref _isInUse, value))
            {
                OnPropertyChanged(nameof(InUseVisibility));
            }
        }
    }

    public Visibility InUseVisibility => IsInUse ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前选中的构建（下拉里那一个）</summary>
    public OptiScalerBuild? CurrentBuild
        => SelectedBuildOption is { Length: > 0 } label && _labelToId.TryGetValue(label, out string? id)
           && _buildsById.TryGetValue(id, out OptiScalerBuild? build)
            ? build
            : null;

    /// <summary>当前构建 id（换版本 / 配配置按它记）</summary>
    public string CurrentBuildId => CurrentBuild?.Id ?? string.Empty;

    public GameId? NrGameId { get; init; }

    public string NrVersionText => "NR：" + (NrGameId is { } game ? AppConfig.GetGameNrVersion(game, "opti") ?? "构建默认" : "构建默认");

    public void RefreshNrChoice() => OnPropertyChanged(nameof(NrVersionText));

    public string? DllPath
    {
        get
        {
            string? original = CurrentBuild?.DllPath;
            if (original is null || NrGameId is null) return original;
            string isolated = Path.Combine(NrRuntimeLibrary.OptiDirectory(AppConfig.CacheRoot,
                NrGameId.GameBiz.Value, CurrentBuildId), Path.GetFileName(original));
            return File.Exists(isolated) ? isolated : original;
        }
    }

    public string Detail => CurrentBuild is not { } build
        ? "这个来源没有可用构建"
        : (string.IsNullOrWhiteSpace(build.DllPath) ? $"目录：{build.Directory}" : $"注入：{build.DllPath}");

    public Visibility DllMissingVisibility =>
        string.IsNullOrWhiteSpace(DllPath) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>这个构建的目录（套配置要往它的 profiles\ 里写；没 dll 就没有）</summary>
    public string? BuildDirectory
        => string.IsNullOrWhiteSpace(DllPath) ? null : Path.GetDirectoryName(DllPath);

    /// <summary>卡片上显示的「当前文件」说明（profiles\<游戏>.ini 是否存在 / 何时改的）</summary>
    public string CurrentProfileText { get; set; } = string.Empty;

    /// <summary>换版本时回调页面（页面用 _loading 屏蔽加载期的那次）</summary>
    internal Action<OptiScalerBuildItemViewModel, string?>? BuildChanged { get; set; }

    /// <summary>把本地装到的构建铺进下拉并选中指定的那个（选过的不属于这个来源就选最新的）</summary>
    public void SetBuilds(IReadOnlyList<OptiScalerBuild> builds, string? selectedId)
    {
        _buildsById.Clear();
        _labelToId.Clear();
        BuildOptions.Clear();

        string? match = null;

        foreach (OptiScalerBuild build in builds)
        {
            string label = string.IsNullOrWhiteSpace(build.AssetName)
                ? build.Version
                : $"{build.Version}  ·  {build.AssetName}";

            // 同名标签（同一版本不同资产）加个后缀，保证唯一
            string unique = label;
            int n = 2;
            while (_labelToId.ContainsKey(unique))
            {
                unique = label + " (" + n++ + ")";
            }

            _buildsById[build.Id] = build;
            _labelToId[unique] = build.Id;
            BuildOptions.Add(unique);

            if (string.Equals(build.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                match = unique;
            }
        }

        IsInUse = builds.Any(b => string.Equals(b.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        _suppressBuildChange = true;
        SelectedBuildOption = match ?? BuildOptions.FirstOrDefault();
        _suppressBuildChange = false;

        NotifyBuildDerived();
    }

    partial void OnSelectedBuildOptionChanged(string? value)
    {
        NotifyBuildDerived();

        if (_suppressBuildChange)
        {
            return;
        }

        BuildChanged?.Invoke(this, CurrentBuildId);
    }

    private void NotifyBuildDerived()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CurrentBuild));
        OnPropertyChanged(nameof(CurrentBuildId));
        OnPropertyChanged(nameof(DllPath));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(DllMissingVisibility));
        OnPropertyChanged(nameof(BuildDirectory));
    }

    /// <summary>「当前配置」下拉的选项：不套配置 + 本地配置名</summary>
    public List<string> PresetOptions { get; set; } = [];

    private string? _currentPreset;

    /// <summary>当前选的配置（TwoWay 绑下拉，切换即套用）</summary>
    public string? CurrentPreset
    {
        get => _currentPreset;
        set
        {
            if (SetProperty(ref _currentPreset, value))
            {
                PresetChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>下拉变化时回调页面（页面用 _loading 屏蔽加载期的那次）</summary>
    internal Action<OptiScalerBuildItemViewModel, string?>? PresetChanged { get; set; }

    // ===================== 卡片扩展配置（点右下角箭头展开） =====================

    [ObservableProperty]
    private bool expanded;

    partial void OnExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandedVisibility));
        OnPropertyChanged(nameof(ExpandButtonVisibility));
        OnPropertyChanged(nameof(CollapseButtonVisibility));
        if (value) QuickReloadRequested?.Invoke(this);
    }

    public Visibility ExpandedVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandButtonVisibility
        => !Expanded && HasQuickConfig ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollapseButtonVisibility
        => Expanded && HasQuickConfig ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>这个卡片能不能展开配置（没有可用构建目录就不行）</summary>
    public bool HasQuickConfig => !string.IsNullOrWhiteSpace(BuildDirectory);

    /// <summary>展开时通知页面重新读当前值</summary>
    internal Action<OptiScalerBuildItemViewModel>? QuickReloadRequested { get; set; }

    /// <summary>扩展配置项（20 项，静态清单见 <see cref="OptiScalerQuickOptions"/>）</summary>
    public ObservableCollection<OptiQuickItemViewModel> QuickItems { get; } = [];
}

/// <summary>卡片扩展配置里的一行（三态 / 选项 / 数字三种形态，按描述子切换控件）</summary>
public sealed partial class OptiQuickItemViewModel : ObservableObject
{
    private readonly OptiQuickDescriptor _descriptor;
    private readonly Func<OptiQuickDescriptor, string, bool> _commit;
    private bool _suppress;
    private string _lastValue = "auto";

    internal OptiQuickItemViewModel(OptiQuickDescriptor descriptor, Func<OptiQuickDescriptor, string, bool> commit)
    {
        _descriptor = descriptor;
        _commit = commit;
    }

    public OptiQuickDescriptor Descriptor => _descriptor;
    public string Label => _descriptor.Label;
    public string ToolTip => _descriptor.ToolTip;
    public IReadOnlyList<OptiQuickChoice> Choices => _descriptor.Choices ?? [];
    public double NumberMinimum => _descriptor.NumberMin;
    public double NumberMaximum => _descriptor.NumberMax;

    public Visibility TriVisibility => _descriptor.Kind == OptiQuickKind.TriState ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptionVisibility => _descriptor.Kind == OptiQuickKind.Option ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NumberVisibility => _descriptor.Kind == OptiQuickKind.Number ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>三态下拉的当前下标（0=auto 1=开 2=关）</summary>
    [ObservableProperty]
    private int triIndex;

    partial void OnTriIndexChanged(int value) => Commit(value switch { 1 => "true", 2 => "false", _ => "auto" });

    /// <summary>选项下拉的当前下标</summary>
    [ObservableProperty]
    private int optionIndex;

    partial void OnOptionIndexChanged(int value)
    {
        if (value >= 0 && value < Choices.Count) Commit(Choices[value].Value);
    }

    /// <summary>数字输入（NaN = auto / 不限制）</summary>
    [ObservableProperty]
    private double numberValue = double.NaN;

    partial void OnNumberValueChanged(double value)
    {
        if (double.IsFinite(value))
        {
            double rounded = _descriptor.NumberInteger ? Math.Round(value) : value;
            rounded = Math.Clamp(rounded, _descriptor.NumberMin, _descriptor.NumberMax);
            Commit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rounded}"));
        }
        else
        {
            Commit("auto");
        }
    }

    private void Commit(string value)
    {
        if (_suppress) return;
        if (_commit(_descriptor, value)) _lastValue = value;
        else LoadValue(_lastValue);
    }

    /// <summary>把 ini 里的当前值填进控件（不动提交）</summary>
    public void LoadValue(string iniValue)
    {
        _lastValue = iniValue;
        _suppress = true;
        try
        {
            switch (_descriptor.Kind)
            {
                case OptiQuickKind.TriState:
                    TriIndex = iniValue.Trim().ToLowerInvariant() switch
                    {
                        "true" or "1" or "yes" => 1,
                        "false" or "0" or "no" => 2,
                        _ => 0,
                    };
                    break;
                case OptiQuickKind.Option:
                    int found = Choices
                        .Select((c, i) => (c, i))
                        .Where(t => string.Equals(t.c.Value, iniValue.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.i)
                        .FirstOrDefault(-1);
                    OptionIndex = found;
                    break;
                case OptiQuickKind.Number:
                    NumberValue = double.TryParse(iniValue.Trim(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                        out double parsed)
                        ? parsed
                        : double.NaN;
                    break;
            }
        }
        finally
        {
            _suppress = false;
        }
    }
}

/// <summary>
/// 「OptiScaler」页（左侧一级导航）：**只选这个游戏用哪个构建**（单选）。
///
/// <para>
/// 总开关（全局开/关）和下载 / 删除都在「全局插件 → OptiScaler」里 —— 跟插件、模块一套风格。
/// 没给这个游戏选过构建就不会注入它。
/// </para>
/// </summary>
public sealed partial class OptiScalerPage : PageBase
{
    private readonly ILogger<OptiScalerPage> _logger = AppConfig.GetLogger<OptiScalerPage>();
    private readonly ObservableCollection<OptiScalerBuildItemViewModel> _items = [];

    private GameId? _gameId;
    private bool _loading;
    private readonly LatestAsyncOperation _loadOperation = new();

    public OptiScalerPage()
    {
        InitializeComponent();

        // 源列表错峰入场（和顶部游戏栏同一套 500ms + 45ms）
        OptiScalerSourceList.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as GameId;

    }

    protected override void OnLoaded() => Load();

    protected override void OnUnloaded()
    {
        _loadOperation.Cancel();
        _loading = false;
    }

    private OptiScalerLibrary? CreateLibrary()
    {
        string root = AppConfig.OptiScalerRootPath;
        return root.Length == 0 ? null : new OptiScalerLibrary(root);
    }

    private sealed record BuildSnapshot(OptiScalerBuild Build, OptiScalerSource? Source,
        List<string> Presets, string ProfileText);

    private async void Load()
    {
        if (PageCancellationToken.IsCancellationRequested) return;
        using var operation = _loadOperation.Begin(PageCancellationToken);
        GameId? gameId = _gameId;
        _loading = true;
        try
        {
            _items.Clear();

            // 注入时机（OptiScaler，按游戏）
            UpdateOptiInjectDelayUi();

            string gameName = _gameId?.GameBiz.Value ?? string.Empty;
            TextBlock_Game.Text = string.IsNullOrWhiteSpace(gameName) ? "（没选游戏）" : $"这个游戏：{gameName}";

            OptiScalerLibrary? library = CreateLibrary();
            string? selectedId = gameId is null ? null : AppConfig.GetSelectedOptiScalerId(gameId);
            string? gameKey = gameId?.GameBiz.ToString();
            bool migrate = gameId is not null && string.IsNullOrWhiteSpace(selectedId)
                && AppConfig.GetUseOptiScalerLaunchOption(gameId);
            TextBlock_Status.Text = "正在读取本地 OptiScaler 构建…";
            var snapshot = await Task.Run(() =>
            {
                var rows = new List<BuildSnapshot>();
                var catalog = OptiScalerCatalog.LoadFile(RemoteCatalogService.OptiScalerCachePath);
                var presets = OptiScalerPresets.List()
                    .Select(name => (Name: name, Sources: OptiScalerPresets.GetSources(name))).ToList();
                foreach (var build in library?.List(includeDirectorySize: false, cancellationToken: operation.Token) ?? [])
                {
                    operation.Token.ThrowIfCancellationRequested();
                    string? profile = build.DllPath is { } dll && gameKey is not null
                        ? OptiScalerProfiles.ProfilePath(Path.GetDirectoryName(dll)!, gameKey) : null;
                    string profileText;
                    try
                    {
                        profileText = profile is null ? "当前文件：这个构建没有可用目录"
                            : File.Exists(profile)
                                ? $"当前文件：profiles\\{Path.GetFileName(profile)}  {File.GetLastWriteTime(profile):yyyy-MM-dd HH:mm}"
                                : "当前文件：还没有 profiles 配置（首次启动游戏时生成）";
                    }
                    catch { profileText = "当前文件：读不到"; }
                    rows.Add(new(build, OptiScalerCatalog.Find(catalog, build.SourceId),
                        [OptiScalerPresets.CurrentLabel, .. presets.Where(p => p.Sources.Count == 0
                            || p.Sources.Contains(build.SourceId, StringComparer.OrdinalIgnoreCase)).Select(p => p.Name)], profileText));
                }
                var legacy = migrate ? library?.GetSelected(includeDirectorySize: false, cancellationToken: operation.Token) : null;
                return (Rows: rows, Legacy: legacy);
            }, operation.Token);
            if (!operation.IsCurrent || !ReferenceEquals(gameId, _gameId)) return;
            if (migrate && gameId is not null)
            {
                OptiScalerMigration.Run(gameId, snapshot.Legacy);
                selectedId = AppConfig.GetSelectedOptiScalerId(gameId);
            }

            if (library is not null)
            {
                // 一个构建一张卡片，点一下就是单选（用户：以前多个 opt 单选、点卡片就换）。
                // 不再按来源分组折叠成一个下拉 —— 那样卡片本身点不动，用户找不到地方选。
                // （List() 本身就是新装的在前，顺序照旧）
                foreach (BuildSnapshot row in snapshot.Rows)
                {
                    OptiScalerBuild build = row.Build;
                    OptiScalerSource? source = row.Source;

                    var item = new OptiScalerBuildItemViewModel
                    {
                        NrGameId = gameId,
                        SourceId = build.SourceId,
                        SourceName = source?.Name ?? build.SourceId,
                        TagsText = source?.Tags is { Length: > 0 } tags ? string.Join(" ", tags) : string.Empty,
                    };

                    item.SetBuilds([build], selectedId);
                    item.BuildChanged = OnBuildChanged;
                    EnsureQuickItems(item);
                    AttachPreset(item, row.Presets, row.ProfileText);
                    ReloadQuickValues(item);
                    _items.Add(item);
                }
            }

            InitializeDllNameBox();

            TextBlock_Count.Text = _items.Count == 0
                ? "一个构建都没有 —— 先到左下角「全局插件 → OptiScaler」里下一个。"
                : $"共 {_items.Count} 个构建，点卡片选一个。";

            OptiScalerBuildItemViewModel? inUse = _items.FirstOrDefault(i => i.IsInUse);
            TextBlock_Status.Text = inUse is null
                ? "这个游戏还没选构建 —— 在卡片里「切换版本」选一个才会注入。"
                : $"这个游戏用：{inUse.Title} · {inUse.SelectedBuildOption}";
            MotionAnimations.PlayListEntrance(OptiScalerSourceList);
            _logger.LogInformation("OptiScaler page loaded {Count} builds for {Game}", _items.Count, gameName);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load OptiScaler page");
            if (operation.IsCurrent) TextBlock_Status.Text = "读取 OptiScaler 构建失败，请检查目录后刷新。";
        }
        finally
        {
            if (operation.IsCurrent) _loading = false;
        }
    }

    private void Button_Refresh_Click(object sender, RoutedEventArgs e) => Load();

    /// <summary>「注入时机」输入框：0~30 秒，留空 = 跟随全局默认（按游戏）</summary>
    private void UpdateOptiInjectDelayUi()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            int? current = _gameId is null ? null : AppConfig.GetOptiScalerInjectDelaySeconds(_gameId.GameBiz);
            NumberBox_OptiInjectDelay.Value = current is int value ? value : double.NaN;
            NumberBox_OptiInjectDelay.PlaceholderText = _gameId is null
                ? "跟随全局"
                : $"跟随全局（{AppConfig.GetDefaultInjectionDelaySeconds(_gameId.GameBiz)} 秒）";
            NumberBox_OptiInjectDelay.IsEnabled = _gameId is not null;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void NumberBox_OptiInjectDelay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _gameId is null)
        {
            return;
        }

        int? seconds = double.IsNaN(args.NewValue)
            ? null
            : (int)Math.Clamp(Math.Round(args.NewValue), 0, AppConfig.MaxInjectionWarmupSeconds);

        AppConfig.SetOptiScalerInjectDelaySeconds(_gameId.GameBiz, seconds);

        TextBlock_Status.Text = seconds is null
            ? "OptiScaler 注入时机：跟随全局默认。"
            : seconds <= 0
                ? "OptiScaler 注入：立即注入（不等）。"
                : $"OptiScaler 注入：等游戏起来 {seconds} 秒后再注入。";
    }

    /// <summary>
    /// 点整张卡片 = 这个游戏改用这一版（单选，和以前那个 ListView 单选是一个意思）。
    /// 卡片里的下拉 / 按钮自己会吃掉点击，所以只有点卡片空白处才会走到这里。
    /// </summary>
    private void ComboBox_Nr_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo && _gameId is { } game)
            NrVersionPicker.Fill(combo, AppConfig.GetGameNrVersion(game, "opti"));
    }

    private void ComboBox_Nr_DropDownOpened(object sender, object e) => ComboBox_Nr_Loaded(sender, new RoutedEventArgs());

    private async void ComboBox_Nr_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not false || combo.SelectedItem is not ComboBoxItem { IsEnabled: true } selected) return;
        if (_gameId is not { } game || _loading || sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item }
            || item.CurrentBuild?.DllPath is not { } dll) return;
        string? version = selected.Tag as string;
        if (string.Equals(version, AppConfig.GetGameNrVersion(game, "opti"), StringComparison.OrdinalIgnoreCase)) return;
        combo.IsEnabled = false;
        try
        {
            if (version is not null || Directory.Exists(NrRuntimeLibrary.OptiDirectory(AppConfig.CacheRoot, game.GameBiz.Value, item.CurrentBuildId)))
                await Task.Run(() => NrRuntimeLibrary.PrepareOpti(AppConfig.CacheRoot, game.GameBiz.Value, item.CurrentBuildId, dll, version));
            if (_gameId?.GameBiz != game.GameBiz) return;
            AppConfig.SetGameNrVersion(game, "opti", version);
            OnBuildChanged(item, item.CurrentBuildId);
            foreach (var row in _items) row.RefreshNrChoice();
            NrVersionPicker.RefreshInline(this, version);
            TextBlock_Status.Text = "本游戏 OptiScaler NR：" + (version ?? "构建默认") + "（重启游戏生效）";
        }
        catch (Exception ex) { TextBlock_Status.Text = "NR 版本切换失败：" + ex.Message; }
        finally
        {
            NrVersionPicker.Fill(combo, AppConfig.GetGameNrVersion(game, "opti"));
            combo.IsEnabled = item.HasQuickConfig;
        }
    }

    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_loading || _gameId is null
            || sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item })
        {
            return;
        }

        OnBuildChanged(item, item.CurrentBuildId);
        e.Handled = true;
    }

    /// <summary>某张卡片被选中（点卡片 / 从下拉里换）：给这个游戏选这个构建</summary>
    private void OnBuildChanged(OptiScalerBuildItemViewModel item, string? buildId)
    {
        if (_loading || _gameId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(buildId))
        {
            return;
        }

        try
        {
            string? nr = AppConfig.GetGameNrVersion(_gameId, "opti");
            if ((nr is not null || Directory.Exists(NrRuntimeLibrary.OptiDirectory(AppConfig.CacheRoot, _gameId.GameBiz.Value, buildId)))
                && item.CurrentBuild?.DllPath is { } original)
                NrRuntimeLibrary.PrepareOpti(AppConfig.CacheRoot, _gameId.GameBiz.Value, buildId, original, nr);
        }
        catch (Exception ex) { TextBlock_Status.Text = "NR 加载目录准备失败：" + ex.Message; return; }

        AppConfig.SetOptiScalerVersion(_gameId, buildId);

        // 在这里选构建 = 明确要用它 —— 顺手把启动选项里的「启用OptiScaler」也勾上
        AppConfig.SetUseOptiScalerLaunchOption(_gameId, true);

        // 只有真正选中的那张卡片算「使用中」
        foreach (OptiScalerBuildItemViewModel other in _items)
        {
            other.IsInUse = ReferenceEquals(other, item);
        }

        TextBlock_Status.Text = $"这个游戏改用：{item.Title} · {item.SelectedBuildOption}";

        // 「当前配置」按构建走，换版本要重新挂一遍
        AttachPreset(item);
        ReloadQuickValues(item);

        // 「全局插件 → OptiScaler」那边也用 state.json 里那个「当前构建」，同步一下省得两边不一致
        try
        {
            CreateLibrary()?.Select(buildId);
        }
        catch
        {
            // 同步失败不影响这边的选择
        }
    }

    private void Button_OpenLibrary_Click(object sender, RoutedEventArgs e)
        => OpenFolder(AppConfig.OptiScalerRootPath);

    private void OpenFolder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open OptiScaler folder {Path}", path);
        }
    }

    #region 配置配置

    private void AttachPreset(OptiScalerBuildItemViewModel item, List<string>? options = null, string? profileText = null)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            // 只放「不限分支」或「明确允许这个分支」的配置（例如 [ada] 那条只给 mfg-ada）
            item.PresetOptions = options ??
            [
                OptiScalerPresets.CurrentLabel,
                .. OptiScalerPresets.List().Where(p => OptiScalerPresets.AllowsSource(p, item.SourceId)),
            ];
            item.PresetChanged = OnPresetChanged;

            string saved = _gameId is null
                ? string.Empty
                : OptiScalerPresets.SelectedPreset(_gameId.GameBiz.ToString(), item.BuildDirectory);

            item.CurrentPreset = !string.IsNullOrWhiteSpace(saved)
                                 && !string.Equals(saved, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal)
                                 && item.PresetOptions.Contains(saved)
                ? saved
                : OptiScalerPresets.CurrentLabel;

            if (profileText is not null)
            {
                item.CurrentProfileText = profileText;
                return;
            }

            // 把「这个游戏现在的 profiles 配置」写在卡片上  profiles 是按游戏分的，换游戏就换一份
            string? profilePath = item.BuildDirectory is { } buildDir && _gameId is { } gameId
                ? OptiScalerProfiles.ProfilePath(buildDir, gameId.GameBiz.ToString())
                : null;

            try
            {
                item.CurrentProfileText = profilePath is null
                    ? "当前文件：这个构建没有可用目录"
                    : File.Exists(profilePath)
                        ? $"当前文件：profiles\\{Path.GetFileName(profilePath)}  {File.GetLastWriteTime(profilePath):yyyy-MM-dd HH:mm}"
                        : "当前文件：还没有 profiles 配置（首次启动游戏时生成）";
            }
            catch
            {
                item.CurrentProfileText = "当前文件：读不到";
            }
        }
        finally { _loading = wasLoading; }
    }

    #region 卡片扩展配置（20 项快捷开关）

    /// <summary>点卡片右下角的展开 / 收起箭头（两个静态按钮，靠 Visibility 切换）</summary>
    private void Button_ExpandQuick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OptiScalerBuildItemViewModel item })
        {
            item.Expanded = !item.Expanded;
            if (item.Expanded) ReloadQuickValues(item);
            MotionAnimations.PlayItemAreaExpand(OptiScalerSourceList, item, "OptiQuickConfig", item.Expanded);
        }
    }

    /// <summary>首次给卡片建 20 个快捷项（描述子是静态清单）</summary>
    private void EnsureQuickItems(OptiScalerBuildItemViewModel item)
    {
        if (item.QuickItems.Count > 0)
        {
            return;
        }

        foreach (OptiQuickDescriptor descriptor in OptiScalerQuickOptions.Descriptors)
        {
            if (descriptor.Key is "SmoothMotion" or "HookOriginalNvngxOnly" or "AutoExposure") continue;
            item.QuickItems.Add(new OptiQuickItemViewModel(descriptor,
                (desc, value) => CommitQuickValue(item, desc, value)));
        }
    }

    /// <summary>展开（或换配置 / 换构建）时，从当前生效的那份 ini 里重新读 20 个值</summary>
    private void ReloadQuickValues(OptiScalerBuildItemViewModel item)
    {
        if (_gameId is null)
        {
            return;
        }

        EnsureQuickItems(item);
        string gameKey = _gameId.GameBiz.ToString();
        string iniText = string.Empty;
        try
        {
            if (item.BuildDirectory is { } buildDir)
            {
                string presetName = OptiScalerPresets.SelectedPreset(gameKey, buildDir);
                if (presetName.Length > 0)
                {
                    iniText = OptiScalerPresets.Read(presetName) ?? string.Empty;
                }
                else
                {
                    string profile = OptiScalerProfiles.ProfilePath(buildDir, gameKey);
                    iniText = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
                }
            }

            foreach (OptiQuickItemViewModel quick in item.QuickItems)
            {
                quick.LoadValue(OptiScalerQuickOptions.ReadValue(iniText, quick.Descriptor));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reload quick config values");
        }
    }

    /// <summary>
    /// 快捷开关落盘：挂着命名配置 → 写配置 ini（下次启动重放）；否则写这个游戏自己的
    /// profile，并尽量立刻同步进主 ini（只在这个游戏正持有主配置且没有未回收改动时）。
    /// </summary>
    private bool CommitQuickValue(OptiScalerBuildItemViewModel item, OptiQuickDescriptor descriptor, string value)
    {
        if (_loading || _gameId is null || item.BuildDirectory is not { } buildDir)
        {
            return false;
        }

        string gameKey = _gameId.GameBiz.ToString();
        try
        {
            string presetName = OptiScalerPresets.SelectedPreset(gameKey, buildDir);
            if (presetName.Length > 0)
            {
                string? text = OptiScalerPresets.Read(presetName);
                if (text is null)
                {
                    TextBlock_Status.Text = $"读不到配置「{presetName}」，没写入。";
                    return false;
                }

                string updated = OptiScalerQuickOptions.ApplyValue(text, descriptor, value);
                if (!OptiScalerPresets.Save(presetName, updated, OptiScalerPresets.GetSources(presetName)))
                {
                    TextBlock_Status.Text = $"配置「{presetName}」写入失败（文件被占用？）。";
                    return false;
                }

                TextBlock_Status.Text = $"已写入配置「{presetName}」：{descriptor.Section}/{descriptor.Key} = {value}（下次启动游戏生效）。";
                return true;
            }

            if (!OptiScalerProfiles.TryPrepareProfileForRead(buildDir, gameKey))
            {
                TextBlock_Status.Text = "配置有并行修改，先解决冲突再改快捷开关。";
                return false;
            }

            string profile = OptiScalerProfiles.ProfilePath(buildDir, gameKey);
            string baseText = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
            string updatedProfile = OptiScalerQuickOptions.ApplyValue(baseText, descriptor, value);
            bool written = string.Equals(OptiScalerProfiles.RuntimeOwner(buildDir), gameKey, StringComparison.Ordinal)
                && OptiScalerProfiles.TryUpdateActiveProfile(buildDir, gameKey, _ => updatedProfile);
            if (!written)
            {
                // 这个游戏没持有主配置（或持有但有未回收的运行时改动）：只写 profile，下次启动 Activate 时会重放
                written = OptiScalerProfiles.TryWriteProfile(buildDir, gameKey, updatedProfile);
            }

            TextBlock_Status.Text = written
                ? $"已写入 profiles\\{gameKey}.ini：{descriptor.Section}/{descriptor.Key} = {value}。"
                : "profile 写入失败（文件被占用？）。";
            return written;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Commit quick opti value");
            TextBlock_Status.Text = "写入失败：" + ex.Message;
            return false;
        }
    }

    #endregion

    private void OnPresetChanged(OptiScalerBuildItemViewModel item, string? preset)
    {
        // 加载时 AttachPreset 会设一次值，那次不算用户切换
        if (_loading || _gameId is null || string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        if (!OptiScalerPresets.AllowsSource(preset, item.SourceId))
        {
            TextBlock_Status.Text = $"配置「{preset}」限定了 OptiScaler 分支，当前构建是 {item.SourceId}，用不了。";
            AttachPreset(item);
            return;
        }

        string gameKey = _gameId.GameBiz.ToString();
        if (item.BuildDirectory is { } previousBuild
            && !OptiScalerPresets.CollectFromRuntimeChecked(previousBuild).Succeeded)
        {
            TextBlock_Status.Text = "配置有并行修改或无法安全保存，请解决冲突后再切换配置。";
            AttachPreset(item);
            return;
        }
        AppConfig.SetValue(preset, OptiScalerPresets.PresetKey(gameKey, item.CurrentBuildId));
        // 记下「这个游戏挂的是哪份配置」 游戏改完退出时按它同步回配置
        AppConfig.SetValue(preset, OptiScalerPresets.FollowKey(gameKey));
        ReloadQuickValues(item);

        if (string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal))
        {
            // Returning to Current must detach any previously selected named preset.
            // Otherwise exit-time sync keeps writing the live profile into a stale
            // named config and makes the next Apply appear to revert.
            AppConfig.SetValue(string.Empty, OptiScalerPresets.FollowKey(gameKey));
            TextBlock_Status.Text = $"{item.Title}：保持这个游戏自己的配置不动（{item.CurrentProfileText}）。";
            return;
        }

        if (item.BuildDirectory is not { } buildDirectory)
        {
            TextBlock_Status.Text = $"{item.Title}：这个构建里没有 OptiScaler.dll，套不了配置。";
            return;
        }

        bool ok = OptiScalerPresets.Apply(buildDirectory, gameKey, preset);
        TextBlock_Status.Text = ok
            ? $"{item.Title}：已套用「{preset}」，下次启动游戏生效（游戏内 Save 后可用「修改」存回配置）。"
            : $"{item.Title}：套用「{preset}」失败  配置文件读不到或目录不可写。";
    }

    #region 注入用的 DLL 名字

    private const string DllNameCustomLabel = "自定义";

    /// <summary>给几个常见代理名，默认 OptiScaler（= 现有行为，不改文件名）</summary>
    private static readonly string[] BuiltinDllNames =
        ["OptiScaler", "dxgi", "d3d12", "version", "winmm", "dinput8", "dbghelp"];

    private void InitializeDllNameBox()
    {
        List<string> items = [.. BuiltinDllNames, DllNameCustomLabel];
        ComboBox_DllName.ItemsSource = items;

        string saved = _gameId is null ? "OptiScaler" : AppConfig.GetOptiScalerDllName(_gameId);
        string? match = items.FirstOrDefault(i => string.Equals(i, saved, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            ComboBox_DllName.SelectedItem = match;
            TextBox_CustomDllName.Visibility = Visibility.Collapsed;
        }
        else
        {
            ComboBox_DllName.SelectedItem = DllNameCustomLabel;
            TextBox_CustomDllName.Text = saved;
            TextBox_CustomDllName.Visibility = Visibility.Visible;
        }
    }

    private void ComboBox_DllName_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _gameId is null)
        {
            return;
        }

        string? selected = ComboBox_DllName.SelectedItem as string;

        if (string.Equals(selected, DllNameCustomLabel, StringComparison.Ordinal))
        {
            TextBox_CustomDllName.Visibility = Visibility.Visible;
            TextBox_CustomDllName.Focus(FocusState.Programmatic);
            return;
        }

        TextBox_CustomDllName.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        AppConfig.SetOptiScalerDllName(_gameId, selected);
        TextBlock_Status.Text = $"注入时用的 DLL 名：{selected}.dll（注入前会复制出这份文件名）";
    }

    private void TextBox_CustomDllName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _gameId is null)
        {
            return;
        }

        string text = TextBox_CustomDllName.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        AppConfig.SetOptiScalerDllName(_gameId, text);
        TextBlock_Status.Text = $"注入时用的 DLL 名：{text}（自定义）";
    }

    #endregion

    private static OptiScalerBuildItemViewModel? BuildOf(object sender)
        => (sender as FrameworkElement)?.DataContext as OptiScalerBuildItemViewModel;

    /// <summary>新增：空配置 / 复制现有配置 / 从网络下载 / 从文件导入</summary>
    private async void Button_PresetAdd_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOf(sender) is not { } item)
        {
            return;
        }

        if (!OptiScalerPresets.IsAvailable)
        {
            TextBlock_Status.Text = "还没定位到 OptiScaler 目录，配置没地方放  先去「全局插件  OptiScaler」装一个构建。";
            return;
        }

        const string networkLabel = "从网络下载配置";
        const string fileLabel = "从文件导入（.ini / .zip）";

        var nameBox = new TextBox
        {
            Header = "配置名",
            PlaceholderText = "例如：40系6倍帧生成 NR50%2层（从文件导入可留空）",
            MinWidth = 340,
        };

        var sourceItems = new List<string> { OptiScalerPresets.EmptyLabel };
        sourceItems.AddRange(OptiScalerPresets.List());
        sourceItems.Add(networkLabel);
        sourceItems.Add(fileLabel);

        var sourceBox = new ComboBox
        {
            Header = "内容来源",
            MinWidth = 340,
            ItemsSource = sourceItems,
            SelectedIndex = 0,
        };

        var remoteBox = new ComboBox
        {
            Header = "远端配置",
            MinWidth = 340,
            Visibility = Visibility.Collapsed,
        };

        var pickedText = new TextBlock
        {
            Text = "还没选文件。",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var pickButton = new Button { Content = "选择文件…" };
        var pickPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        pickPanel.Children.Add(pickButton);
        pickPanel.Children.Add(pickedText);

        var hint = new TextBlock
        {
            Text = "空配置 = 一份全默认的 OptiScaler.ini；复制现有 = 以某个本地配置为底改；" +
                   "从文件导入 = 选一份现成的 .ini 原样导入（不改编码），或选 .zip 解出里面全部 .ini —— " +
                   "重名自动加序号，不覆盖现有配置。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(sourceBox);
        panel.Children.Add(remoteBox);
        panel.Children.Add(pickPanel);
        panel.Children.Add(hint);

        List<OptiScalerPresetCatalog.RemotePreset> remote = [];
        bool fetched = false;
        string? pickedPath = null;

        pickButton.Click += async (_, _) =>
        {
            string? picked = await FileDialogHelper.PickSingleFileAsync(
                XamlRoot,
                ("OptiScaler 配置（ini）", ".ini"),
                ("Zip 压缩包", ".zip"));
            if (picked is null)
            {
                return;
            }

            pickedPath = picked;
            pickedText.Text = Path.GetFileName(picked);
        };

        sourceBox.SelectionChanged += async (_, _) =>
        {
            bool network = string.Equals(sourceBox.SelectedItem as string, networkLabel, StringComparison.Ordinal);
            remoteBox.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
            pickPanel.Visibility = string.Equals(sourceBox.SelectedItem as string, fileLabel, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!network)
            {
                return;
            }

            if (fetched)
            {
                return;
            }

            fetched = true;
            remoteBox.PlaceholderText = "正在拉取远端目录";
            remote = await OptiScalerPresetCatalog.FetchAsync(CancellationToken.None);
            // 只留允许用在这个分支上的（比如 [ada] 那条只在 mfg-ada 上出现）
            remote = remote.Where(p => p.AllowsSource(item.SourceId)).ToList();

            // 游戏检测：**不隐藏**（隐藏会让用户以为配置丢了），当前游戏的排最前；
            // 显卡检测：再按「适配本机显卡」排（RTX 40 的 ada 靠前）。
            // 每条都会在名字后标注游戏与显卡匹配情况。
            string? gameBiz = _gameId?.GameBiz.Value;
            string? gpuClass = OptiScalerPresetCatalog.GpuClass;
            remote = [.. remote
                .OrderByDescending(p => p.GameRank(gameBiz))
                .ThenByDescending(p => gpuClass is not null
                    && string.Equals(p.Gpu, gpuClass, StringComparison.OrdinalIgnoreCase))];
            remoteBox.ItemsSource = remote;
            remoteBox.PlaceholderText = remote.Count == 0 ? "拉不到远端目录（网络不通？）" : "选一条";
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新增配置",
            Content = panel,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string name = nameBox.Text?.Trim() ?? string.Empty;
        List<string> imported = [];
        List<string>? newSources = null;
        string content = string.Empty;

        if (string.Equals(sourceBox.SelectedItem as string, fileLabel, StringComparison.Ordinal))
        {
            // —— 从文件导入：.ini 原样拷一份；.zip 解出里面全部 .ini（重名加序号，不覆盖）——
            if (pickedPath is null)
            {
                TextBlock_Status.Text = "还没选文件  先点「选择文件…」。";
                return;
            }

            if (string.Equals(Path.GetExtension(pickedPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(OptiScalerPresets.Root);
                    using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(pickedPath);
                    foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
                    {
                        // 条目名两种分隔符都可能有（Compress-Archive 的历史包袱）
                        string flat = entry.FullName.Replace('\\', '/');
                        if (flat.EndsWith("/"))
                        {
                            continue;
                        }

                        flat = flat[(flat.LastIndexOf('/') + 1)..];
                        if (flat.Length == 0
                            || flat.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)
                            || !flat.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string finalName = UniquePresetName(Path.GetFileNameWithoutExtension(flat));
                        using Stream entryStream = entry.Open();
                        using FileStream target = File.Create(OptiScalerPresets.PathOf(finalName));
                        entryStream.CopyTo(target);
                        imported.Add(finalName);
                    }
                }
                catch
                {
                    // 包读不了 / 写不进去，按「没找到」走下面的提示
                }

                if (imported.Count == 0)
                {
                    TextBlock_Status.Text = "压缩包里没有找到 .ini 配置（或包读不了）。";
                    return;
                }
            }
            else
            {
                string finalName = UniquePresetName(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(pickedPath));
                if (!OptiScalerPresets.ImportFile(pickedPath, finalName))
                {
                    TextBlock_Status.Text = $"导入「{Path.GetFileName(pickedPath)}」失败  文件读不了或目录不可写。";
                    return;
                }

                imported.Add(finalName);
            }
        }
        else
        {
            if (name.Length == 0)
            {
                TextBlock_Status.Text = "配置名不能为空。";
                return;
            }

            if (string.Equals(sourceBox.SelectedItem as string, networkLabel, StringComparison.Ordinal))
            {
                if (remoteBox.SelectedItem is not OptiScalerPresetCatalog.RemotePreset picked)
                {
                    TextBlock_Status.Text = "还没选远端配置。";
                    return;
                }

                string? downloaded = await OptiScalerPresetCatalog.DownloadAsync(picked, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(downloaded))
                {
                    TextBlock_Status.Text = $"下载「{picked.Name}」失败  网络不通或远端文件缺失。";
                    return;
                }

                newSources = picked.Sources;
                content = downloaded;
            }
            else if (sourceBox.SelectedItem is string source
                     && !string.Equals(source, OptiScalerPresets.EmptyLabel, StringComparison.Ordinal))
            {
                content = OptiScalerPresets.Read(source) ?? string.Empty;
            }

            if (!OptiScalerPresets.Save(name, content, newSources))
            {
                TextBlock_Status.Text = $"写入配置「{name}」失败  目录不可写。";
                return;
            }

            imported.Add(name);
        }

        // 建完直接切过去（用户流程：测完一份存起来  换新的空配置接着测）
        string? addKey = _gameId?.GameBiz.ToString();
        if (!string.IsNullOrWhiteSpace(addKey))
        {
            AppConfig.SetValue(imported[^1], OptiScalerPresets.PresetKey(addKey, item.CurrentBuildId));
            // 记下「这个游戏挂的是哪份配置」 游戏改完退出时按它同步回配置
            AppConfig.SetValue(imported[^1], OptiScalerPresets.FollowKey(addKey));
        }

        TextBlock_Status.Text = imported.Count == 1
            ? $"已新增配置「{imported[0]}」，下拉已切过去。"
            : $"已从压缩包导入 {imported.Count} 份配置：{string.Join("、", imported)}（已切到最后一份）。";
        Load();
    }

    /// <summary>导入用的预设名：非法字符清洗；与现有预设重名时加序号（不悄悄覆盖）</summary>
    private string UniquePresetName(string baseName)
    {
        string name = OptiScalerPresets.Sanitize(baseName);
        if (!File.Exists(OptiScalerPresets.PathOf(name)))
        {
            return name;
        }

        for (int i = 2; i < 100; i++)
        {
            string candidate = $"{name} {i}";
            if (!File.Exists(OptiScalerPresets.PathOf(candidate)))
            {
                return candidate;
            }
        }

        return name + " " + DateTime.Now.ToString("HHmmss");
    }

    /// <summary>修改：把构建当前生效的 OptiScaler.ini 覆盖回选中的配置</summary>
    /// <summary>
    /// 修改：用系统默认编辑器打开这份配置文件（不再用内置对话框  之前它只渲染出一行）。
    /// 「当前配置」打开的是这个游戏的 profiles\<游戏>.ini；选配置时打开配置自己的 .ini。
    /// </summary>
    private void Button_PresetOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOf(sender) is not { } item)
        {
            return;
        }

        string? preset = item.CurrentPreset;
        if (string.IsNullOrWhiteSpace(preset))
        {
            TextBlock_Status.Text = "先在左边「当前配置」里选一项，再点「修改」。";
            return;
        }

        string? path;

        if (string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal))
        {
            string? gameKey = _gameId?.GameBiz.ToString();
            if (item.BuildDirectory is not { } buildDirectory || string.IsNullOrWhiteSpace(gameKey))
            {
                TextBlock_Status.Text = "这个构建没有可用目录，改不了。";
                return;
            }

            // 还没有就先从主 ini 继承一份，保证打开的是真实文件
            if (!OptiScalerPresets.CollectFromRuntimeChecked(buildDirectory).Succeeded
                || !OptiScalerProfiles.TryPrepareProfileForRead(buildDirectory, gameKey))
            {
                TextBlock_Status.Text = "配置有并行修改或无法安全保存，请保留文件并解决冲突后重试。";
                return;
            }
            path = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
        }
        else
        {
            path = OptiScalerPresets.PathOf(preset);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            TextBlock_Status.Text = $"找不到要改的文件：{path}";
            return;
        }

        try
        {
            OpenWithEditor(path);
            TextBlock_Status.Text = string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal)
                ? $"已用默认编辑器打开这个游戏的配置：{path}（改完保存即可，下次启动游戏生效）"
                : $"已用默认编辑器打开配置「{preset}」：{path}（改完保存即可，下次启动游戏会用它重建这个游戏的配置）";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = $"打不开编辑器：{ex.Message}  文件在 {path}";
        }
    }

    /// <summary>直接打开配置目录（回答「存哪了」）</summary>
    /// <summary>
    /// 用编辑器打开一份 ini。**不能只靠系统关联**：这台机器上 .ini 没有默认程序，
    /// ShellExecute 直接抛「找不到应用程序」。所以依次退：默认程序  记事本  打开所在目录。
    /// </summary>
    private void OpenWithEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Default editor failed, falling back to notepad: {Path}", path);
        }

        try
        {
            var notepad = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            notepad.ArgumentList.Add(path);
            Process.Start(notepad);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "notepad failed: {Path}", path);
        }

        // 最后兜底：把所在目录打开，用户自己双击
        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
    }

    private void Button_OpenPresetFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = OptiScalerPresets.Root;
            if (string.IsNullOrWhiteSpace(root))
            {
                TextBlock_Status.Text = "还没定位到 OptiScaler 根目录。";
                return;
            }

            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            TextBlock_Status.Text = $"配置目录：{root}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = $"打不开配置目录：{ex.Message}";
        }
    }

    /// <summary>
    /// 重命名配置。选中的是「当前配置」时，含义是**把这份现在的配置存成一个命名配置**
    ///  用户要的流程：默认只有当前配置  测好一个  存成「xxx游戏xxx倍」 再换新的空配置接着测。
    /// </summary>
    private async void Button_PresetRename_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOf(sender) is not { } item)
        {
            return;
        }

        string? preset = item.CurrentPreset;
        if (string.IsNullOrWhiteSpace(preset))
        {
            TextBlock_Status.Text = "先在左边「当前配置」里选一项。";
            return;
        }

        bool isCurrentProfile = string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal);

        var box = new TextBox { Text = preset, Header = "新名字", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isCurrentProfile ? "当前配置另存为" : "重命名配置",
            Content = box,
            PrimaryButtonText = isCurrentProfile ? "另存为" : "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = box.Text?.Trim() ?? string.Empty;
        if (newName.Length == 0)
        {
            TextBlock_Status.Text = "新名字不能为空。";
            return;
        }

        string? gameKey = _gameId?.GameBiz.ToString();

        if (isCurrentProfile)
        {
            // 把「当前生效的那份 profiles 配置」另存成命名配置，然后下拉切过去
            if (item.BuildDirectory is not { } buildDirectory || string.IsNullOrWhiteSpace(gameKey))
            {
                TextBlock_Status.Text = "这个构建没有可用目录，存不了。";
                return;
            }

            if (!OptiScalerPresets.CollectFromRuntimeChecked(buildDirectory).Succeeded
                || !OptiScalerProfiles.TryPrepareProfileForRead(buildDirectory, gameKey))
            {
                TextBlock_Status.Text = "配置有并行修改或无法安全保存，请保留文件并解决冲突后重试。";
                return;
            }
            string profilePath = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            if (!File.Exists(profilePath))
            {
                TextBlock_Status.Text = "这个构建里还没有 OptiScaler.ini  先启动一次游戏，或先套用一个配置。";
                return;
            }

            // 另存出来的是**配置**：OptiDllPath 那份是 Hub 启动时按本机路径钉的，
            // 别跟着进配置（新配置没有旧值可留，就退回 OptiScaler 自己的默认值）。
            string captured = OptiScalerRuntime.RestoreMachinePinnedKeys(
                File.ReadAllText(profilePath), OptiScalerPresets.Read(newName),
                OptiScalerRuntime.DefaultOptiDllPath);

            if (!OptiScalerPresets.Save(newName, captured))
            {
                TextBlock_Status.Text = $"存成配置「{newName}」失败  名字重复或目录不可写。";
                return;
            }

            AppConfig.SetValue(newName, OptiScalerPresets.PresetKey(gameKey, item.CurrentBuildId));
            TextBlock_Status.Text = $"已把当前配置存成配置「{newName}」，下拉已切过去。";
            Load();
            return;
        }

        if (!OptiScalerPresets.Rename(preset, newName))
        {
            TextBlock_Status.Text = "重命名失败  名字重复、配置不存在，或目录不可写。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(gameKey))
        {
            // 下拉原来指着旧名字，跟着改过去
            AppConfig.SetValue(newName, OptiScalerPresets.PresetKey(gameKey, item.CurrentBuildId));
        }

        TextBlock_Status.Text = $"「{preset}」已改名为「{newName}」。";
        Load();
    }

    /// <summary>修改：直接编辑这份配置的 ini 内容（下拉才是切换；这里只改文件）</summary>
    private async void Button_PresetEditContent_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOf(sender) is not { } item)
        {
            return;
        }

        string? preset = item.CurrentPreset;
        if (string.IsNullOrWhiteSpace(preset))
        {
            TextBlock_Status.Text = "先在左边「当前配置」里选一项，再点「修改」。";
            return;
        }

        // 「当前配置」= 这个游戏自己的 profiles\<游戏>.ini，一样可以改
        bool isCurrentProfile = string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal);
        string? profilePath = null;
        string? profileGameKey = null;
        string? profileBuildDirectory = null;
        string? content;
        string? hangingPreset = null;   // 这个游戏挂着的命名配置：启动时会覆盖 profile

        if (isCurrentProfile)
        {
            string? gameKey = _gameId?.GameBiz.ToString();
            if (item.BuildDirectory is not { } buildDirectory || string.IsNullOrWhiteSpace(gameKey))
            {
                TextBlock_Status.Text = "这个构建没有可用目录，改不了。";
                return;
            }

            profilePath = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            profileGameKey = gameKey;
            profileBuildDirectory = buildDirectory;
            hangingPreset = OptiScalerPresets.SelectedPreset(gameKey, buildDirectory);
            if (!OptiScalerPresets.CollectFromRuntimeChecked(buildDirectory).Succeeded
                || !OptiScalerProfiles.TryPrepareProfileForRead(buildDirectory, gameKey))
            {
                TextBlock_Status.Text = "配置有并行修改或无法安全保存，请保留文件并解决冲突后重试。";
                return;
            }

            content = File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;
        }
        else
        {
            content = OptiScalerPresets.Read(preset);
        }

        if (content is null)
        {
            TextBlock_Status.Text = $"读不到「{preset}」的文件。";
            return;
        }

        var box = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 620,
            Height = 420,
        };

        var scroll = new ScrollViewer
        {
            Content = box,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"修改配置配置：{preset}",
            Content = scroll,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string text = box.Text ?? string.Empty;

        if (isCurrentProfile)
        {
            try
            {
                if (profileBuildDirectory is null || profileGameKey is null
                    || !OptiScalerProfiles.TryWriteProfile(profileBuildDirectory, profileGameKey, text, content))
                {
                    TextBlock_Status.Text = "保存失败：配置已被其它操作修改或目录不可写，请重新打开。";
                    return;
                }
                TextBlock_Status.Text = hangingPreset is { Length: > 0 }
                    ? $"已保存这个游戏的配置（{Path.GetFileName(profilePath)}）；注意这个游戏挂着配置「{hangingPreset}」，下次启动会用它覆盖这份文件。"
                    : $"已保存这个游戏的配置（{Path.GetFileName(profilePath)}）。";
            }
            catch (Exception ex)
            {
                TextBlock_Status.Text = $"保存失败：{ex.Message}";
            }

            return;
        }

        // 保留这条配置原有的分支限制（别把 sources 洗掉）
        if (!OptiScalerPresets.Save(preset, text, OptiScalerPresets.GetSources(preset)))
        {
            TextBlock_Status.Text = $"保存配置「{preset}」失败  目录不可写。";
            return;
        }

        TextBlock_Status.Text = $"配置「{preset}」已改；下次启动游戏会用它重建这个游戏的配置。";
    }

    private async void Button_PresetEdit_Click(object sender, RoutedEventArgs e)
    {
        if (BuildOf(sender) is not { } item)
        {
            return;
        }

        string? preset = item.CurrentPreset;
        if (string.IsNullOrWhiteSpace(preset)
            || string.Equals(preset, OptiScalerPresets.CurrentLabel, StringComparison.Ordinal))
        {
            TextBlock_Status.Text = "先在左边「当前配置」里选一个配置，再点「修改」。";
            return;
        }

        if (item.BuildDirectory is not { } buildDirectory)
        {
            TextBlock_Status.Text = $"{item.Title}：这个构建里没有 OptiScaler.dll，改不了配置。";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改配置配置",
            Content = $"把「{preset}」覆盖成这个构建当前生效的 OptiScaler.ini？\\n\\n"
                      + "（在游戏内改过设置并 Save 之后，用这个把改动存回配置）",
            PrimaryButtonText = "覆盖",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        bool ok = OptiScalerPresets.CaptureFromBuild(buildDirectory, preset);
        TextBlock_Status.Text = ok
            ? $"已把当前生效的配置存回配置「{preset}」。"
            : $"配置「{preset}」保存失败  构建目录里没有 OptiScaler.ini 或目录不可写。";
    }

    #endregion

}
