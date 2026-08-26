using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition.Vlm;

public sealed record VlmRecognizerOptions
{
    /// <summary>Адрес VLM-хоста без пути: http://localhost:11434 или http://gpu-box:1234.</summary>
    public required string Endpoint { get; init; }

    public required string Model { get; init; }

    public PromptVariant Prompt { get; init; } = PromptVariant.Russian;

    /// <summary>Крупная модель на слабой карте отвечает минутами — таймаут задаётся с запасом.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Нужен только OpenAI-совместимым фасадам, которые его требуют. Локальная Ollama — нет.</summary>
    public string? ApiKey { get; init; }

    public PreprocessOptions Preprocess { get; init; } = new();
}

/// <summary>
/// Общая часть распознавателей поверх HTTP: предобработка, замер времени, разбор
/// ответа по схеме. Различаются реализации только формой запроса.
/// </summary>
public abstract class VlmRecognizer(HttpClient http, VlmRecognizerOptions options, IImagePreprocessor preprocessor)
    : IMeterRecognizer, IVariantRecognizer
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IImagePreprocessor _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));

    protected VlmRecognizerOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<RecognitionResult> RecognizeAsync(MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meter);

        IReadOnlyList<MeterImage> images = _preprocessor.Prepare(jpeg, Options.Preprocess);
        return await RecognizeVariantsAsync(meter, images, ct).ConfigureAwait(false);
    }

    public async Task<RecognitionResult> RecognizeVariantsAsync(
        MeterSpec meter,
        IReadOnlyList<MeterImage> images,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(images);

        if (images.Count == 0)
        {
            throw new ArgumentException("Нет ни одного варианта кадра для распознавания.", nameof(images));
        }

        long started = Stopwatch.GetTimestamp();
        string content = await RequestAsync(meter, images, ct).ConfigureAwait(false);
        long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        RecognitionResult result = VlmReadingParser.Parse(meter, content);

        return result with
        {
            ElapsedMs = elapsed,
            Crop = images.FirstOrDefault(i => i.Kind == MeterImageKind.DialCrop)?.Jpeg,
        };
    }

    /// <summary>Отправляет запрос конкретному серверу и возвращает содержимое ответа модели.</summary>
    protected abstract Task<string> RequestAsync(
        MeterSpec meter,
        IReadOnlyList<MeterImage> images,
        CancellationToken ct);

    protected async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseInfo,
        CancellationToken ct)
        where TResponse : class
    {
        // Таймаут задаётся токеном, а не HttpClient.Timeout: клиент приходит из
        // фабрики и общий на процесс, менять его свойства из распознавателя нельзя.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.Timeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(path))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, requestInfo),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(Options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
        }

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RecognitionException(
                $"Модель {Options.Model} не ответила за {Options.Timeout.TotalSeconds:N0} с.");
        }
        catch (HttpRequestException ex)
        {
            throw new RecognitionException($"VLM-хост {Options.Endpoint} недоступен: {ex.Message}", ex);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new RecognitionException(
                    $"VLM-хост ответил {(int)response.StatusCode} {response.StatusCode}: {Snippet(body)}");
            }

            TResponse? parsed;

            try
            {
                parsed = JsonSerializer.Deserialize(body, responseInfo);
            }
            catch (JsonException ex)
            {
                throw new RecognitionException($"Ответ VLM-хоста не разбирается: {ex.Message}. {Snippet(body)}", ex);
            }

            return parsed ?? throw new RecognitionException("VLM-хост вернул пустой ответ.");
        }
    }

    protected static string ToBase64(MeterImage image) => Convert.ToBase64String(image.Jpeg);

    /// <summary>Тело ошибки бывает страницей на сотни килобайт — в журнал уходит начало.</summary>
    protected static string Snippet(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(пустое тело)" : body.Length <= 400 ? body : body[..400] + "…";

    private Uri BuildUri(string path)
    {
        string root = Options.Endpoint.TrimEnd('/');

        return Uri.TryCreate($"{root}{path}", UriKind.Absolute, out Uri? uri)
            ? uri
            : throw new RecognitionException($"Адрес VLM-хоста '{Options.Endpoint}' не является корректным URL.");
    }
}

/// <summary>
/// Основная реализация: локальная Ollama. Картинки уходят в images[], схема — в format,
/// temperature 0, stream false.
/// </summary>
public sealed class OllamaRecognizer(HttpClient http, VlmRecognizerOptions options, IImagePreprocessor preprocessor)
    : VlmRecognizer(http, options, preprocessor)
{
    protected override async Task<string> RequestAsync(
        MeterSpec meter,
        IReadOnlyList<MeterImage> images,
        CancellationToken ct)
    {
        var request = new OllamaChatRequest
        {
            Model = Options.Model,
            Stream = false,
            Format = RecognitionSchema.Element,
            Messages =
            [
                new OllamaMessage
                {
                    Role = "system",
                    Content = MeterPromptBuilder.System(meter, Options.Prompt),
                },
                new OllamaMessage
                {
                    Role = "user",
                    Content = MeterPromptBuilder.User(meter, Options.Prompt, images),
                    Images = [.. images.Select(ToBase64)],
                },
            ],
        };

        OllamaChatResponse response = await PostAsync(
            "/api/chat",
            request,
            RecognitionJsonContext.Default.OllamaChatRequest,
            RecognitionJsonContext.Default.OllamaChatResponse,
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new RecognitionException($"Ollama вернула ошибку: {response.Error}");
        }

        return response.Message?.Content
            ?? throw new RecognitionException("Ollama вернула ответ без содержимого сообщения.");
    }
}

/// <summary>
/// LM Studio, llama.cpp, vLLM и всё остальное с фасадом OpenAI: картинки уходят
/// частями content в виде data:-URI, схема — в response_format.
/// </summary>
public sealed class OpenAiCompatibleRecognizer(
    HttpClient http,
    VlmRecognizerOptions options,
    IImagePreprocessor preprocessor)
    : VlmRecognizer(http, options, preprocessor)
{
    protected override async Task<string> RequestAsync(
        MeterSpec meter,
        IReadOnlyList<MeterImage> images,
        CancellationToken ct)
    {
        List<OpenAiContentPart> parts =
        [
            new OpenAiContentPart
            {
                Type = "text",
                Text = MeterPromptBuilder.User(meter, Options.Prompt, images),
            },
            .. images.Select(static image => new OpenAiContentPart
            {
                Type = "image_url",
                ImageUrl = new OpenAiImageUrl { Url = $"data:image/jpeg;base64,{ToBase64(image)}" },
            }),
        ];

        var request = new OpenAiChatRequest
        {
            Model = Options.Model,
            Stream = false,
            Temperature = 0,
            ResponseFormat = new OpenAiResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new OpenAiJsonSchema
                {
                    Name = "meter_reading",
                    Schema = RecognitionSchema.Element,
                },
            },
            Messages =
            [
                new OpenAiMessage
                {
                    Role = "system",
                    Content =
                    [
                        new OpenAiContentPart
                        {
                            Type = "text",
                            Text = MeterPromptBuilder.System(meter, Options.Prompt),
                        },
                    ],
                },
                new OpenAiMessage { Role = "user", Content = parts },
            ],
        };

        OpenAiChatResponse response = await PostAsync(
            "/v1/chat/completions",
            request,
            RecognitionJsonContext.Default.OpenAiChatRequest,
            RecognitionJsonContext.Default.OpenAiChatResponse,
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(response.Error?.Message))
        {
            throw new RecognitionException($"VLM-хост вернул ошибку: {response.Error.Message}");
        }

        return response.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new RecognitionException("VLM-хост вернул ответ без единого варианта завершения.");
    }
}
