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
///
/// <c>serial</c> обязателен, хотя и допускает null: необязательное поле модель просто
/// опускает — qwen3-vl так и делала, и серийный номер не читался ни разу. Обязательным
/// его делает не педантизм, а то, что по нему хост узнаёт, какой счётчик на снимке:
/// два холодных и два горячих в квартире внешне неразличимы.
///
/// Целая и дробная части запрашиваются раздельно, хотя модели и путают их местами.
/// Замер показал, что вариант «всё показание одной строкой, делим сами» эту болезнь
/// не лечит: модель всё равно ставит разделитель не туда, а доля неверных цифр при
/// этом вчетверо выше (21 % против 5 %). Раздельные поля дают ей больше структуры.
///
/// У <c>notes</c> ограничена длина, и это не косметика: свободное текстовое поле в
/// конце ответа — то место, где болтливая модель зацикливается и повторяет один
/// абзац до упора в лимит генерации. JSON остаётся незакрытым, и показание, уже
/// прочитанное верно в первых полях, теряется целиком.
/// </summary>
/// <summary>
/// Что именно спрашивают у модели этим запросом.
///
/// Разделение существует потому, что внимание модели — ограниченный ресурс:
/// попросив заодно прочитать серийный номер, показание получаешь хуже. Два коротких
/// запроса дороже одного по времени, но каждый решает ровно одну задачу.
/// </summary>
public enum VlmPass
{
    /// <summary>Показание и серийный номер одним запросом.</summary>
    Full = 0,

    /// <summary>Только показание.</summary>
    ReadingOnly = 1,

    /// <summary>Только серийный номер.</summary>
    SerialOnly = 2,
}

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
            "notes":             { "type": "string", "maxLength": 400 }
          },
          "required": ["serial", "integer_part", "value", "confidence"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Схема прохода, который читает только показание. Серийного номера в ней нет
    /// вовсе: замер показал, что просьба заодно прочитать номер отнимает у модели
    /// внимание от цифр — доля неверных разрядов растёт с 8 % до 24 %.
    /// </summary>
    public const string ReadingOnlyJson = """
        {
          "type": "object",
          "properties": {
            "integer_part":      { "type": "string" },
            "fractional_part":   { "type": ["string", "null"] },
            "value":             { "type": "number" },
            "digit_confidences": { "type": "array", "items": { "type": "number" } },
            "confidence":        { "type": "number" },
            "notes":             { "type": "string", "maxLength": 400 }
          },
          "required": ["integer_part", "value", "confidence"],
          "additionalProperties": false
        }
        """;

    /// <summary>Схема прохода, который читает только серийный номер.</summary>
    public const string SerialOnlyJson = """
        {
          "type": "object",
          "properties": {
            "serial":     { "type": ["string", "null"] },
            "confidence": { "type": "number" },
            "notes":      { "type": "string", "maxLength": 400 }
          },
          "required": ["serial", "confidence"],
          "additionalProperties": false
        }
        """;

    /// <summary>Схема как узел JSON — вставляется в тело запроса без повторного разбора.</summary>
    public static JsonElement Element { get; } = Parse(Json);

    public static JsonElement ReadingOnlyElement { get; } = Parse(ReadingOnlyJson);

    public static JsonElement SerialOnlyElement { get; } = Parse(SerialOnlyJson);

    /// <summary>Схема нужного прохода.</summary>
    public static JsonElement For(VlmPass pass) => pass switch
    {
        VlmPass.ReadingOnly => ReadingOnlyElement,
        VlmPass.SerialOnly => SerialOnlyElement,
        _ => Element,
    };

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Ответ прохода, который читает только серийный номер.</summary>
public sealed record VlmSerial
{
    public string? Serial { get; init; }

    public double Confidence { get; init; }

    public string? Notes { get; init; }
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
