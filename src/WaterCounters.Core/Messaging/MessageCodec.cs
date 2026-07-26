using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Messaging;

/// <summary>Сборка, кодирование и разбор конвертов сообщений.</summary>
public static class MessageCodec
{
    /// <summary>
    /// Идентификатор сообщения: 13 цифр Unix-времени в миллисекундах + 8 случайных hex.
    /// Лексикографическая сортировка совпадает с хронологической, поэтому список файлов
    /// в папке очереди уже отсортирован по времени создания.
    /// </summary>
    public static string NewMessageId(DateTimeOffset createdUtc)
    {
        long millis = createdUtc.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[4];
        RandomNumberGenerator.Fill(random);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{millis:D13}-{Convert.ToHexString(random).ToLowerInvariant()}");
    }

    /// <summary>
    /// Детерминированный идентификатор для задач, которые обе стороны могут создать
    /// независимо. Прогноз за период порождают и телефон, и watchdog десктопа —
    /// одинаковый MessageId схлопывает дубль в одну задачу.
    /// </summary>
    public static string DeterministicMessageId(MessageType type, PeriodKey period) =>
        $"{type.ToString().ToLowerInvariant()}-{period}";

    public static MessageEnvelope Create<TPayload>(
        MessageType type,
        PeriodKey period,
        string deviceId,
        TPayload payload,
        DateTimeOffset createdUtc,
        string? messageId = null)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return new MessageEnvelope
        {
            MessageId = messageId ?? NewMessageId(createdUtc),
            Type = type,
            Period = period.ToString(),
            DeviceId = deviceId,
            CreatedUtc = createdUtc,
            Payload = JsonSerializer.SerializeToElement(payload, TypeInfo<TPayload>()),
        };
    }

    public static byte[] Encode(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, WaterCountersJsonContext.Default.MessageEnvelope);
    }

    public static MessageEnvelope Decode(ReadOnlySpan<byte> utf8Json)
    {
        MessageEnvelope? envelope = JsonSerializer.Deserialize(
            utf8Json, WaterCountersJsonContext.Default.MessageEnvelope);

        if (envelope is null)
        {
            throw new MessageFormatException("Тело сообщения десериализовалось в null.");
        }

        if (envelope.SchemaVersion > MessageEnvelope.CurrentSchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(envelope.SchemaVersion);
        }

        if (!PeriodKey.TryParse(envelope.Period, out _))
        {
            throw new MessageFormatException($"Период '{envelope.Period}' не соответствует формату yyyy-MM.");
        }

        return envelope;
    }

    public static bool TryDecode(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out MessageEnvelope? envelope, out string? error)
    {
        try
        {
            envelope = Decode(utf8Json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or MessageFormatException or UnsupportedSchemaVersionException)
        {
            envelope = null;
            error = ex.Message;
            return false;
        }
    }

    public static TPayload GetPayload<TPayload>(this MessageEnvelope envelope)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new MessageFormatException($"Сообщение {envelope.MessageId} не содержит payload.");
        }

        return envelope.Payload.Deserialize(TypeInfo<TPayload>())
            ?? throw new MessageFormatException($"Payload сообщения {envelope.MessageId} десериализовался в null.");
    }

    public static PeriodKey GetPeriod(this MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return PeriodKey.Parse(envelope.Period);
    }

    private static JsonTypeInfo<TPayload> TypeInfo<TPayload>() =>
        WaterCountersJsonContext.Default.GetTypeInfo(typeof(TPayload)) as JsonTypeInfo<TPayload>
        ?? throw new InvalidOperationException(
            $"Тип {typeof(TPayload)} не зарегистрирован в {nameof(WaterCountersJsonContext)}. " +
            "Добавьте [JsonSerializable] — иначе сериализация сломается под trimming в MAUI.");
}

public sealed class MessageFormatException(string message) : Exception(message);

public sealed class UnsupportedSchemaVersionException(int version)
    : Exception($"Версия схемы {version} новее поддерживаемой ({MessageEnvelope.CurrentSchemaVersion}). Обновите приложение.")
{
    public int Version { get; } = version;
}
