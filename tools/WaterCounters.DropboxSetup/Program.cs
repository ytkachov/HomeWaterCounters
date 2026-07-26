using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;
using WaterCounters.Core.Storage.Dropbox;
using WaterCounters.DropboxSetup;

Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("Утилита рассчитана на Windows: токен хранится под DPAPI.");
    return 2;
}

var tokenStore = new DpapiTokenStore();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return command switch
    {
        "login" => await LoginAsync(tokenStore, cts.Token),
        "status" => await StatusAsync(tokenStore, cts.Token),
        "smoke" => await SmokeAsync(tokenStore, cts.Token),
        "logout" => await LogoutAsync(tokenStore),
        _ => Help(),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Прервано.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка: {ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        WaterCounters — настройка доступа к Dropbox

          login    авторизоваться и сохранить refresh-токен (DPAPI, текущий пользователь)
          status   показать, есть ли сохранённый токен и работает ли он
          smoke    прогнать реальные операции в папке приложения: upload/list/move/longpoll/delete
          logout   удалить сохранённый токен
        """);
    return 0;
}

static async Task<int> LoginAsync(DpapiTokenStore tokenStore, CancellationToken ct)
{
    var flow = new DropboxPkceFlow(DropboxAppInfo.DesktopRedirectUri);
    DropboxAuthorizationRequest request = flow.CreateRequest();

    using var listener = new LoopbackOAuthListener(DropboxAppInfo.DesktopRedirectUri);
    Task<DropboxRedirectResult> redirect = listener.WaitForRedirectAsync(ct);

    Console.WriteLine("Открываю браузер для входа в Dropbox…");
    Console.WriteLine($"Если он не открылся, перейдите вручную:\n{request.AuthorizeUri}\n");

    OpenBrowser(request.AuthorizeUri);

    DropboxRedirectResult result = await redirect;

    if (!result.IsSuccess)
    {
        Console.Error.WriteLine($"Авторизация не завершена: {result.Error}");
        return 1;
    }

    DropboxTokens tokens = await flow.ExchangeAsync(request, result.Code!, result.State, ct);
    await tokenStore.SaveAsync(tokens.RefreshToken, ct);

    Console.WriteLine($"Готово. Refresh-токен сохранён: {tokenStore.TokenPath}");
    Console.WriteLine("Он зашифрован DPAPI и читается только этой учётной записью Windows.");
    return 0;
}

static async Task<int> StatusAsync(DpapiTokenStore tokenStore, CancellationToken ct)
{
    string? token = await tokenStore.GetAsync(ct);

    if (token is null)
    {
        Console.WriteLine("Токена нет. Запустите: dotnet run --project tools/WaterCounters.DropboxSetup -- login");
        return 1;
    }

    using DropboxRemoteStore store = DropboxRemoteStore.Create(token);
    IReadOnlyList<RemoteEntry> root = await store.ListAsync("/", ct);

    Console.WriteLine($"Токен на месте, доступ работает. В корне папки приложения файлов: {root.Count}");
    return 0;
}

static async Task<int> LogoutAsync(DpapiTokenStore tokenStore)
{
    await tokenStore.ClearAsync();
    Console.WriteLine("Токен удалён.");
    return 0;
}

/// <summary>
/// Прогон боевых операций против реального Dropbox. Проверяются ровно те свойства,
/// на которых держится очередь: конфликт при повторной записи, атомарность Move и
/// то, что longpoll действительно замечает изменения.
/// </summary>
static async Task<int> SmokeAsync(DpapiTokenStore tokenStore, CancellationToken ct)
{
    string? token = await tokenStore.GetAsync(ct);

    if (token is null)
    {
        Console.Error.WriteLine("Сначала выполните login.");
        return 1;
    }

    using DropboxRemoteStore store = DropboxRemoteStore.Create(token);

    string suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    string sandbox = $"/smoke-{suffix}";
    string first = $"{sandbox}/a.json";
    string moved = $"{sandbox}/b.json";
    int failures = 0;

    void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name}{(detail is null ? "" : $" — {detail}")}");
        if (!ok)
        {
            failures++;
        }
    }

    Console.WriteLine($"Песочница: {sandbox}\n");

    try
    {
        byte[] payload = Encoding.UTF8.GetBytes("""{"проверка":true}""");

        RemoteEntry uploaded = await store.UploadAsync(first, payload, RemoteWriteMode.FailIfExists, ct);
        Check("upload", uploaded.Path == first, uploaded.Path);

        Check("exists", await store.ExistsAsync(first, ct));
        Check("download", (await store.DownloadAsync(first, ct)).SequenceEqual(payload));

        bool conflicted = false;
        try
        {
            await store.UploadAsync(first, payload, RemoteWriteMode.FailIfExists, ct);
        }
        catch (RemoteConflictException)
        {
            conflicted = true;
        }

        // Без этого Dropbox молча создал бы «a (2).json», и дедупликация сообщений
        // перестала бы работать, никак себя не проявив.
        Check("повторная запись даёт конфликт, а не автопереименование", conflicted);

        string cursor = await store.GetCursorAsync(sandbox, ct);

        RemoteEntry relocated = await store.MoveAsync(first, moved, ct);
        Check("move", relocated.Path == moved, relocated.Path);
        Check("исходный путь освободился", !await store.ExistsAsync(first, ct));

        bool moveConflicted = false;
        await store.UploadAsync(first, payload, RemoteWriteMode.FailIfExists, ct);
        try
        {
            await store.MoveAsync(first, moved, ct);
        }
        catch (RemoteConflictException)
        {
            moveConflicted = true;
        }

        Check("move на занятый путь даёт конфликт (основа атомарного захвата)", moveConflicted);

        RemoteChanges changes = await store.WaitForChangesAsync(cursor, TimeSpan.FromSeconds(30), ct);
        Check("longpoll заметил изменения", changes.HasChanges,
            $"изменено {changes.ChangedPaths.Count}, удалено {changes.DeletedPaths.Count}");

        IReadOnlyList<RemoteEntry> listed = await store.ListAsync(sandbox, ct);
        Check("list вернул оба файла", listed.Count == 2, string.Join(", ", listed.Select(e => e.Path)));

        bool notFound = false;
        try
        {
            await store.DownloadAsync($"{sandbox}/нет-такого.json", ct);
        }
        catch (RemoteNotFoundException)
        {
            notFound = true;
        }

        Check("download несуществующего даёт RemoteNotFound", notFound);

        Check("list несуществующей папки возвращает пусто",
            (await store.ListAsync($"{sandbox}/нет-такой-папки", ct)).Count == 0);

        await SmokeQueueAsync(store, sandbox, Check, ct);
    }
    finally
    {
        Console.WriteLine("\nУбираю песочницу…");
        await CleanupAsync(store, sandbox);
    }

    Console.WriteLine(failures == 0 ? "\nВсё зелёное." : $"\nПровалов: {failures}");
    return failures == 0 ? 0 : 1;
}

/// <summary>Полный цикл очереди на настоящем Dropbox, а не на in-memory заглушке.</summary>
static async Task SmokeQueueAsync(
    IRemoteStore store,
    string sandbox,
    Action<string, bool, string?> check,
    CancellationToken ct)
{
    Console.WriteLine("\nОчередь сообщений:");

    var layout = new QueueLayout($"{sandbox}/queue-test");
    var queue = new MessageQueue(store, layout);
    var period = new PeriodKey(2026, 7);
    string deterministicId = MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, period);

    MessageEnvelope? published = await queue.PublishAsync(
        MessageType.SubmitForecast, period, "smoke", new SubmitForecastPayload { Reason = "smoke" },
        deterministicId, ct);

    check("publish", published is not null, published?.MessageId);

    MessageEnvelope? duplicate = await queue.PublishAsync(
        MessageType.SubmitForecast, period, "smoke", new SubmitForecastPayload { Reason = "smoke" },
        deterministicId, ct);

    check("повторная публикация того же id схлопывается", duplicate is null, null);

    IReadOnlyList<RemoteEntry> pending = await queue.ListPendingAsync(QueueDirection.ToDesktop, ct);
    check("сообщение видно в очереди", pending.Count == 1, null);

    ClaimResult claim = await queue.TryClaimAsync(pending[0].Path, ct);
    check("claim", claim.Outcome == ClaimOutcome.Claimed, claim.Outcome.ToString());

    ClaimResult second = await queue.TryClaimAsync(pending[0].Path, ct);
    check("повторный claim отдаёт TakenByOther", second.Outcome == ClaimOutcome.TakenByOther, null);

    MessageEnvelope? afterClaim = await queue.PublishAsync(
        MessageType.SubmitForecast, period, "smoke", new SubmitForecastPayload { Reason = "smoke" },
        deterministicId, ct);

    check("дедупликация работает и когда задача уже в processing", afterClaim is null, null);

    await queue.CompleteAsync(claim.Envelope!, claim.ProcessingPath!, ct);
    check("complete переносит в архив периода",
        await store.ExistsAsync(layout.DonePath(period, claim.Envelope!.FileName), ct), null);
}

/// <summary>Рекурсивная уборка песочницы: Dropbox не удаляет непустые папки одним вызовом.</summary>
static async Task CleanupAsync(IRemoteStore store, string folder)
{
    foreach (string nested in new[]
             {
                 $"{folder}/queue-test/queue/done/2026-07",
                 $"{folder}/queue-test/queue/processing",
                 $"{folder}/queue-test/queue/failed",
                 $"{folder}/queue-test/queue/to-desktop",
                 $"{folder}/queue-test/queue/to-mobile",
                 folder,
             })
    {
        foreach (RemoteEntry entry in await store.ListAsync(nested, CancellationToken.None))
        {
            try
            {
                await store.DeleteAsync(entry.Path, CancellationToken.None);
            }
            catch (RemoteNotFoundException)
            {
                // Уже удалён.
            }
        }
    }
}

static void OpenBrowser(Uri uri)
{
    try
    {
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Не удалось открыть браузер автоматически: {ex.Message}");
    }
}
