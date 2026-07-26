using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace WaterCounters.Core.Metering;

/// <summary>
/// Расчётный период — год и месяц. Сериализуется как "yyyy-MM".
/// </summary>
[JsonConverter(typeof(PeriodKeyJsonConverter))]
public readonly record struct PeriodKey : IComparable<PeriodKey>
{
    public PeriodKey(int year, int month)
    {
        if (year is < 1900 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "Год вне допустимого диапазона.");
        }

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "Месяц должен быть от 1 до 12.");
        }

        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    /// <summary>Порядковый номер месяца от нулевой точки — основа для сравнения и арифметики.</summary>
    private int Ordinal => (Year * 12) + (Month - 1);

    public static PeriodKey FromDate(DateTimeOffset moment) => new(moment.Year, moment.Month);

    public PeriodKey AddMonths(int months)
    {
        int ordinal = Ordinal + months;
        return new PeriodKey(Math.DivRem(ordinal, 12, out int monthIndex), monthIndex + 1);
    }

    public PeriodKey Next() => AddMonths(1);

    public PeriodKey Previous() => AddMonths(-1);

    /// <summary>Количество месяцев от <paramref name="earlier"/> до текущего периода.</summary>
    public int MonthsSince(PeriodKey earlier) => Ordinal - earlier.Ordinal;

    /// <summary>Дата дедлайна внутри периода. День обрезается до длины месяца (31 → 30 в апреле).</summary>
    public DateOnly DeadlineDate(int dayOfMonth)
    {
        if (dayOfMonth is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfMonth), dayOfMonth, "День должен быть от 1 до 31.");
        }

        int clamped = Math.Min(dayOfMonth, DateTime.DaysInMonth(Year, Month));
        return new DateOnly(Year, Month, clamped);
    }

    public static PeriodKey Parse(string value) =>
        TryParse(value, out PeriodKey period)
            ? period
            : throw new FormatException($"Период '{value}' не соответствует формату yyyy-MM.");

    public static bool TryParse([NotNullWhen(true)] string? value, out PeriodKey period)
    {
        period = default;

        if (value is null || value.Length != 7 || value[4] != '-')
        {
            return false;
        }

        if (!int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month))
        {
            return false;
        }

        if (year is < 1900 or > 9999 || month is < 1 or > 12)
        {
            return false;
        }

        period = new PeriodKey(year, month);
        return true;
    }

    public int CompareTo(PeriodKey other) => Ordinal.CompareTo(other.Ordinal);

    public static bool operator <(PeriodKey left, PeriodKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(PeriodKey left, PeriodKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(PeriodKey left, PeriodKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(PeriodKey left, PeriodKey right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
