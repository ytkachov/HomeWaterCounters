using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

public class PeriodKeyTests
{
    [Theory]
    [InlineData("2026-01", 2026, 1)]
    [InlineData("2026-12", 2026, 12)]
    [InlineData("1999-07", 1999, 7)]
    public void Parse_ValidStrings(string text, int year, int month)
    {
        PeriodKey period = PeriodKey.Parse(text);

        Assert.Equal(year, period.Year);
        Assert.Equal(month, period.Month);
        Assert.Equal(text, period.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("2026/01")]
    [InlineData("26-01")]
    [InlineData("2026-1")]
    [InlineData("2026-01-01")]
    [InlineData(null)]
    public void TryParse_RejectsMalformed(string? text)
    {
        Assert.False(PeriodKey.TryParse(text, out _));
    }

    [Fact]
    public void AddMonths_CrossesYearBoundaryBothWays()
    {
        var december = new PeriodKey(2026, 12);

        Assert.Equal(new PeriodKey(2027, 1), december.Next());
        Assert.Equal(new PeriodKey(2026, 11), december.Previous());
        Assert.Equal(new PeriodKey(2027, 6), december.AddMonths(6));
        Assert.Equal(new PeriodKey(2025, 12), december.AddMonths(-12));
    }

    [Fact]
    public void MonthsSince_CountsAcrossYears()
    {
        Assert.Equal(12, new PeriodKey(2027, 3).MonthsSince(new PeriodKey(2026, 3)));
        Assert.Equal(1, new PeriodKey(2027, 1).MonthsSince(new PeriodKey(2026, 12)));
        Assert.Equal(-1, new PeriodKey(2026, 12).MonthsSince(new PeriodKey(2027, 1)));
    }

    [Fact]
    public void Comparison_OrdersChronologically()
    {
        Assert.True(new PeriodKey(2026, 1) < new PeriodKey(2026, 2));
        Assert.True(new PeriodKey(2027, 1) > new PeriodKey(2026, 12));
        Assert.True(new PeriodKey(2026, 5) >= new PeriodKey(2026, 5));
    }

    [Fact]
    public void DeadlineDate_ClampsToMonthLength()
    {
        // 31-е число в апреле не существует — дедлайн должен съехать на 30-е,
        // иначе напоминание в коротком месяце просто не сработает.
        Assert.Equal(new DateOnly(2026, 4, 30), new PeriodKey(2026, 4).DeadlineDate(31));
        Assert.Equal(new DateOnly(2026, 2, 28), new PeriodKey(2026, 2).DeadlineDate(30));
        Assert.Equal(new DateOnly(2028, 2, 29), new PeriodKey(2028, 2).DeadlineDate(30));
        Assert.Equal(new DateOnly(2026, 5, 15), new PeriodKey(2026, 5).DeadlineDate(15));
    }

    [Fact]
    public void Constructor_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodKey(2026, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodKey(2026, 13));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodKey(1800, 1));
    }
}
