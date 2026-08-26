using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Scheduling;
using WaterCounters.Core.State;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;

namespace WaterCounters.Desktop.Services;

/// <summary>
/// Ежечасная проверка срока: льготный период истёк, показаний нет — считаем прогноз.
///
/// Служба не считает прогноз сама, а кладёт в очередь задачу <c>SubmitForecast</c> с
/// детерминированным идентификатором <c>submitforecast-2026-07</c>. Ту же задачу с тем
/// же идентификатором может породить телефон, и дубль схлопнется сам собой — даже если
/// первая копия уже уехала в processing или done.
/// </summary>
public sealed class DeadlineWatchdogService(
    MessageQueue queue,
    ISettingsProvider settings,
    ReadingHistoryStore history,
    DesktopOptions options,
    ILogger<DeadlineWatchdogService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly MessageQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ReadingHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<DeadlineWatchdogService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Проверка срока сдачи сорвалась.");
            }

            await Task.Delay(_options.WatchdogInterval, _clock, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        SubmissionSchedule schedule = _settings.Schedule;
        DateTimeOffset now = _clock.GetUtcNow();
        ReadingHistory history = await _history.LoadAsync(ct).ConfigureAwait(false);

        foreach (PeriodKey period in schedule.OpenPeriods(now))
        {
            if (!schedule.IsForecastDue(period, now))
            {
                continue;
            }

            if (history.IsClosed(period))
            {
                continue;
            }

            SubmissionWindow window = schedule.WindowFor(period);
            string reason = $"льготный период истёк {window.GraceEnd:yyyy-MM-dd}, фотографий за {period} не появилось";

            MessageEnvelope? published = await _queue.PublishAsync(
                MessageType.SubmitForecast,
                period,
                _options.DeviceId,
                new SubmitForecastPayload { Reason = reason },
                MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, period),
                ct).ConfigureAwait(false);

            if (published is null)
            {
                // Задача уже проходила через очередь — дубль схлопнулся, как задумано.
                continue;
            }

            _logger.LogWarning("Срок за {Period} прошёл: поставлена задача на прогноз. {Reason}", period, reason);
        }
    }
}

/// <summary>
/// Heartbeat в <c>/queue/to-mobile</c>: телефон показывает «обработчик на связи».
/// Сообщение перезаписывается по фиксированному имени, а не копится в очереди —
/// история состояний никому не нужна, нужен только последний признак жизни.
/// </summary>
public sealed class HealthPublisherService(
    IRemoteStore store,
    QueueLayout layout,
    ISettingsProvider settings,
    DesktopOptions options,
    ILogger<HealthPublisherService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<HealthPublisherService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat не опубликован.");
            }

            await Task.Delay(_options.HeartbeatInterval, _clock, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        PeriodKey period = _settings.Schedule.CurrentPeriod(now);

        string health = _settings.Secrets is null
            ? "секреты недоступны: мастер-пароль не задан"
            : "ok";

        MessageEnvelope envelope = MessageCodec.Create(
            MessageType.DesktopStatus,
            period,
            _options.DeviceId,
            new DesktopStatusPayload
            {
                Version = DesktopVersion.Current,
                LastSeenUtc = now,
                VlmModel = _settings.Current.Recognition.Model,
                Health = health,
            },
            now,
            HeartbeatMessageId);

        // Перезапись по фиксированному имени, а не публикация через очередь. История
        // состояний никому не нужна — нужен последний признак жизни, и накапливать
        // ради него по файлу каждые пятнадцать минут было бы просто мусором.
        await _store.UploadAsync(
                _layout.PendingPath(QueueDirection.ToMobile, envelope.FileName),
                MessageCodec.Encode(envelope),
                RemoteWriteMode.Overwrite,
                ct)
            .ConfigureAwait(false);

        _logger.LogDebug("Heartbeat опубликован: {Health}.", health);
    }

    private const string HeartbeatMessageId = "desktopstatus";
}

public static class DesktopVersion
{
    public static string Current { get; } =
        typeof(DesktopVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
