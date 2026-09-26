using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// NVIDIA 驱动配置（DRS）读写：给「AI 插帧」开关和兼容性检测里的「DLSS-FG 帧生成模型预设」用。
/// 结构体和接口 ID 与 <see cref="NvDrsMfgCount"/> 一致（NVDRS_SETTING_V1 = 12320 字节，
/// NVDRS_APPLICATION_V4 = 20492 字节，NVDRS_PROFILE_V1 = 4116 字节）。
/// </summary>
internal static class NvDrsInterop
{
    // ---- 接口 ID（nvapi_interface.h） ----
    private const uint IdInitialize = 0x0150e828;
    private const uint IdCreateSession = 0x0694d52e;
    private const uint IdDestroySession = 0xdad9cff8;
    private const uint IdLoadSettings = 0x375dbd6b;
    private const uint IdSaveSettings = 0xfcbc7e14;
    private const uint IdFindApplicationByName = 0xeee566b2;
    private const uint IdGetSetting = 0x73bf8338;
    private const uint IdSetSetting = 0x577dd202;

    // 扩展接口：比公开版多几个保留参数，能读写驱动不公开的设置（Smooth Motion - Enable 就是这种）。
    // NVIDIA Profile Inspector 也是用这两条。
    private const uint IdGetSettingExtended = 0xEA99498D;
    private const uint IdSetSettingExtended = 0x8A2CF5F5;
    private const uint IdGetSettingNameFromId = 0xd61cbe6e;
    private const uint IdGetSettingIdFromName = 0xcb7309cd;
    private const uint IdCreateProfile = 0xcc176068;
    private const uint IdCreateApplication = 0x4347a9de;
    private const uint IdGetCurrentGlobalProfile = 0x617bff9f;

    /// <summary>「Override DLSS-FG preset」。</summary>
    public const uint DlssFgPresetSettingId = 0x10E41DF1;

    /// <summary>帧生成模型预设 B。A=1、B=2、…、Z=26，0x00FFFFFE=Default、0x00FFFFFF=Latest。</summary>
    public const uint DlssFgPresetB = 0x00000002;

    /// <summary>「Smooth Motion - Enable」：AI 插帧总开关，Off=0 / On=1。只能用扩展接口读写。</summary>
    public const uint SmoothMotionEnableSettingId = 0xB0D384C0;

    /// <summary>「Smooth Motion - Enabled APIs」：允许用插帧的 API 掩码（DX12=1、DX11=2、Vulkan=4）。</summary>
    public const uint SmoothMotionApisSettingId = 0xB0CC0875;

    /// <summary>全允许掩码：DX12 + DX11 + Vulkan。</summary>
    public const uint SmoothMotionAllApis = 0x00000007;

    /// <summary>
    /// 「Ultra Low Latency - Enabled」：**真正**让驱动低延迟调度生效的开关（Off=0 / On=1）。
    /// NVIDIA Profile Inspector 的自定义设置表（CustomSettingNames.xml）里就是这个 ID；
    /// 之前只写 CPL State，看着像改了，驱动其实没启用低延迟。
    /// </summary>
    public const uint UltraLowLatencyEnabledSettingId = 0x10835000;

    /// <summary>低延迟模式：开。</summary>
    public const uint UltraLowLatencyEnabledOn = 0x00000001;

    /// <summary>设置名（日志 / 报错用）。</summary>
    public const string UltraLowLatencyEnabledSettingName = "Ultra Low Latency - Enabled";

    /// <summary>
    /// 「Ultra Low Latency - CPL State」：驱动给 NVIDIA 控制面板记的下拉镜像值（Off=0 / On=1 / Ultra=2），
    /// 本身不改变驱动行为 —— 面板显示「Ultra」靠它，真正生效靠
    /// <see cref="UltraLowLatencyEnabledSettingId"/>，两个都写。
    /// </summary>
    public const uint UltraLowLatencyCplStateSettingId = 0x0005F543;

    /// <summary>CPL 下拉显示成 Ultra。</summary>
    public const uint UltraLowLatencyCplUltra = 0x00000002;

    /// <summary>CPL State 的设置名（日志 / 报错用）。</summary>
    public const string UltraLowLatencyCplStateSettingName = "Ultra Low Latency - CPL State";

    /// <summary>「RTX HDR - Enable」：NVIDIA App 的 RTX HDR 开关（0/1）。公开接口读不到，要走扩展接口。</summary>
    public const uint RtxHdrEnableSettingId = 0x00DD48FB;

    /// <summary>「RTX Dynamic Vibrance - Enable」：NVIDIA App 的动态亮丽开关（0/1）。同样只能走扩展接口。</summary>
    public const uint RtxDynamicVibranceEnableSettingId = 0x00980880;

    /// <summary>「Enable DLSS-FG override」：NVIDIA App 的 DLSS 帧生成覆盖开关（0/1）。</summary>
    public const uint DlssFgOverrideEnableSettingId = 0x10E41E03;

    /// <summary>「Override DLSSG mode」：DLSSG 模式覆盖。</summary>
    public const uint DlssgModeSettingId = 0x10308298;

    /// <summary>「Override DLSSG multi-frame count」：多帧生成数量覆盖（写 0 = N/A，即不覆盖）。</summary>
    public const uint DlssgMultiFrameCountSettingId = 0x104D6667;

    /// <summary>设置名（日志用）。</summary>
    public const string SmoothMotionSettingName = "Smooth Motion - Enable";

    /// <summary>「Enabled APIs」的设置名（日志用）。</summary>
    public const string SmoothMotionApisSettingName = "Smooth Motion - Enabled APIs";

    // ---- 结构体布局（同 NvDrsMfgCount） ----
    private const int SettingSize = 12320;
    private const int SettingIdOffset = 4100;
    private const int SettingTypeOffset = 4104;
    private const int SettingLocationOffset = 4108;
    private const int CurrentValueOffset = 8220;   // union2 起始（union 各 4100 字节）
    private const int UnicodeMax = 2048;

    private const int AppSize = 20492;             // NVDRS_APPLICATION_V4
    private const int AppNameOffset = 8;
    private const int AppFriendlyNameOffset = 8 + 4096;

    private const int ProfileSize = 4116;          // NVDRS_PROFILE_V1
    private const int ProfileNameOffset = 4;

    private const int TypeDword = 0;
    private const int LocationCurrentProfile = 0;

    /// <summary>设置值在驱动里的来源</summary>
    public enum ValueSource
    {
        CurrentProfile = 0,
        GlobalProfile = 1,
        BaseProfile = 2,
        DefaultProfile = 3,
    }

    /// <summary>读出来的状态；<see cref="Error"/> 非空表示这次没读成</summary>
    public sealed record State(bool Ok, bool Overridden, uint Value, string Display, string? Error, int Status = 0, bool NotStored = false);

    // ---- 函数指针 ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionDelegate(out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindApplicationByNameDelegate(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingDelegate(IntPtr session, IntPtr profile, IntPtr setting);

    /// <summary>扩展版读：比公开版多一个保留参数（NPI 传 0）</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingExtendedDelegate(IntPtr session, IntPtr profile, uint settingId, IntPtr setting, ref uint reserved);

    /// <summary>扩展版写：比公开版多两个保留参数（NPI 传 0、0）</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingExtendedDelegate(IntPtr session, IntPtr profile, IntPtr setting, uint reserved1, uint reserved2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingNameFromIdDelegate(uint settingId, IntPtr nameBuffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingIdFromNameDelegate(IntPtr nameBuffer, out uint settingId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetProfileDelegate(IntPtr session, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateProfileDelegate(IntPtr session, IntPtr profile, out IntPtr newProfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateApplicationDelegate(IntPtr session, IntPtr profile, IntPtr application);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvApiQueryInterface(uint id);

    private static T? Resolve<T>(uint id) where T : Delegate
    {
        IntPtr ptr;
        try
        {
            ptr = NvApiQueryInterface(id);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static void WriteWideString(IntPtr buffer, int byteOffset, string text, int maxChars)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        int limit = Math.Max(0, (maxChars - 1) * 2);
        int count = Math.Min(bytes.Length, limit);
        if (count > 0)
        {
            Marshal.Copy(bytes, 0, buffer + byteOffset, count);
        }
    }

    /// <summary>读当前游戏某项设置（不修改）。</summary>
    public static State Read(string exeFileName, uint settingId, string settingName, Func<uint, string>? describe = null) =>
        Run(exeFileName, settingId, settingName, writeValue: null, describe: describe, profileTitle: null, appFriendlyName: null, createIfMissing: false);

    /// <summary>读「全局配置」里的某项设置（NVIDIA App 的全局开关一般落在这里）。</summary>
    public static State ReadGlobal(uint settingId, string settingName, Func<uint, string>? describe = null)
    {
        describe ??= value => $"0x{value:X8}";

        string? initError = TryInit(out _);
        if (initError is not null)
        {
            return new State(false, false, 0, string.Empty, initError);
        }

        var createSession = Resolve<CreateSessionDelegate>(IdCreateSession);
        var destroySession = Resolve<SessionDelegate>(IdDestroySession);
        var loadSettings = Resolve<SessionDelegate>(IdLoadSettings);
        var getSetting = Resolve<GetSettingDelegate>(IdGetSetting);
        var getSettingExtended = Resolve<GetSettingExtendedDelegate>(IdGetSettingExtended);
        var getGlobalProfile = Resolve<GetProfileDelegate>(IdGetCurrentGlobalProfile);

        if (createSession is null || destroySession is null || loadSettings is null || getGlobalProfile is null
            || (getSetting is null && getSettingExtended is null))
        {
            return new State(false, false, 0, string.Empty, "驱动没提供 DRS 接口（NvAPI_DRS_*）。");
        }

        int status = createSession(out IntPtr session);
        if (status != 0)
        {
            return new State(false, false, 0, string.Empty, $"NvAPI_DRS_CreateSession 失败（{status}）。");
        }

        try
        {
            status = loadSettings(session);
            if (status != 0)
            {
                return new State(false, false, 0, string.Empty, $"NvAPI_DRS_LoadSettings 失败（{status}）。");
            }

            status = getGlobalProfile(session, out IntPtr profile);
            if (status != 0)
            {
                return new State(false, false, 0, string.Empty, $"读全局配置失败（{status}）。");
            }

            return ReadSetting(session, profile, settingId, settingName, describe, getSetting, getSettingExtended);
        }
        catch (Exception ex)
        {
            return new State(false, false, 0, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            destroySession(session);
        }
    }

    /// <summary>
    /// 把当前游戏的某项设置写成 DWORD。游戏在驱动里还没有配置条目时，会先建一个
    /// 「HoYoShadeHub - ？」条目并把 exe 挂上去（等价于 NVIDIA App 给游戏建条目）。
    /// </summary>
    public static State WriteDword(string exeFileName, uint settingId, uint value, string settingName, Func<uint, string>? describe = null, string? profileTitle = null, string? appFriendlyName = null) =>
        Run(exeFileName, settingId, settingName, writeValue: value, describe: describe, profileTitle: profileTitle, appFriendlyName: appFriendlyName, createIfMissing: true);

    /// <summary>按名字解析设置 ID（NvAPI_DRS_GetSettingIdFromName）。解析不到返回 ok=false。</summary>
    public static (bool Ok, uint Id, string? Error) ResolveSettingId(string settingName)
    {
        var initialize = Resolve<InitializeDelegate>(IdInitialize);
        var createSession = Resolve<CreateSessionDelegate>(IdCreateSession);
        var destroySession = Resolve<SessionDelegate>(IdDestroySession);
        var loadSettings = Resolve<SessionDelegate>(IdLoadSettings);
        var getIdFromName = Resolve<GetSettingIdFromNameDelegate>(IdGetSettingIdFromName);

        if (initialize is null)
        {
            return (false, 0, "读不到 nvapi64.dll 的 nvapi_QueryInterface  这台机器可能没装 NVIDIA 驱动。");
        }

        if (createSession is null || destroySession is null || loadSettings is null || getIdFromName is null)
        {
            return (false, 0, "驱动没提供 DRS 接口（NvAPI_DRS_*）。");
        }

        _ = initialize();

        IntPtr nameBuffer = IntPtr.Zero;
        int status = createSession(out IntPtr session);
        if (status != 0)
        {
            return (false, 0, $"NvAPI_DRS_CreateSession 失败（{status}）。");
        }

        try
        {
            status = loadSettings(session);
            if (status != 0)
            {
                return (false, 0, $"NvAPI_DRS_LoadSettings 失败（{status}）。");
            }

            nameBuffer = Marshal.AllocHGlobal(UnicodeMax * 2);
            Marshal.Copy(new byte[UnicodeMax * 2], 0, nameBuffer, UnicodeMax * 2);
            WriteWideString(nameBuffer, 0, settingName, UnicodeMax);

            status = getIdFromName(nameBuffer, out uint settingId);
            if (status != 0)
            {
                return (false, 0, $"驱动不认识「{settingName}」（NvAPI_DRS_GetSettingIdFromName 返回 {status}，-160 = 没这项）。");
            }

            return (true, settingId, null);
        }
        catch (Exception ex)
        {
            return (false, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (nameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nameBuffer);
            }

            destroySession(session);
        }
    }

    private static State Run(string exeFileName, uint settingId, string settingName, uint? writeValue, Func<uint, string>? describe, string? profileTitle, string? appFriendlyName, bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return new State(false, false, 0, string.Empty, "没拿到这个游戏的 exe 文件名，无法定位驱动配置。");
        }

        describe ??= value => $"0x{value:X8}";

        string? initError = TryInit(out _);
        if (initError is not null)
        {
            return new State(false, false, 0, string.Empty, initError);
        }

        var createSession = Resolve<CreateSessionDelegate>(IdCreateSession);
        var destroySession = Resolve<SessionDelegate>(IdDestroySession);
        var loadSettings = Resolve<SessionDelegate>(IdLoadSettings);
        var saveSettings = Resolve<SessionDelegate>(IdSaveSettings);
        var findApplication = Resolve<FindApplicationByNameDelegate>(IdFindApplicationByName);
        var getSetting = Resolve<GetSettingDelegate>(IdGetSetting);
        var setSetting = Resolve<SetSettingDelegate>(IdSetSetting);
        var getSettingExtended = Resolve<GetSettingExtendedDelegate>(IdGetSettingExtended);
        var setSettingExtended = Resolve<SetSettingExtendedDelegate>(IdSetSettingExtended);
        var createProfile = Resolve<CreateProfileDelegate>(IdCreateProfile);
        var createApplication = Resolve<CreateApplicationDelegate>(IdCreateApplication);

        if (createSession is null || destroySession is null || loadSettings is null
            || findApplication is null || (getSetting is null && getSettingExtended is null))
        {
            return new State(false, false, 0, string.Empty, "驱动没提供 DRS 接口（NvAPI_DRS_*）。");
        }

        if (writeValue is not null
            && (setSetting is null && setSettingExtended is null || createProfile is null || createApplication is null))
        {
            return new State(false, false, 0, string.Empty, "驱动没提供 NvAPI_DRS_SetSetting / CreateProfile / CreateApplication。");
        }

        int status = createSession(out IntPtr session);
        if (status != 0)
        {
            return new State(false, false, 0, string.Empty, $"NvAPI_DRS_CreateSession 失败（{status}）。");
        }

        IntPtr appBuffer = IntPtr.Zero;
        IntPtr settingBuffer = IntPtr.Zero;
        try
        {
            status = loadSettings(session);
            if (status != 0)
            {
                return new State(false, false, 0, string.Empty, $"NvAPI_DRS_LoadSettings 失败（{status}）。");
            }

            appBuffer = Marshal.AllocHGlobal(AppSize);
            Marshal.Copy(new byte[AppSize], 0, appBuffer, AppSize);
            Marshal.WriteInt32(appBuffer, 0, AppSize | (4 << 16));
            WriteWideString(appBuffer, AppNameOffset, exeFileName, UnicodeMax);

            status = findApplication(session, appBuffer + AppNameOffset, out IntPtr profile, appBuffer);
            if (status != 0)
            {
                // 驱动里还没有这个 exe 的条目：写的时候可以现建一个（读就只能说没有）
                if (!createIfMissing)
                {
                    return new State(false, false, 0, string.Empty,
                        $"驱动里没有「{exeFileName}」的配置条目（{status}）：先运行一次游戏，或在 NVIDIA App 里打开过它的图形设置。");
                }

                status = EnsureApplication(session, createProfile!, createApplication!, exeFileName, profileTitle, appFriendlyName, out profile, out string? ensureError);
                if (ensureError is not null)
                {
                    return new State(false, false, 0, string.Empty, ensureError);
                }
            }

            if (writeValue is not { } target)
            {
                return ReadSetting(session, profile, settingId, settingName, describe, getSetting, getSettingExtended);
            }

            settingBuffer = Marshal.AllocHGlobal(SettingSize);
            Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
            Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));
            Marshal.WriteInt32(settingBuffer, SettingIdOffset, unchecked((int)settingId));
            Marshal.WriteInt32(settingBuffer, SettingTypeOffset, TypeDword);
            Marshal.WriteInt32(settingBuffer, SettingLocationOffset, LocationCurrentProfile);
            Marshal.WriteInt32(settingBuffer, CurrentValueOffset, unchecked((int)target));

            // 扩展接口优先，失败再退回公开接口
            status = setSettingExtended is not null
                ? setSettingExtended(session, profile, settingBuffer, 0, 0)
                : setSetting!(session, profile, settingBuffer);
            if (status != 0 && setSettingExtended is not null && setSetting is not null)
            {
                status = setSetting(session, profile, settingBuffer);
            }

            if (status != 0)
            {
                string why = status == -137
                    ? "写驱动配置需要管理员权限：请用管理员身份启动 Hub。"
                    : $"写驱动配置失败（{status}）：驱动可能不认识设置 0x{settingId:X8}，或权限不足。";
                return new State(false, false, 0, string.Empty, why, status);
            }

            if (saveSettings is not null)
            {
                status = saveSettings(session);
                if (status != 0)
                {
                    return new State(false, false, 0, string.Empty, $"NvAPI_DRS_SaveSettings 失败（{status}）。");
                }
            }

            // 读回确认
            State after = ReadSetting(session, profile, settingId, settingName, describe, getSetting, getSettingExtended);
            if (!after.Ok || !after.Overridden || after.Value != target)
            {
                return new State(false, after.Overridden, after.Value, after.Display, $"写入后回读对不上（期望 0x{target:X8}，读到 {after.Display}）。");
            }

            return after;
        }
        catch (Exception ex)
        {
            return new State(false, false, 0, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (settingBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(settingBuffer);
            }

            if (appBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(appBuffer);
            }

            destroySession(session);
        }
    }

    /// <summary>驱动里没有这个 exe 的条目时：建一个 profile（名字带 Hub 前缀）再把 exe 挂上去。</summary>
    private static int EnsureApplication(
        IntPtr session,
        CreateProfileDelegate createProfile,
        CreateApplicationDelegate createApplication,
        string exeFileName,
        string? profileTitle,
        string? appFriendlyName,
        out IntPtr profile,
        out string? error)
    {
        profile = IntPtr.Zero;
        error = null;

        IntPtr profileBuffer = Marshal.AllocHGlobal(ProfileSize);
        IntPtr appBuffer = Marshal.AllocHGlobal(AppSize);
        try
        {
            Marshal.Copy(new byte[ProfileSize], 0, profileBuffer, ProfileSize);
            Marshal.WriteInt32(profileBuffer, 0, ProfileSize | (1 << 16));
            WriteWideString(profileBuffer, ProfileNameOffset, profileTitle ?? $"HoYoShadeHub - {exeFileName}", UnicodeMax);

            int status = createProfile(session, profileBuffer, out profile);
            if (status != 0)
            {
                error = $"NvAPI_DRS_CreateProfile 失败（{status}） 驱动配置要管理员权限才能写。";
                return status;
            }

            Marshal.Copy(new byte[AppSize], 0, appBuffer, AppSize);
            Marshal.WriteInt32(appBuffer, 0, AppSize | (4 << 16));
            WriteWideString(appBuffer, AppNameOffset, exeFileName, UnicodeMax);
            WriteWideString(appBuffer, AppFriendlyNameOffset, appFriendlyName ?? exeFileName, UnicodeMax);

            status = createApplication(session, profile, appBuffer);
            if (status != 0)
            {
                error = $"NvAPI_DRS_CreateApplication 失败（{status}）。";
                return status;
            }

            return 0;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(profileBuffer);
            Marshal.FreeHGlobal(appBuffer);
        }
    }

    private static State ReadSetting(
        IntPtr session,
        IntPtr profile,
        uint settingId,
        string settingName,
        Func<uint, string> describe,
        GetSettingDelegate? getSetting,
        GetSettingExtendedDelegate? getSettingExtended)
    {
        IntPtr settingBuffer = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
            Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));

            // 先走扩展接口（能读到 Smooth Motion - Enable 这种驱动不公开的设置），失败再退回公开接口
            uint reserved = 0;
            int status = getSettingExtended is not null
                ? getSettingExtended(session, profile, settingId, settingBuffer, ref reserved)
                : getSetting!(session, profile, settingId, settingBuffer);

            if (status != 0 && getSettingExtended is not null && getSetting is not null)
            {
                Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
                Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));
                status = getSetting(session, profile, settingId, settingBuffer);
            }

            if (status != 0)
            {
                // -160：这个游戏配置里没存过这一条（不代表驱动不支持）
                bool notStored = status == -160;
                string why = notStored
                    ? $"驱动配置里还没有「{settingName}」。"
                    : $"读驱动配置失败（{status}）：设置 0x{settingId:X8} 这台驱动可能没有。";
                return new State(false, false, 0, string.Empty, why, status, notStored);
            }

            int location = Marshal.ReadInt32(settingBuffer, SettingLocationOffset);
            uint current = unchecked((uint)Marshal.ReadInt32(settingBuffer, CurrentValueOffset));
            bool overridden = location == LocationCurrentProfile;

            return new State(true, overridden, current, DescribeValue(current, overridden, describe), null);
        }
        finally
        {
            Marshal.FreeHGlobal(settingBuffer);
        }
    }

    private static string? TryInit(out InitializeDelegate? initialize)
    {
        initialize = Resolve<InitializeDelegate>(IdInitialize);
        if (initialize is null)
        {
            return "读不到 nvapi64.dll 的 nvapi_QueryInterface  这台机器可能没装 NVIDIA 驱动。";
        }

        _ = initialize();
        return null;
    }

    private static string DescribeValue(uint value, bool overridden, Func<uint, string> describe)
    {
        string meaning = describe(value);
        return overridden ? meaning : meaning + "  继承默认";
    }

    /// <summary>「Override DLSS-FG preset」的可读文本（A=1、B=2、…、Z=26）。</summary>
    public static string DescribeFgPreset(uint value) => value switch
    {
        0 => "OFF（0）",
        >= 1 and <= 26 => $"Preset {(char)('A' + value - 1)}（0x{value:X8}）",
        0x00FFFFFE => "Default（0x00FFFFFE）",
        0x00FFFFFF => "Latest（0x00FFFFFF）",
        _ => $"0x{value:X8}（未知）",
    };

    /// <summary>开关型设置的可读文本（0/1）。</summary>
    public static string DescribeBinary(uint value) => value switch
    {
        0 => "OFF（0）",
        1 => "ON（1）",
        _ => $"0x{value:X8}（未知）",
    };

    /// <summary>「Ultra Low Latency - CPL State」的可读文本（面板下拉镜像值）。</summary>
    public static string DescribeUltraLowLatencyCplState(uint value) => value switch
    {
        0 => "Off（0）",
        1 => "On（1）",
        2 => "Ultra（2）",
        _ => $"0x{value:X8}（未知）",
    };

    /// <summary>Smooth Motion 的 API 掩码的可读文本。</summary>
    public static string DescribeSmoothMotionApis(uint value)
    {
        if (value == 0)
        {
            return "OFF（0 任何 API 都不插帧）";
        }

        List<string> parts = [];
        if ((value & 1) != 0)
        {
            parts.Add("DX12");
        }

        if ((value & 2) != 0)
        {
            parts.Add("DX11");
        }

        if ((value & 4) != 0)
        {
            parts.Add("Vulkan");
        }

        string extra = (value & ~7u) == 0 ? string.Empty : $" + 未知位 0x{value & ~7u:X}";
        return $"{string.Join(" + ", parts)}{extra}（0x{value:X8}）";
    }

    /// <summary>读某个游戏的 Smooth Motion 开关（0=关、1=开）。</summary>
    public static State ReadSmoothMotionEnable(string exeFileName) =>
        Read(exeFileName, SmoothMotionEnableSettingId, "Smooth Motion - Enable", DescribeBinary);

    /// <summary>开关某个游戏的 Smooth Motion（写 0xB0D384C0 = 1 / 0，和 NVIDIA App 改的是同一项）。</summary>
    public static State WriteSmoothMotionEnable(string exeFileName, bool enable, string? profileTitle = null, string? appFriendlyName = null) =>
        WriteDword(exeFileName, SmoothMotionEnableSettingId, enable ? 1u : 0u, "Smooth Motion - Enable", DescribeBinary, profileTitle, appFriendlyName);

    /// <summary>读某个游戏的「允许哪些 API 用插帧」掩码（0 = 任何 API 都不允许）。</summary>
    public static State ReadSmoothMotionApis(string exeFileName) =>
        Read(exeFileName, SmoothMotionApisSettingId, SmoothMotionApisSettingName, DescribeSmoothMotionApis);

    /// <summary>读某个游戏的某项 DWORD 设置（低延迟模式用）。</summary>
    public static State ReadDword(string exeFileName, uint settingId, string settingName, Func<uint, string>? describe = null) =>
        Read(exeFileName, settingId, settingName, describe);

}
