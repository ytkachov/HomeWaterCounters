using WaterCounters.Core.Forecasting;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

public class ConsumptionForecasterTests
{
    private readonly ConsumptionForecaster _forecaster = new();

    [Fact]
    public void Refuses_WhenHistoryIsEmpty()
    {
        bool ok = _forecaster.TryForecast(TestData.ColdWater, [], new PeriodKey(2026, 7), out _, out ForecastFailure? failure);

        Assert.False(ok);
        Assert.NotNull(failure);
        Assert.Contains("нет ни одного", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_WhenTooFewDeltas()
    {
        // Одно показание — это ноль дельт. Выдумывать число нельзя: оно уйдёт
        // поставщику и станет базой для следующих начислений.
        List<MeterReading> history = TestData.HistoryFor(TestData.ColdWater, new PeriodKey(2026, 6), 100m);

        bool ok = _forecaster.TryForecast(TestData.ColdWater, history, new PeriodKey(2026, 7), out _, out ForecastFailure? failure);

        Assert.False(ok);
        Assert.Contains("недостаточно", failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Forecast_UsesMedianOfRecentDeltas()
    {
        // Дельты 5, 5, 5 → медиана 5, прогноз 115 + 5.
        List<MeterReading> history = TestData.HistoryFor(
            TestData.ColdWater, new PeriodKey(2026, 3), 100m, 105m, 110m, 115m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(ForecastMethod.RecentMedian, result.Method);
        Assert.Equal(5m, result.PredictedDelta);
        Assert.Equal(120m, result.PredictedValue);
        Assert.Equal(115m, result.PreviousValue);
        Assert.Equal(3, result.SampleSize);
    }

    [Fact]
    public void Forecast_MedianIgnoresSingleOutlier()
    {
        // Месяц с прорывом трубы (дельта 100) не должен утащить прогноз наверх —
        // ровно ради этого медиана, а не среднее.
        List<MeterReading> history = TestData.HistoryFor(
            TestData.ColdWater, new PeriodKey(2026, 1), 100m, 105m, 110m, 210m, 215m, 220m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(5m, result.PredictedDelta);
    }

    [Fact]
    public void Forecast_RoundsDownToMeterPrecision()
    {
        // Дельты 3.333 и 3.334 → медиана 3.3335, счётчик знает только тысячные:
        // округляем ВНИЗ, потому что завышение — это переплата.
        List<MeterReading> history = TestData.HistoryFor(
            TestData.ColdWater, new PeriodKey(2026, 4), 100m, 103.333m, 106.667m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(3.333m, result.PredictedDelta);
        Assert.Equal(110m, result.PredictedValue);
    }

    [Fact]
    public void Forecast_SkipsGapsInHistory()
    {
        // Между мартом и июнем пропуск: разность 30 покрывает три месяца и как
        // помесячная дельта негодна. Остаются только соседние пары.
        List<MeterReading> history =
        [
            .. TestData.HistoryFor(TestData.ColdWater, new PeriodKey(2026, 1), 100m, 105m, 110m),
            new MeterReading
            {
                MeterKey = TestData.ColdWater.Key,
                Period = new PeriodKey(2026, 6),
                Value = 140m,
                Source = ReadingSource.Manual,
            },
        ];

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(5m, result.PredictedDelta);
        Assert.Equal(145m, result.PredictedValue);
        Assert.Equal(2, result.SampleSize);
    }

    [Fact]
    public void Forecast_DiscardsNegativeDeltas()
    {
        // Замена счётчика: значение упало. Точка выбрасывается, а не считается нулём.
        List<MeterReading> history = TestData.HistoryFor(
            TestData.ColdWater, new PeriodKey(2026, 1), 100m, 105m, 110m, 5m, 10m, 15m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(5m, result.PredictedDelta);
        Assert.Equal(20m, result.PredictedValue);
    }

    [Fact]
    public void Forecast_PrefersSameMonthLastYear()
    {
        // Год ровных 5 м³ и повышенный расход 8 в июле прошлого года. Для июля берём
        // сезонное значение, а не медиану.
        List<MeterReading> history = SeasonalHistory(julyLastYearDelta: 8m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(ForecastMethod.SeasonalYearOverYear, result.Method);
        Assert.Equal(8m, result.PredictedDelta);
        Assert.Contains(result.Notes, n => n.Contains("прошлого года", StringComparison.Ordinal));
    }

    [Fact]
    public void Forecast_CapsSeasonalSpikeAtMultipleOfMedian()
    {
        // Даже сезонный всплеск ограничен потолком ×2 от медианы: аномалия прошлого
        // года не должна автоматически повторяться в этом.
        var forecaster = new ConsumptionForecaster(new ForecastOptions { MaxDeltaMultiplier = 2m });
        List<MeterReading> history = SeasonalHistory(julyLastYearDelta: 100m);

        ForecastResult result = forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        // Медиана 5, потолок ×2 → 10, несмотря на прошлогодние 100.
        Assert.Equal(10m, result.PredictedDelta);
        Assert.Contains(result.Notes, n => n.Contains("ограничен потолком", StringComparison.Ordinal));
    }

    /// <summary>
    /// 14 месяцев с 2025-02: все дельты по 5 м³, кроме дельты, попадающей в 2025-07.
    /// Дельта относится к периоду, в котором она набежала, — то есть прибавка между
    /// значением за 2025-06 и значением за 2025-07.
    /// </summary>
    private static List<MeterReading> SeasonalHistory(decimal julyLastYearDelta)
    {
        var start = new PeriodKey(2025, 2);
        var julyLastYear = new PeriodKey(2025, 7);

        List<decimal> values = [];
        decimal current = 100m;
        PeriodKey period = start;

        for (int i = 0; i < 14; i++)
        {
            values.Add(current);
            current += period.Next() == julyLastYear ? julyLastYearDelta : 5m;
            period = period.Next();
        }

        return TestData.HistoryFor(TestData.ColdWater, start, [.. values]);
    }

    [Fact]
    public void Forecast_IgnoresOtherMeters()
    {
        List<MeterReading> history =
        [
            .. TestData.HistoryFor(TestData.ColdWater, new PeriodKey(2026, 4), 100m, 105m, 110m),
            .. TestData.HistoryFor(TestData.Electricity, new PeriodKey(2026, 4), 5000m, 5300m, 5600m),
        ];

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(5m, result.PredictedDelta);
        Assert.Equal(115m, result.PredictedValue);
    }

    [Fact]
    public void Forecast_IgnoresPeriodsAtOrAfterTarget()
    {
        // Значение за сам целевой период (999) уже лежит в истории — например,
        // после частичного прогона. В расчёт оно попасть не должно.
        List<MeterReading> history = TestData.HistoryFor(
            TestData.ColdWater, new PeriodKey(2026, 3), 100m, 105m, 110m, 115m, 999m);

        ForecastResult result = _forecaster.Forecast(TestData.ColdWater, history, new PeriodKey(2026, 7));

        Assert.Equal(115m, result.PreviousValue);
        Assert.Equal(120m, result.PredictedValue);
    }

    [Fact]
    public void Forecast_ThrowsWhenUnavailable()
    {
        Assert.Throws<ForecastUnavailableException>(
            () => _forecaster.Forecast(TestData.ColdWater, [], new PeriodKey(2026, 7)));
    }
}
