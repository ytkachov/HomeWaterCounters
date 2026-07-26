namespace WaterCounters.Core.Metering;

public enum ReadingSource
{
    /// <summary>Распознано VLM-моделью с фотографии.</summary>
    Recognized = 0,

    /// <summary>Вычислено прогнозом при отсутствии фотографий.</summary>
    Forecast = 1,

    /// <summary>Введено или исправлено вручную на экране подтверждения.</summary>
    Manual = 2,
}

/// <summary>Показание одного счётчика за один период.</summary>
public sealed record MeterReading
{
    public required string MeterKey { get; init; }

    public required PeriodKey Period { get; init; }

    public required decimal Value { get; init; }

    public required ReadingSource Source { get; init; }

    public DateTimeOffset? CapturedUtc { get; init; }

    /// <summary>Серийный номер, прочитанный моделью с фото. Сверяется с <see cref="MeterSpec.SerialNumber"/>.</summary>
    public string? RecognizedSerial { get; init; }

    /// <summary>Уверенность модели 0..1. Null для ручного ввода и прогноза.</summary>
    public double? Confidence { get; init; }

    /// <summary>Путь к исходному фото в Dropbox, если показание получено с фотографии.</summary>
    public string? PhotoPath { get; init; }
}
