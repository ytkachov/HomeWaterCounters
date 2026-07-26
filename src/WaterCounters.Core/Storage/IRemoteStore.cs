namespace WaterCounters.Core.Storage;

public sealed record RemoteEntry
{
    public required string Path { get; init; }

    public required long Size { get; init; }

    public required DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Ревизия содержимого — меняется при каждой записи. Для оптимистичных проверок.</summary>
    public required string Revision { get; init; }
}

public sealed record RemoteChanges
{
    public required string Cursor { get; init; }

    public required IReadOnlyList<string> ChangedPaths { get; init; }

    public required IReadOnlyList<string> DeletedPaths { get; init; }

    public bool HasChanges => ChangedPaths.Count > 0 || DeletedPaths.Count > 0;

    public static RemoteChanges Empty(string cursor) => new()
    {
        Cursor = cursor,
        ChangedPaths = [],
        DeletedPaths = [],
    };
}

public enum RemoteWriteMode
{
    /// <summary>Записать только если файла нет. Основа атомарного «захвата» задачи.</summary>
    FailIfExists = 0,

    Overwrite = 1,
}

/// <summary>
/// Абстракция над удалённой папкой (Dropbox). Держится узкой намеренно: заменить
/// Dropbox на другое хранилище должно быть возможно без правок вызывающего кода,
/// а тесты гоняются на <see cref="InMemoryRemoteStore"/> без сети.
/// </summary>
public interface IRemoteStore
{
    /// <summary>Непосредственное содержимое папки, без рекурсии. Отсутствующая папка — пустой список.</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct = default);

    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <exception cref="RemoteNotFoundException">Файла нет.</exception>
    Task<byte[]> DownloadAsync(string path, CancellationToken ct = default);

    /// <exception cref="RemoteConflictException">
    /// Режим <see cref="RemoteWriteMode.FailIfExists"/> и файл уже существует.
    /// </exception>
    Task<RemoteEntry> UploadAsync(
        string path,
        ReadOnlyMemory<byte> content,
        RemoteWriteMode mode = RemoteWriteMode.FailIfExists,
        CancellationToken ct = default);

    /// <summary>
    /// Атомарное перемещение. Именно на нём построен «захват» сообщения из очереди:
    /// две копии десктопа могут одновременно попытаться забрать одну задачу, и ровно
    /// одна получит успех, вторая — <see cref="RemoteConflictException"/>.
    /// </summary>
    /// <exception cref="RemoteNotFoundException">Исходного файла нет.</exception>
    /// <exception cref="RemoteConflictException">Целевой путь занят.</exception>
    Task<RemoteEntry> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Курсор, описывающий текущее состояние папки. Отправная точка для longpoll.</summary>
    Task<string> GetCursorAsync(string folder, CancellationToken ct = default);

    /// <summary>
    /// Ждёт изменений в папке с момента <paramref name="cursor"/>. Возвращается сразу,
    /// если изменения уже накопились, иначе висит до таймаута и отдаёт пустой результат
    /// с тем же курсором.
    /// </summary>
    Task<RemoteChanges> WaitForChangesAsync(string cursor, TimeSpan timeout, CancellationToken ct = default);
}

public class RemoteStoreException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class RemoteNotFoundException(string path)
    : RemoteStoreException($"Путь '{path}' не найден в удалённом хранилище.")
{
    public string Path { get; } = path;
}

public sealed class RemoteConflictException(string path)
    : RemoteStoreException($"Путь '{path}' уже занят.")
{
    public string Path { get; } = path;
}
