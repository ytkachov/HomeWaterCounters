using System.Net;
using System.Text;
using WaterCounters.Core.Storage.Dropbox;

namespace WaterCounters.DropboxSetup;

/// <summary>
/// Локальный слушатель, ловящий редирект Dropbox после согласия пользователя.
/// Это стандартный для десктопа способ завершить PKCE-поток: браузер отправляет код
/// на 127.0.0.1, и он никогда не покидает машину.
/// </summary>
public sealed class LoopbackOAuthListener(string redirectUri) : IDisposable
{
    private readonly HttpListener _listener = CreateListener(redirectUri);

    public async Task<DropboxRedirectResult> WaitForRedirectAsync(CancellationToken ct)
    {
        _listener.Start();

        try
        {
            HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            DropboxRedirectResult result = DropboxPkceFlow.ParseRedirect(context.Request.Url!);

            await RespondAsync(context, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();

    private static HttpListener CreateListener(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        var listener = new HttpListener();

        // HttpListener требует префикс с завершающим слэшем.
        listener.Prefixes.Add($"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}/");
        return listener;
    }

    private static async Task RespondAsync(HttpListenerContext context, DropboxRedirectResult result)
    {
        string message = result.IsSuccess
            ? "Готово. Возвращайтесь в консоль — окно можно закрыть."
            : $"Не получилось: {result.Error}";

        string html = $"""
            <!doctype html>
            <html lang="ru">
            <head><meta charset="utf-8"><title>WaterCounters</title></head>
            <body style="font-family:system-ui;margin:4rem;text-align:center">
              <h2>{WebUtility.HtmlEncode(message)}</h2>
            </body>
            </html>
            """;

        byte[] buffer = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = result.IsSuccess ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;

        await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        context.Response.Close();
    }
}
