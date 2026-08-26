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

    public bool ExactMatch => Actual == Case.Expectation.Value;

    public bool IntegerMatch => Actual is { } value && decimal.Truncate(value) == decimal.Truncate(Case.Expectation.Value);

    public bool SerialMatch =>
        Case.Expectation.Serial is null ||
        (ActualSerial is not null && Normalize(ActualSerial) == Normalize(Case.Expectation.Serial));

    private static string Normalize(string serial) => new([.. serial.Where(char.IsLetterOrDigit)]);
}

public sealed record BenchReport
{
    public required BenchCombination Combination { get; init; }

    public required IReadOnlyList<CaseOutcome> Outcomes { get; init; }

    public int Total => Outcomes.Count;

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
            byte[] jpeg = await File.ReadAllBytesAsync(fixture.Path, ct).ConfigureAwait(false);
            long started = Environment.TickCount64;

            try
            {
                RecognitionResult result = await recognizer
                    .RecognizeAsync(fixture.Meter, jpeg, ct)
                    .ConfigureAwait(false);

                outcomes.Add(new CaseOutcome
                {
                    Case = fixture,
                    Actual = result.Value,
                    ActualSerial = result.Serial,
                    Confidence = result.Confidence,
                    ElapsedMs = result.ElapsedMs > 0 ? result.ElapsedMs : Environment.TickCount64 - started,
                });
            }
            catch (RecognitionException ex)
            {
                // Недоступная модель — такой же результат замера, как неверная цифра:
                // строка остаётся в таблице, иначе комбинация выглядела бы безупречной.
                outcomes.Add(new CaseOutcome
                {
                    Case = fixture,
                    ElapsedMs = Environment.TickCount64 - started,
                    Error = ex.Message,
                });
            }
        }

        return new BenchReport { Combination = combination, Outcomes = outcomes };
    }

    private IMeterRecognizer Build(BenchCombination combination)
    {
        var preprocess = new PreprocessOptions
        {
            MaxDimension = _options.MaxImageDimension,
            Enhance = combination.Preprocess,
            DetectPanel = combination.Preprocess,
        };

        IImagePreprocessor preprocessor = combination.Preprocess
            ? new OpenCvImagePreprocessor()
            : new PassThroughImagePreprocessor();

        var vlmOptions = new VlmRecognizerOptions
        {
            Endpoint = _options.Endpoint,
            Model = combination.Model,
            Prompt = combination.Prompt,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
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
