using System.Text;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;

namespace WaterCounters.Core.Messaging;

public enum ClaimOutcome
{
    /// <summary>Задача успешно захвачена, можно обрабатывать.</summary>
    Claimed = 0,

    /// <summary>Кто-то другой забрал её раньше, либо файл уже уехал. Не ошибка.</summary>
    TakenByOther = 1,

    /// <summary>Файл захвачен, но не разбирается. Уже перемещён в failed — обрабатывать нечего.</summary>
    Poisoned = 2,
}

public sealed record ClaimResult
{
    public required ClaimOutcome Outcome { get; init; }

    public MessageEnvelope? Envelope { get; init; }

    /// <summary>Путь в папке processing — нужен для последующего Complete/Fail.</summary>
    public string? ProcessingPath { get; init; }

    public string? Error { get; init; }

    public static ClaimResult TakenByOther() => new() { Outcome = ClaimOutcome.TakenByOther };
}

/// <summary>
/// Очередь сообщений поверх удалённой папки.
///
/// Жизненный цикл: pending → processing → done | failed.
/// Переход pending → processing делается атомарным Move, поэтому одну задачу
/// не могут забрать две копии обработчика. Файл в processing после падения —
/// это ровно то, что нужно переобработать при следующем старте.
/// </summary>
public sealed class MessageQueue(IRemoteStore store, QueueLayout layout, TimeProvider? clock = null)
{
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public QueueLayout Layout => _layout;

    /// <summary>
    /// Публикует сообщение в папку, соответствующую его направлению.
    /// Если сообщение с таким MessageId уже проходило через очередь (лежит в pending,
    /// processing, done или failed) — публикация не делается. Это то, что схлопывает
    /// дубль прогноза от телефона и от watchdog-а десктопа.
    /// </summary>
    /// <returns>Опубликованный конверт, либо null, если сообщение уже было.</returns>
    public async Task<MessageEnvelope?> PublishAsync<TPayload>(
        MessageType type,
        PeriodKey period,
        string deviceId,
        TPayload payload,
        string? messageId = null,
        CancellationToken ct = default)
        where TPayload : class
    {
        MessageEnvelope envelope = MessageCodec.Create(
            type, period, deviceId, payload, _clock.GetUtcNow(), messageId);

        if (messageId is not null && await WasSeenAsync(envelope, ct).ConfigureAwait(false))
        {
            return null;
        }

        string path = _layout.PendingPath(QueueLayout.DirectionOf(type), envelope.FileName);

        try
        {
            await _store.UploadAsync(path, MessageCodec.Encode(envelope), RemoteWriteMode.FailIfExists, ct)
                .ConfigureAwait(false);
        }
        catch (RemoteConflictException)
        {
            return null;
        }

        return envelope;
    }

    /// <summary>Файлы, ожидающие обработки, в порядке возрастания MessageId (то есть по времени).</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListPendingAsync(QueueDirection direction, CancellationToken ct = default)
    {
        IReadOnlyList<RemoteEntry> entries = await _store
            .ListAsync(_layout.PendingFolder(direction), ct)
            .ConfigureAwait(false);

        return [.. entries.Where(static e => e.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Пытается захватить задачу: атомарно переносит файл в processing и разбирает его.
    /// Нечитаемое сообщение сразу уезжает в failed рядом с текстом ошибки — оно никогда
    /// не вернётся в очередь и не заблокирует обработку остальных.
    /// </summary>
    public async Task<ClaimResult> TryClaimAsync(string pendingPath, CancellationToken ct = default)
    {
        string fileName = RemotePath.GetFileName(pendingPath);
        string processingPath = _layout.ProcessingPath(fileName);

        try
        {
            await _store.MoveAsync(pendingPath, processingPath, ct).ConfigureAwait(false);
        }
        catch (RemoteNotFoundException)
        {
            return ClaimResult.TakenByOther();
        }
        catch (RemoteConflictException)
        {
            return ClaimResult.TakenByOther();
        }

        byte[] content = await _store.DownloadAsync(processingPath, ct).ConfigureAwait(false);

        if (!MessageCodec.TryDecode(content, out MessageEnvelope? envelope, out string? error))
        {
            await QuarantineAsync(processingPath, error ?? "неизвестная ошибка разбора", ct).ConfigureAwait(false);

            return new ClaimResult
            {
                Outcome = ClaimOutcome.Poisoned,
                ProcessingPath = processingPath,
                Error = error,
            };
        }

        return new ClaimResult
        {
            Outcome = ClaimOutcome.Claimed,
            Envelope = envelope,
            ProcessingPath = processingPath,
        };
    }

    /// <summary>Задача выполнена — файл переезжает в архив периода.</summary>
    public async Task CompleteAsync(MessageEnvelope envelope, string processingPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        string destination = _layout.DonePath(envelope.GetPeriod(), envelope.FileName);
        await MoveTolerantAsync(processingPath, destination, ct).ConfigureAwait(false);
    }

    /// <summary>Задача провалена окончательно — файл и текст ошибки уезжают в failed.</summary>
    public async Task FailAsync(string processingPath, string reason, CancellationToken ct = default)
    {
        await QuarantineAsync(processingPath, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Сообщения, оставшиеся в processing после падения процесса. Вызывается при старте:
    /// обработчик обязан быть идемпотентным, потому что задача могла упасть на любом шаге.
    /// </summary>
    public async Task<IReadOnlyList<ClaimResult>> RecoverAbandonedAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RemoteEntry> entries = await _store
            .ListAsync(_layout.ProcessingFolder, ct)
            .ConfigureAwait(false);

        List<ClaimResult> recovered = [];

        foreach (RemoteEntry entry in entries)
        {
            if (!entry.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] content;

            try
            {
                content = await _store.DownloadAsync(entry.Path, ct).ConfigureAwait(false);
            }
            catch (RemoteNotFoundException)
            {
                continue;
            }

            if (MessageCodec.TryDecode(content, out MessageEnvelope? envelope, out string? error))
            {
                recovered.Add(new ClaimResult
                {
                    Outcome = ClaimOutcome.Claimed,
                    Envelope = envelope,
                    ProcessingPath = entry.Path,
                });
            }
            else
            {
                await QuarantineAsync(entry.Path, error ?? "неизвестная ошибка разбора", ct).ConfigureAwait(false);

                recovered.Add(new ClaimResult
                {
                    Outcome = ClaimOutcome.Poisoned,
                    ProcessingPath = entry.Path,
                    Error = error,
                });
            }
        }

        return recovered;
    }

    /// <summary>Проходило ли сообщение с таким идентификатором через очередь в любой стадии.</summary>
    private async Task<bool> WasSeenAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        string fileName = envelope.FileName;
        QueueDirection direction = QueueLayout.DirectionOf(envelope.Type);

        string[] candidates =
        [
            _layout.PendingPath(direction, fileName),
            _layout.ProcessingPath(fileName),
            _layout.DonePath(envelope.GetPeriod(), fileName),
            _layout.FailedPath(fileName),
        ];

        foreach (string candidate in candidates)
        {
            if (await _store.ExistsAsync(candidate, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task QuarantineAsync(string processingPath, string reason, CancellationToken ct)
    {
        string fileName = RemotePath.GetFileName(processingPath);
        string destination = _layout.FailedPath(fileName);

        await MoveTolerantAsync(processingPath, destination, ct).ConfigureAwait(false);

        string note = $"{_clock.GetUtcNow():O}\n{reason}\n";
        await _store.UploadAsync(
                destination + ".error.txt",
                Encoding.UTF8.GetBytes(note),
                RemoteWriteMode.Overwrite,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Перемещение, устойчивое к повторному запуску: файла уже нет — значит предыдущая
    /// попытка успела, цель занята — значит там уже лежит результат этой же задачи.
    /// </summary>
    private async Task MoveTolerantAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            await _store.MoveAsync(source, destination, ct).ConfigureAwait(false);
        }
        catch (RemoteNotFoundException)
        {
            // Уже перемещён предыдущей попыткой.
        }
        catch (RemoteConflictException)
        {
            await _store.DeleteAsync(source, ct).ConfigureAwait(false);
        }
    }
}
