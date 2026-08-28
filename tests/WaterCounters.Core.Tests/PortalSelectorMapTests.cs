using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

/// <summary>
/// Карта селекторов — единственное место, где числа и даты превращаются в то, что
/// увидит чужая форма. Ошибка здесь не падает, а тихо уезжает в кабинет.
/// </summary>
public sealed class PortalSelectorMapTests
{
    private static PortalSelectorMap Map => new()
    {
        Name = "test",
        LoginUrl = "https://example.test/login",
        LoginInput = "#login",
        PasswordInput = "#password",
        SubmitLoginButton = "#enter",
        LoggedInMarker = ".account",
        ReadingInput = "#value-{portalId}",
        SubmitReadingsButton = "#save",
        SuccessMarker = ".ok",
    };

    [Fact]
    public void FormatValue_DropsTrailingZeros()
    {
        // decimal помнит разрядность источника: распознанные 919,000 кубометров
        // ушли бы в форму строкой "919,000" — для кабинета с целыми кубометрами
        // это отказ формы или неверное показание.
        PortalSelectorMap map = Map with { DecimalSeparator = "," };

        Assert.Equal("919", map.FormatValue(919.000m));
        Assert.Equal("919,5", map.FormatValue(919.500m));
        Assert.Equal("0", map.FormatValue(0.000m));
    }

    [Fact]
    public void FormatValue_UsesPortalDecimalSeparator()
    {
        Assert.Equal("123,456", (Map with { DecimalSeparator = "," }).FormatValue(123.456m));
        Assert.Equal("123.456", Map.FormatValue(123.456m));
    }

    [Fact]
    public void FormatValue_WithExplicitFormat_ObeysIt()
    {
        PortalSelectorMap map = Map with { DecimalSeparator = ",", ValueFormat = "0.000" };

        Assert.Equal("919,000", map.FormatValue(919m));
    }

    [Fact]
    public void FormatValue_TruncatesToPortalDecimalsInsteadOfRounding()
    {
        // 926,603 кубометра — это 926 полных. Округление вверх до 927 заявило бы
        // непотреблённый кубометр, и переплата уехала бы поставщику необратимо.
        PortalSelectorMap map = Map with { DecimalSeparator = ",", ValueDecimals = 0 };

        Assert.Equal("926", map.FormatValue(926.603m));
        Assert.Equal("926", map.FormatValue(926.999m));
        Assert.Equal("62184", map.FormatValue(62184.8m));
    }

    [Fact]
    public void FormatValue_KeepsRequestedDecimalsWhenPortalTakesThem()
    {
        PortalSelectorMap map = Map with { DecimalSeparator = ",", ValueDecimals = 1 };

        Assert.Equal("62184,8", map.FormatValue(62184.89m));
    }

    [Fact]
    public void Expand_SubstitutesPeriodTokens()
    {
        string selector = Map.Expand(
            "tr:has(td:text-is('01.{MM}.{yyyy}')):has(td:text-is('{value}'))",
            period: new PeriodKey(2026, 8),
            value: "919");

        Assert.Equal("tr:has(td:text-is('01.08.2026')):has(td:text-is('919'))", selector);
    }

    [Fact]
    public void Expand_LeavesUnknownTokensAlone()
    {
        // Селектор успеха собирается до отправки, когда значения ещё нет: подстановка
        // должна дождаться его, а не подставить пустоту и совпасть с чем попало.
        string selector = Map.Expand("tr:has(td:text-is('{value}'))", portalId: "W-1");

        Assert.Equal("tr:has(td:text-is('{value}'))", selector);
    }

    [Fact]
    public void MeterPageUrl_DrivesPerMeterMode()
    {
        Assert.False(Map.IsPerMeter);

        PortalSelectorMap perMeter = Map with { MeterPageUrl = "https://example.test/m?id={portalId}" };

        Assert.True(perMeter.IsPerMeter);
        Assert.Equal("https://example.test/m?id=W-1", perMeter.MeterPageUrlFor("W-1"));
    }

    [Fact]
    public void MeterPageUrlFor_WithoutTemplate_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => Map.MeterPageUrlFor("W-1"));
    }
}
