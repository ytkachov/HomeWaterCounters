using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Messaging;

// ---------------------------------------------------------------------------
// Мобильное → десктоп
// ---------------------------------------------------------------------------

/// <summary>Ссылка на уже загруженное в Dropbox фото.</summary>
public sealed record PhotoRef
{
    public required string MeterKey { get; init; }

    public required string PhotoPath { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }
}

/// <summary>
/// «Фотографии готовы, распознавай». Кладётся в очередь строго ПОСЛЕ загрузки всех
/// файлов — это commit-маркер комплекта.
/// </summary>
public sealed record SubmitReadingsPayload
{
    public required IReadOnlyList<PhotoRef> Photos { get; init; }
}

/// <summary>«Фото не будет, посчитай прогноз».</summary>
public sealed record SubmitForecastPayload
{
    public required string Reason { get; init; }
}

public sealed record ConfirmedReading
{
    public required string MeterKey { get; init; }

    public required decimal Value { get; init; }

    /// <summary>true, если пользователь исправил предложенное значение вручную.</summary>
    public bool WasEdited { get; init; }
}

/// <summary>Пользователь подтвердил показания на телефоне — можно отправлять в кабинет.</summary>
public sealed record ReadingsConfirmedPayload
{
    public required string ProposalId { get; init; }

    public required IReadOnlyList<ConfirmedReading> Readings { get; init; }
}

public sealed record ReadingsRejectedPayload
{
    public required string ProposalId { get; init; }

    public required string Reason { get; init; }
}

// ---------------------------------------------------------------------------
// Десктоп → мобильное
// ---------------------------------------------------------------------------

public sealed record ProposedReading
{
    public required string MeterKey { get; init; }

    public required decimal Value { get; init; }

    public string? RecognizedSerial { get; init; }

    public double? Confidence { get; init; }

    /// <summary>Путь в Dropbox к кропу циферблата — показывается рядом с полем ввода.</summary>
    public string? CropPath { get; init; }

    public decimal? PreviousValue { get; init; }

    public decimal? Delta { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Распознанные (или спрогнозированные) значения, ожидающие подтверждения.</summary>
public sealed record ReadingsProposedPayload
{
    public required string ProposalId { get; init; }

    public required string SourceMessageId { get; init; }

    /// <summary>true — значения не с фото, а вычислены прогнозом. На экране красный баннер.</summary>
    public required bool IsForecast { get; init; }

    public required IReadOnlyList<ProposedReading> Readings { get; init; }
}

/// <summary>Истории слишком мало, чтобы прогнозировать. Числа не выдумываем — просим ручной ввод.</summary>
public sealed record ForecastUnavailablePayload
{
    public required string Reason { get; init; }

    public required IReadOnlyList<string> MeterKeys { get; init; }
}

public sealed record SubmissionSucceededPayload
{
    public required IReadOnlyList<MeterReading> Readings { get; init; }

    public string? ReceiptScreenshotPath { get; init; }

    /// <summary>true — прогон был в режиме dry-run, показания в кабинет фактически не ушли.</summary>
    public required bool WasDryRun { get; init; }
}

public sealed record SubmissionFailedPayload
{
    public required string Error { get; init; }

    public string? ScreenshotPath { get; init; }

    public string? TracePath { get; init; }

    public required int AttemptCount { get; init; }
}

/// <summary>Heartbeat десктопа — мобильное показывает «десктоп на связи».</summary>
public sealed record DesktopStatusPayload
{
    public required string Version { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    public string? VlmModel { get; init; }

    public required string Health { get; init; }
}
