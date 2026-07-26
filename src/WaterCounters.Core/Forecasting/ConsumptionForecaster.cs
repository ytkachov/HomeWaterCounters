using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Forecasting;

public enum ForecastMethod
{
    /// <summary>Тот же месяц прошлого года, скорректированный на годовой тренд.</summary>
    SeasonalYearOverYear = 0,

    /// <summary>Медиана последних нескольких дельт.</summary>
    RecentMedian = 1,
}

public sealed record ForecastResult
{
    public required string MeterKey { get; init; }

    public required decimal PredictedValue { get; init; }

    public required decimal PredictedDelta { get; init; }

    public required decimal PreviousValue { get; init; }

    public required ForecastMethod Method { get; init; }

    /// <summary>Сколько помесячных дельт участвовало в расчёте.</summary>
    public required int SampleSize { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ForecastFailure
{
    public required string MeterKey { get; init; }

    public required string Reason { get; init; }
}

public sealed record ForecastOptions
{
    /// <summary>Минимум дельт, ниже которого прогнозировать отказываемся.</summary>
    public int MinimumDeltas { get; init; } = 2;

    /// <summary>Сколько последних дельт берём в медиану.</summary>
    public int RecentWindow { get; init; } = 6;

    /// <summary>Верхняя граница как множитель к медиане — защита от выброса.</summary>
    public decimal MaxDeltaMultiplier { get; init; } = 2m;

    /// <summary>Ограничение годового тренда, чтобы аномальный год не улетал в космос.</summary>
    public decimal MaxTrendMultiplier { get; init; } = 1.5m;
}

/// <summary>
/// Прогноз потребления, когда фотографии не сделаны.
///
/// Два принципа, оба сознательные:
/// 1. Медиана, а не среднее — один месяц отпуска или прорыв трубы не должны утащить оценку.
/// 2. Округление вниз. Занижение исправится в следующем месяце по факту; завышение —
///    это переплата сейчас и завышенная база потребления на будущее.
/// </summary>
public sealed class ConsumptionForecaster(ForecastOptions? options = null)
{
    private readonly ForecastOptions _options = options ?? new ForecastOptions();

    /// <summary>
    /// Считает прогноз на период <paramref name="target"/>.
    /// </summary>
    /// <param name="history">
    /// История показаний счётчика. Порядок неважен, дубли по периоду недопустимы.
    /// </param>
    public ForecastResult Forecast(MeterSpec meter, IReadOnlyCollection<MeterReading> history, PeriodKey target)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(history);

        if (!TryForecast(meter, history, target, out ForecastResult? result, out ForecastFailure? failure))
        {
            throw new ForecastUnavailableException(failure!.Reason);
        }

        return result!;
    }

    public bool TryForecast(
        MeterSpec meter,
        IReadOnlyCollection<MeterReading> history,
        PeriodKey target,
        out ForecastResult? result,
        out ForecastFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(history);

        result = null;
        failure = null;

        List<MeterReading> ordered =
        [
            .. history
                .Where(r => r.MeterKey == meter.Key && r.Period < target)
                .GroupBy(r => r.Period)
                .Select(g => g.OrderByDescending(r => r.Source == ReadingSource.Manual).First())
                .OrderBy(r => r.Period)
        ];

        if (ordered.Count == 0)
        {
            failure = Fail(meter, "нет ни одного предыдущего показания");
            return false;
        }

        MeterReading previous = ordered[^1];

        // Дельта достоверна, только если два показания идут подряд по месяцам:
        // разрыв в истории означает, что дельта покрывает несколько периодов.
        List<MonthlyDelta> deltas = [];

        for (int i = 1; i < ordered.Count; i++)
        {
            MeterReading from = ordered[i - 1];
            MeterReading to = ordered[i];

            if (to.Period.MonthsSince(from.Period) != 1)
            {
                continue;
            }

            decimal step = to.Value - from.Value;

            // Отрицательная дельта — переполнение барабана или замена счётчика.
            // И то и другое делает выборку негодной, поэтому просто выбрасываем точку.
            if (step < 0)
            {
                continue;
            }

            deltas.Add(new MonthlyDelta(to.Period, step));
        }

        if (deltas.Count < _options.MinimumDeltas)
        {
            failure = Fail(
                meter,
                $"истории недостаточно: пригодных помесячных дельт {deltas.Count}, нужно минимум {_options.MinimumDeltas}");
            return false;
        }

        List<string> notes = [];
        decimal median = Median([.. deltas.TakeLast(_options.RecentWindow).Select(d => d.Value)]);
        ForecastMethod method = ForecastMethod.RecentMedian;
        decimal rawDelta = median;

        // FirstOrDefault по структуре вернул бы default вместо null, поэтому ищем явно.
        MonthlyDelta? sameMonthLastYear = null;

        foreach (MonthlyDelta candidate in deltas)
        {
            if (candidate.Period.Month == target.Month && target.MonthsSince(candidate.Period) == 12)
            {
                sameMonthLastYear = candidate;
                break;
            }
        }

        if (sameMonthLastYear is { } seasonal && seasonal.Value > 0)
        {
            decimal trend = EstimateYearOverYearTrend(deltas);
            rawDelta = seasonal.Value * trend;
            method = ForecastMethod.SeasonalYearOverYear;
            notes.Add($"взят {target.Month:D2} месяц прошлого года ({seasonal.Value}) с трендом ×{trend:0.###}");
        }

        decimal cap = median * _options.MaxDeltaMultiplier;

        if (rawDelta > cap)
        {
            notes.Add($"прогноз {rawDelta} ограничен потолком {cap} (×{_options.MaxDeltaMultiplier} от медианы)");
            rawDelta = cap;
        }

        decimal delta = meter.RoundDown(Math.Max(0m, rawDelta));
        decimal predicted = previous.Value + delta;

        if (predicted > meter.MaxValue)
        {
            notes.Add("значение перевалило за разрядность счётчика — учтено переполнение барабана");
            predicted -= meter.MaxValue + meter.SmallestIncrement;
        }

        result = new ForecastResult
        {
            MeterKey = meter.Key,
            PredictedValue = predicted,
            PredictedDelta = delta,
            PreviousValue = previous.Value,
            Method = method,
            SampleSize = deltas.Count,
            Notes = notes,
        };

        return true;
    }

    /// <summary>Отношение потребления последних 12 месяцев к предыдущим 12, зажатое в разумные рамки.</summary>
    private decimal EstimateYearOverYearTrend(List<MonthlyDelta> deltas)
    {
        if (deltas.Count < 18)
        {
            return 1m;
        }

        decimal recent = deltas.TakeLast(12).Sum(d => d.Value);
        decimal earlier = deltas.SkipLast(12).TakeLast(12).Sum(d => d.Value);

        if (earlier <= 0)
        {
            return 1m;
        }

        decimal trend = recent / earlier;
        decimal lower = 1m / _options.MaxTrendMultiplier;

        return Math.Clamp(trend, lower, _options.MaxTrendMultiplier);
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        int middle = values.Count / 2;

        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2m;
    }

    private static ForecastFailure Fail(MeterSpec meter, string reason) => new()
    {
        MeterKey = meter.Key,
        Reason = reason,
    };

    private readonly record struct MonthlyDelta(PeriodKey Period, decimal Value);
}

public sealed class ForecastUnavailableException(string reason)
    : Exception($"Прогноз невозможен: {reason}.")
{
    public string Reason { get; } = reason;
}
