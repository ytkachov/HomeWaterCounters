using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.State;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.State;
using WaterCounters.Portal;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Tests;

/// <summary>
/// Проверяются критерии готовности из docs/recognition-service.md: режим проверки не
/// нажимает кнопку отправки, критическое замечание удерживает отправку даже без него,
/// повторный запуск не отправляет показания второй раз.
/// </summary>
public class ReadingPipelineTests
{
    [Fact]
    public async Task DryRunFillsTheFormButNeverPressesSend()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: true));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.DryRun, result.Outcome);
        Assert.Equal(1, harness.Portal.CallCount);
        Assert.Equal(0, harness.Portal.SubmitCount);
    }

    [Fact]
    public async Task RealRunSubmitsAndClosesThePeriod()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");
        await harness.SeedHistoryAsync(DesktopTestData.HistoryFor(DesktopTestData.ColdWater, 1200m, 1210m, 1220m));

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(1, harness.Portal.SubmitCount);
        Assert.Equal("W-1", harness.Portal.LastReadings[0].PortalId);

        ReadingHistory history = await harness.History.LoadAsync();
        Assert.True(history.IsClosed(DesktopTestData.Period));
        Assert.Equal(1234.567m, history.Latest(DesktopTestData.ColdWater.Key)!.Value);
    }

    [Fact]
    public async Task ClosedPeriodIsNotProcessedTwice()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);

        await harness.Pipeline.ProcessPhotosAsync(DesktopTestData.Period, batch, ConfirmationMode.Direct);
        PipelineResult second = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        // Признак повторной обработки — запись в history.json, ровно как в спецификации.
        Assert.Equal(SubmissionOutcome.AlreadySubmitted, second.Outcome);
        Assert.Equal(1, harness.Portal.SubmitCount);
    }

    [Fact]
    public async Task ReadingBelowPreviousHoldsSubmissionEvenWithDryRunOff()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        await harness.SeedHistoryAsync(DesktopTestData.HistoryFor(DesktopTestData.ColdWater, 1200m, 1300m, 1400m));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 999.000m, "12-345-678");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.HeldForReview, result.Outcome);
        Assert.Equal(0, harness.Portal.CallCount);
        Assert.Contains("меньше предыдущего", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerialMismatchHoldsSubmission()
    {
        // Опаснее неверной цифры: перепутанные местами счётчики делают неправильными
        // оба показания сразу, поэтому серийник сверяется даже при совпавшем имени файла.
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, serial: "98-765-432");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.HeldForReview, result.Outcome);
        Assert.Equal(0, harness.Portal.CallCount);
        Assert.Contains("не совпадает", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPhotoDoesNotBlockTheOtherMeters()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer
            .Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678")
            .Answer(DesktopTestData.HotWater, 890.123m, "98-765-432");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater, DesktopTestData.HotWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(2, harness.Portal.LastReadings.Count);

        // Счётчик без фотографии попадает в отчёт как требующий внимания.
        ReadingCandidate electricity = result.Readings.Single(r => r.Meter.Key == "electricity");
        Assert.False(electricity.HasValue);
        Assert.Contains("не найдена", electricity.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadablePhotoNeverBecomesAGuessedNumber()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, value: null);

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.HeldForReview, result.Outcome);
        Assert.Equal(0, harness.Portal.CallCount);
        Assert.All(result.Readings, r => Assert.False(r.HasValue));
    }

    [Fact]
    public async Task RecognitionFailureIsReportedNotThrown()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Throws = new RecognitionException("VLM-хост недоступен");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        // Устранимый сбой, а не «прочитать нельзя»: наблюдатель обязан попробовать
        // ещё раз, иначе выключенная на полчаса Ollama похоронила бы живую пачку.
        Assert.Equal(SubmissionOutcome.Failed, result.Outcome);
        Assert.Contains(result.Readings, r => r.Failure == "VLM-хост недоступен");

        SubmissionRecord? record = await harness.Local.FindSubmissionAsync(DesktopTestData.Period.ToString(), "fp-1");
        Assert.Equal(SubmissionOutcome.Failed, record!.Outcome);
    }

    [Fact]
    public async Task UnreadablePhotoIsNotTreatedAsARetryableFailure()
    {
        // Модель ответила, но цифры не читаются. Повторять нечего — снимок нужен новый,
        // и пачка обязана остаться закрытой до перезалива фотографий.
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, value: null);

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.HeldForReview, result.Outcome);
    }

    [Fact]
    public async Task PortalFailureIsRecordedAsRetryable()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");
        harness.Portal.Error = "кабинет не открылся";

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Failed, result.Outcome);

        // Период не закрыт: следующий проход обязан попробовать снова.
        ReadingHistory history = await harness.History.LoadAsync();
        Assert.False(history.IsClosed(DesktopTestData.Period));

        SubmissionRecord? record = await harness.Local.FindSubmissionAsync(DesktopTestData.Period.ToString(), "fp-1");
        Assert.Equal(SubmissionOutcome.Failed, record!.Outcome);
    }

    [Fact]
    public async Task MobileModeProposesInsteadOfSubmitting()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.AwaitMobile);

        Assert.Equal(SubmissionOutcome.AwaitingConfirmation, result.Outcome);
        Assert.Equal(0, harness.Portal.CallCount);

        MessageEnvelope proposal = Assert.Single(await harness.ToMobileAsync());
        Assert.Equal(MessageType.ReadingsProposed, proposal.Type);
        Assert.Equal(1234.567m, proposal.GetPayload<ReadingsProposedPayload>().Readings[0].Value);
    }

    [Fact]
    public async Task ConfirmedReadingsGoStraightToThePortal()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));

        PipelineResult result = await harness.Pipeline.SubmitConfirmedAsync(
            DesktopTestData.Period,
            [new ConfirmedReading { MeterKey = "cold-water", Value = 1234.567m, WasEdited = true }]);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(1, harness.Portal.SubmitCount);
    }

    [Fact]
    public async Task ForecastUsesHistoryAndIsMarkedAsSuch()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        await harness.SeedHistoryAsync(DesktopTestData.HistoryFor(DesktopTestData.ColdWater, 100m, 110m, 120m, 130m));

        harness.Settings.Current = harness.Settings.Current with { Meters = [DesktopTestData.ColdWater] };

        PipelineResult result = await harness.Pipeline.ProcessForecastAsync(
            DesktopTestData.Period, "срок прошёл", ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(140m, harness.Portal.LastReadings[0].Value);

        ReadingHistory history = await harness.History.LoadAsync();
        Assert.True(history.PeriodRecord(DesktopTestData.Period)!.WasForecast);
    }

    [Fact]
    public async Task ForecastWithoutHistoryAsksForManualEntryInsteadOfInventingNumbers()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Settings.Current = harness.Settings.Current with { Meters = [DesktopTestData.ColdWater] };

        PipelineResult result = await harness.Pipeline.ProcessForecastAsync(
            DesktopTestData.Period, "срок прошёл", ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.HeldForReview, result.Outcome);
        Assert.Equal(0, harness.Portal.CallCount);

        MessageEnvelope message = Assert.Single(
            await harness.ToMobileAsync(),
            m => m.Type == MessageType.ForecastUnavailable);

        Assert.Equal(["cold-water"], message.GetPayload<ForecastUnavailablePayload>().MeterKeys);
    }

    [Fact]
    public async Task EveryOutcomeSendsAReport()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");
        harness.Portal.Error = "кабинет не открылся";

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        await harness.Pipeline.ProcessPhotosAsync(DesktopTestData.Period, batch, ConfirmationMode.Direct);

        MailContent letter = Assert.Single(harness.Mailer.Sent);
        Assert.Contains("сбой", letter.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("кабинет не открылся", letter.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyClosedPortalPeriodIsNotSubmittedAgain()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678");
        harness.Portal.Status = SubmissionStatus.AlreadySubmitted;

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.AlreadySubmitted, result.Outcome);
        Assert.True((await harness.History.LoadAsync()).IsClosed(DesktopTestData.Period));
    }
}

/// <summary>
/// Замечание валидатора не превращает предупреждение в блокировку: решение остаётся
/// за человеком, задача валидатора — направить его взгляд.
/// </summary>
public class ValidationSeverityTests
{
    [Fact]
    public async Task WarningDoesNotHoldSubmission()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));

        // Потребление втрое выше обычного — предупреждение, но не запрет.
        await harness.SeedHistoryAsync(DesktopTestData.HistoryFor(DesktopTestData.ColdWater, 100m, 110m, 120m));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 400m, "12-345-678");

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Contains(
            result.Readings.Single(r => r.Meter.Key == "cold-water").Issues,
            i => i.Code == Core.Validation.ReadingValidator.CodeDeltaTooLarge);
    }

    [Fact]
    public async Task LowConfidenceIsAWarningNotAWall()
    {
        await using var harness = new PipelineHarness(DesktopTestData.Settings(dryRun: false));
        harness.Recognizer.Answer(DesktopTestData.ColdWater, 1234.567m, "12-345-678", confidence: 0.42);

        PhotoBatchDecision batch = await harness.UploadPhotosAsync(DesktopTestData.ColdWater);
        PipelineResult result = await harness.Pipeline.ProcessPhotosAsync(
            DesktopTestData.Period, batch, ConfirmationMode.Direct);

        Assert.Equal(SubmissionOutcome.Submitted, result.Outcome);
        Assert.Contains(
            result.Readings.Single(r => r.Meter.Key == "cold-water").Issues,
            i => i.Code == Core.Validation.ReadingValidator.CodeLowConfidence);
    }
}
