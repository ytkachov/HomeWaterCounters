using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition;

public sealed record EnsembleOptions
{
    /// <summary>Сколько проходов делать. 1 — голосования нет, распознаватель работает напрямую.</summary>
    public int Passes { get; init; } = 3;

    /// <summary>
    /// Множители кропа по проходам. Один и тот же снимок, обрезанный чуть теснее и чуть
    /// шире, модель читает по-разному, и расхождение между проходами — это и есть
    /// честный сигнал «здесь я не уверена», которого от самой модели не добиться.
    /// </summary>
    public IReadOnlyList<double> CropScales { get; init; } = [1.0, 0.85, 1.2];
}

/// <summary>
/// Два-три прохода с разными кропами и голосование большинством.
///
/// Уверенность результата умножается на долю согласившихся проходов: разошедшийся
/// ансамбль обязан провалиться сквозь порог валидатора и попасть человеку на глаза,
/// а не выдать самое уверенное из противоречивых чтений за истину.
/// </summary>
public sealed class EnsembleRecognizer(
    IVariantRecognizer inner,
    IImagePreprocessor preprocessor,
    PreprocessOptions preprocess,
    EnsembleOptions? options = null) : IMeterRecognizer
{
    private readonly IVariantRecognizer _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IImagePreprocessor _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
    private readonly PreprocessOptions _preprocess = preprocess ?? throw new ArgumentNullException(nameof(preprocess));
    private readonly EnsembleOptions _options = options ?? new EnsembleOptions();

    public async Task<RecognitionResult> RecognizeAsync(MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meter);

        int passes = Math.Clamp(_options.Passes, 1, Math.Max(1, _options.CropScales.Count));

        List<RecognitionResult> results = [];
        List<string> failures = [];

        for (int pass = 0; pass < passes; pass++)
        {
            PreprocessOptions options = _preprocess with { CropScale = _options.CropScales[pass] };
            IReadOnlyList<MeterImage> images = _preprocessor.Prepare(jpeg, options);

            try
            {
                results.Add(await _inner.RecognizeVariantsAsync(meter, images, ct).ConfigureAwait(false));
            }
            catch (RecognitionException ex)
            {
                // Провал одного прохода — не провал ансамбля: остальные ещё могут сойтись.
                failures.Add($"проход {pass + 1} (кроп ×{options.CropScale:0.##}) не удался: {ex.Message}");
            }
        }

        if (results.Count == 0)
        {
            throw new RecognitionException(
                failures.Count > 0
                    ? "Все проходы ансамбля провалились. " + string.Join("; ", failures)
                    : "Ансамбль не сделал ни одного прохода.");
        }

        return Vote(results, failures, passes);
    }

    private static RecognitionResult Vote(
        IReadOnlyList<RecognitionResult> results,
        List<string> failures,
        int passes)
    {
        List<RecognitionResult> read = [.. results.Where(static r => r.Value is not null)];

        if (read.Count == 0)
        {
            RecognitionResult first = results[0];

            return first with
            {
                Confidence = 0,
                Warnings = [.. first.Warnings, .. failures, "ни один проход не прочитал показание"],
            };
        }

        List<string> warnings = [.. failures];

        // Сначала полное совпадение значения, затем — совпадение только целой части.
        // Разделение не косметическое: за целую часть счёт и выставляется, расхождение
        // в литрах стоит копейки, расхождение в кубометрах — тысячи.
        IGrouping<decimal, RecognitionResult> byValue = Largest(read, static r => r.Value!.Value);

        if (byValue.Count() > 1 || read.Count == 1)
        {
            return Combine(byValue, warnings, read.Count, passes, exact: true);
        }

        IGrouping<decimal, RecognitionResult> byInteger = Largest(read, static r => decimal.Truncate(r.Value!.Value));

        if (byInteger.Count() > 1)
        {
            warnings.Add(
                $"проходы разошлись в дробной части: {Values(read)} — взято чтение с наибольшей уверенностью");

            return Combine(byInteger, warnings, read.Count, passes, exact: false);
        }

        warnings.Add(
            $"проходы разошлись в целой части: {Values(read)} — значение требует проверки человеком");

        return Combine(byValue, warnings, read.Count, passes, exact: false);
    }

    /// <summary>Крупнейшая группа; при равенстве — та, где выше максимальная уверенность.</summary>
    private static IGrouping<decimal, RecognitionResult> Largest(
        IReadOnlyList<RecognitionResult> read,
        Func<RecognitionResult, decimal> key) =>
        read.GroupBy(key)
            .OrderByDescending(static g => g.Count())
            .ThenByDescending(static g => g.Max(static r => r.Confidence))
            .First();

    private static RecognitionResult Combine(
        IGrouping<decimal, RecognitionResult> winners,
        List<string> warnings,
        int readCount,
        int passes,
        bool exact)
    {
        List<RecognitionResult> group = [.. winners];
        RecognitionResult best = group.MaxBy(static r => r.Confidence)!;

        double agreement = (double)group.Count / passes;
        double confidence = Math.Clamp(group.Average(static r => r.Confidence) * agreement, 0, 1);

        if (passes > 1)
        {
            string incomplete = readCount < passes ? $", прочитали показание {readCount}" : string.Empty;
            warnings.Add($"согласие ансамбля: {group.Count} из {passes} проходов{incomplete}");
        }

        // Предупреждения победивших проходов сохраняются: перекат барабана или
        // непрочитанный серийник — это свойство снимка, а не конкретного прохода.
        return best with
        {
            Value = exact ? winners.Key : best.Value,
            Confidence = confidence,
            Warnings = [.. group.SelectMany(static r => r.Warnings).Distinct(StringComparer.Ordinal), .. warnings],
        };
    }

    private static string Values(IEnumerable<RecognitionResult> results) =>
        string.Join(", ", results.Select(static r => r.Value));
}
