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
    public const string ProfilesFolderName = "profiles";

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
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);

        try
        {
            if (!File.Exists(profile))
            {
                File.Copy(mainIni, profile, overwrite: false);
                return true;
            }

            File.Copy(profile, mainIni, overwrite: true);
            return true;
        }
        catch
        {
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

        string mainIni = Path.Combine(buildDirectory, OptiScalerRuntime.ConfigFileName);
        if (!File.Exists(mainIni))
        {
            return false;
        }

        string profile = ProfilePath(buildDirectory, gameKey);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.Copy(mainIni, profile, overwrite: true);
            return true;
        }
        catch
        {
            return false;
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
