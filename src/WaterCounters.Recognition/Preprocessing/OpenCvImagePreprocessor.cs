using OpenCvSharp;

namespace WaterCounters.Recognition.Preprocessing;

/// <summary>
/// Подготовка кадра к подаче в модель: коррекция ориентации по EXIF, поиск лицевой
/// панели и перспективное выравнивание, CLAHE для тёмных кадров.
///
/// На выход идут два варианта — полный кадр и тесный кроп циферблата. Полный нужен,
/// чтобы модель прочитала серийный номер и поняла, что вообще перед ней; кроп — чтобы
/// цифры занимали заметную долю кадра. Подача обоих вариантов заметно повышает попадание.
/// </summary>
public sealed class OpenCvImagePreprocessor : IImagePreprocessor
{
    /// <summary>Ниже этой средней яркости канала L включается CLAHE.</summary>
    private const double DarkThreshold = 110;

    private const double MinPanelAreaShare = 0.06;
    private const double MaxPanelAreaShare = 0.95;
    private const double CenterCropShare = 0.72;
    private const int MinPanelSide = 64;

    /// <summary>
    /// Проверяет, что нативная часть OpenCV загружается. Вызывается фабрикой при
    /// старте: обработчик без выравнивания работает хуже, но работает, а вот падение
    /// при старте из-за отсутствующей DLL не оставляет вариантов вообще.
    /// </summary>
    public static bool IsAvailable(out string? error)
    {
        try
        {
            using var probe = new Mat(1, 1, MatType.CV_8UC3, Scalar.All(0));
            error = null;
            return !probe.Empty();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<MeterImage> Prepare(ReadOnlyMemory<byte> jpeg, PreprocessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[] source = jpeg.ToArray();

        // IgnoreOrientation — потому что поворот применяется явно ниже: молчаливая
        // трактовка тега разными сборками OpenCV различается, а сюрприз здесь стоит
        // повёрнутого на 90° циферблата, который не читается вовсе.
        using Mat decoded = Cv2.ImDecode(source, ImreadModes.Color | ImreadModes.IgnoreOrientation);

        if (decoded.Empty())
        {
            throw new RecognitionException("Файл не декодируется как изображение.");
        }

        using Mat oriented = Orient(decoded, ExifOrientation.Read(source));
        using Mat enhanced = options.Enhance ? EnhanceIfDark(oriented) : oriented.Clone();

        List<MeterImage> images = [];

        if (options.IncludeFullFrame)
        {
            images.Add(Encode(MeterImageKind.FullFrame, enhanced, options));
        }

        if (options.IncludeDialCrop || images.Count == 0)
        {
            using Mat dial = ExtractDial(enhanced, options);
            images.Add(Encode(MeterImageKind.DialCrop, dial, options));
        }

        return images;
    }

    /// <summary>
    /// Применяет тег EXIF к уже декодированному кадру. Публичный, потому что тем же
    /// поворотом обязан пользоваться генератор искажённых фикстур: иначе его варианты
    /// окажутся повёрнуты относительно оригинала на все 90°, и замер устойчивости
    /// будет мерить не то, что собирался.
    /// </summary>
    public static Mat Orient(Mat src, int orientation)
    {
        switch (orientation)
        {
            case 2:
            {
                Mat dst = new();
                Cv2.Flip(src, dst, FlipMode.Y);
                return dst;
            }

            case 3:
            {
                Mat dst = new();
                Cv2.Rotate(src, dst, RotateFlags.Rotate180);
                return dst;
            }

            case 4:
            {
                Mat dst = new();
                Cv2.Flip(src, dst, FlipMode.X);
                return dst;
            }

            case 5:
            {
                using Mat transposed = new();
                Cv2.Transpose(src, transposed);
                Mat dst = new();
                Cv2.Flip(transposed, dst, FlipMode.Y);
                return dst;
            }

            case 6:
            {
                Mat dst = new();
                Cv2.Rotate(src, dst, RotateFlags.Rotate90Clockwise);
                return dst;
            }

            case 7:
            {
                using Mat transposed = new();
                Cv2.Transpose(src, transposed);
                Mat dst = new();
                Cv2.Flip(transposed, dst, FlipMode.X);
                return dst;
            }

            case 8:
            {
                Mat dst = new();
                Cv2.Rotate(src, dst, RotateFlags.Rotate90Counterclockwise);
                return dst;
            }

            default:
                return src.Clone();
        }
    }

    /// <summary>
    /// CLAHE по каналу яркости, и только на тёмных кадрах: на нормально освещённом
    /// снимке она вытягивает шум и блики на стекле до уровня цифр.
    /// </summary>
    private static Mat EnhanceIfDark(Mat src)
    {
        using Mat lab = src.CvtColor(ColorConversionCodes.BGR2Lab);
        Cv2.Split(lab, out Mat[] channels);

        try
        {
            if (Cv2.Mean(channels[0]).Val0 >= DarkThreshold)
            {
                return src.Clone();
            }

            using CLAHE clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            using Mat equalized = new();
            clahe.Apply(channels[0], equalized);
            equalized.CopyTo(channels[0]);

            using Mat merged = new();
            Cv2.Merge(channels, merged);
            return merged.CvtColor(ColorConversionCodes.Lab2BGR);
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private static Mat ExtractDial(Mat src, PreprocessOptions options) =>
        options.DetectPanel && TryFindPanel(src, out Point2f[] quad)
            ? WarpPanel(src, quad, options.CropScale)
            : CenterCrop(src, options.CropScale);

    /// <summary>Наибольший выпуклый четырёхугольник разумной площади — это лицевая панель.</summary>
    private static bool TryFindPanel(Mat src, out Point2f[] quad)
    {
        quad = [];

        using Mat gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);
        using Mat blurred = gray.GaussianBlur(new Size(5, 5), 0);
        using Mat edges = blurred.Canny(50, 150);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        using Mat closed = new();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(
            closed,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        double frameArea = (double)src.Width * src.Height;
        double bestArea = 0;
        Point[]? best = null;

        foreach (Point[] contour in contours)
        {
            double area = Cv2.ContourArea(contour);

            if (area < frameArea * MinPanelAreaShare || area > frameArea * MaxPanelAreaShare || area <= bestArea)
            {
                continue;
            }

            Point[] approx = Cv2.ApproxPolyDP(contour, 0.02 * Cv2.ArcLength(contour, true), true);

            if (approx.Length != 4 || !Cv2.IsContourConvex(approx))
            {
                continue;
            }

            bestArea = area;
            best = approx;
        }

        if (best is null)
        {
            return false;
        }

        quad = OrderCorners(best);
        return true;
    }

    /// <summary>Углы по часовой стрелке от левого верхнего — порядок, который ждёт GetPerspectiveTransform.</summary>
    private static Point2f[] OrderCorners(Point[] points)
    {
        Point2f[] pts = [.. points.Select(static p => new Point2f(p.X, p.Y))];

        Point2f topLeft = pts.MinBy(static p => p.X + p.Y);
        Point2f bottomRight = pts.MaxBy(static p => p.X + p.Y);
        Point2f topRight = pts.MinBy(static p => p.Y - p.X);
        Point2f bottomLeft = pts.MaxBy(static p => p.Y - p.X);

        return [topLeft, topRight, bottomRight, bottomLeft];
    }

    private static Mat WarpPanel(Mat src, Point2f[] quad, double scale)
    {
        Point2f[] corners = ScaleAboutCenter(quad, scale, src.Width, src.Height);

        int width = Math.Max(MinPanelSide, (int)Math.Round(Math.Max(
            Distance(corners[0], corners[1]),
            Distance(corners[3], corners[2]))));

        int height = Math.Max(MinPanelSide, (int)Math.Round(Math.Max(
            Distance(corners[0], corners[3]),
            Distance(corners[1], corners[2]))));

        Point2f[] target =
        [
            new(0, 0),
            new(width - 1, 0),
            new(width - 1, height - 1),
            new(0, height - 1),
        ];

        using Mat transform = Cv2.GetPerspectiveTransform(corners, target);
        Mat dst = new();
        Cv2.WarpPerspective(src, dst, transform, new Size(width, height));
        return dst;
    }

    private static Point2f[] ScaleAboutCenter(Point2f[] quad, double scale, int width, int height)
    {
        if (Math.Abs(scale - 1.0) < 0.001)
        {
            return quad;
        }

        float centerX = quad.Average(static p => p.X);
        float centerY = quad.Average(static p => p.Y);

        return
        [
            .. quad.Select(p => new Point2f(
                Math.Clamp(centerX + ((p.X - centerX) * (float)scale), 0, width - 1),
                Math.Clamp(centerY + ((p.Y - centerY) * (float)scale), 0, height - 1))),
        ];
    }

    /// <summary>Запасной кроп, когда рамку панели найти не удалось: циферблат почти всегда в центре кадра.</summary>
    private static Mat CenterCrop(Mat src, double scale)
    {
        double share = Math.Clamp(CenterCropShare * scale, 0.2, 1.0);

        int width = Math.Min(src.Width, Math.Max(MinPanelSide, (int)Math.Round(src.Width * share)));
        int height = Math.Min(src.Height, Math.Max(MinPanelSide, (int)Math.Round(src.Height * share)));

        var roi = new Rect((src.Width - width) / 2, (src.Height - height) / 2, width, height);

        using var view = new Mat(src, roi);
        return view.Clone();
    }

    private static MeterImage Encode(MeterImageKind kind, Mat image, PreprocessOptions options)
    {
        using Mat scaled = Downscale(image, options.MaxDimension);

        Cv2.ImEncode(
            ".jpg",
            scaled,
            out byte[] buffer,
            new ImageEncodingParam(ImwriteFlags.JpegQuality, options.JpegQuality));

        if (buffer.Length == 0)
        {
            throw new RecognitionException("Подготовленный кадр не кодируется в JPEG.");
        }

        return new MeterImage(kind, buffer, scaled.Width, scaled.Height);
    }

    private static Mat Downscale(Mat src, int maxDimension)
    {
        int longest = Math.Max(src.Width, src.Height);

        if (maxDimension <= 0 || longest <= maxDimension)
        {
            return src.Clone();
        }

        double factor = (double)maxDimension / longest;
        Mat dst = new();

        Cv2.Resize(
            src,
            dst,
            new Size(
                Math.Max(1, (int)Math.Round(src.Width * factor)),
                Math.Max(1, (int)Math.Round(src.Height * factor))),
            interpolation: InterpolationFlags.Area);

        return dst;
    }

    private static double Distance(Point2f a, Point2f b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
