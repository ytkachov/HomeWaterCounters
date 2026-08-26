using WaterCounters.Core.Metering;
using WaterCounters.Core.Validation;

namespace WaterCounters.Desktop.Processing;

/// <summary>Кто подтверждает показания перед отправкой.</summary>
public enum ConfirmationMode
{
    /// <summary>
    /// Телефона нет: роль подтверждения играет режим проверки <c>portal.dryRun</c>.
    /// Первый этап — фотографии раскладываются вручную.
    /// </summary>
    Direct = 0,

    /// <summary>
    /// Целевая схема: показания уезжают на телефон и ждут <c>ReadingsConfirmed</c>.
    /// Включается тем, что задачу прислал телефон.
    /// </summary>
    AwaitMobile = 1,
}

/// <summary>Показание одного счётчика со всем, что о нём известно к моменту решения об отправке.</summary>
public sealed record ReadingCandidate
{
    public required MeterSpec Meter { get; init; }

    /// <summary>Null — прочитать не удалось. Число в этом случае не выдумывается.</summary>
    public decimal? Value { get; init; }

    public required ReadingSource Source { get; init; }

    public string? RecognizedSerial { get; init; }

    public double? Confidence { get; init; }

    public string? PhotoPath { get; init; }

    /// <summary>Кроп циферблата: уходит в письмо и, в целевой схеме, на телефон.</summary>
    public byte[]? Crop { get; init; }

    public string? CropPath { get; init; }

    public decimal? PreviousValue { get; init; }

    public decimal? Delta { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    /// <summary>Замечания распознавания: перекат барабана, расхождение проходов ансамбля.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Причина, по которой показания нет вовсе.</summary>
    public string? Failure { get; init; }

    /// <summary>
    /// Сбой, который имеет смысл повторить: не ответил VLM-хост, не скачалась
    /// фотография. Отличать его от нечитаемого снимка обязательно — иначе выключенная
    /// на полчаса Ollama навсегда пометила бы пачку обработанной, и период уехал бы
    /// в прогноз при живых фотографиях.
    /// </summary>
    public bool IsTransientFailure { get; init; }

    public bool HasValue => Value is not null;

    /// <summary>
    /// Замечание, которое обязано удержать отправку даже при выключенном режиме
    /// проверки: показание меньше предыдущего, чужой серийник, выход за разрядность.
    /// </summary>
    public bool HasCriticalIssue => Issues.Any(static i => i.Severity == ValidationSeverity.Critical);
}

public sealed record PipelineResult
{
    public required State.SubmissionOutcome Outcome { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<ReadingCandidate> Readings { get; init; } = [];

    public bool WasDryRun { get; init; }
}
