using WaterCounters.Core.Metering;
using WaterCounters.Core.Configuration;

namespace WaterCounters.Portal.Tests;

/// <summary>
/// Адаптер гоняется настоящим Chromium против макета кабинета. Именно так и только
/// так проверяется то, ради чего он написан: реальные клики, реальные cookie и
/// реальные ответы формы.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class PortalAdapterTests : IDisposable
{
    private static readonly PeriodKey Period = new(2026, 7);

    private readonly MockPortalServer _portal = new();
    private readonly string _profileDirectory = Path.Combine(
        PlaywrightBrowsersFixture.ProfileRoot, Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _portal.Dispose();

        try
        {
            if (Directory.Exists(_profileDirectory))
            {
                Directory.Delete(_profileDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Профиль браузера мог не успеть освободиться — на результат теста не влияет.
        }
    }

    private PortalSelectorMap Map => new()
    {
        Name = "mock",
        LoginUrl = _portal.LoginUrl,
        ReadingsUrl = _portal.ReadingsUrl,
        LoginInput = "#username",
        PasswordInput = "#password",
        SubmitLoginButton = "#do-login",
        LoggedInMarker = ".account-header",
        LoginErrorMarker = ".login-error",
        ReadingInput = "[data-meter='{portalId}'] input.reading",
        SubmitReadingsButton = "#save-readings",
        SuccessMarker = ".alert-success",
        AlreadySubmittedMarker = ".period-closed",
        ValidationErrorMarker = ".field-error",
        DecimalSeparator = ",",
        ActionTimeoutMs = 5_000,
        NavigationTimeoutMs = 10_000,
    };

    private Task<ConfigurablePortalAdapter> CreateAdapterAsync(PortalSelectorMap? map = null) =>
        ConfigurablePortalAdapter.CreateAsync(
            map ?? Map,
            new PortalAdapterOptions
            {
                UserDataDirectory = _profileDirectory,
                Headless = true,
            });

    private static IReadOnlyList<PortalReading> Readings() =>
    [
        new PortalReading { MeterKey = "cold-water", PortalId = "W-1", Value = 123.456m },
        new PortalReading { MeterKey = "electricity", PortalId = "E-1", Value = 5000.1m },
    ];

    [Fact]
    public async Task Login_WithValidCredentials_Succeeds()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();

        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        Assert.True(await adapter.IsLoggedInAsync());
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReportsPortalMessage()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();

        PortalLoginException ex = await Assert.ThrowsAsync<PortalLoginException>(
            () => adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, "неверный")));

        // Различие принципиальное: «пароль не подошёл» правится в настройках,
        // а «маркер не найден» означает, что сайт переделали.
        Assert.Contains("Неверный логин или пароль", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WhenMarkerMissing_ReportsLayoutChangeRatherThanBadPassword()
    {
        PortalSelectorMap broken = Map with { LoggedInMarker = ".marker-which-does-not-exist" };
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(broken);

        PortalLoginException ex = await Assert.ThrowsAsync<PortalLoginException>(
            () => adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword)));

        Assert.Contains("маркер авторизации не появился", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_SendsValuesInPortalDecimalFormat()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(Period, Readings(), dryRun: false);

        Assert.Equal(SubmissionStatus.Submitted, receipt.Status);
        Assert.Equal("Показания приняты", receipt.PortalMessage);
        Assert.NotNull(receipt.Screenshot);

        // Кабинет ждёт запятую; при точке он молча отбросил бы дробную часть.
        Assert.Equal("123,456", _portal.Accepted["W-1"]);
        Assert.Equal("5000,1", _portal.Accepted["E-1"]);
    }

    [Fact]
    public async Task Submit_DryRun_FillsFormButNeverSubmits()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(Period, Readings(), dryRun: true);

        Assert.Equal(SubmissionStatus.DryRun, receipt.Status);
        Assert.NotNull(receipt.Screenshot);

        // Главная гарантия режима проверки: сервер не увидел ни одной отправки.
        Assert.Equal(0, _portal.SubmitCount);
        Assert.Empty(_portal.Accepted);
    }

    [Fact]
    public async Task Submit_WhenPeriodAlreadyClosed_DoesNotSendAgain()
    {
        _portal.PeriodClosed = true;

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(Period, Readings(), dryRun: false);

        Assert.Equal(SubmissionStatus.AlreadySubmitted, receipt.Status);
        Assert.Equal(0, _portal.SubmitCount);
    }

    [Fact]
    public async Task Submit_TwiceInARow_SecondCallIsRecognisedAsAlreadySubmitted()
    {
        // Ровно то, что произойдёт при перезапуске десктопа после падения между
        // отправкой и записью результата.
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        await adapter.SubmitAsync(Period, Readings(), dryRun: false);
        SubmissionReceipt second = await adapter.SubmitAsync(Period, Readings(), dryRun: false);

        Assert.Equal(SubmissionStatus.AlreadySubmitted, second.Status);
        Assert.Equal(1, _portal.SubmitCount);
    }

    [Fact]
    public async Task Submit_WhenPortalRejectsValue_RaisesWithPortalText()
    {
        // Разделитель намеренно неверный — кабинет ответит ошибкой валидации.
        PortalSelectorMap wrongFormat = Map with { DecimalSeparator = "." };

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(wrongFormat);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        PortalSubmissionException ex = await Assert.ThrowsAsync<PortalSubmissionException>(
            () => adapter.SubmitAsync(Period, Readings(), dryRun: false));

        Assert.Contains("Недопустимый формат числа", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Screenshot);
        Assert.Empty(_portal.Accepted);
    }

    [Fact]
    public async Task Submit_WhenMeterFieldMissing_NamesTheMeter()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        IReadOnlyList<PortalReading> unknown =
        [
            new PortalReading { MeterKey = "gas", PortalId = "G-9", Value = 1m },
        ];

        PortalSubmissionException ex = await Assert.ThrowsAsync<PortalSubmissionException>(
            () => adapter.SubmitAsync(Period, unknown, dryRun: false));

        Assert.Contains("G-9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _portal.SubmitCount);
    }

    [Fact]
    public async Task Submit_WithoutLogin_FailsBeforeTouchingTheForm()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();

        PortalSubmissionException ex = await Assert.ThrowsAsync<PortalSubmissionException>(
            () => adapter.SubmitAsync(Period, Readings(), dryRun: false));

        Assert.Contains("Сессия не активна", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _portal.SubmitCount);
    }

    [Fact]
    public async Task Session_SurvivesAdapterRestart_WhenPortalIssuesPersistentCookie()
    {
        // Ради этого и нужен постоянный профиль: 2FA или капчу проходим один раз
        // руками, дальше автоматика работает молча.
        _portal.UsePersistentCookie = true;

        await using (ConfigurablePortalAdapter first = await CreateAdapterAsync())
        {
            await first.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));
        }

        await using ConfigurablePortalAdapter second = await CreateAdapterAsync();

        Assert.True(await second.IsLoggedInAsync());
    }

    [Fact]
    public async Task Session_DoesNotSurviveRestart_WhenPortalIssuesSessionCookie()
    {
        // Обратная сторона: если кабинет выдаёт сессионную cookie, браузер её
        // выбросит при закрытии, и постоянный профиль не поможет — каждый запуск
        // десктопа потребует нового входа со всеми капчами и 2FA.
        // Это ограничение самого сайта, а не адаптера; знать о нём нужно заранее.
        _portal.UsePersistentCookie = false;

        await using (ConfigurablePortalAdapter first = await CreateAdapterAsync())
        {
            await first.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));
            Assert.True(await first.IsLoggedInAsync());
        }

        await using ConfigurablePortalAdapter second = await CreateAdapterAsync();

        Assert.False(await second.IsLoggedInAsync());
    }

    /// <summary>
    /// Кабинет, принимающий показания по одному счётчику. Отдельного признака
    /// «уже сдано» у такого кабинета нет: сдано или нет, видно только по строке
    /// истории с датой начала периода.
    /// </summary>
    private PortalSelectorMap PerMeterMap => Map with
    {
        Name = "mock-per-meter",
        MeterPageUrl = _portal.MeterPageUrl,
        ReadingInput = "input[name='sch_val']",
        AlreadySubmittedMarker = "table.history tr:has(td:text-is('01.{MM}.{yyyy}'))",
        SuccessMarker = "table.history tr:has(td:text-is('01.{MM}.{yyyy}')):has(td:text-is('{value}'))",
    };

    private static IReadOnlyList<PortalReading> WholeReadings() =>
    [
        new PortalReading { MeterKey = "cold-water", PortalId = "W-1", Value = 919m },
        new PortalReading { MeterKey = "electricity", PortalId = "E-1", Value = 62179m },
    ];

    [Fact]
    public async Task SubmitPerMeter_SendsEachMeterAsItsOwnSubmission()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(PerMeterMap);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(_portal.CurrentPeriod, WholeReadings(), dryRun: false);

        Assert.Equal(SubmissionStatus.Submitted, receipt.Status);

        // Одна форма — один счётчик: две отправки, а не одна на всю квартиру.
        Assert.Equal(2, _portal.SubmitCount);
        Assert.Equal("919", _portal.Accepted["W-1"]);
        Assert.Equal("62179", _portal.Accepted["E-1"]);

        Assert.All(receipt.Details, d => Assert.Equal(SubmissionStatus.Submitted, d.Status));
        Assert.All(receipt.Details, d => Assert.NotNull(d.Screenshot));
    }

    [Fact]
    public async Task SubmitPerMeter_WithOnlyPreviousPeriodInHistory_StillSubmits()
    {
        // Ровно та ошибка, на которую напрашивается такой кабинет: последняя строка
        // истории есть всегда, и принять её за «уже сдано» — значит молча пропустить
        // период. Признаком служит только дата начала, совпавшая с текущим периодом.
        _portal.History["W-1"] = [(_portal.CurrentPeriod.Previous(), "911")];
        _portal.History["E-1"] = [(_portal.CurrentPeriod.Previous(), "61471")];

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(PerMeterMap);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(_portal.CurrentPeriod, WholeReadings(), dryRun: false);

        Assert.Equal(SubmissionStatus.Submitted, receipt.Status);
        Assert.Equal(2, _portal.SubmitCount);
    }

    [Fact]
    public async Task SubmitPerMeter_SkipsOnlyTheMeterAlreadySubmittedForThisPeriod()
    {
        // Обычное состояние в середине месяца: электричество сдано, вода ещё нет.
        _portal.History["E-1"] = [(_portal.CurrentPeriod, "62179")];

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(PerMeterMap);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(_portal.CurrentPeriod, WholeReadings(), dryRun: false);

        Assert.Equal(SubmissionStatus.Submitted, receipt.Status);
        Assert.Equal(1, _portal.SubmitCount);
        Assert.Equal("919", _portal.Accepted["W-1"]);
        Assert.False(_portal.Accepted.ContainsKey("E-1"));

        Assert.Equal(
            SubmissionStatus.Submitted,
            receipt.Details.Single(d => d.Reading.PortalId == "W-1").Status);
        Assert.Equal(
            SubmissionStatus.AlreadySubmitted,
            receipt.Details.Single(d => d.Reading.PortalId == "E-1").Status);
    }

    [Fact]
    public async Task SubmitPerMeter_WhenEveryMeterAlreadySubmitted_ReportsAlreadySubmitted()
    {
        _portal.History["W-1"] = [(_portal.CurrentPeriod, "919")];
        _portal.History["E-1"] = [(_portal.CurrentPeriod, "62179")];

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(PerMeterMap);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(_portal.CurrentPeriod, WholeReadings(), dryRun: false);

        Assert.Equal(SubmissionStatus.AlreadySubmitted, receipt.Status);
        Assert.Equal(0, _portal.SubmitCount);
    }

    [Fact]
    public async Task SubmitPerMeter_DryRun_TouchesNoForm()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(PerMeterMap);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        SubmissionReceipt receipt = await adapter.SubmitAsync(_portal.CurrentPeriod, WholeReadings(), dryRun: true);

        Assert.Equal(SubmissionStatus.DryRun, receipt.Status);
        Assert.Equal(0, _portal.SubmitCount);
        Assert.Empty(_portal.Accepted);
        Assert.All(receipt.Details, d => Assert.Equal(SubmissionStatus.DryRun, d.Status));
    }

    [Fact]
    public async Task SubmitPerMeter_WhenPortalRejectsValue_NamesTheMeter()
    {
        PortalSelectorMap wrongFormat = PerMeterMap with { DecimalSeparator = "." };

        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync(wrongFormat);
        await adapter.LoginAsync(new PortalCredentials(_portal.ValidLogin, _portal.ValidPassword));

        IReadOnlyList<PortalReading> fractional =
        [
            new PortalReading { MeterKey = "cold-water", PortalId = "W-1", Value = 919.5m },
        ];

        PortalSubmissionException ex = await Assert.ThrowsAsync<PortalSubmissionException>(
            () => adapter.SubmitAsync(_portal.CurrentPeriod, fractional, dryRun: false));

        Assert.Contains("W-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Недопустимый формат числа", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_portal.Accepted);
    }

    [Fact]
    public async Task Submit_WithEmptyList_IsRejectedLocally()
    {
        await using ConfigurablePortalAdapter adapter = await CreateAdapterAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.SubmitAsync(Period, [], dryRun: false));
    }
}
