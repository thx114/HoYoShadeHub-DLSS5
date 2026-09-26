using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>一台活动显示器的高级颜色（Windows HDR）状态。没读到名字就用 \.\DISPLAY? 占位。</summary>
internal sealed record DisplayHdrEntry(string DeviceName, bool Supported, bool Enabled);

/// <summary>
/// 读 / 改「显示器的 Windows HDR（高级颜色）」状态。
///
/// <para>
/// 为什么需要它：RTX HDR（NVIDIA app 那个把 SDR 游戏重映射成 HDR 的滤镜）**要求显示器的
/// Windows HDR 是打开的**。HDR 没开的时候，RTX HDR 这个开关开着也一点效果都没有 ——
/// 用户反馈的就是这个（「就算开着也是失效的」）。
/// </para>
///
/// <para>
/// 实现走 DisplayConfig（<c>user32!QueryDisplayConfig</c> +
/// <c>DISPLAYCONFIG_DEVICE_INFO_GET/SET_ADVANCED_COLOR_INFO</c>），这是 Win32 应用唯一的
/// 公开途径；读不到一律返回空 / 说明，绝不猜一个值。显示器的 HDR 也**不能**从注册表判断 ——
/// <c>VideoSettings\EnableHDRForPlayback</c> 那个键说的是「播放流式 HDR 视频」，和桌面 HDR 无关。
/// </para>
/// </summary>
internal sealed class DisplayHdrState
{
    private static readonly ILogger Logger = AppConfig.GetLogger<DisplayHdrState>();

    // DISPLAYCONFIG_DEVICE_INFO_TYPE
    private const uint GetSourceName = 1;
    private const uint GetAdvancedColorInfo = 9;
    private const uint SetAdvancedColorState = 10;

    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER(20) + WCHAR viewGdiDeviceName[32]</summary>
    private const uint SourceNameRequestSize = 84;

    /// <summary>header(20) + union(4) + colorEncoding(4) + bitsPerColorChannel(4)</summary>
    private const uint AdvancedColorRequestSize = 32;

    /// <summary>header(20) + union(4)</summary>
    private const uint SetAdvancedColorRequestSize = 24;

    /// <summary>DISPLAYCONFIG_MODE_INFO 的字节数（我们只当它是块占位内存，不读内容）</summary>
    private const int ModeInfoSize = 64;

    /// <summary>DISPLAYCONFIG_PATH_INFO 的字节数；对不上就说明结构布局变了，直接放弃检测</summary>
    private const int PathInfoSize = 72;

    /// <summary>所有活动显示器的高级颜色状态；读不到返回空表（调用方按「读不到」处理）。</summary>
    public static IReadOnlyList<DisplayHdrEntry> Read()
    {
        var result = new List<DisplayHdrEntry>();

        try
        {
            if (Marshal.SizeOf<PathInfo>() != PathInfoSize)
            {
                Logger.LogDebug("DISPLAYCONFIG_PATH_INFO 布局不是预期的 {Size} 字节，跳过显示器 HDR 检测",
                    Marshal.SizeOf<PathInfo>());
                return result;
            }

            if (!TryQueryPaths(out byte[] paths, out uint count))
            {
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                int offset = i * PathInfoSize;
                Luid sourceAdapter = ReadLuid(paths, offset);
                uint sourceId = ReadUInt(paths, offset + 8);
                Luid targetAdapter = ReadLuid(paths, offset + 20);
                uint targetId = ReadUInt(paths, offset + 28);
                bool targetAvailable = ReadUInt(paths, offset + 60) != 0;

                if (!targetAvailable)
                {
                    continue;
                }

                (bool Supported, bool Enabled)? color = ReadAdvancedColor(targetAdapter, targetId);
                if (color is null)
                {
                    continue;
                }

                string name = ReadSourceName(sourceAdapter, sourceId) ?? @"\.DISPLAY?";
                result.Add(new DisplayHdrEntry(name, color.Value.Supported, color.Value.Enabled));
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读显示器 HDR 状态失败");
            result.Clear();
        }

        return result;
    }

    /// <summary>有没有任何一台活动显示器开着 Windows HDR；读不到返回 null。</summary>
    public static bool? AnyEnabled(IReadOnlyList<DisplayHdrEntry> entries)
        => entries.Count == 0 ? null : entries.Any(e => e.Enabled);

    /// <summary>有没有「支持 HDR 但没开」的显示器（可以一键打开）</summary>
    public static DisplayHdrEntry? FirstSupportedButOff(IReadOnlyList<DisplayHdrEntry> entries)
    {
        foreach (DisplayHdrEntry entry in entries)
        {
            if (entry.Supported && !entry.Enabled)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>一键打开 Windows HDR 时优先挑的那台：主显示器（\.\DISPLAY1）优先，否则第一台支持的。</summary>
    public static DisplayHdrEntry? PickForEnable(IReadOnlyList<DisplayHdrEntry> entries)
    {
        foreach (DisplayHdrEntry entry in entries)
        {
            if (entry.Supported
                && string.Equals(entry.DeviceName, @"\.DISPLAY1", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return FirstSupportedButOff(entries);
    }

    /// <summary>把人话拼给界面用：`\.DISPLAY1（支持，未开）` 之类。</summary>
    public static string Describe(IReadOnlyList<DisplayHdrEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "读不到显示器状态";
        }

        var parts = new List<string>();
        foreach (DisplayHdrEntry entry in entries)
        {
            string state = entry.Enabled ? "HDR 开着" : entry.Supported ? "支持但没开" : "不支持 HDR";
            parts.Add($"{entry.DeviceName}（{state}）");
        }

        return string.Join("、", parts);
    }

    /// <summary>给某台显示器开 / 关 Windows HDR；成功返回 null，失败返回给用户看的说明。</summary>
    public static string? SetEnabled(string? deviceName, bool enabled)
    {
        try
        {
            if (Marshal.SizeOf<PathInfo>() != PathInfoSize || !TryQueryPaths(out byte[] paths, out uint count))
            {
                return "读不到当前显示器拓扑，没法直接改；可以到 Windows 设置 → 系统 → 显示 → HDR 手动打开。";
            }

            for (int i = 0; i < count; i++)
            {
                int offset = i * PathInfoSize;
                Luid sourceAdapter = ReadLuid(paths, offset);
                uint sourceId = ReadUInt(paths, offset + 8);
                Luid targetAdapter = ReadLuid(paths, offset + 20);
                uint targetId = ReadUInt(paths, offset + 28);

                string? name = ReadSourceName(sourceAdapter, sourceId);
                if (name is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(deviceName)
                    && !name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] request = BuildRequest(SetAdvancedColorState, SetAdvancedColorRequestSize, targetAdapter, targetId);
                WriteUInt(request, 20, enabled ? 1u : 0u);

                int rc = DisplayConfigSetDeviceInfo(request);
                if (rc == ErrorSuccess)
                {
                    return null;
                }

                Logger.LogDebug("DisplayConfigSetDeviceInfo 失败：{Code}", rc);

                return rc == ErrorInvalidParameter
                    ? "这台机器不允许直接改 HDR（Win11 24H2 起这套接口变了）；到 Windows 设置 → 系统 → 显示 → HDR 手动打开也一样。"
                    : $"改 HDR 失败（错误码 {rc}），到 Windows 设置 → 系统 → 显示 → HDR 手动打开试试。";
            }

            return "没找到要改的那台显示器。";
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "改显示器 HDR 失败");
            return "改显示器 HDR 出错：" + ex.Message;
        }
    }

    private static bool TryQueryPaths(out byte[] paths, out uint count)
    {
        paths = [];
        count = 0;

        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint numPaths, out uint numModes) != ErrorSuccess)
        {
            return false;
        }

        // 插拔显示器 / 切分辨率会让两次调用之间拓扑变化：给点余量，不够就重问一次再试
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint pathCount = numPaths + 4;
            uint modeCount = numModes + 8;

            byte[] pathBuffer = new byte[pathCount * PathInfoSize];
            byte[] modeBuffer = new byte[modeCount * ModeInfoSize];

            int rc = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            if (rc == ErrorSuccess)
            {
                paths = pathBuffer;
                count = pathCount;
                return true;
            }

            if (rc != ErrorInsufficientBuffer)
            {
                Logger.LogDebug("QueryDisplayConfig 失败：{Code}", rc);
                return false;
            }

            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out numPaths, out numModes) != ErrorSuccess)
            {
                return false;
            }
        }

        return false;
    }

    private static string? ReadSourceName(Luid adapter, uint id)
    {
        byte[] request = BuildRequest(GetSourceName, SourceNameRequestSize, adapter, id);
        if (DisplayConfigGetDeviceInfo(request) != ErrorSuccess)
        {
            return null;
        }

        int end = 20;
        while (end + 1 < request.Length && !(request[end] == 0 && request[end + 1] == 0))
        {
            end += 2;
        }

        return end <= 20 ? null : Encoding.Unicode.GetString(request, 20, end - 20);
    }

    private static (bool Supported, bool Enabled)? ReadAdvancedColor(Luid adapter, uint id)
    {
        byte[] request = BuildRequest(GetAdvancedColorInfo, AdvancedColorRequestSize, adapter, id);
        if (DisplayConfigGetDeviceInfo(request) != ErrorSuccess)
        {
            return null;
        }

        uint value = ReadUInt(request, 20);
        return ((value & 1) != 0, (value & 2) != 0);
    }

    private static byte[] BuildRequest(uint type, uint size, Luid adapter, uint id)
    {
        byte[] buffer = new byte[size];
        WriteUInt(buffer, 0, type);
        WriteUInt(buffer, 4, size);
        WriteUInt(buffer, 8, adapter.LowPart);
        WriteUInt(buffer, 12, (uint)adapter.HighPart);
        WriteUInt(buffer, 16, id);
        return buffer;
    }

    private static Luid ReadLuid(byte[] buffer, int offset)
        => new() { LowPart = ReadUInt(buffer, offset), HighPart = (int)ReadUInt(buffer, offset + 4) };

    private static uint ReadUInt(byte[] buffer, int offset)
        => (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    private static void WriteUInt(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [In, Out] byte[] pathInfoArray, ref uint numModeInfoArrayElements, [In, Out] byte[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] deviceInfo);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo([In, Out] byte[] deviceInfo);
}
