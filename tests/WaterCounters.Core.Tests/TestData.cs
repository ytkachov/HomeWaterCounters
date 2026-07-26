using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static MeterSpec ColdWater { get; } = new()
    {
        Key = "cold-water",
        DisplayName = "Холодная вода",
        Kind = MeterKind.ColdWater,
        Unit = "м³",
        IntegerDigits = 5,
        FractionDigits = 3,
        SerialNumber = "12-345-678",
    };

    public static MeterSpec Electricity { get; } = new()
    {
        Key = "electricity",
        DisplayName = "Электричество",
        Kind = MeterKind.Electricity,
        Unit = "кВт·ч",
        IntegerDigits = 6,
        FractionDigits = 1,
    };

    /// <summary>История подряд идущих месяцев с заданными значениями, начиная с <paramref name="start"/>.</summary>
    public static List<MeterReading> HistoryFor(MeterSpec meter, PeriodKey start, params decimal[] values)
    {
        List<MeterReading> readings = [];
        PeriodKey period = start;

        foreach (decimal value in values)
        {
            readings.Add(new MeterReading
            {
                MeterKey = meter.Key,
                Period = period,
                Value = value,
                Source = ReadingSource.Manual,
            });

            period = period.Next();
        }

        return readings;
    }
}
