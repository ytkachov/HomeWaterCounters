using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.State;

namespace WaterCounters.Desktop.Services;

/// <summary>
/// Захват и обработка задач из <c>/queue/to-desktop</c>.
///
/// Переход pending → processing делается атомарным Move, поэтому одну задачу не могут
/// забрать две копии обработчика. При старте вызывается восстановление: всё, что
/// осталось в processing, — это ровно те задачи, которые прервало падение процесса.
/// </summary>
public sealed class MessageProcessorService(
    MessageQueue queue,
    IRemoteStore store,
    ISettingsProvider settings,
    ReadingPipeline pipeline,
    ILocalState local,
    ChangeSignal signal,
    DesktopOptions options,
    ILogger<MessageProcessorService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly MessageQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ReadingPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly ILocalState _local = local ?? throw new ArgumentNullException(nameof(local));
    private readonly ChangeSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<MessageProcessorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Разбор очереди сорвался.");
            }

            await _signal.WaitAsync(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ClaimResult> abandoned = await _queue.RecoverAbandonedAsync(ct).ConfigureAwait(false);

            foreach (ClaimResult claim in abandoned.Where(static c => c.Outcome == ClaimOutcome.Claimed))
            {
                _logger.LogWarning(
                    "Задача {MessageId} осталась после падения процесса — обрабатываем заново.",
                    claim.Envelope!.MessageId);

                await HandleAsync(claim, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Восстановление прерванных задач не удалось.");
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        IReadOnlyList<RemoteEntry> pending = await _queue
            .ListPendingAsync(QueueDirection.ToDesktop, ct)
            .ConfigureAwait(false);

        foreach (RemoteEntry entry in pending)
        {
            ct.ThrowIfCancellationRequested();

            ClaimResult claim = await _queue.TryClaimAsync(entry.Path, ct).ConfigureAwait(false);

            switch (claim.Outcome)
            {
                case ClaimOutcome.Claimed:
                    await HandleAsync(claim, ct).ConfigureAwait(false);
                    break;

                case ClaimOutcome.Poisoned:
                    _logger.LogError("Сообщение {Path} не разбирается и уехало в failed: {Error}", entry.Path, claim.Error);
                    break;

                default:
                    // Забрал кто-то другой либо файл уже уехал — это не ошибка.
                    break;
            }
        }
    }

    private async Task HandleAsync(ClaimResult claim, CancellationToken ct)
    {
        MessageEnvelope envelope = claim.Envelope!;
        PeriodKey period = envelope.GetPeriod();

        // Идемпотентность: задача могла упасть между обработкой и переносом в done.
        if (await _local.WasProcessedAsync(envelope.MessageId, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Сообщение {MessageId} уже обработано — закрываем повтор.", envelope.MessageId);
            await _queue.CompleteAsync(envelope, claim.ProcessingPath!, ct).ConfigureAwait(false);
            return;
        }

        string outcome;

        try
        {
            outcome = await DispatchAsync(envelope, period, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Остановка хоста — задача остаётся в processing и будет восстановлена.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Обработка {MessageId} провалилась.", envelope.MessageId);
            await _queue.FailAsync(claim.ProcessingPath!, ex.ToString(), ct).ConfigureAwait(false);
            return;
        }

        await _local.MarkProcessedAsync(
            new ProcessedMessage
            {
                MessageId = envelope.MessageId,
                Type = envelope.Type.ToString(),
                Period = envelope.Period,
                ProcessedUtc = _clock.GetUtcNow(),
                Outcome = outcome,
            },
            ct).ConfigureAwait(false);

        await _queue.CompleteAsync(envelope, claim.ProcessingPath!, ct).ConfigureAwait(false);
    }

    private async Task<string> DispatchAsync(MessageEnvelope envelope, PeriodKey period, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageType.SubmitReadings:
            {
                SubmitReadingsPayload payload = envelope.GetPayload<SubmitReadingsPayload>();
                PhotoBatchDecision batch = await BuildBatchAsync(payload, ct).ConfigureAwait(false);

                // Задачу прислал телефон — значит, подтверждать будет он, а не dryRun.
                PipelineResult result = await _pipeline
                    .ProcessPhotosAsync(period, batch, ConfirmationMode.AwaitMobile, ct)
                    .ConfigureAwait(false);

                return result.Outcome.ToString();
            }

            case MessageType.SubmitForecast:
            {
                SubmitForecastPayload payload = envelope.GetPayload<SubmitForecastPayload>();

                PipelineResult result = await _pipeline
                    .ProcessForecastAsync(period, payload.Reason, ConfirmationMode.Direct, ct)
                    .ConfigureAwait(false);

                return result.Outcome.ToString();
            }

            case MessageType.ReadingsConfirmed:
            {
                ReadingsConfirmedPayload payload = envelope.GetPayload<ReadingsConfirmedPayload>();

                PipelineResult result = await _pipeline
                    .SubmitConfirmedAsync(period, payload.Readings, ct)
                    .ConfigureAwait(false);

                return result.Outcome.ToString();
            }

            case MessageType.ReadingsRejected:
            {
                ReadingsRejectedPayload payload = envelope.GetPayload<ReadingsRejectedPayload>();

                _logger.LogWarning(
                    "Показания за {Period} отклонены на телефоне: {Reason}. Ждём новых фотографий.",
                    period,
                    payload.Reason);

                return "Rejected";
            }

            default:
                // Сообщение, адресованное не нам либо из будущей схемы. Конверт разобран,
                // тип неизвестен — задача закрывается, а не копится в очереди.
                _logger.LogWarning("Тип {Type} обработчику не адресован — сообщение закрыто.", envelope.Type);
                return "Ignored";
        }
    }

    /// <summary>
    /// Пачка из сообщения телефона. Файл сообщения — commit-маркер комплекта,
    /// поэтому список фотографий берётся из него, а не из содержимого папки:
    /// полуготовый набор обработчик увидеть не должен.
    /// </summary>
    private async Task<PhotoBatchDecision> BuildBatchAsync(SubmitReadingsPayload payload, CancellationToken ct)
    {
        List<PhotoAssignment> assignments = [];
        List<string> missingFiles = [];

        // Папки перечисляются по одной: у комплекта они почти всегда одни и те же,
        // а метаданные файла нужны целиком — размер и ревизия входят в журнал.
        Dictionary<string, IReadOnlyList<RemoteEntry>> folders = new(RemotePath.Comparer);

        foreach (PhotoRef photo in payload.Photos)
        {
            MeterSpec? meter = _settings.Current.MeterByKey(photo.MeterKey);

            if (meter is null)
            {
                _logger.LogWarning("Фото для неизвестного счётчика {MeterKey} пропущено.", photo.MeterKey);
                continue;
            }

            string folder = RemotePath.GetFolder(photo.PhotoPath);

            if (!folders.TryGetValue(folder, out IReadOnlyList<RemoteEntry>? entries))
            {
                entries = await ListAsync(folder, ct).ConfigureAwait(false);
                folders[folder] = entries;
            }

            RemoteEntry? entry = entries.FirstOrDefault(e => RemotePath.Comparer.Equals(e.Path, photo.PhotoPath));

            if (entry is null)
            {
                missingFiles.Add(photo.PhotoPath);
                continue;
            }

            assignments.Add(new PhotoAssignment(meter, entry, PhotoMatch.ByFileName));
        }

        List<MeterSpec> missing =
        [
            .. _settings.Current.OrderedMeters.Where(m => assignments.All(a => a.Meter.Key != m.Key))
        ];

        return new PhotoBatchDecision
        {
            Readiness = BatchReadiness.Ready,
            Reason = missingFiles.Count == 0
                ? "комплект прислан телефоном"
                : $"комплект прислан телефоном, не найдены файлы: {string.Join(", ", missingFiles)}",
            Assignments = assignments,
            MissingMeters = missing,
            Fingerprint = string.Empty,
        };
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct)
    {
        try
        {
            return await _store.ListAsync(folder, ct).ConfigureAwait(false);
        }
        catch (RemoteStoreException ex)
        {
            _logger.LogWarning(ex, "Папка {Folder} не читается.", folder);
            return [];
        }
    }
}
