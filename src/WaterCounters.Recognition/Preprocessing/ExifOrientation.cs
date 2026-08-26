using System.Buffers.Binary;

namespace WaterCounters.Recognition.Preprocessing;

/// <summary>
/// Чтение тега ориентации (0x0112) из APP1-сегмента JPEG.
///
/// Читается явно, а не отдаётся на откуп декодеру: телефоны сплошь и рядом пишут
/// кадр «как снято с матрицы» и добавляют тег поворота, и разные декодеры трактуют
/// это по-разному. Счётчик, повёрнутый на 90°, не читается вовсе.
/// </summary>
public static class ExifOrientation
{
    public const int Normal = 1;

    private const byte MarkerPrefix = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte StartOfScan = 0xDA;
    private const byte App1 = 0xE1;
    private const ushort OrientationTag = 0x0112;

    /// <summary>Значение 1…8 по стандарту EXIF. Отсутствие тега и любой мусор — это 1.</summary>
    public static int Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != MarkerPrefix || jpeg[1] != StartOfImage)
        {
            return Normal;
        }

        int position = 2;

        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != MarkerPrefix)
            {
                return Normal;
            }

            byte marker = jpeg[position + 1];

            // Заполнители 0xFF между сегментами допустимы стандартом.
            if (marker == MarkerPrefix)
            {
                position++;
                continue;
            }

            if (marker == StartOfScan)
            {
                return Normal;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);

            if (length < 2 || position + 2 + length > jpeg.Length)
            {
                return Normal;
            }

            ReadOnlySpan<byte> segment = jpeg.Slice(position + 4, length - 2);

            if (marker == App1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                return ReadFromTiff(segment[6..]);
            }

            position += 2 + length;
        }

        return Normal;
    }

    private static int ReadFromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return Normal;
        }

        bool bigEndian = tiff[0] == 'M' && tiff[1] == 'M';

        if (!bigEndian && (tiff[0] != 'I' || tiff[1] != 'I'))
        {
            return Normal;
        }

        if (ReadUInt16(tiff[2..], bigEndian) != 0x002A)
        {
            return Normal;
        }

        uint ifdOffset = ReadUInt32(tiff[4..], bigEndian);

        if (ifdOffset + 2 > (uint)tiff.Length)
        {
            return Normal;
        }

        ReadOnlySpan<byte> ifd = tiff[(int)ifdOffset..];
        ushort entries = ReadUInt16(ifd, bigEndian);

        for (int i = 0; i < entries; i++)
        {
            int entryOffset = 2 + (i * 12);

            if (entryOffset + 12 > ifd.Length)
            {
                return Normal;
            }

            ReadOnlySpan<byte> entry = ifd[entryOffset..];

            if (ReadUInt16(entry, bigEndian) != OrientationTag)
            {
                continue;
            }

            // Значение SHORT лежит прямо в поле значения, в его первых двух байтах.
            int value = ReadUInt16(entry[8..], bigEndian);
            return value is >= 1 and <= 8 ? value : Normal;
        }

        return Normal;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
}
