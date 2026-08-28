using System.Globalization;
using System.Net;
using System.Text;
using WaterCounters.Core.Metering;

namespace WaterCounters.Portal.Tests;

/// <summary>
/// Макет личного кабинета: настоящий HTTP-сервер с серверной сессией, а не статический
/// HTML. Именно поэтому им можно проверить то, ради чего адаптер вообще написан —
/// вход по cookie, отказ при неверном пароле, ошибку валидации формы, закрытый период
/// и переживание сессии между «запусками» браузера.
/// </summary>
public sealed class MockPortalServer : IDisposable
{
    private const string SessionCookie = "mock-portal-session";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _sessions = [];
    private readonly object _gate = new();
    private readonly Task _loop;

    public MockPortalServer(int port = 0)
    {
        Port = port == 0 ? FindFreePort() : port;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string LoginUrl => $"{BaseUrl}/login";

    public string ReadingsUrl => $"{BaseUrl}/readings";

    /// <summary>Шаблон страницы одного счётчика — кабинет, принимающий показания поштучно.</summary>
    public string MeterPageUrl => $"{BaseUrl}/meter?id={{portalId}}";

    public string ValidLogin { get; set; } = "user";

    public string ValidPassword { get; set; } = "правильный";

    /// <summary>Период уже закрыт — форма не показывается.</summary>
    public bool PeriodClosed { get; set; }

    /// <summary>Показания, принятые сервером. То, что реально долетело до «кабинета».</summary>
    public Dictionary<string, string> Accepted { get; } = new(StringComparer.Ordinal);

    /// <summary>Идентификаторы счётчиков, для которых кабинет рисует поля ввода.</summary>
    public List<string> Meters { get; } = ["W-1", "E-1"];

    /// <summary>Период, за который кабинет сейчас принимает показания.</summary>
    public PeriodKey CurrentPeriod { get; set; } = new(2026, 8);

    /// <summary>
    /// История по каждому счётчику — то, из чего поштучный кабинет рисует таблицу
    /// «предыдущие показания». Признака «уже сдано» у такого кабинета нет: сдано или
    /// нет, видно только по дате начала периода в строке истории.
    /// </summary>
    public Dictionary<string, List<(PeriodKey Period, string Value)>> History { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Выдавать постоянную cookie («запомнить меня») или сессионную. Различие
    /// принципиально: сессионную браузер выбрасывает при закрытии, и постоянный
    /// профиль перестаёт спасать от повторного входа.
    /// </summary>
    public bool UsePersistentCookie { get; set; } = true;

    /// <summary>Сколько раз сервер принимал отправку формы — ловит повторные отправки.</summary>
    public int SubmitCount { get; private set; }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Уже остановлен.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Цикл завершился по отмене — это нормально.
        }

        ((IDisposable)_listener).Dispose();
        _cts.Dispose();
    }

    private static int FindFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteAsync(context, 500, $"<pre>{WebUtility.HtmlEncode(ex.ToString())}</pre>").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath.TrimEnd('/');

        switch (path)
        {
            case "/login" when context.Request.HttpMethod == "GET":
                await WriteAsync(context, 200, LoginPage(null)).ConfigureAwait(false);
                return;

            case "/login" when context.Request.HttpMethod == "POST":
                await HandleLoginAsync(context).ConfigureAwait(false);
                return;

            case "/readings" when context.Request.HttpMethod == "GET":
                await HandleReadingsPageAsync(context).ConfigureAwait(false);
                return;

            case "/readings" when context.Request.HttpMethod == "POST":
                await HandleSubmitAsync(context).ConfigureAwait(false);
                return;

            case "/meter" when context.Request.HttpMethod == "GET":
                await HandleMeterPageAsync(context).ConfigureAwait(false);
                return;

            case "/meter" when context.Request.HttpMethod == "POST":
                await HandleMeterSubmitAsync(context).ConfigureAwait(false);
                return;

            default:
                await WriteAsync(context, 404, "<h1>404</h1>").ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleLoginAsync(HttpListenerContext context)
    {
        Dictionary<string, string> form = await ReadFormAsync(context).ConfigureAwait(false);

        form.TryGetValue("username", out string? login);
        form.TryGetValue("password", out string? password);

        if (login != ValidLogin || password != ValidPassword)
        {
            await WriteAsync(context, 200, LoginPage("Неверный логин или пароль")).ConfigureAwait(false);
            return;
        }

        string session = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            _sessions.Add(session);
        }

        string cookie = UsePersistentCookie
            ? $"{SessionCookie}={session}; Path=/; Max-Age=2592000"
            : $"{SessionCookie}={session}; Path=/";

        context.Response.AppendHeader("Set-Cookie", cookie);
        context.Response.Redirect("/readings");
        context.Response.Close();
    }

    private async Task HandleReadingsPageAsync(HttpListenerContext context)
    {
        if (!IsAuthenticated(context))
        {
            await WriteAsync(context, 200, LoginPage(null)).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, 200, ReadingsPage(null, null)).ConfigureAwait(false);
    }

    private async Task HandleSubmitAsync(HttpListenerContext context)
    {
        if (!IsAuthenticated(context))
        {
            await WriteAsync(context, 200, LoginPage(null)).ConfigureAwait(false);
            return;
        }

        SubmitCount++;
        Dictionary<string, string> form = await ReadFormAsync(context).ConfigureAwait(false);

        foreach (string meter in Meters)
        {
            if (!form.TryGetValue(meter, out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                await WriteAsync(context, 200, ReadingsPage($"Не заполнено показание для {meter}", null))
                    .ConfigureAwait(false);
                return;
            }

            // Кабинет принимает только запятую — типичное поведение российских порталов.
            if (raw.Contains('.', StringComparison.Ordinal))
            {
                await WriteAsync(context, 200, ReadingsPage($"Недопустимый формат числа: {raw}", null))
                    .ConfigureAwait(false);
                return;
            }
        }

        foreach (string meter in Meters)
        {
            Accepted[meter] = form[meter];
        }

        PeriodClosed = true;
        await WriteAsync(context, 200, ReadingsPage(null, "Показания приняты")).ConfigureAwait(false);
    }

    private async Task HandleMeterPageAsync(HttpListenerContext context)
    {
        if (!IsAuthenticated(context))
        {
            await WriteAsync(context, 200, LoginPage(null)).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, 200, MeterPage(MeterId(context), null)).ConfigureAwait(false);
    }

    private async Task HandleMeterSubmitAsync(HttpListenerContext context)
    {
        if (!IsAuthenticated(context))
        {
            await WriteAsync(context, 200, LoginPage(null)).ConfigureAwait(false);
            return;
        }

        string meter = MeterId(context);
        Dictionary<string, string> form = await ReadFormAsync(context).ConfigureAwait(false);
        SubmitCount++;

        if (!form.TryGetValue("sch_val", out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            await WriteAsync(context, 200, MeterPage(meter, "Не заполнено показание")).ConfigureAwait(false);
            return;
        }

        // Кабинет принимает только запятую — типичное поведение российских порталов.
        if (raw.Contains('.', StringComparison.Ordinal))
        {
            await WriteAsync(context, 200, MeterPage(meter, $"Недопустимый формат числа: {raw}")).ConfigureAwait(false);
            return;
        }

        Accepted[meter] = raw;
        Rows(meter).Insert(0, (CurrentPeriod, raw));

        await WriteAsync(context, 200, MeterPage(meter, null)).ConfigureAwait(false);
    }

    private static string MeterId(HttpListenerContext context) =>
        context.Request.QueryString["id"] ?? string.Empty;

    private List<(PeriodKey Period, string Value)> Rows(string meter)
    {
        if (!History.TryGetValue(meter, out List<(PeriodKey Period, string Value)>? rows))
        {
            rows = [];
            History[meter] = rows;
        }

        return rows;
    }

    private bool IsAuthenticated(HttpListenerContext context)
    {
        string? session = context.Request.Cookies[SessionCookie]?.Value;

        if (session is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.Contains(session);
        }
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);

        Dictionary<string, string> form = new(StringComparer.Ordinal);

        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separator].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            form[key] = value;
        }

        return form;
    }

    private static string LoginPage(string? error) => $"""
        <!doctype html>
        <html lang="ru"><head><meta charset="utf-8"><title>Кабинет — вход</title></head>
        <body>
          <h1>Вход в личный кабинет</h1>
          {(error is null ? "" : $"<div class=\"login-error\">{WebUtility.HtmlEncode(error)}</div>")}
          <form method="post" action="/login">
            <input id="username" name="username" type="text">
            <input id="password" name="password" type="password">
            <button id="do-login" type="submit">Войти</button>
          </form>
        </body></html>
        """;

    private string ReadingsPage(string? validationError, string? success)
    {
        var inputs = new StringBuilder();

        if (!PeriodClosed)
        {
            foreach (string meter in Meters)
            {
                inputs.Append($"""
                    <div class="meter-row" data-meter="{meter}">
                      <label>{meter}</label>
                      <input class="reading" name="{meter}" type="text">
                    </div>
                    """);
            }
        }

        string body = PeriodClosed
            ? """<div class="period-closed">Показания за период уже переданы</div>"""
            : $"""
                <form method="post" action="/readings">
                  {inputs}
                  <button id="save-readings" type="submit">Передать показания</button>
                </form>
                """;

        return $"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><title>Кабинет — показания</title></head>
            <body>
              <div class="account-header">Личный кабинет</div>
              {(validationError is null ? "" : $"<div class=\"field-error\">{WebUtility.HtmlEncode(validationError)}</div>")}
              {(success is null ? "" : $"<div class=\"alert-success\">{WebUtility.HtmlEncode(success)}</div>")}
              {body}
            </body></html>
            """;
    }

    /// <summary>
    /// Страница одного счётчика: единственное поле, своя кнопка и таблица истории.
    /// Строка истории — единственный след того, что показание принято, поэтому в ней
    /// обязана быть и дата начала периода, и само значение.
    /// </summary>
    private string MeterPage(string meter, string? validationError)
    {
        var rows = new StringBuilder();

        foreach ((PeriodKey period, string value) in Rows(meter))
        {
            var start = new DateOnly(period.Year, period.Month, 1);
            DateOnly end = start.AddMonths(1).AddDays(-1);

            rows.Append(
                $"<tr><td>{start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}</td>" +
                $"<td>{end.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}</td>" +
                $"<td>{WebUtility.HtmlEncode(value)}</td></tr>");
        }

        return $"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><title>Кабинет — счётчик {WebUtility.HtmlEncode(meter)}</title></head>
            <body>
              <div class="account-header">Личный кабинет</div>
              {(validationError is null ? "" : $"<div class=\"field-error\">{WebUtility.HtmlEncode(validationError)}</div>")}
              <form method="post" action="/meter?id={WebUtility.HtmlEncode(meter)}">
                <input class="reading" name="sch_val" type="text">
                <button id="save-readings" type="submit">Ввод</button>
              </form>
              <table class="history">
                <tr><td>Дата начала</td><td>Дата окончания</td><td>Показания</td></tr>
                {rows}
              </table>
            </body></html>
            """;
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string html)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;

        await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        context.Response.Close();
    }
}
