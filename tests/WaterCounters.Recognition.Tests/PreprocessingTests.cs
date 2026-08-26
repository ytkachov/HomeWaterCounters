using System.Buffers.Binary;
using OpenCvSharp;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition.Tests;

public class ExifOrientationTests
{
    [Fact]
    public void MissingExifMeansNormalOrientation() =>
        Assert.Equal(ExifOrientation.Normal, ExifOrientation.Read(RecognitionTestData.SyntheticMeterJpeg()));

    [Fact]
    public void GarbageIsNormalOrientationRatherThanAnException() =>
        Assert.Equal(ExifOrientation.Normal, ExifOrientation.Read([1, 2, 3, 4, 5]));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void ReadsTheTagInBothByteOrders(int orientation)
    {
        Assert.Equal(orientation, ExifOrientation.Read(JpegWithExif(orientation, bigEndian: false)));
        Assert.Equal(orientation, ExifOrientation.Read(JpegWithExif(orientation, bigEndian: true)));
    }

    [Fact]
    public void OutOfRangeTagValueIsIgnored() =>
        Assert.Equal(ExifOrientation.Normal, ExifOrientation.Read(JpegWithExif(42, bigEndian: false)));

    /// <summary>Собирает JPEG, у которого настоящий APP1-сегмент Exif с одним тегом ориентации.</summary>
    private static byte[] JpegWithExif(int orientation, bool bigEndian)
    {
        byte[] tiff = new byte[8 + 2 + 12 + 4];
        Span<byte> span = tiff;

        span[0] = span[1] = (byte)(bigEndian ? 'M' : 'I');
        Write16(span[2..], 0x002A, bigEndian);
        Write32(span[4..], 8, bigEndian);

        Write16(span[8..], 1, bigEndian);              // одна запись в IFD0
        Write16(span[10..], 0x0112, bigEndian);        // тег Orientation
        Write16(span[12..], 3, bigEndian);             // тип SHORT
        Write32(span[14..], 1, bigEndian);             // одно значение
        Write16(span[18..], (ushort)orientation, bigEndian);

        byte[] header = "Exif\0\0"u8.ToArray();
        byte[] payload = [.. header, .. tiff];

        byte[] body = RecognitionTestData.SyntheticMeterJpeg(64, 64);

        // FFD8 + APP1(длина, полезная нагрузка) + всё тело исходного файла после FFD8.
        byte[] segmentLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(segmentLength, (ushort)(payload.Length + 2));

        return [0xFF, 0xD8, 0xFF, 0xE1, .. segmentLength, .. payload, .. body.AsSpan(2).ToArray()];
    }

    private static void Write16(Span<byte> target, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        }
    }

    private static void Write32(Span<byte> target, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        }
    }
}

public class OpenCvImagePreprocessorTests
{
    private readonly OpenCvImagePreprocessor _preprocessor = new();

    [Fact]
    public void NativeLibraryIsAvailable()
    {
        Assert.True(OpenCvImagePreprocessor.IsAvailable(out string? error), error);
    }

    [Fact]
    public void ProducesFullFrameAndDialCrop()
    {
        IReadOnlyList<MeterImage> images = _preprocessor.Prepare(
            RecognitionTestData.SyntheticMeterJpeg(),
            new PreprocessOptions());

        Assert.Equal(2, images.Count);
        Assert.Equal(MeterImageKind.FullFrame, images[0].Kind);
        Assert.Equal(MeterImageKind.DialCrop, images[1].Kind);
        Assert.All(images, image => Assert.True(image.Jpeg.Length > 0));
    }

    [Fact]
    public void DialCropIsSmallerThanTheWholeFrame()
    {
        IReadOnlyList<MeterImage> images = _preprocessor.Prepare(
            RecognitionTestData.SyntheticMeterJpeg(),
            new PreprocessOptions());

        MeterImage full = images[0];
        MeterImage crop = images[1];

        Assert.True(
            (long)crop.Width * crop.Height < (long)full.Width * full.Height,
            $"кроп {crop.Width}×{crop.Height} должен быть меньше кадра {full.Width}×{full.Height}");
    }

    [Fact]
    public void OversizedFrameIsScaledDownToTheConfiguredLimit()
    {
        IReadOnlyList<MeterImage> images = _preprocessor.Prepare(
            RecognitionTestData.SyntheticMeterJpeg(2400, 1800),
            new PreprocessOptions { MaxDimension = 800 });

        Assert.All(images, image => Assert.True(Math.Max(image.Width, image.Height) <= 800));
    }

    [Fact]
    public void TighterCropScaleYieldsASmallerCrop()
    {
        byte[] source = RecognitionTestData.SyntheticMeterJpeg();

        MeterImage wide = Crop(source, 1.0);
        MeterImage tight = Crop(source, 0.7);

        Assert.True(
            (long)tight.Width * tight.Height < (long)wide.Width * wide.Height,
            $"кроп ×0.7 ({tight.Width}×{tight.Height}) должен быть теснее кропа ×1.0 ({wide.Width}×{wide.Height})");
    }

    [Fact]
    public void RotatedFrameComesBackUpright()
    {
        // Портретный кадр с тегом «повернуть на 90°» обязан вернуться альбомным:
        // повёрнутый циферблат модель не читает вовсе.
        byte[] rotated = Portrait(orientation: 6);

        MeterImage full = _preprocessor.Prepare(rotated, new PreprocessOptions())[0];

        Assert.True(full.Width > full.Height, $"после коррекции ожидался альбомный кадр, получен {full.Width}×{full.Height}");
    }

    [Fact]
    public void UndecodableBytesFailWithAnExplanation()
    {
        RecognitionException error = Assert.Throws<RecognitionException>(() =>
            _preprocessor.Prepare(RecognitionTestData.OpaqueJpeg, new PreprocessOptions()));

        Assert.Contains("не декодируется", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledPanelDetectionStillReturnsACrop()
    {
        IReadOnlyList<MeterImage> images = _preprocessor.Prepare(
            RecognitionTestData.SyntheticMeterJpeg(),
            new PreprocessOptions { DetectPanel = false });

        Assert.Equal(MeterImageKind.DialCrop, images[^1].Kind);
    }

    private MeterImage Crop(byte[] source, double scale) =>
        _preprocessor.Prepare(source, new PreprocessOptions { IncludeFullFrame = false, CropScale = scale })[0];

    /// <summary>Портретный кадр 300×600 с указанной ориентацией в EXIF.</summary>
    private static byte[] Portrait(int orientation)
    {
        using var frame = new Mat(600, 300, MatType.CV_8UC3, Scalar.All(40));
        Cv2.Rectangle(frame, new Rect(50, 150, 200, 300), Scalar.All(210), thickness: -1);
        Cv2.ImEncode(".jpg", frame, out byte[] encoded);

        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01,
            0x03, 0x00,
            0x01, 0x00, 0x00, 0x00,
            (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        byte[] length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));

        return [0xFF, 0xD8, 0xFF, 0xE1, .. length, .. payload, .. encoded.AsSpan(2).ToArray()];
    }
}
