using System.Globalization;
using System.Text;
using WaterCounters.Recognition.Bench;

BenchOptions options;

try
{
    options = BenchOptions.Parse(args);
}
catch (BenchUsageException ex)
{
    if (!string.IsNullOrEmpty(ex.Message))
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine();
    }

    Console.WriteLine(BenchOptions.Usage);
    return string.IsNullOrEmpty(ex.Message) ? 0 : 2;
}

IReadOnlyList<FixtureCase> cases;

try
{
    cases = BenchRunner.LoadFixtures(options.Fixtures);
}
catch (BenchUsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (cases.Count == 0)
{
    Console.Error.WriteLine(
        $"В '{options.Fixtures}' нет размеченных фикстур. " +
        "Ожидается имя вида cold-water_01234.567_12-345-678.jpg.");
    return 2;
}

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// Таймаут держит распознаватель на своём токене; клиент не должен обрывать раньше него.
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var runner = new BenchRunner(options, http);

Console.WriteLine($"Фикстур: {cases.Count}. Комбинаций: {options.Combinations.Count}. Хост: {options.Endpoint}");

if (options.Augment > 0)
{
    int variants = Math.Min(options.Augment, FixtureAugmenter.MaxVariants);

    // Проговаривается явно и каждый раз: доля совпадений считается по вариантам, а
    // независимых наблюдений за ней стоит ровно столько, сколько настоящих снимков.
    Console.WriteLine(
        $"Аугментация: {variants} вариантов на снимок, всего {cases.Count * (variants + 1)} прогонов. " +
        $"Это мера устойчивости к условиям съёмки — независимых фотографий по-прежнему {cases.Count}.");
}
Console.WriteLine();

List<BenchReport> reports = [];

foreach (BenchCombination combination in options.Combinations)
{
    if (cancellation.IsCancellationRequested)
    {
        break;
    }

    Console.Write($"  {combination} … ");

    BenchReport report;

    try
    {
        report = await runner.RunAsync(combination, cases, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("прервано");
        break;
    }

    reports.Add(report);
    Console.WriteLine(
        $"точных {report.ExactShare:P1}, по целой части {report.IntegerShare:P1}, {report.MeanLatencyMs:N0} мс");

    if (options.Verbose)
    {
        PrintMismatches(report);
    }
}

if (reports.Count == 0)
{
    return 1;
}

Console.WriteLine();
PrintTable(reports);

if (options.CsvPath is { } csv)
{
    await File.WriteAllTextAsync(csv, BuildCsv(reports), new UTF8Encoding(true), cancellation.Token);
    Console.WriteLine();
    Console.WriteLine($"Таблица сохранена в {csv}");
}

// Критерий готовности из docs/recognition-service.md: доля точных совпадений по
// целой части не ниже 95 %. Ненулевой код возврата делает его проверяемым в CI.
const double IntegerTarget = 0.95;
BenchReport best = reports.MaxBy(r => r.IntegerShare)!;

Console.WriteLine();

if (best.IntegerShare >= IntegerTarget)
{
    Console.WriteLine($"Порог 95 % по целой части взят: {best.Combination} — {best.IntegerShare:P1}.");
    return 0;
}

Console.WriteLine($"Порог 95 % по целой части не взят. Лучшая комбинация: {best.Combination} — {best.IntegerShare:P1}.");
return 1;

static void PrintMismatches(BenchReport report)
{
    foreach (CaseOutcome outcome in report.Outcomes.Where(o => !o.ExactMatch || o.Error is not null))
    {
        string actual = outcome.Error is { } error
            ? $"ошибка: {error}"
            : $"получено {Format(outcome.Actual)} (уверенность {outcome.Confidence:P0})";

        // Вариант снимка печатается всегда: смысл аугментации в том, чтобы увидеть,
        // какое именно искажение ломает распознавание, а не только сколько их всего.
        string variant = outcome.IsOriginal ? string.Empty : $" [{outcome.Variant}]";

        Console.WriteLine(
            $"      {outcome.Case.Expectation.FileName}{variant}: " +
            $"ждали {outcome.Case.Expectation.Value}, {actual}");
    }
}

static void PrintTable(IReadOnlyList<BenchReport> reports)
{
    string[] headers = ["комбинация", "N", "точных", "целая часть", "ошиб. цифр", "серийник", "мс"];

    List<string[]> rows =
    [
        headers,
        .. reports.OrderByDescending(r => r.IntegerShare).Select(r => new[]
        {
            r.Combination.ToString(),
            r.Total.ToString(CultureInfo.InvariantCulture),
            r.ExactShare.ToString("P1", CultureInfo.InvariantCulture),
            r.IntegerShare.ToString("P1", CultureInfo.InvariantCulture),
            r.DigitErrorShare.ToString("P2", CultureInfo.InvariantCulture),
            r.SerialShare.ToString("P1", CultureInfo.InvariantCulture),
            r.MeanLatencyMs.ToString("N0", CultureInfo.InvariantCulture),
        }),
    ];

    int[] widths = [.. Enumerable.Range(0, headers.Length).Select(i => rows.Max(row => row[i].Length))];

    for (int i = 0; i < rows.Count; i++)
    {
        Console.WriteLine(string.Join("  ", rows[i].Select((cell, column) =>
            column == 0 ? cell.PadRight(widths[column]) : cell.PadLeft(widths[column]))));

        if (i == 0)
        {
            Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        }
    }
}

static string BuildCsv(IReadOnlyList<BenchReport> reports)
{
    var csv = new StringBuilder();
    csv.AppendLine("model;prompt;preprocess;passes;fixtures;errors;exact;integer;digit_errors;serial;mean_ms");

    foreach (BenchReport r in reports)
    {
        csv.Append(CultureInfo.InvariantCulture, $"{r.Combination.Model};{r.Combination.Prompt};");
        csv.Append(CultureInfo.InvariantCulture, $"{(r.Combination.Preprocess ? "on" : "off")};{r.Combination.Passes};");
        csv.Append(CultureInfo.InvariantCulture, $"{r.Total};{r.Errors};");
        csv.Append(CultureInfo.InvariantCulture, $"{r.ExactShare:F4};{r.IntegerShare:F4};");
        csv.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{r.DigitErrorShare:F4};{r.SerialShare:F4};{r.MeanLatencyMs:F0}"));
    }

    return csv.ToString();
}

static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "ничего";
