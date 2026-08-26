using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Scheduling;

namespace WaterCounters.Desktop.Tests;

/// <summary>
/// Расчёт срока живёт в Core, потому что им пользуются обе части: телефон шлёт по нему
/// напоминания, обработчик по нему же решает, пора ли считать прогноз. Две реализации
/// разошлись бы, и расхождение проявилось бы раз в месяц в худший момент.
/// </summary>
public class SubmissionScheduleTests
{
    private static readonly PeriodKey July = new(2026, 7);

    private static readonly SubmissionSchedule Schedule = new(new ScheduleSettings
    {
        DeadlineDayOfMonth = 25,
        GraceDays = 3,
        ReminderDaysBefore = 3,
        TimeZoneId = "UTC",
    });

    [Fact]
    public void WindowIsDeadlinePlusGrace()
    {
        SubmissionWindow window = Schedule.WindowFor(July);

        Assert.Equal(new DateOnly(2026, 7, 22), window.ReminderFrom);
        Assert.Equal(new DateOnly(2026, 7, 25), window.Deadline);
        Assert.Equal(new DateOnly(2026, 7, 28), window.GraceEnd);
    }

    [Fact]
    public void LastDayOfGraceStillWaitsForPhotos()
    {
        // Строгое «больше», а не «больше либо равно»: в последний день льготы человек
        // ещё может успеть снять счётчики, и прогноз затёр бы настоящие показания.
        Assert.False(Schedule.IsForecastDue(July, At(2026, 7, 28, 23)));
        Assert.True(Schedule.IsForecastDue(July, At(2026, 7, 29, 0)));
    }

    [Fact]
    public void DeadlinePassesTheDayAfterTheDeadline()
    {
        Assert.False(Schedule.IsDeadlinePassed(July, At(2026, 7, 25, 23)));
        Assert.True(Schedule.IsDeadlinePassed(July, At(2026, 7, 26, 0)));
    }

    [Fact]
    public void ReminderRunsFromThreeDaysBeforeUntilGraceEnds()
    {
        Assert.False(Schedule.IsReminderDue(July, At(2026, 7, 21, 12)));
        Assert.True(Schedule.IsReminderDue(July, At(2026, 7, 22, 12)));
        Assert.True(Schedule.IsReminderDue(July, At(2026, 7, 28, 12)));
        Assert.False(Schedule.IsReminderDue(July, At(2026, 7, 29, 12)));
    }

    [Fact]
    public void ShortMonthClampsTheDeadlineDay()
    {
        var monthEnd = new SubmissionSchedule(new ScheduleSettings { DeadlineDayOfMonth = 31, TimeZoneId = "UTC" });

        Assert.Equal(new DateOnly(2026, 2, 28), monthEnd.WindowFor(new PeriodKey(2026, 2)).Deadline);
        Assert.Equal(new DateOnly(2026, 4, 30), monthEnd.WindowFor(new PeriodKey(2026, 4)).Deadline);
    }

    [Fact]
    public void OpenPeriodsCoverTheOneThatCouldHaveLapsedWhileTheHostWasOff()
    {
        // Предыдущий период нужен: его льготный срок мог истечь, пока обработчик был
        // выключен, и пропуск месяца не должен случиться молча.
        Assert.Equal(
            new[] { new PeriodKey(2026, 6), new PeriodKey(2026, 7) },
            Schedule.OpenPeriods(At(2026, 7, 2, 10)));
    }

    [Fact]
    public void UnknownTimeZoneFallsBackInsteadOfThrowing()
    {
        // Идентификаторы поясов на Windows и Linux разные, а телефон и десктоп могут
        // стоять на разных системах. Падать из-за этого нельзя — срок важнее часа.
        var schedule = new SubmissionSchedule(new ScheduleSettings { TimeZoneId = "Нет/Такого/Пояса" });

        Assert.Equal(new DateOnly(2026, 7, 25), schedule.WindowFor(July).Deadline);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
