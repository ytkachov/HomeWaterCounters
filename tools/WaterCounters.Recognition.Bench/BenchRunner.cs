using System.Globalization;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;
using WaterCounters.Recognition.Vlm;

namespace WaterCounters.Recognition.Bench;

public sealed record FixtureCase(FixtureExpectation Expectation, MeterSpec Meter, string Path);

public sealed record CaseOutcome
{
    public required FixtureCase Case { get; init; }

    public decimal? Actual { get; init; }

    public string? ActualSerial { get; init; }

    public required long ElapsedMs { get; init; }

    public string? Error { get; init; }

    public double Confidence { get; init; }

    /// <summary>Ответ модели как есть. «Модель промолчала» и «модель ответила не по схеме» — разные диагнозы.</summary>
    public string? RawResponse { get; init; }

    /// <summary>Замечания разбора: именно они объясняют, почему прочитанное не стало значением.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Какой вариант снимка прогонялся: «оригинал», «тёмное», «поворот +12°».</summary>
    public string Variant { get; init; } = "оригинал";

    public bool IsOriginal => Variant == "оригинал";

    public bool ExactMatch => Actual == Case.Expectation.Value;

    public bool IntegerMatch => Actual is { } value && decimal.Truncate(value) == decimal.Truncate(Case.Expectation.Value);

    /// <summary>Считается ровно тем же правилом, что и в конвейере, — иначе замер мерит не то, что работает.</summary>
    public bool SerialMatch =>
        Case.Expectation.Serial is null || SerialNumber.Matches(Case.Expectation.Serial, ActualSerial);
}

public sealed record BenchReport
{
    public required BenchCombination Combination { get; init; }

    public required IReadOnlyList<CaseOutcome> Outcomes { get; init; }

    public int Total => Outcomes.Count;

    /// <summary>Сколько за этими наблюдениями стоит независимых фотографий.</summary>
    public int IndependentPhotos => Outcomes.Count(o => o.IsOriginal);

    public int Errors => Outcomes.Count(o => o.Error is not null);

    public double ExactShare => Share(o => o.ExactMatch);

    public double IntegerShare => Share(o => o.IntegerMatch);

    public double SerialShare => Share(o => o.SerialMatch);

    /// <summary>Доля неверных цифр по всем разрядам всех фикстур — она заметно чувствительнее доли точных совпадений.</summary>
    public double DigitErrorShare
    {
        get
        {
            int wrong = 0;
            int total = 0;

            foreach (CaseOutcome outcome in Outcomes)
            {
                string expected = DigitString(outcome.Case.Expectation.Value, outcome.Case.Meter);
                string actual = outcome.Actual is { } value
                    ? DigitString(value, outcome.Case.Meter)
                    : new string('?', expected.Length);

                total += expected.Length;

                for (int i = 0; i < expected.Length; i++)
                {
                    if (i >= actual.Length || actual[i] != expected[i])
                    {
                        wrong++;
                    }
                }
            }

            return total == 0 ? 0 : (double)wrong / total;
        }
    }

    public double MeanLatencyMs => Outcomes.Count == 0 ? 0 : Outcomes.Average(o => (double)o.ElapsedMs);

    private double Share(Func<CaseOutcome, bool> predicate) =>
        Outcomes.Count == 0 ? 0 : (double)Outcomes.Count(predicate) / Outcomes.Count;

    /// <summary>Значение как строка разрядов без точки — так их можно сравнивать позиционно.</summary>
    private static string DigitString(decimal value, MeterSpec meter)
    {
        decimal scaled = Math.Abs(decimal.Truncate(value * Pow10(meter.FractionDigits)));
        return scaled.ToString(CultureInfo.InvariantCulture)
            .PadLeft(meter.IntegerDigits + meter.FractionDigits, '0');
    }

    private static decimal Pow10(int power)
    {
        decimal result = 1m;

        for (int i = 0; i < power; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

/// <summary>
/// Прогон одной комбинации по всем фикстурам.
///
/// Смысл упражнения ровно один: модель и промпт выбираются замерами, а не на глаз.
/// Разница между двумя моделями на реальных снимках счётчиков не угадывается —
/// она либо измерена, либо неизвестна.
/// </summary>
public sealed class BenchRunner(BenchOptions options, HttpClient http)
{
    private readonly BenchOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public static IReadOnlyList<FixtureCase> LoadFixtures(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new BenchUsageException($"Папка фикстур '{directory}' не найдена.");
        }

        List<FixtureCase> cases = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (StubRecognizer.TryParseFixtureName(Path.GetFileName(path), out FixtureExpectation? expectation))
            {
                cases.Add(new FixtureCase(expectation, MeterFor(expectation), path));
            }
        }

        return cases;
    }

    public async Task<BenchReport> RunAsync(
        BenchCombination combination,
        IReadOnlyList<FixtureCase> cases,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(combination);
        ArgumentNullException.ThrowIfNull(cases);

        IMeterRecognizer recognizer = Build(combination);
        List<CaseOutcome> outcomes = [];

        foreach (FixtureCase fixture in cases)
        {
            byte[] original = await File.ReadAllBytesAsync(fixture.Path, ct).ConfigureAwait(false);
            await DumpPreparedAsync(combination, fixture, original, ct).ConfigureAwait(false);

            foreach (AugmentedFixture variant in FixtureAugmenter.Variants(original, _options.Augment))
            {
                ct.ThrowIfCancellationRequested();
                long started = Environment.TickCount64;

                try
                {
                    RecognitionResult result = await recognizer
                        .RecognizeAsync(fixture.Meter, variant.Jpeg, ct)
                        .ConfigureAwait(false);

                    outcomes.Add(new CaseOutcome
                    {
                        Case = fixture,
                        Variant = variant.Label,
                        Actual = result.Value,
                        ActualSerial = result.Serial,
                        Confidence = result.Confidence,
                        RawResponse = result.RawJson,
                        Warnings = result.Warnings,
                        ElapsedMs = result.ElapsedMs > 0 ? result.ElapsedMs : Environment.TickCount64 - started,
                    });

                    if (variant.Label == "оригинал")
                    {
                        await DumpResponseAsync(combination, fixture, result, ct).ConfigureAwait(false);
                    }
                }
                catch (RecognitionException ex)
                {
                    // Недоступная модель — такой же результат замера, как неверная цифра:
                    // строка остаётся в таблице, иначе комбинация выглядела бы безупречной.
                    outcomes.Add(new CaseOutcome
                    {
                        Case = fixture,
                        Variant = variant.Label,
                        ElapsedMs = Environment.TickCount64 - started,
                        Error = ex.Message,
                    });
                }
            }
        }

        return new BenchReport { Combination = combination, Outcomes = outcomes };
    }

    /// <summary>
    /// Складывает кадры ровно в том виде, в каком они уходят модели: полный кадр и
    /// кроп циферблата, с размерами прямо в имени файла. Единственный способ отличить
    /// «модель не умеет читать барабан» от «модель прислали не туда и не в том масштабе».
    /// </summary>
    private async Task DumpPreparedAsync(
        BenchCombination combination,
        FixtureCase fixture,
        byte[] jpeg,
        CancellationToken ct)
    {
        if (_options.DumpDirectory is not { Length: > 0 } root)
        {
            return;
        }

        string directory = Path.Combine(root, Sanitize(combination.ToString()));
        Directory.CreateDirectory(directory);

        string stem = Path.GetFileNameWithoutExtension(fixture.Path);

        try
        {
            foreach (MeterImage image in PreprocessorFor(combination).Prepare(jpeg, PreprocessFor(combination)))
            {
                string name = $"{stem}__{image.Kind}_{image.Width}x{image.Height}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(directory, name), image.Jpeg, ct).ConfigureAwait(false);
            }
        }
        catch (RecognitionException)
        {
            // Кадр, который не готовится, всё равно попадёт в таблицу ошибкой прогона.
        }
    }

    /// <summary>
    /// Кладёт ответ модели рядом с кадрами, которые ей показали. Без него «получено
    /// ничего» неотличимо от «модель ответила, но не по схеме» и «модель прочитала
    /// верно, а разбор потерял значение» — а чинить это три разные правки.
    /// </summary>
    private async Task DumpResponseAsync(
        BenchCombination combination,
        FixtureCase fixture,
        RecognitionResult result,
        CancellationToken ct)
    {
        if (_options.DumpDirectory is not { Length: > 0 } root)
        {
            return;
        }

        string directory = Path.Combine(root, Sanitize(combination.ToString()));
        Directory.CreateDirectory(directory);

        var text = new System.Text.StringBuilder();
        text.AppendLine($"фикстура : {Path.GetFileName(fixture.Path)}");
        text.AppendLine($"ждали    : {fixture.Expectation.Value}");
        text.AppendLine($"получено : {(result.Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "ничего")}");
        text.AppendLine($"уверенно : {result.Confidence:P0}");
        text.AppendLine();
        text.AppendLine("замечания разбора:");

        foreach (string warning in result.Warnings)
        {
            text.AppendLine($"  - {warning}");
        }

        text.AppendLine();
        text.AppendLine("ответ модели как есть:");
        text.AppendLine(result.RawJson);

        string name = $"{Path.GetFileNameWithoutExtension(fixture.Path)}__ответ.txt";
        await File.WriteAllTextAsync(Path.Combine(directory, name), text.ToString(), ct).ConfigureAwait(false);
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    private PreprocessOptions PreprocessFor(BenchCombination combination) => new()
    {
        MaxDimension = _options.MaxImageDimension,
        Enhance = combination.Preprocess && combination.Enhance,
        DetectPanel = combination.Preprocess,
        IncludeFullFrame = combination.Images != BenchImageSet.DialCrop,
        IncludeDialCrop = combination.Images != BenchImageSet.FullFrame,
    };

    private static IImagePreprocessor PreprocessorFor(BenchCombination combination) =>
        combination.Preprocess
            ? new OpenCvImagePreprocessor()
            : new PassThroughImagePreprocessor();

    private IMeterRecognizer Build(BenchCombination combination)
    {
        PreprocessOptions preprocess = PreprocessFor(combination);
        IImagePreprocessor preprocessor = PreprocessorFor(combination);

        var vlmOptions = new VlmRecognizerOptions
        {
            Endpoint = _options.Endpoint,
            Model = combination.Model,
            Prompt = combination.Prompt,
            SeparateSerialPass = combination.SerialPass,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            ContextTokens = _options.ContextTokens,
            Preprocess = preprocess,
        };

        VlmRecognizer recognizer = _options.Provider == RecognitionProvider.OpenAiCompatible
            ? new OpenAiCompatibleRecognizer(_http, vlmOptions, preprocessor)
            : new OllamaRecognizer(_http, vlmOptions, preprocessor);

        return combination.Passes <= 1
            ? recognizer
            : new EnsembleRecognizer(
                recognizer,
                preprocessor,
                preprocess,
                new EnsembleOptions { Passes = combination.Passes });
    }

    /// <summary>Счётчик, восстановленный из разметки фикстуры: разрядность — из числа, вид — из ключа.</summary>
    private static MeterSpec MeterFor(FixtureExpectation expectation)
    {
        MeterKind kind = expectation.MeterKey switch
        {
            var key when key.Contains("hot", StringComparison.OrdinalIgnoreCase) => MeterKind.HotWater,
            var key when key.Contains("elect", StringComparison.OrdinalIgnoreCase) => MeterKind.Electricity,
            _ => MeterKind.ColdWater,
        };

        return new MeterSpec
        {
            Key = expectation.MeterKey,
            DisplayName = expectation.MeterKey,
            Kind = kind,
            Unit = kind == MeterKind.Electricity ? "кВт·ч" : "м³",
            IntegerDigits = expectation.IntegerDigits,
            FractionDigits = expectation.FractionDigits,
            SerialNumber = expectation.Serial,
        };
    }
}
