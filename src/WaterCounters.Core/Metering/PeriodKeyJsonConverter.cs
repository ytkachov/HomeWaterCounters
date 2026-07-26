using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterCounters.Core.Metering;

/// <summary>Сериализует <see cref="PeriodKey"/> как строку "yyyy-MM".</summary>
public sealed class PeriodKeyJsonConverter : JsonConverter<PeriodKey>
{
    public override PeriodKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Ожидалась строка периода, получено {reader.TokenType}.");
        }

        string? value = reader.GetString();
        return PeriodKey.TryParse(value, out PeriodKey period)
            ? period
            : throw new JsonException($"Период '{value}' не соответствует формату yyyy-MM.");
    }

    public override void Write(Utf8JsonWriter writer, PeriodKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override PeriodKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PeriodKey.TryParse(reader.GetString(), out PeriodKey period)
            ? period
            : throw new JsonException("Ключ словаря не является периодом формата yyyy-MM.");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, PeriodKey value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());
}
