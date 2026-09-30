using System.Text;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// DLSS5 预设切换器（dlss5-preset-switcher.addon64）的分享码编解码。
///
/// <para>
/// 与游戏内插件逐字节兼容（src/dlss5-preset-switcher/dlss5-preset-switcher.cpp 的移植）：
/// <c>D5P1</c> 前缀 + 标志字节（1=LZ 压缩）+ uint32 LE 原始长度 + uint32 LE CRC32 +
/// 负载；Base64 用 URL 安全字母表（<c>A-Za-z0-9-_</c>，无填充）。负载上限 256 KiB。
/// 启动器插件页「预设管理」生成的分享码可以直接粘进游戏内插件的 Paste code，
/// 反过来游戏内 Share 出来的码也能在启动器导入。
/// </para>
/// </summary>
public static class Dlss5PresetShareCode
{
    public const string Prefix = "D5P1";

    public const int MaxPayload = 256 * 1024;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>把预设内容编码成分享码（内容超 256 KiB 返回 null）</summary>
    public static string? Encode(byte[] preset)
    {
        if (preset.Length == 0 || preset.Length > MaxPayload)
        {
            return null;
        }

        byte[] compressed = Compress(preset);
        bool useCompression = compressed.Length + 1 < preset.Length;

        var packet = new List<byte>(compressed.Length + 9)
        {
            (byte)(useCompression ? 1 : 0),
        };
        AppendUint32(packet, (uint)preset.Length);
        AppendUint32(packet, Crc32(preset));
        packet.AddRange(useCompression ? compressed : preset);

        return Prefix + EncodeBase64(packet);
    }

    /// <summary>解码分享码；成功返回预设内容，失败返回 null</summary>
    public static byte[]? Decode(string code)
    {
        int first = -1;
        for (int i = 0; i < code.Length; i++)
        {
            if (!char.IsWhiteSpace(code[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0
            || code.Length - first < Prefix.Length
            || !string.Equals(code.Substring(first, Prefix.Length), Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!DecodeBase64(code.Substring(first + Prefix.Length), out List<byte>? packet)
            || packet.Count < 9)
        {
            return null;
        }

        byte flags = packet[0];
        uint expectedSize = ReadUint32(packet, 1);
        uint checksum = ReadUint32(packet, 5);

        if (expectedSize == 0 || expectedSize > MaxPayload)
        {
            return null;
        }

        // 头 = 1 标志 + 4 原始长度 + 4 CRC，共 9 字节；负载从第 10 字节开始
        byte[] payload = packet.GetRange(9, packet.Count - 9).ToArray();
        byte[] preset;
        if (flags == 0)
        {
            if (payload.Length != expectedSize)
            {
                return null;
            }

            preset = payload;
        }
        else if (flags == 1)
        {
            if (!Decompress(payload, expectedSize, out preset))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return Crc32(preset) == checksum ? preset : null;
    }

    /// <summary>这个文本是不是（可能）一个分享码</summary>
    public static bool LooksLikeCode(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);

    // ---- 下面是编码格式内部实现，和游戏内插件一一对应 ----

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; ++bit)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
            }
        }

        return ~crc;
    }

    private static ushort Hash3(byte[] data, int position)
        => (ushort)((data[position] * 251u + data[position + 1] * 17u + data[position + 2]) & 0xFFFFu);

    private static byte[] Compress(byte[] input)
    {
        var output = new List<byte>(input.Length);
        var head = new int[65536];
        Array.Fill(head, -1);
        var previous = new int[input.Length];
        Array.Fill(previous, -1);

        void AddPosition(int position)
        {
            if (position + 2 >= input.Length)
            {
                return;
            }

            ushort hash = Hash3(input, position);
            previous[position] = head[hash];
            head[hash] = position;
        }

        int pos = 0;
        while (pos < input.Length)
        {
            int controlIndex = output.Count;
            output.Add(0);
            byte controls = 0;

            for (int bit = 0; bit < 8 && pos < input.Length; ++bit)
            {
                int bestLength = 0;
                int bestOffset = 0;
                if (pos + 2 < input.Length)
                {
                    ushort hash = Hash3(input, pos);
                    int candidate = head[hash];
                    int candidates = 0;
                    int oldest = pos > 4096 ? pos - 4096 : 0;
                    while (candidate >= oldest && candidate >= 0 && candidates++ < 64)
                    {
                        int length = 0;
                        while (length < 18 && pos + length < input.Length
                            && candidate + length < input.Length
                            && input[candidate + length] == input[pos + length])
                        {
                            ++length;
                        }

                        if (length > bestLength && length >= 3)
                        {
                            bestLength = length;
                            bestOffset = pos - candidate;
                            if (length == 18)
                            {
                                break;
                            }
                        }

                        candidate = previous[candidate];
                    }
                }

                if (bestLength >= 3)
                {
                    ushort token = (ushort)(((bestOffset - 1) << 4) | (bestLength - 3));
                    output.Add((byte)(token & 0xFF));
                    output.Add((byte)(token >> 8));
                    for (int i = 0; i < bestLength; ++i)
                    {
                        AddPosition(pos++);
                    }
                }
                else
                {
                    controls |= (byte)(1u << bit);
                    output.Add(input[pos]);
                    AddPosition(pos++);
                }
            }

            output[controlIndex] = controls;
        }

        return output.ToArray();
    }

    private static bool Decompress(byte[] input, uint expectedSize, out byte[] output)
    {
        var result = new List<byte>((int)expectedSize);
        int position = 0;
        while (position < input.Length && result.Count < expectedSize)
        {
            byte controls = input[position++];
            for (int bit = 0; bit < 8 && result.Count < expectedSize; ++bit)
            {
                if ((controls & (1u << bit)) != 0)
                {
                    if (position >= input.Length)
                    {
                        output = [];
                        return false;
                    }

                    result.Add(input[position++]);
                    continue;
                }

                if (position + 1 >= input.Length)
                {
                    output = [];
                    return false;
                }

                ushort token = (ushort)(input[position] | (input[position + 1] << 8));
                position += 2;
                int offset = (token >> 4) + 1;
                int length = (token & 0x0F) + 3;
                if (offset > result.Count || result.Count + length > expectedSize)
                {
                    output = [];
                    return false;
                }

                for (int i = 0; i < length; ++i)
                {
                    result.Add(result[result.Count - offset]);
                }
            }
        }

        output = result.ToArray();
        return result.Count == expectedSize && position == input.Length;
    }

    private static string EncodeBase64(IReadOnlyList<byte> data)
    {
        var builder = new StringBuilder((data.Count * 4 + 2) / 3);
        for (int i = 0; i < data.Count; i += 3)
        {
            uint value = (uint)data[i] << 16
                | (i + 1 < data.Count ? (uint)data[i + 1] << 8 : 0)
                | (i + 2 < data.Count ? (uint)data[i + 2] : 0);
            builder.Append(Alphabet[(int)((value >> 18) & 63)]);
            builder.Append(Alphabet[(int)((value >> 12) & 63)]);
            if (i + 1 < data.Count)
            {
                builder.Append(Alphabet[(int)((value >> 6) & 63)]);
            }

            if (i + 2 < data.Count)
            {
                builder.Append(Alphabet[(int)(value & 63)]);
            }
        }

        return builder.ToString();
    }

    private static bool DecodeBase64(string text, out List<byte> output)
    {
        output = [];
        uint value = 0;
        int bits = 0;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || character == '=')
            {
                continue;
            }

            int decoded = Base64Value(character);
            if (decoded < 0)
            {
                return false;
            }

            value = (value << 6) | (uint)decoded;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((value >> bits) & 0xFF));
            }

            if (output.Count > MaxPayload + 16)
            {
                return false;
            }
        }

        return bits < 6;
    }

    private static int Base64Value(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= 'a' and <= 'z' => character - 'a' + 26,
        >= '0' and <= '9' => character - '0' + 52,
        '-' or '+' => 62,
        '_' or '/' => 63,
        _ => -1,
    };

    private static void AppendUint32(List<byte> data, uint value)
    {
        data.Add((byte)value);
        data.Add((byte)(value >> 8));
        data.Add((byte)(value >> 16));
        data.Add((byte)(value >> 24));
    }

    private static uint ReadUint32(IReadOnlyList<byte> data, int offset)
        => data[offset]
           | ((uint)data[offset + 1] << 8)
           | ((uint)data[offset + 2] << 16)
           | ((uint)data[offset + 3] << 24);
}
