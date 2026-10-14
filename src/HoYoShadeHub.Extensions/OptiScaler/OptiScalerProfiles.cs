namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// OptiScaler 的 ini 按游戏分离。
///
/// <para>
/// OptiScaler 没有任何导出 / 环境变量能改配置路径，<c>OptiScaler.ini</c> 固定从 DLL 旁边读。
/// 同一个构建被多个游戏复用时，游戏内叠加层（或手动改 ini）的设置会互相串。
/// 这里在每个构建目录下维护 <c>profiles\&lt;游戏标识&gt;.ini</c>：
/// 注入前把对应游戏的那份激活为主 ini，游戏退出时回写。
/// </para>
/// </summary>
public static class OptiScalerProfiles
{
    private static readonly object Gate = new();
    public const string RuntimeBaselineFileName = ".hub-runtime-baseline";
    public const string RuntimeTransactionFileName = ".hub-profile-transaction";
    public enum CollectionStatus { Unchanged, Collected, Blocked, Failed }
    public readonly record struct RuntimeCollectionResult(CollectionStatus Status, string? Owner)
    {
        public bool Succeeded => Status is CollectionStatus.Unchanged or CollectionStatus.Collected;
    }
    public const string ProfilesFolderName = "profiles";

    /// <summary>
    /// 「主 ini 现在是谁的」这份记账文件（构建目录下，内容就是游戏标识）。
    ///
    /// <para>
    /// 火箭模式 Hub 不启动游戏、也没有进程句柄，游戏退出时它不一定在场；而主 ini 是各游戏共用的，
    /// 下次准备时的 <see cref="Activate"/> 会用 profile 把它盖掉 —— 收不回来就等于改动丢了。
    /// 要收就得知道它属于哪个游戏，光看 ini 内容看不出来，所以在每次铺进主 ini 的
    /// <see cref="Activate"/> 里顺手记一笔。放在构建目录、不放进 ini，免得这份记账跟着
    /// profile → 配置一路污染用户自己的配置。
    /// </para>
    /// </summary>
    public const string RuntimeOwnerFileName = ".hub-runtime-owner";

    /// <summary>构建目录里放各游戏 ini 的文件夹</summary>
    public static string ProfileDirectory(string buildDirectory)
        => Path.Combine(buildDirectory, ProfilesFolderName);

    /// <summary>某个游戏在该构建里的 ini 路径</summary>
    public static string ProfilePath(string buildDirectory, string gameKey)
        => Path.Combine(ProfileDirectory(buildDirectory), Sanitize(gameKey) + ".ini");

    /// <summary>
    /// 注入前激活指定游戏的 ini。
    /// 首次使用（该游戏还没有 profile）从当前主 ini 继承一份；否则用 profile 覆盖主 ini。
    /// </summary>
    /// <returns>true 表示已切换；主 ini 不存在时 false</returns>
    public static bool Activate(string buildDirectory, string gameKey)
    {
        lock (Gate) return ActivateCore(buildDirectory, gameKey);
    }

    private static bool ActivateCore(string buildDirectory, string gameKey)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory)
            || string.IsNullOrWhiteSpace(gameKey))
        {
            return false;
        }

        string mainIni = Path.Combine(buildDirectory, OptiScalerRuntime.ConfigFileName);
        if (!File.Exists(mainIni))
        {
            return false;
        }

        string profile = ProfilePath(buildDirectory, gameKey);
        try
        {
            if (!CollectRuntimeChangesChecked(buildDirectory).Succeeded) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            if (!File.Exists(profile))
            {
                AtomicWrite(profile, File.ReadAllBytes(mainIni));
            }
            return CommitActivation(buildDirectory, gameKey, File.ReadAllBytes(profile));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>主 ini 当前属于哪个游戏；没记过（或读不出来）返回 null —— 这时绝不猜</summary>
    public static string? RuntimeOwner(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(buildDirectory, RuntimeOwnerFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            string owner = File.ReadAllText(path).Trim();
            return owner.Length == 0 ? null : owner;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把上一次运行留在主 ini 里的改动收回到它自己的 profile（不碰别人的）。
    ///
    /// <para>
    /// 用在「Hub 没抓到游戏退出」的场合（火箭模式最常见）：下一次 <see cref="Activate"/> 会拿
    /// profile 盖掉主 ini，不收就等于用户这次在游戏里改的全丢。
    /// </para>
    ///
    /// <para>
    /// 三种情况**必须什么都不做**：记账里没有游戏标识（老构建目录 / 记账丢了）、那个游戏还没有
    /// profile、主 ini 和 profile 内容一模一样。前两种是因为宁可不动也不能把某个游戏的状态写进
    /// 另一个游戏的 profile；第三种是因为「没改动」时回写会拿 profile 盖掉用户刚手改过的配置。
    /// </para>
    /// </summary>
    /// <returns>收回来的游戏标识；没有需要收回的改动返回 null</returns>
    public static string? CollectRuntimeChanges(string buildDirectory)
    {
        var result = CollectRuntimeChangesChecked(buildDirectory);
        if (!result.Succeeded) throw new IOException("Runtime profile collection is blocked or failed.");
        return result.Status == CollectionStatus.Collected ? result.Owner : null;
    }

    public static RuntimeCollectionResult CollectRuntimeChangesChecked(string buildDirectory)
    {
        lock (Gate)
        {
        if (File.Exists(Path.Combine(buildDirectory, RuntimeTransactionFileName)))
            return new(CollectionStatus.Blocked, RuntimeOwner(buildDirectory));
        string? owner = RuntimeOwner(buildDirectory);
        if (owner is null)
        {
            return new(CollectionStatus.Unchanged, null);
        }

        string mainIni = Path.Combine(buildDirectory, OptiScalerRuntime.ConfigFileName);
        string profile = ProfilePath(buildDirectory, owner);
        if (!File.Exists(mainIni) || !File.Exists(profile))
        {
            return new(CollectionStatus.Blocked, owner);
        }

        try
        {
            string baseline = Path.Combine(buildDirectory, RuntimeBaselineFileName);
            byte[] main = File.ReadAllBytes(mainIni);
            byte[] saved = File.ReadAllBytes(profile);
            if (!File.Exists(baseline))
            {
                if (!main.AsSpan().SequenceEqual(saved)) return new(CollectionStatus.Blocked, owner);
                AtomicWrite(baseline, main);
                return new(CollectionStatus.Unchanged, owner);
            }
            byte[] original = File.ReadAllBytes(baseline);
            if (main.AsSpan().SequenceEqual(original)) return new(CollectionStatus.Unchanged, owner);
            if (!saved.AsSpan().SequenceEqual(original) && !saved.AsSpan().SequenceEqual(main))
                return new(CollectionStatus.Blocked, owner);
            AtomicWrite(profile, main);
            AtomicWrite(baseline, main);
            return new(CollectionStatus.Collected, owner);
        }
        catch
        {
            return new(CollectionStatus.Failed, owner);
        }
        }
    }

    public static bool TryPrepareProfileForRead(string buildDirectory, string gameKey)
    {
        lock (Gate)
        {
            if (!CollectRuntimeChangesChecked(buildDirectory).Succeeded) return false;
            string profile = ProfilePath(buildDirectory, gameKey);
            if (File.Exists(profile)) return true;
            return ActivateCore(buildDirectory, gameKey);
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public static bool TryWriteProfile(string buildDirectory, string gameKey, string content, string? expectedContent = null)
    {
        lock (Gate)
        {
            try
            {
                string path = ProfilePath(buildDirectory, gameKey);
                if (expectedContent is not null && (!File.Exists(path)
                    || !string.Equals(File.ReadAllText(path), expectedContent, StringComparison.Ordinal))) return false;
                AtomicWrite(path, new System.Text.UTF8Encoding(false).GetBytes(content));
                return true;
            }
            catch { return false; }
        }
    }

    public static bool TryUpdateActiveProfile(string buildDirectory, string gameKey, Func<string, string> transform)
    {
        lock (Gate)
        {
            try
            {
                if (!string.Equals(RuntimeOwner(buildDirectory), gameKey, StringComparison.Ordinal)
                    || !CollectRuntimeChangesChecked(buildDirectory).Succeeded) return false;
                string main = Path.Combine(buildDirectory, OptiScalerRuntime.ConfigFileName);
                if (!File.ReadAllBytes(main).AsSpan().SequenceEqual(File.ReadAllBytes(ProfilePath(buildDirectory, gameKey))))
                    return false;
                string updated = transform(File.ReadAllText(main));
                return CommitActivation(buildDirectory, gameKey,
                    new System.Text.UTF8Encoding(false).GetBytes(updated), updateProfile: true);
            }
            catch { return false; }
        }
    }

    private static bool CommitActivation(string directory, string gameKey, byte[] content, bool updateProfile = false)
    {
        string[] paths = [Path.Combine(directory, OptiScalerRuntime.ConfigFileName),
            Path.Combine(directory, RuntimeBaselineFileName), Path.Combine(directory, RuntimeOwnerFileName)];
        if (updateProfile) paths = [.. paths, ProfilePath(directory, gameKey)];
        byte[]?[] previous = paths.Select(p => File.Exists(p) ? File.ReadAllBytes(p) : null).ToArray();
        string transaction = Path.Combine(directory, RuntimeTransactionFileName);
        try
        {
            AtomicWrite(transaction, new System.Text.UTF8Encoding(false).GetBytes(gameKey));
            AtomicWrite(paths[0], content);
            AtomicWrite(paths[1], content);
            AtomicWrite(paths[2], new System.Text.UTF8Encoding(false).GetBytes(gameKey));
            if (updateProfile) AtomicWrite(paths[3], content);
            File.Delete(transaction);
            return true;
        }
        catch
        {
            for (int i = paths.Length - 1; i >= 0; i--)
                try
                {
                    if (previous[i] is { } bytes) AtomicWrite(paths[i], bytes);
                    else if (File.Exists(paths[i])) File.Delete(paths[i]);
                }
                catch { }
            // A failed rollback keeps the transaction marker and blocks future collection.
            try
            {
                if (paths.Select((p, i) => previous[i] is { } b
                        ? File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(b) : !File.Exists(p)).All(x => x))
                    File.Delete(transaction);
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// 游戏退出后把主 ini 回写到该游戏的 profile（叠加层里「Save Settings」的改动由此保留）。
    /// </summary>
    public static bool Store(string buildDirectory, string gameKey)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || string.IsNullOrWhiteSpace(gameKey))
        {
            return false;
        }

        lock (Gate)
        {
            if (!string.Equals(RuntimeOwner(buildDirectory), gameKey, StringComparison.Ordinal)) return false;
            return CollectRuntimeChangesChecked(buildDirectory).Succeeded;
        }
    }

    /// <summary>游戏标识只取安全文件名字符，防止 biz 串带路径分隔符</summary>
    private static string Sanitize(string gameKey)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            gameKey = gameKey.Replace(c, '_');
        }

        return gameKey;
    }
}
