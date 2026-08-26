using System.IO;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Portal;

namespace WaterCounters.Desktop.Processing;

public sealed record PortalOutcome
{
    public required SubmissionStatus Status { get; init; }

    public byte[]? Screenshot { get; init; }

    public string? Message { get; init; }

    public string? TracePath { get; init; }

    /// <summary>Заполнено, если отправка не удалась. Провал остаётся результатом, а не исключением.</summary>
    public string? Error { get; init; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// Ввод показаний в кабинет. Отделён интерфейсом от <see cref="ConfigurablePortalAdapter"/>,
/// чтобы конвейер обработки проверялся тестами без поднятия браузера.
/// </summary>
public interface IPortalGateway
{
    Task<PortalOutcome> SubmitAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct = default);
}

/// <summary>
/// Реализация поверх Playwright: поднимает браузер на каждую отправку и гасит его
/// сразу после. Держать браузер запущенным месяцами между периодами незачем — он
/// съедает память и переживает обновления сайта хуже, чем свежий запуск.
/// </summary>
public sealed class PlaywrightPortalGateway(
    ISettingsProvider settings,
    DesktopOptions options,
    ILogger<PlaywrightPortalGateway> logger) : IPortalGateway
{
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<PlaywrightPortalGateway> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<PortalOutcome> SubmitAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readings);

        AppSettings current = _settings.Current;
        PortalSelectorMap? map = current.Portal.Selectors;

        if (!current.Portal.Enabled)
        {
            return Fail("ввод в кабинет выключен в настройках");
        }

        if (map is null)
        {
            return Fail("карта селекторов кабинета не задана в настройках");
        }

        AppSecrets? secrets = _settings.Secrets;

        if (string.IsNullOrWhiteSpace(secrets?.PortalLogin) || string.IsNullOrWhiteSpace(secrets.PortalPassword))
        {
            return Fail("учётные данные кабинета не заданы или секреты не расшифрованы");
        }

        var adapterOptions = new PortalAdapterOptions
        {
            UserDataDirectory = _options.PortalProfileDirectory,
            DiagnosticsDirectory = _options.DiagnosticsDirectory,
            Headless = !_options.ShowBrowser,
        };

        ConfigurablePortalAdapter? adapter = null;

        try
        {
            adapter = await ConfigurablePortalAdapter.CreateAsync(map, adapterOptions, ct).ConfigureAwait(false);

            await adapter.LoginAsync(new PortalCredentials(secrets.PortalLogin, secrets.PortalPassword), ct)
                .ConfigureAwait(false);

            SubmissionReceipt receipt = await adapter.SubmitAsync(period, readings, dryRun, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Кабинет за {Period}: {Status}. {Message}",
                period,
                receipt.Status,
                receipt.PortalMessage);

            return new PortalOutcome
            {
                Status = receipt.Status,
                Screenshot = receipt.Screenshot,
                Message = receipt.PortalMessage,
            };
        }
        catch (PortalException ex)
        {
            _logger.LogError(ex, "Кабинет отверг работу за {Period}.", period);

            return new PortalOutcome
            {
                Status = SubmissionStatus.DryRun,
                Screenshot = ex.Screenshot,
                TracePath = ex.TracePath,
                Error = ex.Message,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Playwright падает и вне PortalException: не поставлен браузер, не хватает
            // прав на профиль. Для конвейера это такой же провал попытки, поэтому
            // исход возвращается, а не пробрасывается наружу.
            _logger.LogError(ex, "Не удалось поднять браузер для кабинета.");
            return Fail(ex.Message);
        }
        finally
        {
            if (adapter is not null)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static PortalOutcome Fail(string error) => new()
    {
        Status = SubmissionStatus.DryRun,
        Error = error,
    };
}
