using System.Globalization;

namespace WaterCounters.Core.Storage;

/// <summary>
/// Хранилище в памяти для тестов. Воспроизводит те свойства Dropbox, на которые
/// опирается очередь: атомарность Move, конфликт при занятом пути, курсорную модель
/// изменений и longpoll-ожидание.
/// </summary>
public sealed class InMemoryRemoteStore : IRemoteStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredFile> _files = new(RemotePath.Comparer);
    private readonly List<ChangeRecord> _changeLog = [];
    private readonly TimeProvider _clock;

    private long _version;
    private TaskCompletionSource _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InMemoryRemoteStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Искусственная задержка перед каждой операцией — для тестов гонок.</summary>
    public Func<string, CancellationToken, Task>? BeforeOperation { get; set; }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(folder);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            List<RemoteEntry> entries = [];

            foreach ((string path, StoredFile file) in _files)
            {
                if (RemotePath.Comparer.Equals(RemotePath.GetFolder(path), normalized))
                {
                    entries.Add(file.ToEntry(path));
                }
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            return Task.FromResult<IReadOnlyList<RemoteEntry>>(entries);
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_files.ContainsKey(normalized));
        }
    }

    public async Task<byte[]> DownloadAsync(string path, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);
        await BeforeAsync(normalized, ct).ConfigureAwait(false);

        lock (_gate)
        {
            return _files.TryGetValue(normalized, out StoredFile? file)
                ? file.Content.ToArray()
                : throw new RemoteNotFoundException(normalized);
        }
    }

    public async Task<RemoteEntry> UploadAsync(
        string path,
        ReadOnlyMemory<byte> content,
        RemoteWriteMode mode = RemoteWriteMode.FailIfExists,
        CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);
        await BeforeAsync(normalized, ct).ConfigureAwait(false);

        byte[] copy = content.ToArray();

        lock (_gate)
        {
            if (mode == RemoteWriteMode.FailIfExists && _files.ContainsKey(normalized))
            {
                throw new RemoteConflictException(normalized);
            }

            RecordChange(normalized, deleted: false);
            var file = new StoredFile(copy, _clock.GetUtcNow(), CurrentRevision);
            _files[normalized] = file;
            return file.ToEntry(normalized);
        }
    }

    public async Task<RemoteEntry> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        string from = RemotePath.Normalize(sourcePath);
        string to = RemotePath.Normalize(destinationPath);
        await BeforeAsync(from, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_files.TryGetValue(from, out StoredFile? file))
            {
                throw new RemoteNotFoundException(from);
            }

            if (RemotePath.Comparer.Equals(from, to))
            {
                return file.ToEntry(to);
            }

            if (_files.ContainsKey(to))
            {
                throw new RemoteConflictException(to);
            }

            RecordChange(from, deleted: true);
            RecordChange(to, deleted: false);
            var moved = new StoredFile(file.Content, _clock.GetUtcNow(), CurrentRevision);
            _files.Remove(from);
            _files[to] = moved;
            return moved.ToEntry(to);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(path);
        await BeforeAsync(normalized, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_files.Remove(normalized))
            {
                throw new RemoteNotFoundException(normalized);
            }

            RecordChange(normalized, deleted: true);
        }
    }

    public Task<string> GetCursorAsync(string folder, CancellationToken ct = default)
    {
        string normalized = RemotePath.Normalize(folder);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(Cursor.Create(normalized, _version));
        }
    }

    public async Task<RemoteChanges> WaitForChangesAsync(string cursor, TimeSpan timeout, CancellationToken ct = default)
    {
        Cursor parsed = Cursor.Parse(cursor);

        while (true)
        {
            Task signal;

            lock (_gate)
            {
                RemoteChanges? changes = CollectChanges(parsed);

                if (changes is not null)
                {
                    return changes;
                }

                signal = _changeSignal.Task;
            }

            Task completed = await Task.WhenAny(signal, Task.Delay(timeout, _clock, ct)).ConfigureAwait(false);

            if (completed != signal)
            {
                ct.ThrowIfCancellationRequested();
                return RemoteChanges.Empty(cursor);
            }
        }
    }

    /// <summary>Снимок содержимого — для отладки тестов.</summary>
    public IReadOnlyDictionary<string, byte[]> Snapshot()
    {
        lock (_gate)
        {
            return _files.ToDictionary(kv => kv.Key, kv => kv.Value.Content.ToArray(), RemotePath.Comparer);
        }
    }

    private RemoteChanges? CollectChanges(Cursor cursor)
    {
        List<string> changed = [];
        List<string> deleted = [];

        foreach (ChangeRecord record in _changeLog)
        {
            if (record.Version <= cursor.Version || !RemotePath.IsInFolder(record.Path, cursor.Folder))
            {
                continue;
            }

            (record.Deleted ? deleted : changed).Add(record.Path);
        }

        return changed.Count == 0 && deleted.Count == 0
            ? null
            : new RemoteChanges
            {
                Cursor = Cursor.Create(cursor.Folder, _version),
                ChangedPaths = changed.Distinct(RemotePath.Comparer).ToArray(),
                DeletedPaths = deleted.Distinct(RemotePath.Comparer).ToArray(),
            };
    }

    private Task BeforeAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return BeforeOperation?.Invoke(path, ct) ?? Task.CompletedTask;
    }

    private string CurrentRevision => _version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Единственная точка, двигающая версию хранилища. Именно поэтому её вызывает и
    /// удаление: иначе запись об удалении получает версию, которую наблюдатель уже
    /// видел, и удаление становится невидимым для longpoll.
    /// </summary>
    private void RecordChange(string path, bool deleted)
    {
        _version++;
        _changeLog.Add(new ChangeRecord(_version, path, deleted));

        TaskCompletionSource previous = _changeSignal;
        _changeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private sealed record StoredFile(byte[] Content, DateTimeOffset ModifiedUtc, string Revision)
    {
        public RemoteEntry ToEntry(string path) => new()
        {
            Path = path,
            Size = Content.LongLength,
            ModifiedUtc = ModifiedUtc,
            Revision = Revision,
        };
    }

    private readonly record struct ChangeRecord(long Version, string Path, bool Deleted);

    private readonly record struct Cursor(string Folder, long Version)
    {
        public static string Create(string folder, long version) =>
            string.Create(CultureInfo.InvariantCulture, $"{version}|{folder}");

        public static Cursor Parse(string cursor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cursor);

            int separator = cursor.IndexOf('|');

            if (separator < 0 ||
                !long.TryParse(cursor.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long version))
            {
                throw new RemoteStoreException($"Курсор '{cursor}' повреждён.");
            }

            return new Cursor(cursor[(separator + 1)..], version);
        }
    }
}
