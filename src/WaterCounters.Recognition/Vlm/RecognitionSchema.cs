using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterCounters.Recognition.Vlm;

/// <summary>
/// Схема ответа модели.
///
/// Ollama с версии 0.5 принимает в поле <c>format</c> полноценную JSON Schema, а не
/// только строку "json". Модель физически не может вернуть текст не по схеме — это
/// надёжнее любого разбора свободного текста регулярками.
///
/// Разряды приходят строками, а не числом: ведущие нули на барабане значимы
/// (00123 и 123 — разные показания на пятиразрядном счётчике), а число их теряет.
/// Поле <c>value</c> при этом тоже запрашивается — расхождение между ним и собранным
/// из разрядов значением само по себе сигнал, что модель путается.
/// </summary>
public static class RecognitionSchema
{
    public const string Json = """
        {
          "type": "object",
          "properties": {
            "serial":            { "type": ["string", "null"] },
            "integer_part":      { "type": "string" },
            "fractional_part":   { "type": ["string", "null"] },
            "value":             { "type": "number" },
            "digit_confidences": { "type": "array", "items": { "type": "number" } },
            "confidence":        { "type": "number" },
            "notes":             { "type": "string" }
          },
          "required": ["integer_part", "value", "confidence"],
          "additionalProperties": false
        }
        """;

    /// <summary>Схема как узел JSON — вставляется в тело запроса без повторного разбора.</summary>
    public static JsonElement Element { get; } = Parse();

    private static JsonElement Parse()
    {
        using JsonDocument document = JsonDocument.Parse(Json);
        return document.RootElement.Clone();
    }
}

/// <summary>Ответ модели ровно в том виде, в каком его описывает <see cref="RecognitionSchema"/>.</summary>
public sealed record VlmReading
{
    public string? Serial { get; init; }

    [JsonPropertyName("integer_part")]
    public string? IntegerPart { get; init; }

    [JsonPropertyName("fractional_part")]
    public string? FractionalPart { get; init; }

    public double Value { get; init; }

    [JsonPropertyName("digit_confidences")]
    public IReadOnlyList<double>? DigitConfidences { get; init; }

    public double Confidence { get; init; }

    public string? Notes { get; init; }
}
