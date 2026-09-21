namespace HoYoShadeHub.Extensions.I18n;

/// <summary>只读一点点 PE 头：拿到「可执行节」在文件里的字节范围，用来限制「改代码里的立即数」这一步。</summary>
internal static class PeImage
{
    /// <summary>各可执行节（.text）的 [起, 止) 文件偏移；解析不了就返回空（调用方据此跳过立即数替换）</summary>
    public static List<(int Start, int End)> ExecutableRanges(byte[] image)
    {
        List<(int Start, int End)> ranges = [];

        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return ranges;
        }

        int peOffset = BitConverter.ToInt32(image, 0x3C);

        if (peOffset <= 0 || peOffset + 24 > image.Length)
        {
            return ranges;
        }

        if (image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
        {
            return ranges;
        }

        int coff = peOffset + 4;
        short sectionCount = BitConverter.ToInt16(image, coff + 2);
        short optionalSize = BitConverter.ToInt16(image, coff + 16);
        int sectionTable = coff + 20 + optionalSize;

        for (int i = 0; i < sectionCount; i++)
        {
            int s = sectionTable + (i * 40);

            if (s + 40 > image.Length)
            {
                break;
            }

            uint characteristics = BitConverter.ToUInt32(image, s + 36);

            // IMAGE_SCN_MEM_EXECUTE
            if ((characteristics & 0x20000000u) == 0)
            {
                continue;
            }

            uint rawSize = BitConverter.ToUInt32(image, s + 16);
            uint rawPointer = BitConverter.ToUInt32(image, s + 20);

            if (rawSize == 0)
            {
                continue;
            }

            long start = rawPointer;
            long end = Math.Min(image.Length, (long)rawPointer + rawSize);

            if (start >= 0 && start < end)
            {
                ranges.Add(((int)start, (int)end));
            }
        }

        return ranges;
    }
}
