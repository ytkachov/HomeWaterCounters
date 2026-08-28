using Microsoft.Playwright;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Configuration;

namespace WaterCounters.Portal;

public sealed record PortalAdapterOptions
{
    /// <summary>
    /// Профиль браузера. Именно он позволяет пройти 2FA или капчу один раз вручную:
    /// cookie-сессия переживает перезапуск, и последующие прогоны идут молча.
    /// </summary>
    public required string UserDataDirectory { get; init; }

    public bool Headless { get; init; } = true;

    /// <summary>Куда складывать trace и скриншоты при падении. Null — не сохранять.</summary>
    public string? DiagnosticsDirectory { get; init; }

    /// <summary>Замедление действий — нужно только при отладке в headed-режиме.</summary>
    public float SlowMoMs { get; init; }
}

/// <summary>
/// Драйвер кабинета, целиком описанный картой селекторов.
///
/// Отправка показаний необратима, поэтому здесь три защиты: режим dryRun (заполняем
/// форму, но не жмём кнопку), проверка «период уже закрыт» до ввода и обязательный
/// скриншот подтверждения как доказательство.
/// </summary>
public sealed class ConfigurablePortalAdapter : IPortalAdapter
{
    private readonly PortalSelectorMap _map;
    private readonly PortalAdapterOptions _options;
    private readonly IPlaywright _playwright;
    private readonly IBrowserContext _context;
    private readonly IPage _page;

    private bool _tracing;

    private ConfigurablePortalAdapter(
        PortalSelectorMap map,
        PortalAdapterOptions options,
        IPlaywright playwright,
        IBrowserContext context,
        IPage page)
    {
        _map = map;
        _options = options;
        _playwright = playwright;
        _context = context;
        _page = page;

        _page.SetDefaultTimeout(map.ActionTimeoutMs);
        _page.SetDefaultNavigationTimeout(map.NavigationTimeoutMs);
    }

    public static async Task<ConfigurablePortalAdapter> CreateAsync(
        PortalSelectorMap map,
        PortalAdapterOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(options.UserDataDirectory);

        IPlaywright playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        IBrowserContext context;

        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                options.UserDataDirectory,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = options.Headless,
                    SlowMo = options.SlowMoMs > 0 ? options.SlowMoMs : null,
                }).ConfigureAwait(false);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }

        IPage page = context.Pages.Count > 0
            ? context.Pages[0]
            : await context.NewPageAsync().ConfigureAwait(false);

        var adapter = new ConfigurablePortalAdapter(map, options, playwright, context, page);
        await adapter.StartTracingAsync().ConfigureAwait(false);
        return adapter;
    }

    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string target = _map.ReadingsUrl ?? _map.LoginUrl;

        await _page.GotoAsync(target).ConfigureAwait(false);
        return await IsVisibleAsync(_map.LoggedInMarker).ConfigureAwait(false);
    }

    public async Task LoginAsync(PortalCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ct.ThrowIfCancellationRequested();

        if (await IsLoggedInAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _page.GotoAsync(_map.LoginUrl).ConfigureAwait(false);
            await _page.Locator(_map.LoginInput).FillAsync(credentials.Login).ConfigureAwait(false);
            await _page.Locator(_map.PasswordInput).FillAsync(credentials.Password).ConfigureAwait(false);
            await _page.Locator(_map.SubmitLoginButton).ClickAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw await FailAsync((m, i) => new PortalLoginException(m, i), 
                $"Не удалось заполнить форму входа — вероятно, изменилась вёрстка: {ex.Message}", ex).ConfigureAwait(false);
        }

        if (await IsVisibleAsync(_map.LoggedInMarker).ConfigureAwait(false))
        {
            return;
        }

        // Разделяем два очень разных случая: «пароль не подошёл» чинится в настройках,
        // «маркер не найден» означает, что сайт переделали и нужна новая карта селекторов.
        string? error = await ReadTextAsync(_map.LoginErrorMarker).ConfigureAwait(false);

        throw await FailAsync((m, i) => new PortalLoginException(m, i), 
            error is not null
                ? $"Кабинет отклонил вход: {error}"
                : "Вход не подтверждён: маркер авторизации не появился, а сообщения об ошибке нет.",
            null).ConfigureAwait(false);
    }

    public async Task<SubmissionReceipt> SubmitAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (readings.Count == 0)
        {
            throw new ArgumentException("Список показаний пуст.", nameof(readings));
        }

        ct.ThrowIfCancellationRequested();

        return _map.IsPerMeter
            ? await SubmitPerMeterAsync(period, readings, dryRun, ct).ConfigureAwait(false)
            : await SubmitOnePageAsync(period, readings, dryRun, ct).ConfigureAwait(false);
    }

    /// <summary>Кабинет принимает всю квартиру одной формой: заполняем все поля и жмём кнопку один раз.</summary>
    private async Task<SubmissionReceipt> SubmitOnePageAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct)
    {
        if (_map.ReadingsUrl is { } url)
        {
            await _page.GotoAsync(url).ConfigureAwait(false);
        }

        if (!await IsVisibleAsync(_map.LoggedInMarker).ConfigureAwait(false))
        {
            throw await FailAsync((m, i) => new PortalSubmissionException(m, i),
                "Сессия не активна — требуется повторный вход.", null).ConfigureAwait(false);
        }

        // Проверка до ввода, а не после: если период закрыт, повторная отправка в
        // лучшем случае отвергается, в худшем — задваивает показания.
        string? alreadySubmitted = Expand(_map.AlreadySubmittedMarker, period: period);

        if (await IsVisibleAsync(alreadySubmitted).ConfigureAwait(false))
        {
            return new SubmissionReceipt
            {
                Status = SubmissionStatus.AlreadySubmitted,
                Readings = readings,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = await ReadTextAsync(alreadySubmitted).ConfigureAwait(false),
            };
        }

        foreach (PortalReading reading in readings)
        {
            ct.ThrowIfCancellationRequested();
            await FillAsync(reading).ConfigureAwait(false);
        }

        if (dryRun)
        {
            return new SubmissionReceipt
            {
                Status = SubmissionStatus.DryRun,
                Readings = readings,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = "Режим проверки: форма заполнена, отправка не выполнялась.",
            };
        }

        await ClickSubmitAsync().ConfigureAwait(false);

        string? success = Expand(_map.SuccessMarker, period: period);

        if (await WaitForMarkerAsync(success).ConfigureAwait(false))
        {
            return new SubmissionReceipt
            {
                Status = SubmissionStatus.Submitted,
                Readings = readings,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = await ReadTextAsync(success).ConfigureAwait(false),
            };
        }

        throw await RejectedAsync(null).ConfigureAwait(false);
    }

    /// <summary>
    /// Кабинет принимает по одному счётчику за визит: своя страница, своё поле, своя
    /// кнопка. Счётчики независимы, поэтому и исход у каждого свой — один может уйти,
    /// второй оказаться уже сданным, а третий упереться в отказ формы.
    /// </summary>
    private async Task<SubmissionReceipt> SubmitPerMeterAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct)
    {
        var details = new List<MeterSubmissionResult>(readings.Count);

        foreach (PortalReading reading in readings)
        {
            ct.ThrowIfCancellationRequested();
            details.Add(await SubmitMeterAsync(period, reading, dryRun).ConfigureAwait(false));
        }

        SubmissionStatus status = dryRun
            ? SubmissionStatus.DryRun
            : details.TrueForAll(d => d.Status == SubmissionStatus.AlreadySubmitted)
                ? SubmissionStatus.AlreadySubmitted
                : SubmissionStatus.Submitted;

        return new SubmissionReceipt
        {
            Status = status,
            Readings = readings,
            Details = details,
            // Скриншот последнего счётчика доказывает только последний счётчик,
            // поэтому доказательства лежат в Details по одному на каждый.
            Screenshot = details[^1].Screenshot,
            PortalMessage = string.Join(
                "; ",
                details.Select(d => $"{d.Reading.MeterKey}: {Describe(d.Status)}")),
        };
    }

    private async Task<MeterSubmissionResult> SubmitMeterAsync(PeriodKey period, PortalReading reading, bool dryRun)
    {
        await _page.GotoAsync(_map.MeterPageUrlFor(reading.PortalId)).ConfigureAwait(false);

        if (!await IsVisibleAsync(_map.LoggedInMarker).ConfigureAwait(false))
        {
            throw await FailAsync((m, i) => new PortalSubmissionException(m, i),
                $"Сессия не активна на странице счётчика '{reading.PortalId}' — требуется повторный вход.",
                null).ConfigureAwait(false);
        }

        string? alreadySubmitted = Expand(_map.AlreadySubmittedMarker, reading.PortalId, period);

        if (await IsVisibleAsync(alreadySubmitted).ConfigureAwait(false))
        {
            return new MeterSubmissionResult
            {
                Reading = reading,
                Status = SubmissionStatus.AlreadySubmitted,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = await ReadTextAsync(alreadySubmitted).ConfigureAwait(false),
            };
        }

        string value = await FillAsync(reading).ConfigureAwait(false);

        if (dryRun)
        {
            return new MeterSubmissionResult
            {
                Reading = reading,
                Status = SubmissionStatus.DryRun,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = "Режим проверки: форма заполнена, отправка не выполнялась.",
            };
        }

        await ClickSubmitAsync().ConfigureAwait(false);

        string? success = Expand(_map.SuccessMarker, reading.PortalId, period, value);

        if (await WaitForMarkerAsync(success).ConfigureAwait(false))
        {
            return new MeterSubmissionResult
            {
                Reading = reading,
                Status = SubmissionStatus.Submitted,
                Screenshot = await ScreenshotAsync().ConfigureAwait(false),
                PortalMessage = await ReadTextAsync(success).ConfigureAwait(false),
            };
        }

        throw await RejectedAsync(reading).ConfigureAwait(false);
    }

    /// <summary>Заполняет поле счётчика и возвращает значение ровно в том виде, в каком оно ушло в форму.</summary>
    private async Task<string> FillAsync(PortalReading reading)
    {
        string selector = _map.ReadingInputFor(reading.PortalId);
        ILocator input = _page.Locator(selector);

        if (await input.CountAsync().ConfigureAwait(false) == 0)
        {
            throw await FailAsync((m, i) => new PortalSubmissionException(m, i),
                $"На странице нет поля для счётчика '{reading.PortalId}' (селектор: {selector}).",
                null).ConfigureAwait(false);
        }

        string value = _map.FormatValue(reading.Value);
        await input.FillAsync(value).ConfigureAwait(false);
        return value;
    }

    private async Task ClickSubmitAsync()
    {
        try
        {
            await _page.Locator(_map.SubmitReadingsButton).ClickAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw await FailAsync((m, i) => new PortalSubmissionException(m, i),
                $"Не удалось нажать кнопку отправки: {ex.Message}", ex).ConfigureAwait(false);
        }

    }

    private async Task<PortalSubmissionException> RejectedAsync(PortalReading? reading)
    {
        string? validation = await ReadTextAsync(_map.ValidationErrorMarker).ConfigureAwait(false);
        string what = reading is null ? string.Empty : $" по счётчику '{reading.PortalId}'";

        return await FailAsync((m, i) => new PortalSubmissionException(m, i),
            validation is not null
                ? $"Кабинет отклонил показания{what}: {validation}"
                : $"Подтверждение отправки{what} не появилось.",
            null).ConfigureAwait(false);
    }

    private static string Describe(SubmissionStatus status) => status switch
    {
        SubmissionStatus.Submitted => "принято",
        SubmissionStatus.AlreadySubmitted => "уже сдано за период",
        _ => "проверка, не отправлялось",
    };

    private string? Expand(string? template, string? portalId = null, PeriodKey? period = null, string? value = null) =>
        template is null ? null : _map.Expand(template, portalId, period, value);

    public async ValueTask DisposeAsync()
    {
        await StopTracingAsync(null).ConfigureAwait(false);
        await _context.DisposeAsync().ConfigureAwait(false);
        _playwright.Dispose();
    }

    /// <summary>
    /// Ждёт появления маркера, а не проверяет его мгновенно.
    ///
    /// Отправка формы перезагружает страницу, и клик возвращает управление раньше,
    /// чем приходит новый DOM. Мгновенная проверка искала бы подтверждение на старой
    /// странице и объявляла бы успешную отправку провалом — то есть заставляла бы
    /// повторить необратимое действие.
    /// </summary>
    private async Task<bool> WaitForMarkerAsync(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        try
        {
            await _page.Locator(selector).First
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached })
                .ConfigureAwait(false);

            return true;
        }
        catch (TimeoutException)
        {
            // Маркера нет — это не сбой, а «кабинет не подтвердил»: разбираться будет
            // вызывающий, у него есть текст ошибки валидации.
            return false;
        }
    }

    private async Task<bool> IsVisibleAsync(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        return await _page.Locator(selector).CountAsync().ConfigureAwait(false) > 0;
    }

    private async Task<string?> ReadTextAsync(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        ILocator locator = _page.Locator(selector);

        if (await locator.CountAsync().ConfigureAwait(false) == 0)
        {
            return null;
        }

        string? text = await locator.First.TextContentAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task<byte[]?> ScreenshotAsync()
    {
        try
        {
            return await _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает исключение вместе с диагностикой. Разбирать падение автоматики на
    /// чужом сайте без скриншота и trace практически невозможно, поэтому они
    /// снимаются всегда, а не по флагу отладки.
    /// </summary>
    private async Task<TException> FailAsync<TException>(
        Func<string, Exception?, TException> create,
        string message,
        Exception? inner)
        where TException : PortalException
    {
        byte[]? screenshot = await ScreenshotAsync().ConfigureAwait(false);
        string? tracePath = await StopTracingAsync("failure").ConfigureAwait(false);

        TException exception = create(message, inner);
        exception.Screenshot = screenshot;
        exception.TracePath = tracePath;
        return exception;
    }

    private async Task StartTracingAsync()
    {
        if (_options.DiagnosticsDirectory is null || _tracing)
        {
            return;
        }

        Directory.CreateDirectory(_options.DiagnosticsDirectory);

        await _context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = false,
        }).ConfigureAwait(false);

        _tracing = true;
    }

    private async Task<string?> StopTracingAsync(string? reason)
    {
        if (!_tracing || _options.DiagnosticsDirectory is null)
        {
            return null;
        }

        _tracing = false;

        if (reason is null)
        {
            await _context.Tracing.StopAsync().ConfigureAwait(false);
            return null;
        }

        string path = Path.Combine(
            _options.DiagnosticsDirectory,
            $"trace-{reason}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");

        await _context.Tracing.StopAsync(new TracingStopOptions { Path = path }).ConfigureAwait(false);
        return path;
    }
}
