using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.State;

namespace WaterCounters.Desktop.Services;

/// <summary>
/// Наблюдение за папкой фотографий при ручной раскладке.
///
/// Мобильного приложения пока нет, поэтому сообщения <c>SubmitReadings</c> никто не
/// отправляет и обработчик обязан сам заметить появление фотографий. От целевой схемы
/// это отличается ровно источником события: всё, что дальше распознавания, общее.
/// </summary>
public sealed class PhotoBatchService(
    ISettingsProvider settings,
    IRemoteStore store,
    QueueLayout layout,
    ReadingPipeline pipeline,
    ILocalState local,
    ChangeSignal signal,
    DesktopOptions options,
    ILogger<PhotoBatchService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly ReadingPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly ILocalState _local = local ?? throw new ArgumentNullException(nameof(local));
    private readonly ChangeSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<PhotoBatchService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var evaluator = new PhotoBatchEvaluator(_clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(evaluator, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Проверка папки фотографий сорвалась.");
            }

            // Просыпаемся и по сигналу от longpoll, и по таймеру: правило «папка не
            // пополнялась N минут» само по себе требует хода времени, а не события.
            await _signal.WaitAsync(_options.PhotoScanInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ScanAsync(PhotoBatchEvaluator evaluator, CancellationToken ct)
    {
        AppSettings current = _settings.Current;

        if (current.Meters.Count == 0)
        {
            return;
        }

        var settling = TimeSpan.FromMinutes(Math.Max(0, current.Recognition.SettlingMinutes));

        foreach (PeriodKey period in _settings.Schedule.OpenPeriods(_clock.GetUtcNow()))
        {
            IReadOnlyList<RemoteEntry> entries = await _store
                .ListAsync(_layout.PhotosFolderFor(period), ct)
                .ConfigureAwait(false);

            PhotoBatchDecision decision = evaluator.Evaluate(entries, current.OrderedMeters, settling);

            if (decision.Readiness == BatchReadiness.Empty)
            {
                continue;
            }

            if (!decision.IsReady)
            {
                _logger.LogDebug("Пачка за {Period} ещё не готова: {Reason}.", period, decision.Reason);
                continue;
            }

            SubmissionRecord? previous = await _local
                .FindSubmissionAsync(period.ToString(), decision.Fingerprint, ct)
                .ConfigureAwait(false);

            // Повторная обработка запускается только если фотографии изменились либо
            // прошлая попытка сорвалась по устранимой причине. Иначе одна и та же
            // неудачная пачка крутилась бы в цикле, рассылая письма каждую минуту.
            if (previous is { Outcome: not SubmissionOutcome.Failed })
            {
                continue;
            }

            _logger.LogInformation(
                "Пачка за {Period} готова ({Reason}): фотографий {Count}, счётчиков без фото {Missing}.",
                period,
                decision.Reason,
                decision.Assignments.Count,
                decision.MissingMeters.Count);

            PipelineResult result = await _pipeline
                .ProcessPhotosAsync(period, decision, ConfirmationMode.Direct, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("Период {Period}: {Outcome}. {Summary}", period, result.Outcome, result.Summary);
        }
    }
}
