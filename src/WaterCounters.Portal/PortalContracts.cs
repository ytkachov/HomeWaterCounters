using WaterCounters.Core.Metering;

namespace WaterCounters.Portal;

public sealed record PortalCredentials(string Login, string Password);

/// <summary>Что именно передаём: ключ счётчика из настроек, идентификатор в кабинете и значение.</summary>
public sealed record PortalReading
{
    public required string MeterKey { get; init; }

    /// <summary>Идентификатор счётчика на сайте — серийник или собственный id кабинета.</summary>
    public required string PortalId { get; init; }

    public required decimal Value { get; init; }
}

public enum SubmissionStatus
{
    /// <summary>Показания приняты кабинетом.</summary>
    Submitted = 0,

    /// <summary>Прогон в режиме проверки: форма заполнена, но кнопка отправки не нажималась.</summary>
    DryRun = 1,

    /// <summary>За этот период уже сдано ранее — повторно не отправляем.</summary>
    AlreadySubmitted = 2,
}

public sealed record SubmissionReceipt
{
    public required SubmissionStatus Status { get; init; }

    public required IReadOnlyList<PortalReading> Readings { get; init; }

    /// <summary>Скриншот страницы подтверждения — доказательство передачи, уходит письмом.</summary>
    public byte[]? Screenshot { get; init; }

    public string? PortalMessage { get; init; }
}

/// <summary>
/// Драйвер личного кабинета. Реализация ходит браузером, потому что API у поставщика нет,
/// но интерфейс намеренно не знает про Playwright: тесты бизнес-логики не должны
/// поднимать браузер, а замена поставщика не должна ломать вызывающий код.
/// </summary>
public interface IPortalAdapter : IAsyncDisposable
{
    Task<bool> IsLoggedInAsync(CancellationToken ct = default);

    /// <exception cref="PortalLoginException">Неверные учётные данные либо изменилась страница входа.</exception>
    Task LoginAsync(PortalCredentials credentials, CancellationToken ct = default);

    Task<SubmissionReceipt> SubmitAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct = default);
}

public class PortalException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Скриншот момента ошибки — без него разбирать падение вслепую невозможно.</summary>
    public byte[]? Screenshot { get; internal set; }

    public string? TracePath { get; internal set; }
}

public sealed class PortalLoginException(string message, Exception? inner = null) : PortalException(message, inner);

public sealed class PortalSubmissionException(string message, Exception? inner = null) : PortalException(message, inner);
