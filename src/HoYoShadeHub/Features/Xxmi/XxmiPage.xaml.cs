using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>MI 实例下拉项</summary>
public sealed record XxmiInstanceItem(string Importer, string Path)
{
    /// <summary>下拉里就显示实例名（ZZMI / GIMI …），太长反而看不清</summary>
    public string Display => Importer;
}

/// <summary>
/// Mods 网格里的一张卡片。
///
/// <para>
/// 数据源还是 <see cref="XxmiModEntry"/>（磁盘上的真实状态），这里额外挂上「展示用」的东西：
/// 预览图、GameBanana mod id、「有新版本」角标。卡片是虚拟化的，滚动时会反复建模板，
/// 所以这些值都缓存成普通属性，不要放进 getter 里现算。
/// </para>
/// </summary>
public sealed partial class XxmiModItem : ObservableObject
{
    internal XxmiModItem(XxmiModEntry entry)
    {
        Name = entry.Name;
        Path = entry.Path;
        IsDirectory = entry.IsDirectory;
        _enabled = entry.Enabled;
        ModId = GameBananaPreviewService.ModIdFromFolderName(entry.Name);
    }

    /// <summary>磁盘上的名字（<c>gb_719975</c> / <c>xyz DISABLED</c>）—— 身份判定和改盘都用它</summary>
    public string Name { get; }

    private string? _remoteName;

    /// <summary>用户自己起的名字（优先级最高）；没设过为 null</summary>
    public string? CustomName { get; set; }

    /// <summary>GameBanana 上的原始名字（用户没起名时才用）</summary>
    public string? RemoteName => _remoteName;

    /// <summary>
    /// 卡片标题，优先级：<b>用户自定义名</b> &gt; GameBanana 拉到的名字 &gt; 目录名。
    /// 目录名的 DISABLED 后缀要剥掉，那是启用状态、不该出现在标题里。
    /// </summary>
    public string DisplayName => CustomName is { Length: > 0 } custom
        ? custom
        : (_remoteName is { Length: > 0 } remote ? remote : XxmiModManager.StripDisabled(Name));

    /// <summary>
    /// 搜索用的文本：把「自定义名 / 远端名 / 目录名 / 作者 / 来源」都拼进来，
    /// 这样用户起了中文名之后，用中文或原来的 gb_xxx 都能搜到。
    /// </summary>
    public string SearchText =>
        string.Join(' ', new[]
        {
            CustomName,
            _remoteName,
            XxmiModManager.StripDisabled(Name),
            Name,
            Author,
            IsFromGameBanana ? "gb_" + ModId : null,
            ModId?.ToString(),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>改自定义名后刷新相关绑定</summary>
    public void ApplyCustomName(string? name)
    {
        CustomName = name;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SearchText));
    }

    /// <summary>详情面板里的作者</summary>
    public string? Author { get; private set; }

    /// <summary>作者名（带 "by " 前缀）；没有作者时为空串，卡片上就整行不占位</summary>
    public string AuthorLine => Author is { Length: > 0 } author ? "by " + author : string.Empty;

    /// <summary>
    /// 悬停时才显示的作者行。
    /// WinUI 的 x:Bind 不会把 bool 自动转 Visibility，所以这里直接给 Visibility。
    /// </summary>
    public Visibility HoverAuthorVisibility =>
        _isHovered && Author is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 悬停时显示「来源 + 作者」两行小字。
    /// 来源对所有 mod 都有（GameBanana #xxx / 文件夹），所以不要求 Author 存在。
    /// </summary>
    public Visibility HoverMetaVisibility =>
        _isHovered ? Visibility.Visible : Visibility.Collapsed;

    private bool _isHovered;

    /// <summary>鼠标是否停在这张卡片上（悬停特效的状态源）</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
            {
                OnPropertyChanged(nameof(CardBorderBrush));
                OnPropertyChanged(nameof(CardBorderThickness));
                OnPropertyChanged(nameof(NameAreaHeight));
                OnPropertyChanged(nameof(HoverAuthorVisibility));
                OnPropertyChanged(nameof(HoverMetaVisibility));
                OnPropertyChanged(nameof(TitleMaxLines));
                OnPropertyChanged(nameof(AccentVisibility));
            }
        }
    }

    /// <summary>悬停时给卡片描一圈高亮边（发光感）</summary>
    public Brush CardBorderBrush => _isHovered
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 190, 255))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 66));

    /// <summary>悬停时边框加粗一点，配合高亮色更像"发光"</summary>
    public Thickness CardBorderThickness => _isHovered
        ? new Thickness(2)
        : new Thickness(1);

    /// <summary>右下高亮边的颜色（只有悬停那圈用得到）</summary>
    public Brush AccentBrush => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 190, 255));

    /// <summary>右下高亮边的显隐</summary>
    public Visibility AccentVisibility => _isHovered ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 名字区高度。
    ///
    /// <para>
    /// 行高按 FontSize 估：13px 标题约 18px/行、11px 副标题约 15px/行，再加上下各 6px 内边距。
    /// 之前给 64px 但内容实际要 ~70px，所以底部那行被裁掉了 —— 这里按行数算足。
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>平时：1 行标题 + 副标题 = 18 + 15 + 12 = 45 → 给 46</item>
    /// <item>悬停无作者：2 行标题 + 副标题 = 36 + 15 + 12 = 63 → 给 64</item>
    /// <item>悬停有作者：2 行标题 + 副标题 + 作者 = 36 + 15 + 15 + 12 = 78 → 给 80</item>
    /// </list>
    /// </summary>
    public double NameAreaHeight
    {
        get
        {
            // 这块是盖在图片上的模糊条，高度 = 文字实际高度 + 上下呼吸空间。
            // 给多了会在文字上方留一条空的模糊区（看着就是"范围太高"），所以卡得紧一点：
            // 平时：1 行标题 18 + 上下各 6 = 30
            // 悬停：2 行标题 36 + 来源 15 + 作者 15 + 上下各 6 = 78
            return _isHovered ? 78 : 30;
        }
    }

    /// <summary>标题最多几行：平时 1 行，悬停时放到 2 行（长名字悬停能看全）</summary>
    public int TitleMaxLines => _isHovered ? 2 : 1;

    /// <summary>悬停时图片轻微放大（1.0 → 1.06）</summary>
    public double ImageScale => _isHovered ? 1.06 : 1.0;

    /// <summary>GameBanana 模组主页</summary>
    public string? ProfileUrl { get; private set; }

    /// <summary>把 GameBanana 拉到的元信息贴到卡片上</summary>
    public void ApplyRemoteInfo(string? name, string? version, string? author, string? profileUrl)
    {
        bool nameChanged = false;

        if (name is { Length: > 0 } && !string.Equals(_remoteName, name, StringComparison.Ordinal))
        {
            _remoteName = name;
            nameChanged = true;
        }

        RemoteVersion = version;
        Author = author;
        ProfileUrl = profileUrl;

        // 作者是异步拉回来的，这里必须把依赖它的几个属性都通知一遍。
        // （少了这几行，作者行一拿到数据就会常显、悬停高度也不对 —— 之前就是这个 bug）
        OnPropertyChanged(nameof(AuthorLine));
        OnPropertyChanged(nameof(HoverAuthorVisibility));
        OnPropertyChanged(nameof(NameAreaHeight));
        OnPropertyChanged(nameof(Info));

        if (nameChanged)
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        OnPropertyChanged(nameof(Info));
    }

    public string Path { get; set; }

    public bool IsDirectory { get; }

    /// <summary>目录名里认出来的 GameBanana mod id（不是 GameBanana 装的 mod 就是 null）</summary>
    public int? ModId { get; }

    /// <summary>是 GameBanana 装进来的 mod（能查更新、有预览图）</summary>
    public bool IsFromGameBanana => ModId is not null;

    /// <summary>右键菜单里「在 GameBanana 打开」这一项的显隐</summary>
    public Visibility BananaMenuVisibility => IsFromGameBanana ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 卡片副标题：来源 / 类型。
    /// <b>不放作者</b> —— 作者归下面那行（只在悬停时出现），两处都写就会重复显示两遍。
    /// </summary>
    public string Info => IsFromGameBanana
        ? $"GameBanana #{ModId}"
        : (IsDirectory ? "文件夹" : "ini 文件");

    /// <summary>详情面板用：完整路径</summary>
    public string FullPath => Path;

    private bool _isVisible = true;

    /// <summary>是否通过当前搜索过滤</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    private Visibility _filterVisibility = Visibility.Visible;

    /// <summary>过滤用显隐（GridView 的 ItemTemplate 绑不了 IsVisible，只能绑 Visibility）</summary>
    public Visibility FilterVisibility
    {
        get => _filterVisibility;
        set => SetProperty(ref _filterVisibility, value);
    }

    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private ImageSource? _previewImage;

    /// <summary>
    /// 预览图；还没拉到时为 null（XAML 里退到占位图）。
    /// 存 <see cref="ImageSource"/> 而不是路径字符串 —— WinUI 的 Image.Source 不认识
    /// <c>"D:\..."</c> 这种裸路径，必须自己包成 BitmapImage。
    /// </summary>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }
    }

    /// <summary>把拉到的本地图片文件设成卡片图（必须在 UI 线程调）</summary>
    public void SetPreviewFromFile(string path)
    {
        try
        {
            PreviewImage = new BitmapImage(new Uri(path));
        }
        catch
        {
            // 图坏了就当没有，别让卡片挂掉
            PreviewImage = null;
        }
    }

    public bool HasPreview => _previewImage is not null;

    /// <summary>占位图的显隐（Image 在没图时会显示成空白，所以自己控一层）</summary>
    public Visibility PlaceholderVisibility => HasPreview ? Visibility.Collapsed : Visibility.Visible;

    private bool _hasUpdate;

    /// <summary>有新版（GameBanana 远端比本地新）—— 卡片上显示「有新版本」角标</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (SetProperty(ref _hasUpdate, value))
            {
                OnPropertyChanged(nameof(UpdateBadgeVisibility));
            }
        }
    }

    /// <summary>
    /// 角标的显隐。<see cref="Visibility"/> 直接当属性暴露，省掉 x:Bind 里挂转换器
    /// （WinUI 的 x:Bind 不会把 bool 自动转成 Visibility）。
    /// </summary>
    public Visibility UpdateBadgeVisibility => _hasUpdate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>远端最新版本号 / 更新时间，详情面板里显示</summary>
    public string? RemoteVersion { get; set; }

    /// <summary>本地记的版本（安装时写下的），详情面板里显示</summary>
    public string? LocalVersion { get; set; }
}

/// <summary>
/// 「模型替换（XXMI）」页（左侧一级导航）。
///
/// 上面选 MI 实例（绝区零 = ZZMI、原神 = GIMI、星铁 = SRMI…，可自动查找或手动指定），
/// 下面管这个实例 <c>Mods\</c> 里的 mod：启用/禁用（改名字加/去掉 DISABLED）、打开、删除、导入文件夹 / zip。
/// </summary>
public sealed partial class XxmiPage : PageBase
{
    private readonly ILogger<XxmiPage> _logger = AppConfig.GetLogger<XxmiPage>();
    private readonly ObservableCollection<XxmiModItem> _mods = [];

    private GameId? _gameId;
    private string? _instance;
    private bool _loadingMods;

    /// <summary>当前实例对应的导入器名（ZZMI / GIMI…），只在底部状态里显示</summary>
    private string _importerName = string.Empty;

    /// <summary>mod 计数文案，和状态文案分开存，拼起来显示</summary>
    private string _modsCountText = string.Empty;

    /// <summary>实例说明文案（MI 实例：xxx / 上次启动：xxx）</summary>
    private string _instanceStatusText = string.Empty;

    private readonly GameBananaPreviewService _previews;

    /// <summary>用户给 mod 起的显示名（只影响显示，不动磁盘）</summary>
    private readonly XxmiRenameStore _renames = XxmiRenameStore.Load();

    /// <summary>搜索框里的关键词（空 = 不过滤）</summary>
    private string _searchText = string.Empty;

    public XxmiPage()
    {
        InitializeComponent();
        _previews = new GameBananaPreviewService(AppConfig.GetLogger<GameBananaPreviewService>());
        GridView_Mods.ItemsSource = _mods;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as GameId;
        LoadInstance();
    }

    private void LoadInstance()
    {
        string? expected = XxmiLocator.ImporterFor(_gameId?.GameBiz);
        string? instance = XxmiLocator.FindInstance(_gameId?.GameBiz, out string? importer);

        _instance = instance;
        TextBox_Instance.Text = instance ?? string.Empty;

        // 顶栏不再放大标题了，但「这个实例属于哪个游戏」有用，并到底部状态里
        _importerName = importer ?? expected ?? string.Empty;

        List<XxmiInstanceItem> items = [];
        string? root = XxmiLocator.FindRoot();

        if (root is not null)
        {
            foreach (string dir in XxmiLocator.ListInstances(root))
            {
                items.Add(new XxmiInstanceItem(Path.GetFileName(dir), dir));
            }
        }

        if (instance is not null && items.All(i => !string.Equals(i.Path, instance, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new XxmiInstanceItem(Path.GetFileName(instance), instance));
        }

        ComboBox_Importer.ItemsSource = items;
        ComboBox_Importer.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Path, instance, StringComparison.OrdinalIgnoreCase));

        string status = instance is null
            ? (expected is null
                ? "这个游戏没有对应的 MI 实例（认识的：ZZMI / GIMI / SRMI / WWMI / HIMI）。"
                : $"没找到 XXMI 的 {expected} 实例。点「自动查找」，或「选择…」手动指定；也可以先用 XXMI Launcher 装一次。")
            : $"MI 实例：{instance}";

        // 启动器那边「启用 XXMI 注入」跑完会写到这儿，方便确认上次到底有没有注进去
        if (AppConfig.XxmiLastLaunch is { Length: > 0 } lastLaunch)
        {
            status += "　上次启动：" + lastLaunch;
        }

        _instanceStatusText = status;

        // 先清计数，免得切实例时残留上一份的数字
        _modsCountText = string.Empty;
        LoadMods();
        UpdateStatusBar();
    }

    /// <summary>底部状态 = 实例说明 + mod 计数（顶栏砍成一行后，这些信息都并到这儿）</summary>
    private void UpdateStatusBar()
    {
        var parts = new List<string>();

        if (_importerName.Length > 0)
        {
            parts.Add(_importerName);
        }

        if (_instanceStatusText.Length > 0)
        {
            parts.Add(_instanceStatusText);
        }

        if (_modsCountText.Length > 0)
        {
            parts.Add(_modsCountText);
        }

        TextBlock_Status.Text = string.Join("　｜　", parts);
    }

    private void LoadMods()
    {
        _loadingMods = true;

        if (_instance is null)
        {
            _mods.Clear();
            _modsCountText = string.Empty;
            _loadingMods = false;
            return;
        }

        string modsDirectory = XxmiLocator.ModsDirectory(_instance);

        if (!Directory.Exists(modsDirectory))
        {
            _mods.Clear();
            _modsCountText = "还没有 Mods 目录（导入一个 mod 就会自动建）";
            _loadingMods = false;
            return;
        }

        List<XxmiModEntry> entries = XxmiModManager.List(modsDirectory);

        // 尽量**就地更新**而不是清空重建：
        // 卡片带预览图，重建会让已经贴上的图全丢、画面闪一下。
        // 用 modId（或名字）当身份，命中的复用旧 item（图还在），只同步启用状态和路径。
        var existing = _mods.ToDictionary(m => IdentityOf(m), StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<XxmiModItem>(entries.Count);

        foreach (XxmiModEntry entry in entries)
        {
            string key = IdentityOf(entry);

            if (existing.TryGetValue(key, out XxmiModItem? item))
            {
                item.Path = entry.Path;
                item.Enabled = entry.Enabled;
                existing.Remove(key);
                rebuilt.Add(item);
            }
            else
            {
                var fresh = new XxmiModItem(entry);
                rebuilt.Add(fresh);
            }

            // 用户起的名字优先（按稳定身份查，启用/禁用改名后也不会丢）
            rebuilt[^1].ApplyCustomName(_renames.GetName(IdentityOf(entry)));
        }

        // 让 _mods 的内容和顺序都对齐 rebuilt（复用过的 item 实例保持不动，所以图不会丢）
        for (int i = 0; i < rebuilt.Count; i++)
        {
            if (i < _mods.Count && ReferenceEquals(_mods[i], rebuilt[i]))
            {
                continue;
            }

            int current = _mods.IndexOf(rebuilt[i]);

            if (current >= 0)
            {
                _mods.Move(current, i);
            }
            else
            {
                _mods.Insert(i, rebuilt[i]);
            }
        }

        // 末尾多出来的就是已经不存在的 mod
        while (_mods.Count > rebuilt.Count)
        {
            _mods.RemoveAt(_mods.Count - 1);
        }

        int enabled = _mods.Count(m => m.Enabled);
        _modsCountText = $"共 {_mods.Count} 个 mod（启用 {enabled} / 禁用 {_mods.Count - enabled}）";
        _loadingMods = false;
        UpdateStatusBar();

        // 预览图是网络活，别卡住列表显示：先出卡片，图拉到了再逐张贴上
        _ = LoadPreviewsAsync();
    }

    /// <summary>
    /// 判断「是不是同一个 mod」用的身份。
    /// <b>不能直接用文件夹名</b> —— 启用/禁用就是改名字，改名后会被当成另一个 mod，
    /// 图标和状态都会白丢。GameBanana 的 mod 有稳定的 modId，用它最准；其余的退回名字（去掉 DISABLED）。
    /// </summary>
    private static string IdentityOf(XxmiModItem item) =>
        item.ModId is { } id ? "gb:" + id : "name:" + XxmiModManager.StripDisabled(item.Name);

    private static string IdentityOf(XxmiModEntry entry) =>
        GameBananaPreviewService.ModIdFromFolderName(entry.Name) is { } id
            ? "gb:" + id
            : "name:" + XxmiModManager.StripDisabled(entry.Name);



    /// <summary>
    /// 给 GameBanana 来的 mod 拉预览图（本地有缓存就直接用），拉到一张贴一张。
    /// 拉不到的（不是 GameBanana 的、或者网络不通）就保持占位图，不算错误。
    /// </summary>
    private async Task LoadPreviewsAsync()
    {
        List<XxmiModItem> targets = [.. _mods.Where(m => m.ModId is not null && !m.HasPreview)];

        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(targets.Select(async item =>
            {
                int modId = item.ModId!.Value;

                // ConfigureAwait(true) 保证回到 UI 线程，BitmapImage 能安全创建
                GameBananaModInfo? info = await _previews.GetModInfoAsync(modId).ConfigureAwait(true);
                string? path = await _previews.GetPreviewAsync(modId).ConfigureAwait(true);

                // 期间用户可能刷新/切实例了，这个 item 已经不在列表里就别动 UI
                if (!_mods.Contains(item))
                {
                    return;
                }

                if (info is not null)
                {
                    // 远端名字比 gb_719975 这种目录名友好得多，用它当卡片标题
                    item.ApplyRemoteInfo(info.Name, info.Version, info.Author, info.ProfileUrl);
                }

                if (path is not null)
                {
                    item.SetPreviewFromFile(path);
                }
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load mod previews failed");
        }
    }

    private void ComboBox_Importer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboBox_Importer.SelectedItem is not XxmiInstanceItem item)
        {
            return;
        }

        _instance = item.Path;
        TextBox_Instance.Text = item.Path;

        if (_gameId is not null)
        {
            AppConfig.SetXxmiInstance(_gameId.GameBiz, item.Path);
        }

        LoadMods();
    }

    private void Button_AutoFind_Click(object sender, RoutedEventArgs e)
    {
        if (_gameId is not null)
        {
            AppConfig.SetXxmiInstance(_gameId.GameBiz, null);
        }

        LoadInstance();
        TextBlock_Status.Text = _instance is null ? "自动查找没找到 XXMI（可以手动指定 MI 目录）。" : $"自动找到：{_instance}";
    }

    private async void Button_Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!XxmiLocator.IsInstance(folder))
            {
                TextBlock_Status.Text = $"{folder} 看起来不是 MI 实例目录（里面没有 d3dx.ini / d3d11.dll）。";
                return;
            }

            if (_gameId is not null)
            {
                AppConfig.SetXxmiInstance(_gameId.GameBiz, folder);
            }

            _instance = folder;
            TextBox_Instance.Text = folder;
            LoadInstance();
            TextBlock_Status.Text = $"已指定：{folder}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "选择目录失败：" + ex.Message;
        }
    }

    private void Button_OpenInstance_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_instance) && Directory.Exists(_instance))
        {
            OpenInExplorer(_instance);
        }
    }

    private void Button_OpenMods_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            return;
        }

        string mods = XxmiLocator.ModsDirectory(_instance);
        Directory.CreateDirectory(mods);
        OpenInExplorer(mods);
    }

    /// <summary>
    /// 打开 XXMI Launcher。模型替换的注入必须由它驱动 —— ZZMI 里那份 d3d11.dll 是"受控版"
    /// （ini 头写着 intended to be loaded by XXMI Launcher），我们用 3dmloader 的 Inject 把它塞进游戏进程
    /// 能成功加载，但它完全不初始化（连 d3d11_log.txt 都不写），所以注入这条路先不做。
    /// </summary>
    private void Button_LaunchXxmi_Click(object sender, RoutedEventArgs e)
    {
        string? root = XxmiLocator.FindRoot();

        if (root is null)
        {
            TextBlock_Status.Text = "找不到 XXMI 安装目录。";
            return;
        }

        string launcher = Path.Combine(root, "Resources", "Bin", "XXMI Launcher.exe");

        if (!File.Exists(launcher))
        {
            TextBlock_Status.Text = $"找不到 {launcher}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = launcher, UseShellExecute = true });
            TextBlock_Status.Text = "已打开 XXMI Launcher —— 在它里面点启动，模型替换才会生效。";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "打开 XXMI Launcher 失败：" + ex.Message;
        }
    }

    private void Button_Refresh_Click(object sender, RoutedEventArgs e) => LoadInstance();

    private async void Button_ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            TextBlock_Status.Text = "先指定 MI 实例目录。";
            return;
        }

        string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            string mods = XxmiLocator.ModsDirectory(_instance);
            Directory.CreateDirectory(mods);
            string imported = XxmiModManager.ImportFolder(mods, folder);
            LoadMods();
            TextBlock_Status.Text = $"已导入：{Path.GetFileName(imported)}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "导入失败：" + ex.Message;
        }
    }

    private async void Button_ImportZip_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            TextBlock_Status.Text = "先指定 MI 实例目录。";
            return;
        }

        string? zip = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("压缩包", ".zip"), ("所有文件", ".*"));

        if (string.IsNullOrWhiteSpace(zip))
        {
            return;
        }

        try
        {
            string mods = XxmiLocator.ModsDirectory(_instance);
            Directory.CreateDirectory(mods);
            string imported = XxmiModManager.ImportZip(mods, zip);
            LoadMods();
            TextBlock_Status.Text = $"已导入并解压：{Path.GetFileName(imported)}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "导入失败：" + ex.Message;
        }
    }

    /// <summary>卡片左上角那个常显的启用开关（左右滑动式）</summary>
    private void ToggleSwitch_ModEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingMods || sender is not ToggleSwitch { DataContext: XxmiModItem item } toggle)
        {
            return;
        }

        bool wanted = toggle.IsOn;

        // 双向绑定在事件之前就把 item.Enabled 改成了 wanted，所以「绑定值」不能当判据；
        // 真正的事实是**磁盘上的目录名**（启用/禁用就是改名）。名字已经对了就别白改一次。
        if (XxmiModManager.IsEnabled(Path.GetFileName(item.Path)) == wanted)
        {
            return;
        }

        try
        {
            item.Path = XxmiModManager.SetEnabled(item.Path, wanted);
            LoadMods();
            TextBlock_Status.Text = $"{item.Name} → {(wanted ? "已启用" : "已禁用")}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "切换失败：" + ex.Message;
            LoadMods();
        }
    }

    /// <summary>
    /// 名字条的磨砂层：卡片加载时给它做一次真高斯模糊。
    /// 直接用 sender（= 那个 Border），不靠 x:Name 去找 —— 模板里同名控件会重复生成。
    /// </summary>
    private void Border_NameBlur_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement border || border.Tag is true)
        {
            return;
        }

        // Tag 打标，避免卡片被复用时重复挂一遍
        border.Tag = true;

        BackdropBlur.Apply(border);
    }

    /// <summary>
    /// 卡片根元素加载：做圆角裁剪（图片悬停放大会溢出圆角）。
    ///
    /// <para>
    /// 只裁<b>装图片的那层 Border</b>，绝不裁卡片本体 ——
    /// 裁卡片本体时，如果几何尺寸在布局完成前算错，会连卡片的边框一起裁掉
    /// （之前就出过这个 bug：悬停的卡片只剩上半圈边框）。
    /// </para>
    /// </summary>
    private void ModCardRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root)
        {
            return;
        }

        // 暂时不做裁剪：Composition 的 Clip 在这个嵌套布局里会把卡片边缘一起切掉，
        // 先保证边框完整（功能优先），圆角溢出问题另想办法。
        _ = root;
    }

    /// <summary>
    /// 给卡片打圆角裁剪（XAML 的 Clip 做不出圆角）。
    ///
    /// <para>
    /// 注意：<see cref="FrameworkElement.ActualWidth"/> 在 Loaded 时可能还是 0，
    /// 直接拿它建 clip 会裁出一个 0 大小的区域、把整张卡片都裁没。
    /// 所以这里先按已知的卡片尺寸建，再挂 SizeChanged 持续跟。
    /// </para>
    /// </summary>
    private static void ApplyRoundedClip(FrameworkElement element, float radius = 6f, float fallbackWidth = 208f, float fallbackHeight = 288f)
    {
        try
        {
            Visual visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);

            var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new System.Numerics.Vector2(
                (float)(element.ActualWidth > 0 ? element.ActualWidth : fallbackWidth),
                (float)(element.ActualHeight > 0 ? element.ActualHeight : fallbackHeight));
            geometry.CornerRadius = new System.Numerics.Vector2(radius, radius);

            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);

            element.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    geometry.Size = new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                }
            };
        }
        catch
        {
            // 裁不上只是四角不够圆，不影响功能
        }
    }

    /// <summary>
    /// 鼠标进卡片：右上角按钮淡入 + 卡片进入悬停态
    /// （发光边框、图片放大、名字区变高并露出作者）。
    /// </summary>
    private void ModCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid { DataContext: XxmiModItem item } card)
        {
            return;
        }

        item.IsHovered = true;

        if (FindDescendant<StackPanel>(card, "Panel_CardActions") is { } actions)
        {
            actions.Opacity = 1;
        }

        // 图片放大：用 Composition 的 Scale 做，带过渡不突兀
        SetPreviewScale(card, 1.06f);
    }

    /// <summary>鼠标离开卡片：按钮淡出、取消悬停态</summary>
    private void ModCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid { DataContext: XxmiModItem item } card)
        {
            return;
        }

        item.IsHovered = false;

        if (FindDescendant<StackPanel>(card, "Panel_CardActions") is { } actions)
        {
            actions.Opacity = 0;
        }

        SetPreviewScale(card, 1f);
    }

    /// <summary>
    /// 缩放卡片里的预览图，<b>带过渡动画</b>。
    ///
    /// <para>
    /// 直接给 <c>visual.Scale</c> 赋值是瞬间跳变（没有动画）。
    /// 这里用 <see cref="Compositor.CreateVector3KeyFrameAnimation"/> 做 0.22s 的缓动，
    /// 配合 <c>CenterPoint</c> 让图片从中心放大、且不把边框顶出去。
    /// </para>
    /// </summary>
    private static void SetPreviewScale(DependencyObject card, float scale)
    {
        try
        {
            if (FindDescendant<Image>(card, "Image_Preview") is not { } image)
            {
                return;
            }

            Visual visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(image);

            // 以图片中心为缩放原点
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)image.ActualWidth / 2f,
                (float)image.ActualHeight / 2f,
                0f);

            Vector3KeyFrameAnimation animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1f, new System.Numerics.Vector3(scale, scale, 1f));
            animation.Duration = TimeSpan.FromMilliseconds(220);

            visual.StartAnimation("Scale", animation);
        }
        catch
        {
            // 动效失败只是不够好看，不影响功能
        }
    }

    /// <summary>右键菜单：在 GameBanana 上打开这个 mod 的页面</summary>
    private async void MenuFlyout_OpenBananaPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: XxmiModItem item })
        {
            return;
        }

        // 优先用它自己的主页（拉元信息时带回来了），没有就按 modId 拼
        string url = item.ProfileUrl is { Length: > 0 } profile
            ? profile
            : $"https://gamebanana.com/mods/{item.ModId}";

        if (item.ModId is null && item.ProfileUrl is null)
        {
            TextBlock_Status.Text = "这个 mod 不是从 GameBanana 装的，没有对应页面。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            TextBlock_Status.Text = "已在浏览器打开：" + url;
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "打开页面失败：" + ex.Message;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 右键菜单：给这个 mod 起一个显示名。
    /// <b>只改显示，不动磁盘</b> —— 目录名保持不变（XXMI/3DMigoto 靠它认 mod，
    /// GameBanana 的更新也要靠 <c>gb_{modId}</c> 定位旧版本）。
    /// 起的名字会参与搜索，原来的 <c>gb_xxxxx</c> 也照样能搜到。
    /// </summary>
    private async void MenuFlyout_RenameMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: XxmiModItem item })
        {
            return;
        }

        var input = new TextBox
        {
            Text = item.CustomName ?? item.DisplayName,
            PlaceholderText = "给它起个自己看得懂的名字",
            SelectionStart = 0,
            SelectionLength = (item.CustomName ?? item.DisplayName).Length,
        };

        var hint = new TextBlock
        {
            Text = "只改显示名，不动磁盘上的文件夹名（XXMI 靠它认 mod）。\n留空 = 恢复成原来的名字。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.7,
            FontSize = 12,
        };

        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(hint);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "重命名",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string? name = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();

        _renames.SetName(IdentityOf(item), name);
        item.ApplyCustomName(name);
        ApplySearchFilter();

        TextBlock_Status.Text = name is null
            ? $"{XxmiModManager.StripDisabled(item.Name)} → 已恢复原名"
            : $"{XxmiModManager.StripDisabled(item.Name)} → 显示为「{name}」（磁盘文件夹名没变）";
    }

    /// <summary>
    /// 按搜索框的关键词过滤卡片。
    /// 匹配范围包含自定义名、GameBanana 名、目录名、作者、modId —— 起了中文名之后，
    /// 用中文或原来的 <c>gb_xxxxx</c> 都能搜到。
    /// </summary>
    private void ApplySearchFilter()
    {
        foreach (XxmiModItem item in _mods)
        {
            bool visible = _searchText.Length == 0
                || item.SearchText.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

            item.IsVisible = visible;
            item.FilterVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        int shown = _mods.Count(m => m.IsVisible);
        _modsCountText = _searchText.Length == 0
            ? $"共 {_mods.Count} 个 mod（启用 {_mods.Count(m => m.Enabled)} / 禁用 {_mods.Count(m => !m.Enabled)}）"
            : $"筛选出 {shown} / {_mods.Count} 个 mod";

        UpdateStatusBar();
    }

    /// <summary>搜索框输入变化（AutoSuggestBox 的事件签名和 TextBox 不一样）</summary>
    private void TextBox_Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只处理用户手动输入，避免程序改文本时又触发一轮过滤
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput
            && args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }

        _searchText = sender.Text?.Trim() ?? string.Empty;
        ApplySearchFilter();
    }

    /// <summary>在可视树里按名字找一个后代元素（卡片模板里拿 x:Name 控件用）</summary>
    private static T? FindDescendant<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindDescendant<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 网格空白处右键 = 页面级操作（导入 / 刷新 / 打开目录…）。
    /// 卡片自己有自己的 ContextFlyout，所以点中卡片时交给卡片处理，这里不抢。
    /// </summary>
    private void ModGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 命中的是卡片（或其子元素）时不弹页面菜单，让卡片的菜单生效
        if (e.OriginalSource is DependencyObject source && FindAncestor<GridViewItem>(source) is not null)
        {
            return;
        }

        if (sender is not FrameworkElement element || element.ContextFlyout is not MenuFlyout flyout)
        {
            return;
        }

        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    /// <summary>往上找第一个指定类型的祖先（找 GridViewItem 用）</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>右键卡片：把 ContextFlyout 弹在鼠标位置</summary>
    private void ModCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextFlyout is not MenuFlyout flyout)
        {
            return;
        }

        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    /// <summary>卡片「属性」按钮 / 右键菜单第一项：列出这个 mod 的详情</summary>
    private async void Button_ModProperties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: XxmiModItem item })
        {
            return;
        }

        var lines = new List<string>
        {
            $"名称：{item.Name}",
            $"类型：{(item.IsDirectory ? "文件夹" : "ini 文件")}",
        };

        if (item.ModId is { } modId)
        {
            lines.Add($"GameBanana：#{modId}");
        }

        if (item.LocalVersion is { Length: > 0 } local)
        {
            lines.Add($"本地版本：{local}");
        }

        if (item.RemoteVersion is { Length: > 0 } remote)
        {
            lines.Add($"远端版本：{remote}");
        }

        lines.Add(string.Empty);
        lines.Add($"路径：{item.FullPath}");

        if (item.IsDirectory && Directory.Exists(item.FullPath))
        {
            try
            {
                long size = new DirectoryInfo(item.FullPath)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                int count = Directory.EnumerateFiles(item.FullPath, "*", SearchOption.AllDirectories).Count();
                lines.Add($"大小：{FormatSize(size)}（{count} 个文件）");
            }
            catch (Exception ex)
            {
                lines.Add("大小：读不出来（" + ex.Message + "）");
            }
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Mod 属性",
            Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "打开所在文件夹",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenInExplorer(item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath)!);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    /// <summary>筛选侧边栏开合（带滑入滑出动画）</summary>
    private void ToggleButton_Filter_Click(object sender, RoutedEventArgs e)
    {
        if (ToggleButton_Filter.IsChecked == true)
        {
            ShowFilterPanel();
        }
        else
        {
            HideFilterPanel();
        }
    }

    private void ShowFilterPanel()
    {
        Panel_Filter.Visibility = Visibility.Visible;

        // 先摆到「收起」的样子，再跑动画，否则会先闪一下全宽
        Panel_Filter.Width = 0;
        Panel_Filter.Opacity = 0;
        Storyboard_ShowFilter.Begin();
    }

    private void HideFilterPanel()
    {
        Storyboard_HideFilter.Completed -= OnHideFilterCompleted;
        Storyboard_HideFilter.Completed += OnHideFilterCompleted;
        Storyboard_HideFilter.Begin();
    }

    /// <summary>收起动画放完才真正隐藏，否则动画会被 Visibility 掐掉</summary>
    private void OnHideFilterCompleted(object? sender, object e)
    {
        Storyboard_HideFilter.Completed -= OnHideFilterCompleted;
        Panel_Filter.Visibility = Visibility.Collapsed;
    }

    private void Button_CloseFilter_Click(object sender, RoutedEventArgs e)
    {
        ToggleButton_Filter.IsChecked = false;
        HideFilterPanel();
    }

    private void Button_OpenMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: XxmiModItem item })
        {
            OpenInExplorer(item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path));
        }
    }

    private async void Button_DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: XxmiModItem item })
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "删除这个 mod？",
            Content = item.DisplayName + "（直接删掉，不进回收站）",
            PrimaryButtonText = "取消",     // 主按钮 = 取消（默认、正常配色）
            SecondaryButtonText = "删除",   // 次按钮 = 删除，下面单独染成红色
            DefaultButton = ContentDialogButton.Primary,
        };

        // 「删除」要给成危险色（红），「取消」保持普通按钮外观。
        // ContentDialog 没有直接的按钮颜色属性，只能在它 Loaded 之后去改按钮资源。
        PaintDeleteButtonDanger(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Secondary)
        {
            return;
        }

        try
        {
            XxmiModManager.Delete(item.Path);
            LoadMods();
            TextBlock_Status.Text = $"已删除：{item.Name}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "删除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把对话框的「次按钮」（这里放的是删除）染成危险红，「主按钮」（取消）保持正常配色。
    ///
    /// <para>
    /// ContentDialog 没有「按钮颜色」这种属性，标准做法是等它 Loaded 之后
    /// 从可视树里找到那个按钮，替换掉它的 Background/Foreground 资源。
    /// 用 Loaded 是因为按钮是模板展开时才存在的。
    /// </para>
    /// </summary>
    private static void PaintDeleteButtonDanger(ContentDialog dialog)
    {
        dialog.Loaded += (_, _) =>
        {
            try
            {
                // 次按钮 = SecondaryButton（我们放的「删除」）
                Button? delete = FindDescendantByName<Button>(dialog, "SecondaryButton");

                if (delete is null)
                {
                    return;
                }

                var red = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 196, 43, 28));
                var redHover = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 220, 60, 45));
                var redPressed = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 160, 30, 20));

                delete.Background = red;
                delete.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                delete.BorderBrush = red;

                // 覆盖各状态的配色，否则鼠标移上去/按下会跳回默认灰
                delete.Resources["ButtonBackgroundPointerOver"] = redHover;
                delete.Resources["ButtonBackgroundPressed"] = redPressed;
                delete.Resources["ButtonBackground"] = red;
                delete.Resources["ButtonForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                delete.Resources["ButtonForegroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                delete.Resources["ButtonForegroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                delete.Resources["ButtonBorderBrush"] = red;
                delete.Resources["ButtonBorderBrushPointerOver"] = redHover;
                delete.Resources["ButtonBorderBrushPressed"] = redPressed;
            }
            catch (Exception)
            {
                // 染不上色只是观感问题，不影响删除功能
            }
        };
    }

    /// <summary>在可视树里按控件名字找一个后代（ContentDialog 的按钮用）</summary>
    private static T? FindDescendantByName<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindDescendantByName<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open folder failed: {Path}", path);
        }
    }
}
