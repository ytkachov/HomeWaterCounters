using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterCounters.Core.Messaging;

/// <summary>
/// Конверт сообщения в очереди. Payload оставлен нетипизированным (<see cref="JsonElement"/>),
/// чтобы получатель со старой схемой мог прочитать конверт, распознать неизвестный тип и
/// отложить сообщение, а не свалиться на десериализации.
/// </summary>
public sealed record MessageEnvelope
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Ключ идемпотентности. Лексикографически сортируем по времени создания.</summary>
    public required string MessageId { get; init; }

    public required MessageType Type { get; init; }

    /// <summary>Расчётный период в формате "yyyy-MM".</summary>
    public required string Period { get; init; }

    public required string DeviceId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public JsonElement Payload { get; init; }

    [JsonIgnore]
    public string FileName => $"{MessageId}.json";
}
