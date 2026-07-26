using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Stone;

namespace WaterCounters.Core.Storage.Dropbox;

public sealed record DropboxStoreOptions
{
    /// <summary>
    /// Таймаут longpoll в секундах. Dropbox принимает 30…480; больше — меньше запросов
    /// и меньше расход батареи, но дольше реакция на закрытие соединения.
    /// </summary>
    public int LongpollTimeoutSeconds { get; init; } = 480;

    /// <summary>Сколько раз повторять запрос при 429 и сетевых сбоях.</summary>
    public int MaxRetries { get; init; } = 4;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Реализация <see cref="IRemoteStore"/> поверх папки приложения Dropbox.
///
/// Две операции несут на себе весь протокол очереди и потому настроены явно:
/// загрузка в режиме <c>Add</c> без автопереименования и <c>MoveV2</c> тоже без
/// автопереименования. По умолчанию Dropbox при конфликте молча создаёт «file (2)» —
/// это тихо сломало бы и дедупликацию, и атомарный захват задачи, поэтому autorename
/// выключен, а конфликт превращается в <see cref="RemoteConflictException"/>.
/// </summary>
public sealed class DropboxRemoteStore : IRemoteStore, IDisposable
{
    private readonly DropboxClient _client;
    private readonly DropboxStoreOptions _options;
    private readonly bool _ownsClient;

    public DropboxRemoteStore(DropboxClient client, DropboxStoreOptions? options = null, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new DropboxStoreOptions();
        _ownsClient = ownsClient;
    }

    /// <summary>Клиент на refresh-токене: access-токен обновляется SDK автоматически.</summary>
    public static DropboxRemoteStore Create(
        string refreshToken,
        string? appKey = null,
        DropboxStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        appKey ??= DropboxAppInfo.AppKey;

        var config = new DropboxClientConfig("WaterCounters")
        {
            HttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
        };

        var client = new DropboxClient(refreshToken, appKey, config);
        return new DropboxRemoteStore(client, options, ownsClient: true);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct = default)
    {
        string path = ToDropboxPath(RemotePath.Normalize(folder));
        List<RemoteEntry> entries = [];

        ListFolderResult page;

        try
        {
            page = await ExecuteAsync(() => _client.Files.ListFolderAsync(path), ct).ConfigureAwait(false);
        }
        catch (ApiException<ListFolderError> ex) when (IsNotFound(ex.ErrorResponse))
        {
            // Отсутствующая папка — это пустая папка. Dropbox не создаёт каталоги
            // заранее, поэтому до первого сообщения очереди её физически нет.
            return [];
        }

        while (true)
        {
            foreach (Metadata metadata in page.Entries)
            {
                if (metadata.IsFile)
                {
                    entries.Add(ToEntry(metadata.AsFile));
                }
            }

            if (!page.HasMore)
            {
                break;
            }

            page = await ExecuteAsync(() => _client.Files.ListFolderContinueAsync(page.Cursor), ct)
                .ConfigureAwait(false);
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        string dropboxPath = ToDropboxPath(RemotePath.Normalize(path));

        try
        {
            await ExecuteAsync(() => _client.Files.GetMetadataAsync(dropboxPath), ct).ConfigureAwait(false);
            return true;
        }
        catch (ApiException<GetMetadataError> ex) when (IsNotFound(ex.ErrorResponse))
        {
            return false;
        }
    }

    public async Task<byte[]> DownloadAsync(string path, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);

        try
        {
            using IDownloadResponse<FileMetadata> response = await ExecuteAsync(
                () => _client.Files.DownloadAsync(ToDropboxPath(normalized)), ct).ConfigureAwait(false);

            return await response.GetContentAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (ApiException<DownloadError> ex) when (IsNotFound(ex.ErrorResponse))
        {
            throw new RemoteNotFoundException(normalized);
        }
    }

    public async Task<RemoteEntry> UploadAsync(
        string path,
        ReadOnlyMemory<byte> content,
        RemoteWriteMode mode = RemoteWriteMode.FailIfExists,
        CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);
        WriteMode writeMode = mode == RemoteWriteMode.Overwrite ? WriteMode.Overwrite.Instance : WriteMode.Add.Instance;

        try
        {
            FileMetadata metadata = await ExecuteAsync(
                () =>
                {
                    // Поток одноразовый: при повторе после 429 нужен новый.
                    var stream = new MemoryStream(content.ToArray(), writable: false);
                    return _client.Files.UploadAsync(
                        ToDropboxPath(normalized),
                        writeMode,
                        autorename: false,
                        strictConflict: true,
                        body: stream);
                },
                ct).ConfigureAwait(false);

            return ToEntry(metadata);
        }
        catch (ApiException<UploadError> ex) when (IsConflict(ex.ErrorResponse))
        {
            throw new RemoteConflictException(normalized);
        }
    }

    public async Task<RemoteEntry> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        string from = RemotePath.Normalize(sourcePath);
        string to = RemotePath.Normalize(destinationPath);

        try
        {
            RelocationResult result = await ExecuteAsync(
                () => _client.Files.MoveV2Async(
                    ToDropboxPath(from),
                    ToDropboxPath(to),
                    autorename: false),
                ct).ConfigureAwait(false);

            return result.Metadata.IsFile
                ? ToEntry(result.Metadata.AsFile)
                : throw new RemoteStoreException($"Ожидался файл по пути '{to}', получена папка.");
        }
        catch (ApiException<RelocationError> ex)
        {
            throw MapRelocationError(ex, from, to);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);

        try
        {
            await ExecuteAsync(() => _client.Files.DeleteV2Async(ToDropboxPath(normalized)), ct).ConfigureAwait(false);
        }
        catch (ApiException<DeleteError> ex) when (IsNotFound(ex.ErrorResponse))
        {
            throw new RemoteNotFoundException(normalized);
        }
    }

    public async Task<string> GetCursorAsync(string folder, CancellationToken ct = default)
    {
        string path = ToDropboxPath(RemotePath.Normalize(folder));

        try
        {
            ListFolderGetLatestCursorResult result = await ExecuteAsync(
                () => _client.Files.ListFolderGetLatestCursorAsync(path), ct).ConfigureAwait(false);

            return result.Cursor;
        }
        catch (ApiException<ListFolderError> ex) when (IsNotFound(ex.ErrorResponse))
        {
            // Папки ещё нет — создаём её, чтобы курсор было к чему привязать.
            await EnsureFolderAsync(path, ct).ConfigureAwait(false);

            ListFolderGetLatestCursorResult result = await ExecuteAsync(
                () => _client.Files.ListFolderGetLatestCursorAsync(path), ct).ConfigureAwait(false);

            return result.Cursor;
        }
    }

    public async Task<RemoteChanges> WaitForChangesAsync(string cursor, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);

        int seconds = Math.Clamp((int)timeout.TotalSeconds, 30, _options.LongpollTimeoutSeconds);

        // Longpoll идёт на отдельный хост без заголовка авторизации и висит до
        // указанного таймаута — это не ошибка и не утечка соединения.
        ListFolderLongpollResult longpoll = await ExecuteAsync(
            () => _client.Files.ListFolderLongpollAsync(cursor, (ulong)seconds), ct).ConfigureAwait(false);

        if (!longpoll.Changes)
        {
            return RemoteChanges.Empty(cursor);
        }

        List<string> changed = [];
        List<string> deleted = [];
        string nextCursor = cursor;
        bool more = true;

        while (more)
        {
            ListFolderResult page = await ExecuteAsync(
                () => _client.Files.ListFolderContinueAsync(nextCursor), ct).ConfigureAwait(false);

            foreach (Metadata metadata in page.Entries)
            {
                if (metadata.IsDeleted)
                {
                    deleted.Add(FromDropboxPath(metadata.AsDeleted.PathDisplay));
                }
                else if (metadata.IsFile)
                {
                    changed.Add(FromDropboxPath(metadata.AsFile.PathDisplay));
                }
            }

            nextCursor = page.Cursor;
            more = page.HasMore;
        }

        return new RemoteChanges
        {
            Cursor = nextCursor,
            ChangedPaths = changed,
            DeletedPaths = deleted,
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task EnsureFolderAsync(string dropboxPath, CancellationToken ct)
    {
        if (dropboxPath.Length == 0)
        {
            return;
        }

        try
        {
            await ExecuteAsync(() => _client.Files.CreateFolderV2Async(dropboxPath), ct).ConfigureAwait(false);
        }
        catch (ApiException<CreateFolderError> ex) when (IsConflict(ex.ErrorResponse))
        {
            // Уже создана параллельно — ровно то, чего мы и добивались.
        }
    }

    /// <summary>Повтор при 429 и сетевых сбоях. Dropbox сам подсказывает паузу в RateLimitException.</summary>
    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        TimeSpan delay = _options.InitialRetryDelay;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (RateLimitException ex) when (attempt < _options.MaxRetries)
            {
                TimeSpan wait = TimeSpan.FromSeconds(Math.Max(1, ex.RetryAfter));
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or RetryException ||
        (ex is HttpException http && http.StatusCode >= 500);

    private static RemoteStoreException MapRelocationError(ApiException<RelocationError> ex, string from, string to)
    {
        RelocationError error = ex.ErrorResponse;

        if (error.IsFromLookup && error.AsFromLookup.Value.IsNotFound)
        {
            return new RemoteNotFoundException(from);
        }

        if (error.IsTo && error.AsTo.Value.IsConflict)
        {
            return new RemoteConflictException(to);
        }

        return new RemoteStoreException($"Не удалось переместить '{from}' в '{to}': {error}", ex);
    }

    private static bool IsNotFound(ListFolderError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    private static bool IsNotFound(GetMetadataError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    private static bool IsNotFound(DownloadError error) => error.IsPath && error.AsPath.Value.IsNotFound;

    private static bool IsNotFound(DeleteError error) => error.IsPathLookup && error.AsPathLookup.Value.IsNotFound;

    private static bool IsConflict(UploadError error) => error.IsPath && error.AsPath.Value.Reason.IsConflict;

    private static bool IsConflict(CreateFolderError error) => error.IsPath && error.AsPath.Value.IsConflict;

    private static RemoteEntry ToEntry(FileMetadata metadata) => new()
    {
        Path = FromDropboxPath(metadata.PathDisplay),
        Size = (long)metadata.Size,
        ModifiedUtc = new DateTimeOffset(DateTime.SpecifyKind(metadata.ServerModified, DateTimeKind.Utc)),
        Revision = metadata.Rev,
    };

    /// <summary>Корень папки приложения в Dropbox — пустая строка, а не "/".</summary>
    private static string ToDropboxPath(string normalized) => normalized == "/" ? string.Empty : normalized;

    private static string FromDropboxPath(string? dropboxPath) =>
        string.IsNullOrEmpty(dropboxPath) ? "/" : RemotePath.Normalize(dropboxPath);
}
