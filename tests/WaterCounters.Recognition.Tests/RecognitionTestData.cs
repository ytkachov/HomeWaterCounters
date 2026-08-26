using OpenCvSharp;
using WaterCounters.Core.Metering;

namespace WaterCounters.Recognition.Tests;

internal static class RecognitionTestData
{
    public static MeterSpec ColdWater { get; } = new()
    {
        Key = "cold-water",
        DisplayName = "Холодная вода",
        Kind = MeterKind.ColdWater,
        Unit = "м³",
        IntegerDigits = 5,
        FractionDigits = 3,
        SerialNumber = "12-345-678",
    };

    public static MeterSpec Electricity { get; } = new()
    {
        Key = "electricity",
        DisplayName = "Электричество",
        Kind = MeterKind.Electricity,
        Unit = "кВт·ч",
        IntegerDigits = 6,
        FractionDigits = 1,
    };

    /// <summary>
    /// Байты, которые распознавателю достаточно просто передать дальше. Для тестов
    /// обращения к модели картинка не обязана быть валидной: предобработка в них
    /// сквозная, а модель — заглушка.
    /// </summary>
    public static byte[] OpaqueJpeg { get; } = [.. Enumerable.Range(0, 256).Select(static i => (byte)i)];

    /// <summary>
    /// Настоящий JPEG заданного размера, нарисованный OpenCV: светлый прямоугольник
    /// «панели» на тёмном фоне. Тестам предобработки нужен файл, который действительно
    /// декодируется, а выдуманные байты OpenCV просто отвергнет.
    /// </summary>
    public static byte[] SyntheticMeterJpeg(int width = 640, int height = 480, int brightness = 200)
    {
        using var frame = new Mat(height, width, MatType.CV_8UC3, Scalar.All(30));

        var panel = new Rect(width / 6, height / 4, width * 2 / 3, height / 2);
        Cv2.Rectangle(frame, panel, Scalar.All(brightness), thickness: -1);

        // Тёмные полосы внутри панели — грубая имитация цифр, чтобы кадр не был плоским.
        for (int i = 1; i <= 5; i++)
        {
            int x = panel.X + (panel.Width * i / 6);
            Cv2.Rectangle(
                frame,
                new Rect(x, panel.Y + (panel.Height / 3), Math.Max(2, panel.Width / 24), panel.Height / 3),
                Scalar.All(20),
                thickness: -1);
        }

        Cv2.ImEncode(".jpg", frame, out byte[] encoded, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));
        return encoded;
    }
}
