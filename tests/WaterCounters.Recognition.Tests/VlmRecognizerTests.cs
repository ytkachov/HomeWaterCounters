using System.Net;
using System.Text.Json;
using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;
using WaterCounters.Recognition.Vlm;

namespace WaterCounters.Recognition.Tests;

public class VlmRecognizerTests : IDisposable
{
    private readonly FakeVlmServer _server = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Ollama_SendsSchemaImagesAndZeroTemperature()
    {
        await RecognizeAsync(RecognitionTestData.ColdWater);

        RecordedRequest request = Assert.Single(_server.Requests);
        Assert.Equal("/api/chat", request.Path);

        JsonElement body = request.Json;

        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal(0, body.GetProperty("options").GetProperty("temperature").GetDouble());

        // Схема уходит объектом, а не строкой "json": именно это не даёт модели
        // ответить свободным текстом.
        JsonElement format = body.GetProperty("format");
        Assert.Equal(JsonValueKind.Object, format.ValueKind);
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("properties").TryGetProperty("integer_part", out _));

        JsonElement user = body.GetProperty("messages")[1];
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal(1, user.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task Ollama_PromptCarriesDigitLayoutOfTheMeter()
    {
        await RecognizeAsync(RecognitionTestData.ColdWater);

        string system = _server.Requests[0].Json.GetProperty("messages")[0].GetProperty("content").GetString()!;

        Assert.Contains("5 цифр до запятой, 3 после", system, StringComparison.Ordinal);
        Assert.Contains("красн", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("перекате", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ollama_ElectricityPromptOmitsTheRedDrumRule()
    {
        await RecognizeAsync(RecognitionTestData.Electricity);

        string system = _server.Requests[0].Json.GetProperty("messages")[0].GetProperty("content").GetString()!;

        Assert.Contains("6 цифр до запятой, 1 после", system, StringComparison.Ordinal);
        Assert.DoesNotContain("водосчётчике", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComposesValueFromDigitsRatherThanTheReportedNumber()
    {
        // Разряды прочитаны верно, арифметика модели — нет. Побеждают разряды.
        _server.Content = _ => FakeVlmServer.Reading("00123", "456", value: 123.4, confidence: 0.9);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(123.456m, result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("value = 123.4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeadingZerosSurviveAndSerialIsTrimmed()
    {
        _server.Content = _ => FakeVlmServer.Reading("00042", "007", 42.007, 0.93, serial: "  12-345-678 ");

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(42.007m, result.Value);
        Assert.Equal("12-345-678", result.Serial);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ShortFractionIsPaddedOnTheRightAndReported()
    {
        // "45" на трёхразрядном барабане — это 450 литров, а не 45.
        _server.Content = _ => FakeVlmServer.Reading("00042", "45", 42.45, 0.9);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(42.450m, result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("дополнены нулями справа", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtraIntegerDigitsFallBackToTheLowerOnes()
    {
        _server.Content = _ => FakeVlmServer.Reading("1200042", "000", 1200042, 0.9);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(42m, result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("взяты младшие", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WeakestDigitBecomesTheConfidenceOfTheWholeReading()
    {
        _server.Content = _ => FakeVlmServer.Reading(
            "00123",
            "456",
            123.456,
            confidence: 0.97,
            digitConfidences: [0.99, 0.99, 0.61, 0.99, 0.99, 0.98, 0.98, 0.98]);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(0.61, result.Confidence, 3);
        Assert.Contains(result.Warnings, w => w.Contains("разряд №3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelNotesReachTheWarnings()
    {
        _server.Content = _ => FakeVlmServer.Reading("00123", "456", 123.456, 0.9, notes: "последний барабан в перекате");

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Contains(result.Warnings, w => w.Contains("перекате", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableDigitsYieldNoValueRatherThanAGuess()
    {
        _server.Content = _ => FakeVlmServer.Reading(string.Empty, null, 0, 0.1);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Null(result.Value);
        Assert.False(result.IsSuccessful);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task FencedJsonIsStillParsed()
    {
        _server.Content = _ => "```json\n" + FakeVlmServer.Reading("00123", "456", 123.456, 0.9) + "\n```";

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(123.456m, result.Value);
    }

    [Fact]
    public async Task ResponseTruncatedInTheNotesFieldStillYieldsTheReading()
    {
        // Реальный случай на qwen3-vl: модель зациклилась в свободном текстовом поле
        // и повторяла один абзац до упора в лимит генерации. Разряды к тому моменту
        // прочитаны верно, и терять снимок из-за незакрытой скобки — расточительство.
        _server.Content = _ =>
            """{"integer_part":"00123","fractional_part":"456","value":123.456,"confidence":0.9,"notes":"Цифры видны""" +
            new string('!', 500);

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Equal(123.456m, result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("оборван", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TruncationBeforeTheFirstCompleteFieldIsNotGuessedAt()
    {
        // Обрыв до первого целого поля — это не «почти ответ», а мусор. Достроить его
        // означало бы выдумать показание, чего распознавание делать не должно никогда.
        _server.Content = _ => """{"integer_part":"001""";

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Null(result.Value);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task GarbageInsteadOfJsonIsRejectedRatherThanRepaired()
    {
        _server.Content = _ => "модель ответила текстом, а не объектом, и запятая, тут есть";

        RecognitionResult result = await RecognizeAsync(RecognitionTestData.ColdWater);

        Assert.Null(result.Value);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task SerialPass_AsksSeparatelyAndKeepsBothAnswers()
    {
        // Смысл разделения: один запрос про цифры, второй про номер. Вместе они
        // конкурируют за внимание модели — доля неверных разрядов вырастает втрое.
        _server.Content = i => i == 1
            ? FakeVlmServer.Reading("00123", "456", 123.456, 0.9)
            : """{"serial":"12-345-678","confidence":0.9}""";

        RecognitionResult result = await RecognizeAsync(
            RecognitionTestData.ColdWater, separateSerialPass: true);

        Assert.Equal(123.456m, result.Value);
        Assert.Equal("12-345-678", result.Serial);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task SerialPass_AsksTheSecondQuestionWithItsOwnSchema()
    {
        _server.Content = i => i == 1
            ? FakeVlmServer.Reading("00123", "456", 123.456, 0.9)
            : """{"serial":"12-345-678","confidence":0.9}""";

        await RecognizeAsync(RecognitionTestData.ColdWater, separateSerialPass: true);

        // В первом запросе серийника нет вовсе, во втором нет показания: каждый
        // проход занят ровно одним делом, и схема это закрепляет.
        JsonElement first = _server.Requests[0].Json.GetProperty("format").GetProperty("properties");
        JsonElement second = _server.Requests[1].Json.GetProperty("format").GetProperty("properties");

        Assert.False(first.TryGetProperty("serial", out _));
        Assert.True(first.TryGetProperty("integer_part", out _));
        Assert.True(second.TryGetProperty("serial", out _));
        Assert.False(second.TryGetProperty("integer_part", out _));
    }

    [Fact]
    public async Task SerialPass_FailingDoesNotDiscardTheReading()
    {
        // Без номера снимок просто не сопоставится со счётчиком автоматически.
        // Терять из-за этого уже прочитанные цифры незачем.
        _server.Content = i => i == 1
            ? FakeVlmServer.Reading("00123", "456", 123.456, 0.9)
            : "не json вовсе";

        RecognitionResult result = await RecognizeAsync(
            RecognitionTestData.ColdWater, separateSerialPass: true);

        Assert.Equal(123.456m, result.Value);
        Assert.Null(result.Serial);
        Assert.Contains(result.Warnings, w => w.Contains("серийный номер не прочитан", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HttpFailureIsReportedWithTheHostAddress()
    {
        _server.Status = HttpStatusCode.InternalServerError;
        _server.RawResponse = "model not found";

        RecognitionException error = await Assert.ThrowsAsync<RecognitionException>(
            () => RecognizeAsync(RecognitionTestData.ColdWater));

        Assert.Contains("500", error.Message, StringComparison.Ordinal);
        Assert.Contains("model not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutNamesTheModelInsteadOfSurfacingAsCancellation()
    {
        _server.Delay = TimeSpan.FromSeconds(5);

        RecognitionException error = await Assert.ThrowsAsync<RecognitionException>(
            () => RecognizeAsync(RecognitionTestData.ColdWater, timeout: TimeSpan.FromMilliseconds(250)));

        Assert.Contains("qwen-test", error.Message, StringComparison.Ordinal);
        Assert.Contains("не ответила", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatible_SendsDataUriAndJsonSchemaResponseFormat()
    {
        var recognizer = new OpenAiCompatibleRecognizer(
            _http,
            Options(TimeSpan.FromSeconds(30)) with { ApiKey = "test-key" },
            new PassThroughImagePreprocessor());

        await recognizer.RecognizeAsync(
            RecognitionTestData.ColdWater,
            RecognitionTestData.OpaqueJpeg,
            CancellationToken.None);

        RecordedRequest request = Assert.Single(_server.Requests);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer test-key", request.Authorization);

        JsonElement body = request.Json;
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());

        JsonElement format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        JsonElement parts = body.GetProperty("messages")[1].GetProperty("content");
        JsonElement image = parts[1];
        Assert.Equal("image_url", image.GetProperty("type").GetString());
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            image.GetProperty("image_url").GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    private async Task<RecognitionResult> RecognizeAsync(
        MeterSpec meter,
        TimeSpan? timeout = null,
        bool separateSerialPass = false)
    {
        var recognizer = new OllamaRecognizer(
            _http,
            Options(timeout ?? TimeSpan.FromSeconds(30)) with { SeparateSerialPass = separateSerialPass },
            new PassThroughImagePreprocessor());

        return await recognizer.RecognizeAsync(meter, RecognitionTestData.OpaqueJpeg, CancellationToken.None);
    }

    private VlmRecognizerOptions Options(TimeSpan timeout) => new()
    {
        Endpoint = _server.BaseUrl,
        Model = "qwen-test",
        Timeout = timeout,
    };
}
