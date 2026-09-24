using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// NVIDIA 驱动配置（DRS）里 DLSS-FG 的「多帧生成数量」覆盖项。
///
/// <para>
/// 事实来源：NVAPI 官方头 <c>NvApiDriverSettings.h</c>（本仓库 OptiScaler 的 external/nvapi 里就有）：
/// <code>
/// NGX_DLSSG_MULTI_FRAME_COUNT_ID = 0x104D6667   // "Override DLSSG multi-frame count"
/// EValues_NGX_DLSSG_MULTI_FRAME_COUNT { OFF = 0, MIN = 1, MAX = 15, DEFAULT = OFF }
/// </code>
/// 它就是 NVIDIA Profile Inspector 里那条 <c>#DLSS-FG- Multi-Frame-Generation Count</c>。
/// </para>
///
/// <para>
/// 「N/A」是 Profile Inspector 对 DWORD 值 <c>0xFFFFFFFF</c> 的显示名（= 不做覆盖 / 不适用）。
/// MFG 解锁（OptiScaler 的 Ada unlock）要求**驱动不要钉数量**，否则游戏里看到的倍数会被驱动覆盖，
/// 所以这里提供「改为 N/A」以及「还原成原值」。
/// </para>
///
/// <para>
/// 结构体布局是按官方头逐字段手算的（<c>NvAPI_UnicodeString</c> 是**内联**的 <c>NvU16[2048]</c>，
/// 不是指针；<c>NVAPI_BINARY_DATA_MAX = 4096</c>）：
/// <code>
/// NVDRS_SETTING_V1 : version@0, settingName@4(4096B), settingId@4100, settingType@4104,
///                    settingLocation@4108, isCurrentPredefined@4112, isPredefinedValid@4116,
///                    union@4120(4100B) x2            => sizeof = 12320
/// </code>
/// 版本字段低 16 位必须等于 sizeof，所以这里用「按 size 反推偏移」的写法，
/// 万一算错也只会拿到 NVAPI 的状态码，不会把驱动配置写坏。
/// </para>
/// </summary>
internal static class NvDrsMfgCount
{
    // ---- nvapi_interface.h 里的接口 ID（权威，勿凭记忆改） ----
    private const uint IdInitialize = 0x0150e828;
    private const uint IdUnload = 0xd22bdd7e;
    private const uint IdCreateSession = 0x0694d52e;
    private const uint IdDestroySession = 0xdad9cff8;
    private const uint IdLoadSettings = 0x375dbd6b;
    private const uint IdSaveSettings = 0xfcbc7e14;
    private const uint IdFindApplicationByName = 0xeee566b2;
    private const uint IdGetSetting = 0x73bf8338;
    private const uint IdSetSetting = 0x577dd202;
    private const uint IdGetSettingNameFromId = 0xd61cbe6e;

    /// <summary>「Override DLSSG multi-frame count」的设置 ID</summary>
    public const uint MultiFrameCountSettingId = 0x104D6667;

    /// <summary>Profile Inspector 里显示成 N/A 的那个值</summary>
    public const uint NaValue = 0xFFFFFFFF;

    // ---- 按官方头算出来的布局 ----
    private const int SettingSize = 12320;
    private const int SettingNameOffset = 4;
    private const int SettingIdOffset = 4100;
    private const int SettingTypeOffset = 4104;
    private const int SettingLocationOffset = 4108;
    private const int CurrentValueOffset = 8220;   // union2 起始（union 各 4100 字节）
    private const int UnicodeMax = 2048;

    private const int AppSize = 20492;
    private const int AppNameOffset = 8;

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
    public sealed record State(bool Ok, bool Overridden, uint Value, string Display, string? Error);

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingNameFromIdDelegate(uint settingId, IntPtr nameBuffer);

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

    private static string? TryInit(out InitializeDelegate? initialize)
    {
        initialize = Resolve<InitializeDelegate>(IdInitialize);
        if (initialize is null)
        {
            return "读不到 nvapi64.dll 的 nvapi_QueryInterface  这台机器可能没装 NVIDIA 驱动。";
        }

        int status = initialize();
        // 已经初始化过（-9 / NVAPI_OK 之外也可能是已加载）都当能用，真正的错误在后续调用里会暴露
        _ = status;
        return null;
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

    private static string ReadWideString(IntPtr buffer, int byteOffset, int maxChars)
    {
        var builder = new StringBuilder();
        for (int i = 0; i + 1 < maxChars * 2; i += 2)
        {
            short value = Marshal.ReadInt16(buffer, byteOffset + i);
            if (value == 0)
            {
                break;
            }

            builder.Append((char)value);
        }

        return builder.ToString();
    }

    /// <summary>把「设置 ID 在驱动里认不认得」问一遍（和结构体大小无关，用来区分「老驱动没这项」）</summary>
    private static string? DescribeSettingId(GetSettingNameFromIdDelegate? getSettingName)
    {
        if (getSettingName is null)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(UnicodeMax * 2);
        try
        {
            int status = getSettingName(MultiFrameCountSettingId, buffer);
            if (status != 0)
            {
                return $"驱动不认识设置 0x{MultiFrameCountSettingId:X8}（NvAPI_DRS_GetSettingNameFromId 返回 {status}） 驱动版本太旧？";
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读当前游戏的 MFG 数量覆盖状态（不修改）。</summary>
    public static State Read(string exeFileName) => Run(exeFileName, writeValue: null);

    /// <summary>把当前游戏的 MFG 数量改为 N/A（0xFFFFFFFF）。</summary>
    public static State SetToNa(string exeFileName) => Run(exeFileName, writeValue: NaValue);

    /// <summary>把当前游戏的 MFG 数量写回指定值（还原用）。</summary>
    public static State Restore(string exeFileName, uint value) => Run(exeFileName, writeValue: value);

    private static State Run(string exeFileName, uint? writeValue)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
        {
            return new State(false, false, 0, string.Empty, "没拿到这个游戏的 exe 文件名，无法定位驱动配置。");
        }

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
        var getSettingName = Resolve<GetSettingNameFromIdDelegate>(IdGetSettingNameFromId);

        if (createSession is null || destroySession is null || loadSettings is null
            || findApplication is null || getSetting is null)
        {
            return new State(false, false, 0, string.Empty, "驱动没提供 DRS 接口（NvAPI_DRS_*）。");
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

            // 先问驱动认不认这个设置项：老驱动可能根本没这项
            string? idError = DescribeSettingId(getSettingName);
            if (idError is not null)
            {
                return new State(false, false, 0, string.Empty, idError);
            }

            appBuffer = Marshal.AllocHGlobal(AppSize);
            Marshal.Copy(new byte[AppSize], 0, appBuffer, AppSize);
            Marshal.WriteInt32(appBuffer, 0, AppSize | (4 << 16));
            WriteWideString(appBuffer, AppNameOffset, exeFileName, UnicodeMax);

            status = findApplication(session, appBuffer + AppNameOffset, out IntPtr profile, appBuffer);
            if (status != 0)
            {
                return new State(false, false, 0, string.Empty,
                    $"NVIDIA 驱动里没有「{exeFileName}」的配置条目（NvAPI_DRS_FindApplicationByName 返回 {status}）。"
                    + "先用 NVIDIA App / Profile Inspector 给这个游戏建过条目，或者先进游戏跑一次再回来。");
            }

            settingBuffer = Marshal.AllocHGlobal(SettingSize);
            Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
            Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));

            status = getSetting(session, profile, MultiFrameCountSettingId, settingBuffer);
            if (status != 0)
            {
                return new State(false, false, 0, string.Empty, $"NvAPI_DRS_GetSetting 失败（{status}）。");
            }

            int location = Marshal.ReadInt32(settingBuffer, SettingLocationOffset);
            uint current = unchecked((uint)Marshal.ReadInt32(settingBuffer, CurrentValueOffset));
            bool overridden = location == LocationCurrentProfile;

            if (writeValue is not { } target)
            {
                return new State(true, overridden, current, Describe(current, overridden), null);
            }

            if (setSetting is null)
            {
                return new State(false, overridden, current, Describe(current, overridden), "驱动没提供 NvAPI_DRS_SetSetting。");
            }

            // 重新构造一份只带要写的字段的 NVDRS_SETTING
            Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
            Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));
            Marshal.WriteInt32(settingBuffer, SettingIdOffset, unchecked((int)MultiFrameCountSettingId));
            Marshal.WriteInt32(settingBuffer, SettingTypeOffset, TypeDword);
            Marshal.WriteInt32(settingBuffer, SettingLocationOffset, LocationCurrentProfile);
            Marshal.WriteInt32(settingBuffer, CurrentValueOffset, unchecked((int)target));

            status = setSetting(session, profile, settingBuffer);
            if (status != 0)
            {
                return new State(false, overridden, current, Describe(current, overridden), $"NvAPI_DRS_SetSetting 失败（{status}）。");
            }

            if (saveSettings is not null)
            {
                status = saveSettings(session);
                if (status != 0)
                {
                    return new State(false, overridden, current, Describe(current, overridden), $"NvAPI_DRS_SaveSettings 失败（{status}）。");
                }
            }

            // 读回确认（写驱动配置要留证据，别只信返回值）
            Marshal.Copy(new byte[SettingSize], 0, settingBuffer, SettingSize);
            Marshal.WriteInt32(settingBuffer, 0, SettingSize | (1 << 16));
            status = getSetting(session, profile, MultiFrameCountSettingId, settingBuffer);
            if (status != 0)
            {
                return new State(false, true, current, Describe(current, true), $"写入后回读失败（{status}）。");
            }

            int newLocation = Marshal.ReadInt32(settingBuffer, SettingLocationOffset);
            uint newValue = unchecked((uint)Marshal.ReadInt32(settingBuffer, CurrentValueOffset));
            bool newOverridden = newLocation == LocationCurrentProfile;
            return new State(true, newOverridden, newValue, Describe(newValue, newOverridden), null);
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

    /// <summary>把数值翻译成人话</summary>
    public static string Describe(uint value, bool overridden)
    {
        if (value == NaValue)
        {
            return overridden ? "N/A（本游戏已覆盖为不适用）" : "N/A（继承默认）";
        }

        string meaning = value switch
        {
            0 => "0（不覆盖 / OFF）",
            >= 1 and <= 15 => $"{value}（覆盖值）",
            _ => $"0x{value:X8}（未知）",
        };

        return overridden ? meaning : meaning + "  继承默认";
    }
}
