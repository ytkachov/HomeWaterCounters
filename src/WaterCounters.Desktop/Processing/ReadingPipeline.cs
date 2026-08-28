using System.IO;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Forecasting;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.State;
using WaterCounters.Core.Storage;
using WaterCounters.Core.Validation;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.State;
using WaterCounters.Portal;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Processing;

/// <summary>
/// Путь от фотографии до кабинета: распознавание, сверка серийников, валидация,
/// решение об отправке, запись истории, письмо и сообщение на телефон.
///
/// Три вещи здесь неслучайны и проверяются тестами:
/// критическое замечание валидатора удерживает отправку независимо от режима проверки;
/// период, уже закрытый в истории, повторно не обрабатывается;
/// при <c>dryRun</c> кнопка отправки в кабинете не нажимается вовсе.
/// </summary>
public sealed class ReadingPipeline(
    ISettingsProvider settings,
    IMeterRecognizer recognizer,
    IRemoteStore store,
    MessageQueue queue,
    ReadingHistoryStore history,
    IPortalGateway portal,
    IMailer mailer,
    ILocalState local,
    DesktopOptions options,
    ILogger<ReadingPipeline> logger,
    TimeProvider? clock = null)
{
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IMeterRecognizer _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly MessageQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly ReadingHistoryStore _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly IPortalGateway _portal = portal ?? throw new ArgumentNullException(nameof(portal));
    private readonly IMailer _mailer = mailer ?? throw new ArgumentNullException(nameof(mailer));
    private readonly ILocalState _local = local ?? throw new ArgumentNullException(nameof(local));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<ReadingPipeline> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Распознаёт пачку фотографий и доводит период до конца.</summary>
    public async Task<PipelineResult> ProcessPhotosAsync(
        PeriodKey period,
        PhotoBatchDecision batch,
        ConfirmationMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        AppSettings current = _settings.Current;
        ReadingHistory known = await _history.LoadAsync(ct).ConfigureAwait(false);

        if (known.IsClosed(period))
        {
            _logger.LogInformation("Период {Period} уже закрыт в истории — обработка не запускается.", period);
            return Skipped(period, "период уже закрыт в истории");
        }

        List<ReadingCandidate> candidates = await RecognizeAsync(period, batch, current, known, ct).ConfigureAwait(false);

        foreach (MeterSpec missing in batch.MissingMeters.Where(m => candidates.All(c => c.Meter.Key != m.Key)))
        {
            candidates.Add(new ReadingCandidate
            {
                Meter = missing,
                Source = ReadingSource.Recognized,
                Failure = "фотография счётчика не найдена",
            });
        }

        return await FinishAsync(period, candidates, batch, isForecast: false, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Считает прогноз, когда фотографий уже не будет, и доводит период до конца.</summary>
    public async Task<PipelineResult> ProcessForecastAsync(
        PeriodKey period,
        string reason,
        ConfirmationMode mode,
        CancellationToken ct = default)
    {
        AppSettings current = _settings.Current;
        ReadingHistory known = await _history.LoadAsync(ct).ConfigureAwait(false);

        if (known.IsClosed(period))
        {
            return Skipped(period, "период уже закрыт в истории");
        }

        var forecaster = new ConsumptionForecaster();
        List<ReadingCandidate> candidates = [];
        List<string> unavailable = [];

        foreach (MeterSpec meter in current.OrderedMeters)
        {
            if (forecaster.TryForecast(meter, known.Readings, period, out ForecastResult? forecast, out ForecastFailure? failure))
            {
                candidates.Add(Validate(
                    new ReadingCandidate
                    {
                        Meter = meter,
                        Value = forecast!.PredictedValue,
                        Source = ReadingSource.Forecast,
                        PreviousValue = forecast.PreviousValue,
                        Delta = forecast.PredictedDelta,
                        Warnings = [$"прогноз ({forecast.Method}, выборка {forecast.SampleSize})", .. forecast.Notes],
                    },
                    known,
                    current));
            }
            else
            {
                // Истории мало — числа не выдумываем. Счётчик уходит в письмо и в
                // сообщение телефону как требующий ручного ввода.
                unavailable.Add(meter.Key);

                candidates.Add(new ReadingCandidate
                {
                    Meter = meter,
                    Source = ReadingSource.Forecast,
                    Failure = failure!.Reason,
                });
            }
        }

        if (unavailable.Count > 0)
        {
            await _queue.PublishAsync(
                MessageType.ForecastUnavailable,
                period,
                _options.DeviceId,
                new ForecastUnavailablePayload { Reason = reason, MeterKeys = unavailable },
                ct: ct).ConfigureAwait(false);
        }

        return await FinishAsync(period, candidates, batch: null, isForecast: true, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Отправляет то, что человек подтвердил на телефоне.</summary>
    public async Task<PipelineResult> SubmitConfirmedAsync(
        PeriodKey period,
        IReadOnlyList<ConfirmedReading> confirmed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        AppSettings current = _settings.Current;
        ReadingHistory known = await _history.LoadAsync(ct).ConfigureAwait(false);

        if (known.IsClosed(period))
        {
            return Skipped(period, "период уже закрыт в истории");
        }

        List<ReadingCandidate> candidates = [];

        foreach (ConfirmedReading reading in confirmed)
        {
            MeterSpec? meter = current.MeterByKey(reading.MeterKey);

            if (meter is null)
            {
                _logger.LogWarning("Подтверждено показание неизвестного счётчика {MeterKey}.", reading.MeterKey);
                continue;
            }

            candidates.Add(Validate(
                new ReadingCandidate
                {
                    Meter = meter,
                    Value = reading.Value,
                    Source = ReadingSource.Manual,
                    Warnings = reading.WasEdited ? ["значение исправлено вручную на телефоне"] : [],
                },
                known,
                current));
        }

        return await FinishAsync(period, candidates, batch: null, isForecast: false, ConfirmationMode.Direct, ct)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Распознавание
    // -----------------------------------------------------------------------

    private async Task<List<ReadingCandidate>> RecognizeAsync(
        PeriodKey period,
        PhotoBatchDecision batch,
        AppSettings current,
        ReadingHistory known,
        CancellationToken ct)
    {
        List<ReadingCandidate> candidates = [];

        foreach (PhotoAssignment assignment in batch.Assignments)
        {
            candidates.Add(await RecognizeOneAsync(period, assignment.Meter, assignment.Entry, current, known, ct)
                .ConfigureAwait(false));
        }

        await MatchLeftoversBySerialAsync(period, batch, current, known, candidates, ct).ConfigureAwait(false);
        return candidates;
    }

    /// <summary>
    /// Второй способ сопоставления: серийный номер, прочитанный на фото. Спасает,
    /// когда файлы называются IMG_1234.jpg. Пробный проход делается по разрядности
    /// первого нераспределённого счётчика — серийник от неё не зависит, а если
    /// счётчик опознан другой, снимок перечитывается уже с его разрядностью.
    /// </summary>
    private async Task MatchLeftoversBySerialAsync(
        PeriodKey period,
        PhotoBatchDecision batch,
        AppSettings current,
        ReadingHistory known,
        List<ReadingCandidate> candidates,
        CancellationToken ct)
    {
        List<MeterSpec> free =
        [
            .. batch.MissingMeters.Where(m => !string.IsNullOrWhiteSpace(m.SerialNumber))
        ];

        foreach (RemoteEntry photo in batch.Unassigned)
        {
            if (free.Count == 0)
            {
                break;
            }

            ReadingCandidate probe = await RecognizeOneAsync(period, free[0], photo, current, known, ct)
                .ConfigureAwait(false);

            // Точное совпадение проверяется первым: при вхождении подряд идущих цифр
            // теоретически может подойти не тот счётчик, и уступать ему точному нельзя.
            MeterSpec? matched =
                free.FirstOrDefault(m => SerialNumber.IsExact(m.SerialNumber, probe.RecognizedSerial))
                ?? free.FirstOrDefault(m => SerialNumber.Matches(m.SerialNumber, probe.RecognizedSerial));

            if (matched is null)
            {
                candidates.Add(probe with
                {
                    Value = null,
                    Failure = probe.RecognizedSerial is { } serial
                        ? $"серийный номер {serial} с файла {RemotePath.GetFileName(photo.Path)} не совпал ни с одним счётчиком"
                        : $"файл {RemotePath.GetFileName(photo.Path)} не привязан к счётчику: ни имя, ни серийный номер не подошли",
                });

                continue;
            }

            ReadingCandidate resolved = matched.Key == free[0].Key
                ? probe
                : await RecognizeOneAsync(period, matched, photo, current, known, ct).ConfigureAwait(false);

            candidates.Add(resolved with
            {
                Warnings = [.. resolved.Warnings, $"счётчик определён по серийному номеру, а не по имени файла"],
            });

            free.RemoveAll(m => m.Key == matched.Key);
        }
    }

    private async Task<ReadingCandidate> RecognizeOneAsync(
        PeriodKey period,
        MeterSpec meter,
        RemoteEntry photo,
        AppSettings current,
        ReadingHistory known,
        CancellationToken ct)
    {
        var candidate = new ReadingCandidate
        {
            Meter = meter,
            Source = ReadingSource.Recognized,
            PhotoPath = photo.Path,
        };

        byte[] jpeg;

        try
        {
            jpeg = await _store.DownloadAsync(photo.Path, ct).ConfigureAwait(false);
        }
        catch (RemoteStoreException ex)
        {
            return candidate with { Failure = $"фотография не скачалась: {ex.Message}", IsTransientFailure = true };
        }

        RecognitionResult result;

        try
        {
            result = await _recognizer.RecognizeAsync(meter, jpeg, ct).ConfigureAwait(false);
        }
        catch (RecognitionException ex)
        {
            _logger.LogError(ex, "Распознавание {MeterKey} за {Period} не удалось.", meter.Key, period);
            return candidate with { Failure = ex.Message, IsTransientFailure = true };
        }

        await _local.RecordRecognitionAsync(
            new RecognitionRun
            {
                Period = period.ToString(),
                MeterKey = meter.Key,
                PhotoPath = photo.Path,
                Model = current.Recognition.Model,
                Value = result.Value,
                Serial = result.Serial,
                Confidence = result.Confidence,
                ElapsedMs = result.ElapsedMs,
                Warnings = string.Join(" | ", result.Warnings),
                CreatedUtc = _clock.GetUtcNow(),
            },
            ct).ConfigureAwait(false);

        candidate = candidate with
        {
            Value = result.Value,
            RecognizedSerial = result.Serial,
            Confidence = result.Confidence,
            Crop = result.Crop,
            Warnings = result.Warnings,
            Failure = result.Value is null ? "показание не прочитано" : null,
        };

        return Validate(candidate, known, current);
    }

    /// <summary>
    /// Прогоняет показание через валидатор Core. Серийник сверяется всегда, даже когда
    /// счётчик определён по имени файла: несовпадение почти наверняка означает, что
    /// счётчики сняты не в том порядке, а это опаснее неверной цифры — неправильными
    /// окажутся оба показания сразу.
    /// </summary>
    private static ReadingCandidate Validate(ReadingCandidate candidate, ReadingHistory known, AppSettings current)
    {
        if (candidate.Value is not { } value)
        {
            return candidate;
        }

        var validator = new ReadingValidator(new ValidationOptions
        {
            MinConfidence = current.Recognition.MinConfidence,
        });

        IReadOnlyList<ValidationIssue> issues = validator.Validate(
            candidate.Meter,
            value,
            known.Readings,
            candidate.RecognizedSerial,
            candidate.Confidence);

        MeterReading? previous = known.Latest(candidate.Meter.Key);

        return candidate with
        {
            Issues = issues,
            PreviousValue = candidate.PreviousValue ?? previous?.Value,
            Delta = candidate.Delta ?? (previous is null ? null : value - previous.Value),
        };
    }

    // -----------------------------------------------------------------------
    // Решение и завершение
    // -----------------------------------------------------------------------

    private async Task<PipelineResult> FinishAsync(
        PeriodKey period,
        List<ReadingCandidate> candidates,
        PhotoBatchDecision? batch,
        bool isForecast,
        ConfirmationMode mode,
        CancellationToken ct)
    {
        AppSettings current = _settings.Current;
        string fingerprint = batch?.Fingerprint ?? string.Empty;

        if (mode == ConfirmationMode.AwaitMobile)
        {
            await ProposeAsync(period, candidates, isForecast, current, ct).ConfigureAwait(false);

            PipelineResult proposed = new()
            {
                Outcome = SubmissionOutcome.AwaitingConfirmation,
                Summary = "показания отправлены на подтверждение на телефон",
                Readings = candidates,
            };

            await CompleteAsync(period, proposed, fingerprint, batch, ct).ConfigureAwait(false);
            return proposed;
        }

        List<ReadingCandidate> ready = [.. candidates.Where(static c => c.HasValue)];
        List<ReadingCandidate> critical = [.. ready.Where(static c => c.HasCriticalIssue)];

        if (critical.Count > 0)
        {
            // Отправка удерживается независимо от dryRun: критическое замечание
            // означает, что показание почти наверняка неверно, а отправка необратима.
            PipelineResult held = new()
            {
                Outcome = SubmissionOutcome.HeldForReview,
                Summary = "отправка удержана: " + string.Join("; ", critical.SelectMany(
                    c => c.Issues
                        .Where(static i => i.Severity == ValidationSeverity.Critical)
                        .Select(i => $"{c.Meter.DisplayName} — {i.Message}"))),
                Readings = candidates,
            };

            _logger.LogWarning("Период {Period}: {Summary}", period, held.Summary);
            await CompleteAsync(period, held, fingerprint, batch, ct).ConfigureAwait(false);
            return held;
        }

        if (ready.Count == 0)
        {
            // Устранимый сбой помечается как Failed, а не HeldForReview: наблюдатель
            // папки повторяет только Failed, и недоступная модель не должна навсегда
            // закрыть пачку, которую он в состоянии обработать через пять минут.
            bool retryable = candidates.Any(static c => c.IsTransientFailure);

            PipelineResult empty = new()
            {
                Outcome = retryable ? SubmissionOutcome.Failed : SubmissionOutcome.HeldForReview,
                Summary = retryable
                    ? "показания не получены из-за сбоя: " +
                      string.Join("; ", candidates.Where(static c => c.IsTransientFailure).Select(static c => c.Failure))
                    : "ни одного показания получить не удалось",
                Readings = candidates,
            };

            await CompleteAsync(period, empty, fingerprint, batch, ct).ConfigureAwait(false);
            return empty;
        }

        bool dryRun = current.Portal.DryRun;

        PortalOutcome outcome = await _portal.SubmitAsync(
            period,
            [.. ready.Select(c => new PortalReading
            {
                MeterKey = c.Meter.Key,
                PortalId = c.Meter.PortalId ?? c.Meter.SerialNumber ?? c.Meter.Key,
                Value = c.Value!.Value,
            })],
            dryRun,
            ct).ConfigureAwait(false);

        PipelineResult result = new()
        {
            Outcome = ToOutcome(outcome, dryRun),
            Summary = outcome.Error ?? outcome.Message ?? outcome.Status.ToString(),
            Readings = candidates,
            WasDryRun = dryRun,
        };

        await CompleteAsync(period, result, fingerprint, batch, ct, outcome).ConfigureAwait(false);
        return result;
    }

    private static SubmissionOutcome ToOutcome(PortalOutcome outcome, bool dryRun)
    {
        if (!outcome.Succeeded)
        {
            return SubmissionOutcome.Failed;
        }

        return outcome.Status switch
        {
            SubmissionStatus.Submitted => SubmissionOutcome.Submitted,
            SubmissionStatus.AlreadySubmitted => SubmissionOutcome.AlreadySubmitted,
            _ => dryRun ? SubmissionOutcome.DryRun : SubmissionOutcome.Failed,
        };
    }

    /// <summary>Запись истории, локального журнала, сообщения телефону и письма.</summary>
    private async Task CompleteAsync(
        PeriodKey period,
        PipelineResult result,
        string fingerprint,
        PhotoBatchDecision? batch,
        CancellationToken ct,
        PortalOutcome? portal = null)
    {
        bool closes = result.Outcome is SubmissionOutcome.Submitted or SubmissionOutcome.AlreadySubmitted;
        bool records = closes || result.Outcome == SubmissionOutcome.DryRun;

        if (records)
        {
            List<MeterReading> readings =
            [
                .. result.Readings.Where(static c => c.HasValue).Select(c => new MeterReading
                {
                    MeterKey = c.Meter.Key,
                    Period = period,
                    Value = c.Value!.Value,
                    Source = c.Source,
                    RecognizedSerial = c.RecognizedSerial,
                    Confidence = c.Confidence,
                    PhotoPath = c.PhotoPath,
                    CapturedUtc = _clock.GetUtcNow(),
                }),
            ];

            await _history.AppendAsync(
                readings,
                new SubmittedPeriod
                {
                    Period = period,
                    SubmittedUtc = _clock.GetUtcNow(),

                    // Режим проверки закрытием не считается: показания в кабинет не
                    // ушли, и период обязан обработаться заново, когда dryRun снимут.
                    WasDryRun = !closes,
                    WasForecast = result.Readings.Any(static c => c.Source == ReadingSource.Forecast),
                    Note = result.Summary,
                },
                ct).ConfigureAwait(false);

            await _local.RecordReadingsAsync(period, readings, ct).ConfigureAwait(false);
        }

        await _local.RecordSubmissionAsync(
            new SubmissionRecord
            {
                Period = period.ToString(),
                Fingerprint = fingerprint,
                Outcome = result.Outcome,
                CreatedUtc = _clock.GetUtcNow(),
                Note = result.Summary,
            },
            ct).ConfigureAwait(false);

        await NotifyMobileAsync(period, result, portal, ct).ConfigureAwait(false);

        await _mailer.SendAsync(
            ReportComposer.Compose(period, result, batch, portal, _settings.Current),
            ct).ConfigureAwait(false);
    }

    private async Task NotifyMobileAsync(
        PeriodKey period,
        PipelineResult result,
        PortalOutcome? portal,
        CancellationToken ct)
    {
        if (result.Outcome == SubmissionOutcome.AwaitingConfirmation)
        {
            return;
        }

        if (result.Outcome is SubmissionOutcome.Submitted or SubmissionOutcome.DryRun or SubmissionOutcome.AlreadySubmitted)
        {
            await _queue.PublishAsync(
                MessageType.SubmissionSucceeded,
                period,
                _options.DeviceId,
                new SubmissionSucceededPayload
                {
                    Readings =
                    [
                        .. result.Readings.Where(static c => c.HasValue).Select(c => new MeterReading
                        {
                            MeterKey = c.Meter.Key,
                            Period = period,
                            Value = c.Value!.Value,
                            Source = c.Source,
                            Confidence = c.Confidence,
                        }),
                    ],
                    WasDryRun = result.WasDryRun,
                },
                ct: ct).ConfigureAwait(false);

            return;
        }

        await _queue.PublishAsync(
            MessageType.SubmissionFailed,
            period,
            _options.DeviceId,
            new SubmissionFailedPayload
            {
                Error = result.Summary,
                TracePath = portal?.TracePath,
                AttemptCount = 1,
            },
            ct: ct).ConfigureAwait(false);
    }

    private async Task ProposeAsync(
        PeriodKey period,
        List<ReadingCandidate> candidates,
        bool isForecast,
        AppSettings current,
        CancellationToken ct)
    {
        List<ProposedReading> proposals = [];

        foreach (ReadingCandidate candidate in candidates.Where(static c => c.HasValue))
        {
            string? cropPath = current.Recognition.UploadCrops && candidate.Crop is { Length: > 0 } crop
                ? await UploadCropAsync(period, candidate.Meter.Key, crop, ct).ConfigureAwait(false)
                : null;

            proposals.Add(new ProposedReading
            {
                MeterKey = candidate.Meter.Key,
                Value = candidate.Value!.Value,
                RecognizedSerial = candidate.RecognizedSerial,
                Confidence = candidate.Confidence,
                CropPath = cropPath,
                PreviousValue = candidate.PreviousValue,
                Delta = candidate.Delta,
                Warnings = [.. candidate.Warnings, .. candidate.Issues.Select(static i => i.Message)],
            });
        }

        string proposalId = MessageCodec.NewMessageId(_clock.GetUtcNow());

        await _queue.PublishAsync(
            MessageType.ReadingsProposed,
            period,
            _options.DeviceId,
            new ReadingsProposedPayload
            {
                ProposalId = proposalId,
                SourceMessageId = proposalId,
                IsForecast = isForecast,
                Readings = proposals,
            },
            ct: ct).ConfigureAwait(false);
    }

    private async Task<string?> UploadCropAsync(PeriodKey period, string meterKey, byte[] crop, CancellationToken ct)
    {
        string path = RemotePath.Combine(_queue.Layout.PhotosFolderFor(period), "crops", $"{meterKey}.jpg");

        try
        {
            await _store.UploadAsync(path, crop, RemoteWriteMode.Overwrite, ct).ConfigureAwait(false);
            return path;
        }
        catch (RemoteStoreException ex)
        {
            // Кроп — удобство, а не показание: его потеря не повод срывать обработку.
            _logger.LogWarning(ex, "Кроп {MeterKey} за {Period} не загрузился.", meterKey, period);
            return null;
        }
    }

    private PipelineResult Skipped(PeriodKey period, string reason)
    {
        _logger.LogInformation("Период {Period}: {Reason}.", period, reason);

        return new PipelineResult
        {
            Outcome = SubmissionOutcome.AlreadySubmitted,
            Summary = reason,
        };
    }
}
