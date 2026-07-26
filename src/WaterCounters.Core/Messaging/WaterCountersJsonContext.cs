using System.Text.Json;
using System.Text.Json.Serialization;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Messaging;

/// <summary>
/// Единственный источник JsonTypeInfo для всего решения. Рефлексивная сериализация
/// отключена в Directory.Build.props — тип, не перечисленный здесь, упадёт при
/// первом обращении, и это сознательно: на устройстве под trimming он всё равно
/// не заработает, лучше узнать об этом на билд-машине.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(SubmitReadingsPayload))]
[JsonSerializable(typeof(SubmitForecastPayload))]
[JsonSerializable(typeof(ReadingsConfirmedPayload))]
[JsonSerializable(typeof(ReadingsRejectedPayload))]
[JsonSerializable(typeof(ReadingsProposedPayload))]
[JsonSerializable(typeof(ForecastUnavailablePayload))]
[JsonSerializable(typeof(SubmissionSucceededPayload))]
[JsonSerializable(typeof(SubmissionFailedPayload))]
[JsonSerializable(typeof(DesktopStatusPayload))]
[JsonSerializable(typeof(MeterSpec))]
[JsonSerializable(typeof(MeterReading))]
[JsonSerializable(typeof(IReadOnlyList<MeterSpec>))]
[JsonSerializable(typeof(IReadOnlyList<MeterReading>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class WaterCountersJsonContext : JsonSerializerContext;
