using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterCounters.Recognition.Vlm;

// ---------------------------------------------------------------------------
// Ollama: POST /api/chat
// ---------------------------------------------------------------------------

internal sealed record OllamaMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }

    /// <summary>Кадры в base64. Именно так Ollama принимает картинки — отдельным полем, не внутри content.</summary>
    public IReadOnlyList<string>? Images { get; init; }
}

internal sealed record OllamaRequestOptions
{
    /// <summary>Ноль, а не «поменьше»: читать цифры — не творческая задача.</summary>
    public double Temperature { get; init; }

    /// <summary>
    /// Размер контекста. Задаётся явно, потому что умолчание Ollama — 4096 токенов,
    /// а два кадра счётчика занимают больше: без этого поля хост отвечает 400
    /// «exceeds the available context size» и распознавание не начинается вовсе.
    /// </summary>
    [JsonPropertyName("num_ctx")]
    public int NumCtx { get; init; }
}

internal sealed record OllamaChatRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<OllamaMessage> Messages { get; init; }

    /// <summary>JSON Schema целиком. Модель физически не сможет ответить не по ней.</summary>
    public JsonElement Format { get; init; }

    public bool Stream { get; init; }

    public OllamaRequestOptions Options { get; init; } = new();
}

internal sealed record OllamaResponseMessage
{
    public string? Content { get; init; }
}

internal sealed record OllamaChatResponse
{
    public OllamaResponseMessage? Message { get; init; }

    public bool Done { get; init; }

    public string? Error { get; init; }
}

// ---------------------------------------------------------------------------
// OpenAI-совместимые серверы: LM Studio, llama.cpp, vLLM — POST /v1/chat/completions
// ---------------------------------------------------------------------------

internal sealed record OpenAiImageUrl
{
    public required string Url { get; init; }
}

internal sealed record OpenAiContentPart
{
    public required string Type { get; init; }

    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    public OpenAiImageUrl? ImageUrl { get; init; }
}

internal sealed record OpenAiMessage
{
    public required string Role { get; init; }

    /// <summary>Массив частей: текст и картинки вперемешку. Строкой здесь обойтись нельзя.</summary>
    public required IReadOnlyList<OpenAiContentPart> Content { get; init; }
}

internal sealed record OpenAiJsonSchema
{
    public required string Name { get; init; }

    /// <summary>strict — тот же смысл, что format у Ollama: ответ не по схеме невозможен.</summary>
    public bool Strict { get; init; } = true;

    public JsonElement Schema { get; init; }
}

internal sealed record OpenAiResponseFormat
{
    public required string Type { get; init; }

    [JsonPropertyName("json_schema")]
    public OpenAiJsonSchema? JsonSchema { get; init; }
}

internal sealed record OpenAiChatRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<OpenAiMessage> Messages { get; init; }

    public double Temperature { get; init; }

    public bool Stream { get; init; }

    [JsonPropertyName("response_format")]
    public OpenAiResponseFormat? ResponseFormat { get; init; }
}

internal sealed record OpenAiResponseMessage
{
    public string? Content { get; init; }
}

internal sealed record OpenAiChoice
{
    public OpenAiResponseMessage? Message { get; init; }
}

internal sealed record OpenAiErrorBody
{
    public string? Message { get; init; }
}

internal sealed record OpenAiChatResponse
{
    public IReadOnlyList<OpenAiChoice>? Choices { get; init; }

    public OpenAiErrorBody? Error { get; init; }
}

/// <summary>
/// Сериализация запросов и ответов VLM без рефлексии: обработчик собирается с
/// выключенным JsonSerializerIsReflectionEnabledByDefault, как и всё остальное решение.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatResponse))]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(VlmReading))]
[JsonSerializable(typeof(VlmSerial))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class RecognitionJsonContext : JsonSerializerContext;
