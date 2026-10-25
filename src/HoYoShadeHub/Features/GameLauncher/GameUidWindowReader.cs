using HoYoShadeHub.Extensions.Games;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>Reads visible UID corners only, in memory. Never reads game memory or persists screenshots.</summary>
internal static class GameUidWindowReader
{
    public static async Task<string?> ReadAsync(int pid, CancellationToken token, nint expectedWindow = 0)
    {
        token.ThrowIfCancellationRequested();
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint owner);
        if (window == 0 || owner != pid || (expectedWindow != 0 && expectedWindow != window)
            || IsIconic(window) || !IsWindowVisible(window)) return null;
        if (!GetClientRect(window, out Rect rect) || rect.Right < 640 || rect.Bottom < 360) return null;
        Point origin = default;
        if (!ClientToScreen(window, ref origin)) return null;
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        int maximum = (int)OcrEngine.MaxImageDimension;
        int width = Math.Min(rect.Right * 2 / 5, Math.Min(1536, maximum));
        int height = Math.Min(rect.Bottom / 5, Math.Min(512, maximum));
        if (width <= 0 || height <= 0) return null;
        var lines = new List<string>();
        foreach ((int x, int y) in new[]
        {
            (origin.X, origin.Y), (origin.X + rect.Right - width, origin.Y),
            (origin.X, origin.Y + rect.Bottom - height),
            (origin.X + rect.Right - width, origin.Y + rect.Bottom - height),
        })
        {
            token.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != window || !IsRegionOwned(pid, x, y, width, height)) return null;
            using var bitmap = Capture(x, y, width, height);
            if (bitmap is null) return null;
            var result = await engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
            if (GetForegroundWindow() != window || !IsRegionOwned(pid, x, y, width, height)) return null;
            foreach (var line in result.Lines) lines.Add(line.Text);
        }
        GetWindowThreadProcessId(window, out owner);
        if (GetForegroundWindow() != window || owner != pid) return null;
        return UidReportProtocol.ParseVisibleUid(lines);
    }

    // Reject obviously covered/off-screen crops; this cannot detect a forged overlay inside the game process.
    private static bool IsRegionOwned(int pid, int x, int y, int width, int height)
    {
        foreach (Point point in new[] { new Point { X = x + 1, Y = y + 1 },
            new Point { X = x + width - 2, Y = y + 1 }, new Point { X = x + 1, Y = y + height - 2 },
            new Point { X = x + width - 2, Y = y + height - 2 }, new Point { X = x + width / 2, Y = y + height / 2 } })
        {
            GetWindowThreadProcessId(WindowFromPoint(point), out uint owner);
            if (owner != pid) return false;
        }
        return true;
    }

    internal static nint CurrentWindow(int pid)
    {
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint owner);
        return owner == pid ? window : 0;
    }

    private static SoftwareBitmap? Capture(int x, int y, int width, int height)
    {
        nint desktop = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        if (desktop == 0) return null;
        try
        {
            memory = CreateCompatibleDC(desktop);
            if (memory == 0) return null;
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(desktop, ref info, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0) return null;
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) return null;
            if (!BitBlt(memory, 0, 0, width, height, desktop, x, y, 0x00CC0020)) return null;
            if (!GdiFlush()) return null;
            byte[] bytes = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            IBuffer buffer = writer.DetachBuffer();
            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            try { result.CopyFromBuffer(buffer); return result; }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int w, int h, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
