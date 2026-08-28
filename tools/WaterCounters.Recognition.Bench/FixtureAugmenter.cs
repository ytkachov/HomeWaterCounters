using OpenCvSharp;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition.Bench;

public sealed record AugmentedFixture(string Label, byte[] Jpeg);

/// <summary>
/// Порча фикстуры предсказуемым образом: темнее, светлее, повёрнута, смазана.
///
/// Отвечает на вопрос «переживёт ли распознавание съёмку чуть иначе», а не заменяет
/// новые фотографии. Варианты одного кадра не независимы: тот же ракурс, те же блики
/// на тех же местах, тот же момент барабана. Двадцать вариантов пяти снимков — это
/// по-прежнему пять наблюдений, и отчёт обязан говорить об этом прямо, иначе доля
/// совпадений выглядит весомее, чем есть.
///
/// Преобразования детерминированы: одна и та же фикстура даёт одни и те же варианты,
/// иначе замер нельзя было бы повторить.
/// </summary>
public static class FixtureAugmenter
{
    /// <summary>Порядок важен: при --augment N берутся первые N, от самого частого к самому редкому.</summary>
    private static readonly (string Label, Func<Mat, Mat> Apply)[] Transforms =
    [
        ("тёмное", static src => Brightness(src, 0.55, -20)),
        ("светлое", static src => Brightness(src, 1.45, 25)),
        ("поворот +12°", static src => Rotate(src, 12)),
        ("поворот -12°", static src => Rotate(src, -12)),
        ("наклон", static src => Tilt(src, 0.06)),
        ("смаз", static src => Blur(src, 9)),
        ("шум", static src => Noise(src, 12)),
        ("блик", static src => Glare(src)),
    ];

    public static int MaxVariants => Transforms.Length;

    /// <summary>Исходный кадр и <paramref name="count"/> его испорченных вариантов.</summary>
    public static IReadOnlyList<AugmentedFixture> Variants(byte[] jpeg, int count, int jpegQuality = 92)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        List<AugmentedFixture> result = [new AugmentedFixture("оригинал", jpeg)];

        if (count <= 0)
        {
            return result;
        }

        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color | ImreadModes.IgnoreOrientation);

        if (decoded.Empty())
        {
            return result;
        }

        // Кадр выпрямляется по EXIF до искажения: перекодирование тег теряет, и без
        // этого «поворот на 12°» превратился бы в поворот на 102° относительно
        // оригинала, который предобработка выпрямляет сама.
        using Mat source = OpenCvImagePreprocessor.Orient(decoded, ExifOrientation.Read(jpeg));

        foreach ((string label, Func<Mat, Mat> apply) in Transforms.Take(Math.Min(count, Transforms.Length)))
        {
            using Mat transformed = apply(source);

            Cv2.ImEncode(".jpg", transformed, out byte[] encoded,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality));

            if (encoded.Length > 0)
            {
                result.Add(new AugmentedFixture(label, encoded));
            }
        }

        return result;
    }

    private static Mat Brightness(Mat src, double gain, double bias)
    {
        Mat dst = new();
        src.ConvertTo(dst, MatType.CV_8UC3, gain, bias);
        return dst;
    }

    /// <summary>
    /// Поворот с расширением холста: обрезать углы нельзя, счётчик может стоять у края.
    /// EXIF при этом теряется, но предобработка всё равно читает его из исходных байтов.
    /// </summary>
    private static Mat Rotate(Mat src, double degrees)
    {
        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        using Mat rotation = Cv2.GetRotationMatrix2D(center, degrees, 1.0);

        double cos = Math.Abs(rotation.At<double>(0, 0));
        double sin = Math.Abs(rotation.At<double>(0, 1));

        int width = (int)((src.Height * sin) + (src.Width * cos));
        int height = (int)((src.Height * cos) + (src.Width * sin));

        rotation.Set(0, 2, rotation.At<double>(0, 2) + ((width / 2.0) - center.X));
        rotation.Set(1, 2, rotation.At<double>(1, 2) + ((height / 2.0) - center.Y));

        Mat dst = new();
        Cv2.WarpAffine(src, dst, rotation, new Size(width, height), borderMode: BorderTypes.Replicate);
        return dst;
    }

    /// <summary>Съёмка сбоку, а не в упор: верх кадра сжимается, низ растягивается.</summary>
    private static Mat Tilt(Mat src, double strength)
    {
        float dx = (float)(src.Width * strength);

        Point2f[] from =
        [
            new(0, 0), new(src.Width - 1, 0),
            new(src.Width - 1, src.Height - 1), new(0, src.Height - 1),
        ];

        Point2f[] to =
        [
            new(dx, 0), new(src.Width - 1 - dx, 0),
            new(src.Width - 1, src.Height - 1), new(0, src.Height - 1),
        ];

        using Mat transform = Cv2.GetPerspectiveTransform(from, to);
        Mat dst = new();
        Cv2.WarpPerspective(src, dst, transform, src.Size(), borderMode: BorderTypes.Replicate);
        return dst;
    }

    private static Mat Blur(Mat src, int kernel)
    {
        Mat dst = new();
        Cv2.GaussianBlur(src, dst, new Size(kernel, kernel), 0);
        return dst;
    }

    /// <summary>Шум с постоянным зерном — иначе замер перестал бы повторяться.</summary>
    private static Mat Noise(Mat src, double sigma)
    {
        using var noise = new Mat(src.Size(), MatType.CV_16SC3);
        var rng = new RNG(20260828);
        rng.Fill(noise, DistributionType.Normal, 0, sigma);

        using var signed = new Mat();
        src.ConvertTo(signed, MatType.CV_16SC3);
        Cv2.Add(signed, noise, signed);

        Mat dst = new();
        signed.ConvertTo(dst, MatType.CV_8UC3);
        return dst;
    }

    /// <summary>Пятно от вспышки на стекле — на электросчётчике оно есть и на исходном снимке.</summary>
    private static Mat Glare(Mat src)
    {
        Mat dst = src.Clone();

        int radius = Math.Max(24, Math.Min(src.Width, src.Height) / 6);
        var center = new Point(src.Width / 3, src.Height / 3);

        using var overlay = new Mat(src.Size(), MatType.CV_8UC3, Scalar.All(0));
        Cv2.Circle(overlay, center, radius, Scalar.All(255), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(overlay, overlay, new Size(0, 0), radius / 3.0);
        Cv2.AddWeighted(dst, 1.0, overlay, 0.75, 0, dst);

        return dst;
    }
}
