namespace WaterCounters.Recognition.Preprocessing;

public enum MeterImageKind
{
    /// <summary>Весь кадр — модель видит корпус целиком и может прочитать серийный номер.</summary>
    FullFrame = 0,

    /// <summary>Тесный кроп циферблата, при удаче выровненный по перспективе.</summary>
    DialCrop = 1,
}

public sealed record MeterImage(MeterImageKind Kind, byte[] Jpeg, int Width, int Height);

public sealed record PreprocessOptions
{
    /// <summary>Длинная сторона кадра, подаваемого модели.</summary>
    public int MaxDimension { get; init; } = 1280;

    /// <summary>
    /// CLAHE на тёмных кадрах. По умолчанию выключено: замер по фикстурам показал
    /// устойчивое ухудшение — на снимке в тёмной нише выравнивание вытягивает шум и
    /// блики на стекле до уровня цифр. Значение оставлено настраиваемым, потому что
    /// проверено на одной квартире и одной камере.
    /// </summary>
    public bool Enhance { get; init; }

    /// <summary>Искать лицевую панель и выравнивать перспективу.</summary>
    public bool DetectPanel { get; init; } = true;

    /// <summary>
    /// Множитель к найденной рамке панели. Ансамбль варьирует именно его: один и тот же
    /// снимок, обрезанный чуть теснее и чуть шире, модель читает по-разному, и расхождение
    /// между проходами — это и есть сигнал «здесь я не уверена».
    /// </summary>
    public double CropScale { get; init; } = 1.0;

    public bool IncludeFullFrame { get; init; } = true;

    /// <summary>
    /// Класть ли в запрос кроп циферблата. Выключается не ради экономии: две картинки
    /// одного счётчика модель иногда читает как две разные, склеивая цифры барабана
    /// с цифрами серийного номера. Что лучше — решается замером, а не рассуждением.
    /// </summary>
    public bool IncludeDialCrop { get; init; } = true;

    public int JpegQuality { get; init; } = 92;
}

/// <summary>
/// Подготовка кадра к подаче в модель. Реализация на OpenCV делает коррекцию по EXIF,
/// перспективное выравнивание панели и CLAHE; пустая реализация отдаёт кадр как есть.
/// </summary>
public interface IImagePreprocessor
{
    IReadOnlyList<MeterImage> Prepare(ReadOnlyMemory<byte> jpeg, PreprocessOptions options);
}

/// <summary>
/// Предобработка, которая ничего не делает. Нужна в двух случаях: предобработка
/// выключена в настройках, и нативная часть OpenCV недоступна.
/// </summary>
public sealed class PassThroughImagePreprocessor : IImagePreprocessor
{
    public IReadOnlyList<MeterImage> Prepare(ReadOnlyMemory<byte> jpeg, PreprocessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [new MeterImage(MeterImageKind.FullFrame, jpeg.ToArray(), 0, 0)];
    }
}
