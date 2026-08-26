using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition;

/// <summary>
/// Результат распознавания одной фотографии.
///
/// <see cref="Value"/> равен null, когда прочитать цифры не удалось: выдумывать
/// число нельзя ни при каких обстоятельствах — неверное показание уходит в кабинет
/// необратимо, а отсутствующее просто попадает в письмо как требующее внимания.
/// </summary>
public sealed record RecognitionResult(
    string? Serial,
    decimal? Value,
    double Confidence,
    string RawJson,
    IReadOnlyList<string> Warnings)
{
    public bool IsSuccessful => Value is not null;

    /// <summary>Кроп циферблата — уходит в Dropbox и показывается на телефоне рядом с полем ввода.</summary>
    public byte[]? Crop { get; init; }

    /// <summary>Сколько миллисекунд заняло обращение к модели. Нужно бенчмарку и журналу.</summary>
    public long ElapsedMs { get; init; }

    public static RecognitionResult Failed(string reason, string rawJson = "") =>
        new(null, null, 0, rawJson, [reason]);
}

public interface IMeterRecognizer
{
    Task<RecognitionResult> RecognizeAsync(
        MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct);
}

/// <summary>
/// Распознавание по уже подготовленным вариантам кадра. Отделено от
/// <see cref="IMeterRecognizer"/> потому, что ансамбль сам управляет кропами:
/// он делает несколько проходов с разной обрезкой и голосует по результатам.
/// </summary>
public interface IVariantRecognizer
{
    Task<RecognitionResult> RecognizeVariantsAsync(
        MeterSpec meter, IReadOnlyList<MeterImage> images, CancellationToken ct);
}

public class RecognitionException(string message, Exception? inner = null) : Exception(message, inner);
