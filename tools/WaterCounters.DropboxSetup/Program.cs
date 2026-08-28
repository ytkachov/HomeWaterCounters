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

// Проверяем ключ до всего остального: без этого заглушка доезжает до Dropbox,
// и пользователь видит "Invalid client_id" на странице вместо внятного указания,
// что именно поправить.
if (command is "login" or "status" or "smoke" or "ls" or "pull" or "put" && !DropboxAppInfo.IsConfigured)
{
    Console.Error.WriteLine(DropboxAppInfo.ConfigurationHint);
    return 3;
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
        "ls" => await ListFolderAsync(tokenStore, args, cts.Token),
        "pull" => await PullAsync(tokenStore, args, cts.Token),
        "put" => await PutAsync(tokenStore, args, cts.Token),
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
          ls       показать содержимое папки в облаке:  ls /photos/2026-08
          pull     скачать папку из облака на диск:     pull /photos/2026-08 C:\путь
          put      залить файл в облако:                put C:\файл /config/settings.json
          logout   удалить сохранённый токен

        ls, pull и put ходят в Dropbox напрямую по API и не зависят от десктопного
        клиента: ими можно забрать или положить файлы, когда синхронизация не работает.
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

/// <summary>
/// Содержимое папки в облаке. Показывает то, что видит API, а не то, что успел
/// синхронизировать десктопный клиент — разница между ними и есть ответ на вопрос
/// «файлы уже загрузились или ещё нет».
/// </summary>
static async Task<int> ListFolderAsync(DpapiTokenStore tokenStore, string[] args, CancellationToken ct)
{
    string folder = args.Length > 1 ? args[1] : "/";

    using DropboxRemoteStore? store = await OpenAsync(tokenStore, ct);

    if (store is null)
    {
        return 1;
    }

    IReadOnlyList<RemoteEntry> entries = await store.ListAsync(folder, ct);

    if (entries.Count == 0)
    {
        Console.WriteLine($"В '{folder}' файлов нет.");
        return 0;
    }

    Console.WriteLine($"В '{folder}' файлов: {entries.Count}");

    foreach (RemoteEntry entry in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {entry.ModifiedUtc:yyyy-MM-dd HH:mm}  {entry.Size,10:N0}  {RemotePath.GetFileName(entry.Path)}");
    }

    return 0;
}

/// <summary>
/// Скачивает папку из облака на диск. Нужна, когда клиент Dropbox не синхронизирует:
/// API отдаёт файлы независимо от него. Уже скачанные файлы того же размера
/// пропускаются, чтобы повтор команды не тянул всё заново.
/// </summary>
/// <summary>
/// Кладёт один файл в облако. Перезапись явная: настройки и секреты правятся
/// редко, а молча затереть чужую свежую версию — худшее, что тут можно сделать.
/// </summary>
static async Task<int> PutAsync(DpapiTokenStore tokenStore, string[] args, CancellationToken ct)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Использование: put <файл-на-диске> <путь-в-облаке> [--force]");
        return 2;
    }

    string source = args[1];
    string target = args[2];
    bool force = args.Contains("--force", StringComparer.Ordinal);

    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"Файл '{source}' не найден.");
        return 2;
    }

    using DropboxRemoteStore? store = await OpenAsync(tokenStore, ct);

    if (store is null)
    {
        return 1;
    }

    byte[] content = await File.ReadAllBytesAsync(source, ct);

    try
    {
        RemoteEntry entry = await store.UploadAsync(
            target,
            content,
            force ? RemoteWriteMode.Overwrite : RemoteWriteMode.FailIfExists,
            ct);

        Console.WriteLine($"Загружено: {entry.Path}, {entry.Size:N0} байт.");
        return 0;
    }
    catch (RemoteConflictException)
    {
        Console.Error.WriteLine(
            $"Файл '{target}' уже есть в облаке. Повторите с --force, если действительно нужно перезаписать.");
        return 1;
    }
}

static async Task<int> PullAsync(DpapiTokenStore tokenStore, string[] args, CancellationToken ct)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Использование: pull <папка-в-облаке> <папка-на-диске>");
        return 2;
    }

    string folder = args[1];
    string destination = args[2];

    using DropboxRemoteStore? store = await OpenAsync(tokenStore, ct);

    if (store is null)
    {
        return 1;
    }

    IReadOnlyList<RemoteEntry> entries = await store.ListAsync(folder, ct);

    if (entries.Count == 0)
    {
        Console.WriteLine($"В '{folder}' файлов нет — скачивать нечего.");
        return 0;
    }

    Directory.CreateDirectory(destination);

    int copied = 0;
    int skipped = 0;

    foreach (RemoteEntry entry in entries)
    {
        string target = Path.Combine(destination, RemotePath.GetFileName(entry.Path));

        if (File.Exists(target) && new FileInfo(target).Length == entry.Size)
        {
            skipped++;
            continue;
        }

        byte[] content = await store.DownloadAsync(entry.Path, ct);
        await File.WriteAllBytesAsync(target, content, ct);

        Console.WriteLine($"  {RemotePath.GetFileName(entry.Path),-40} {entry.Size,10:N0} байт");
        copied++;
    }

    Console.WriteLine($"Скачано: {copied}, пропущено (уже на диске): {skipped}. Папка: {destination}");
    return 0;
}

/// <summary>Открывает хранилище на сохранённом токене, либо объясняет, чего не хватает.</summary>
static async Task<DropboxRemoteStore?> OpenAsync(DpapiTokenStore tokenStore, CancellationToken ct)
{
    string? token = await tokenStore.GetAsync(ct);

    if (token is not null)
    {
        return DropboxRemoteStore.Create(token);
    }

    Console.Error.WriteLine("Токена нет. Запустите: dotnet run --project tools/WaterCounters.DropboxSetup -- login");
    return null;
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
