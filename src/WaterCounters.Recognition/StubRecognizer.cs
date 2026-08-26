using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using WaterCounters.Core.Metering;

namespace WaterCounters.Recognition;

/// <summary>
/// Размеченная фикстура: имя файла вида <c>&lt;meterKey&gt;_&lt;ожидаемое&gt;_&lt;серийник&gt;.jpg</c>.
/// </summary>
public sealed record FixtureExpectation
{
    public required string MeterKey { get; init; }

    public required decimal Value { get; init; }

    public string? Serial { get; init; }

    public required string FileName { get; init; }

    /// <summary>
    /// Разрядность, прочитанная из самой разметки: "01234.567" — это 5 и 3.
    /// Благодаря этому фикстура самодостаточна, и бенчмарку не нужны настройки,
    /// чтобы знать, чего ждать от счётчика.
    /// </summary>
    public required int IntegerDigits { get; init; }

    public required int FractionDigits { get; init; }
}

/// <summary>
/// Распознаватель для разработки без GPU: отдаёт значение, размеченное в имени файла
/// фикстуры.
///
/// Сопоставление идёт по содержимому файла, а не по имени: интерфейс распознавания
/// получает голые байты, и это правильно — он не должен зависеть от того, откуда
/// фотография взялась. Хэш решает задачу, ничего не ломая.
/// </summary>
public sealed class StubRecognizer : IMeterRecognizer
{
    private readonly Dictionary<string, FixtureExpectation> _byContent;
    private readonly decimal? _fallbackValue;
    private readonly string? _fallbackSerial;

    private StubRecognizer(
        Dictionary<string, FixtureExpectation> byContent,
        decimal? fallbackValue,
        string? fallbackSerial)
    {
        _byContent = byContent;
        _fallbackValue = fallbackValue;
        _fallbackSerial = fallbackSerial;
    }

    /// <summary>Уверенность, которую отдаёт заглушка. Заведомо выше любого разумного порога.</summary>
    public const double StubConfidence = 0.99;

    public IReadOnlyCollection<FixtureExpectation> Fixtures => _byContent.Values;

    /// <summary>
    /// Индексирует размеченные фикстуры. Файлы, не подходящие под схему имени,
    /// пропускаются молча: рядом с фикстурами обычно лежат и просто снимки.
    /// </summary>
    public static StubRecognizer FromFixtures(string directory, decimal? fallbackValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Dictionary<string, FixtureExpectation> index = new(StringComparer.Ordinal);

        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.AllDirectories))
            {
                if (TryParseFixtureName(Path.GetFileName(path), out FixtureExpectation? expectation))
                {
                    index[HashOf(File.ReadAllBytes(path))] = expectation;
                }
            }
        }

        return new StubRecognizer(index, fallbackValue, null);
    }

    /// <summary>Заглушка с одним фиксированным ответом — для прогонов хоста без фикстур.</summary>
    public static StubRecognizer Fixed(decimal value, string? serial = null) =>
        new(new Dictionary<string, FixtureExpectation>(StringComparer.Ordinal), value, serial);

    /// <summary>Разбирает имя вида <c>cold-water_01234.567_12-345-678.jpg</c>.</summary>
    public static bool TryParseFixtureName(string fileName, [NotNullWhen(true)] out FixtureExpectation? expectation)
    {
        expectation = null;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        string[] parts = name.Split('_');

        // Ключ счётчика — строго kebab-case в нижнем регистре, как MeterSpec.Key.
        // Без этой проверки под схему имени попадает обычный IMG_1234.jpg с камеры,
        // и снимок без разметки молча превращается в «ожидаемое значение 1234».
        if (parts.Length is < 2 or > 3 || !IsMeterKey(parts[0]))
        {
            return false;
        }

        if (!decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
        {
            return false;
        }

        int dot = parts[1].IndexOf('.', StringComparison.Ordinal);

        expectation = new FixtureExpectation
        {
            MeterKey = parts[0],
            Value = value,
            Serial = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null,
            FileName = fileName,
            IntegerDigits = dot < 0 ? parts[1].Length : dot,
            FractionDigits = dot < 0 ? 0 : parts[1].Length - dot - 1,
        };

        return true;
    }

    public Task<RecognitionResult> RecognizeAsync(MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ct.ThrowIfCancellationRequested();

        if (_byContent.TryGetValue(HashOf(jpeg.Span), out FixtureExpectation? expectation))
        {
            return Task.FromResult(Result(expectation.Value, expectation.Serial ?? meter.SerialNumber, expectation.FileName));
        }

        if (_fallbackValue is { } fallback)
        {
            return Task.FromResult(Result(fallback, _fallbackSerial ?? meter.SerialNumber, "fixed"));
        }

        return Task.FromResult(RecognitionResult.Failed(
            "заглушка распознавания: размеченная фикстура для этого снимка не найдена"));
    }

    private static RecognitionResult Result(decimal value, string? serial, string source)
    {
        string json = JsonFor(value, serial, source);

        return new RecognitionResult(serial, value, StubConfidence, json, [$"значение взято из заглушки ({source})"]);
    }

    private static string JsonFor(decimal value, string? serial, string source) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"serial":{{(serial is null ? "null" : $"\"{serial}\"")}},"value":{{value}},"confidence":{{StubConfidence}},"notes":"stub:{{source}}"}""");

    private static bool IsMeterKey(string candidate) =>
        candidate.Length > 0 &&
        char.IsAsciiLetterLower(candidate[0]) &&
        candidate.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static string HashOf(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));
}
