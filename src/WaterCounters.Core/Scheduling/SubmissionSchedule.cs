using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Scheduling;

/// <summary>Календарь одного периода: когда напоминать, когда срок, когда сдаваться и считать прогноз.</summary>
public sealed record SubmissionWindow
{
    public required PeriodKey Period { get; init; }

    public required DateOnly ReminderFrom { get; init; }

    public required DateOnly Deadline { get; init; }

    /// <summary>Последний день льготного периода включительно.</summary>
    public required DateOnly GraceEnd { get; init; }
}

/// <summary>
/// Расчёт срока сдачи и льготного периода.
///
/// Живёт в Core, потому что им пользуются обе части: телефон по нему шлёт напоминания,
/// обработчик по нему же решает, пора ли считать прогноз. Две реализации неизбежно
/// разошлись бы, и расхождение проявилось бы раз в месяц в худший момент.
/// </summary>
public sealed class SubmissionSchedule
{
    private readonly ScheduleSettings _settings;
    private readonly TimeZoneInfo _zone;

    public SubmissionSchedule(ScheduleSettings? settings = null)
    {
        _settings = settings ?? new ScheduleSettings();
        _zone = ResolveZone(_settings.TimeZoneId);
    }

    /// <summary>Локальная дата в календаре срока. Именно она, а не UTC: срок — календарный.</summary>
    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);

    public PeriodKey CurrentPeriod(DateTimeOffset now)
    {
        DateOnly today = Today(now);
        return new PeriodKey(today.Year, today.Month);
    }

    public SubmissionWindow WindowFor(PeriodKey period)
    {
        DateOnly deadline = period.DeadlineDate(_settings.DeadlineDayOfMonth);

        return new SubmissionWindow
        {
            Period = period,
            ReminderFrom = deadline.AddDays(-Math.Max(0, _settings.ReminderDaysBefore)),
            Deadline = deadline,
            GraceEnd = deadline.AddDays(Math.Max(0, _settings.GraceDays)),
        };
    }

    public bool IsReminderDue(PeriodKey period, DateTimeOffset now)
    {
        SubmissionWindow window = WindowFor(period);
        DateOnly today = Today(now);
        return today >= window.ReminderFrom && today <= window.GraceEnd;
    }

    public bool IsDeadlinePassed(PeriodKey period, DateTimeOffset now) => Today(now) > WindowFor(period).Deadline;

    /// <summary>
    /// Льготный период истёк — фотографий уже не будет. Строгое «больше», а не «больше
    /// либо равно»: в последний день льготы человек ещё может успеть снять счётчики.
    /// </summary>
    public bool IsForecastDue(PeriodKey period, DateTimeOffset now) => Today(now) > WindowFor(period).GraceEnd;

    /// <summary>
    /// Периоды, которые обработчик обязан держать в поле зрения. Текущий — потому что
    /// он в работе; предыдущий — потому что его льготный период мог истечь, пока
    /// обработчик был выключен, и пропуск месяца не должен молча случиться.
    /// </summary>
    public IReadOnlyList<PeriodKey> OpenPeriods(DateTimeOffset now)
    {
        PeriodKey current = CurrentPeriod(now);
        return [current.Previous(), current];
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Идентификаторы поясов на Windows и Linux разные, а телефон и десктоп могут
            // стоять на разных системах. Падать из-за этого нельзя — срок важнее часа.
            return TimeZoneInfo.Local;
        }
    }
}
