using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WaterCounters.Recognition.Tests;

/// <summary>
/// Заглушка VLM-хоста: настоящий HTTP-сервер, отвечающий как Ollama и как
/// OpenAI-совместимый фасад.
///
/// Именно сервер, а не подменённый HttpMessageHandler: проверять надо в том числе
/// то, что запрос вообще уходит по правильному пути с правильным телом, а на
/// подменённом обработчике это проверяется только на слово.
/// </summary>
public sealed class FakeVlmServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _gate = new();

    public FakeVlmServer()
    {
        Port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Что «возвращает модель» — содержимое сообщения. Аргумент: номер обращения с единицы.</summary>
    public Func<int, string> Content { get; set; } = static _ => Reading("01234", "567", 1234.567, 0.95);

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Тело ответа целиком, в обход обычной сборки конверта. Для проверки битых ответов.</summary>
    public string? RawResponse { get; set; }

    /// <summary>Задержка перед ответом — проверяет таймаут распознавателя.</summary>
    public TimeSpan Delay { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Ответ модели по схеме распознавания — то, что кладётся в content.</summary>
    public static string Reading(
        string integerPart,
        string? fractionalPart,
        double value,
        double confidence,
        string? serial = null,
        IReadOnlyList<double>? digitConfidences = null,
        string? notes = null)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (serial is null)
            {
                writer.WriteNull("serial");
            }
            else
            {
                writer.WriteString("serial", serial);
            }

            writer.WriteString("integer_part", integerPart);

            if (fractionalPart is null)
            {
                writer.WriteNull("fractional_part");
            }
            else
            {
                writer.WriteString("fractional_part", fractionalPart);
            }

            writer.WriteNumber("value", value);

            if (digitConfidences is not null)
            {
                writer.WriteStartArray("digit_confidences");

                foreach (double digit in digitConfidences)
                {
                    writer.WriteNumberValue(digit);
                }

                writer.WriteEndArray();
            }

            writer.WriteNumber("confidence", confidence);

            if (notes is not null)
            {
                writer.WriteString("notes", notes);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Слушатель закрыт — цикл падает штатно.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await HandleAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                // Клиент ушёл по таймауту — для теста это ожидаемый исход.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        string path = context.Request.Url?.AbsolutePath ?? string.Empty;

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        int index;

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(path, body, context.Request.Headers["Authorization"]));
            index = _requests.Count;
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct).ConfigureAwait(false);
        }

        string payload = RawResponse ?? Envelope(path, Content(index));
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        context.Response.StatusCode = (int)Status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string Envelope(string path, string content)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (path.Contains("/v1/chat/completions", StringComparison.Ordinal))
            {
                writer.WriteStartArray("choices");
                writer.WriteStartObject();
                writer.WriteStartObject("message");
                writer.WriteString("role", "assistant");
                writer.WriteString("content", content);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStartObject("message");
                writer.WriteString("role", "assistant");
                writer.WriteString("content", content);
                writer.WriteEndObject();
                writer.WriteBoolean("done", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

public sealed record RecordedRequest(string Path, string Body, string? Authorization)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}
