using Microsoft.Extensions.Time.Testing;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;

namespace WaterCounters.Core.Tests;

public class MessageQueueTests
{
    private static readonly PeriodKey Period = new(2026, 7);

    private readonly InMemoryRemoteStore _store = new();
    private readonly QueueLayout _layout = new();
    private readonly FakeTimeProvider _clock = new(TestData.Epoch);
    private readonly MessageQueue _queue;

    public MessageQueueTests() => _queue = new MessageQueue(_store, _layout, _clock);

    [Fact]
    public async Task Publish_PlacesMessageInDirectionFolder()
    {
        await PublishForecastAsync();
        await PublishProposalAsync();

        IReadOnlyList<RemoteEntry> toDesktop = await _queue.ListPendingAsync(QueueDirection.ToDesktop);
        IReadOnlyList<RemoteEntry> toMobile = await _queue.ListPendingAsync(QueueDirection.ToMobile);

        Assert.Single(toDesktop);
        Assert.Single(toMobile);
        Assert.StartsWith("/queue/to-desktop/", toDesktop[0].Path, StringComparison.Ordinal);
        Assert.StartsWith("/queue/to-mobile/", toMobile[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_MovesToProcessingAndDecodes()
    {
        MessageEnvelope published = (await PublishForecastAsync())!;
        RemoteEntry pending = (await _queue.ListPendingAsync(QueueDirection.ToDesktop))[0];

        ClaimResult claim = await _queue.TryClaimAsync(pending.Path);

        Assert.Equal(ClaimOutcome.Claimed, claim.Outcome);
        Assert.Equal(published.MessageId, claim.Envelope!.MessageId);
        Assert.Equal("deadline+5", claim.Envelope.GetPayload<SubmitForecastPayload>().Reason);
        Assert.False(await _store.ExistsAsync(pending.Path));
        Assert.True(await _store.ExistsAsync(claim.ProcessingPath!));
    }

    [Fact]
    public async Task Claim_SecondAttemptReportsTakenByOther()
    {
        await PublishForecastAsync();
        RemoteEntry pending = (await _queue.ListPendingAsync(QueueDirection.ToDesktop))[0];

        Assert.Equal(ClaimOutcome.Claimed, (await _queue.TryClaimAsync(pending.Path)).Outcome);
        Assert.Equal(ClaimOutcome.TakenByOther, (await _queue.TryClaimAsync(pending.Path)).Outcome);
    }

    [Fact]
    public async Task Complete_ArchivesUnderPeriod()
    {
        MessageEnvelope published = (await PublishForecastAsync())!;
        ClaimResult claim = await ClaimFirstAsync();

        await _queue.CompleteAsync(claim.Envelope!, claim.ProcessingPath!);

        Assert.False(await _store.ExistsAsync(claim.ProcessingPath!));
        Assert.True(await _store.ExistsAsync($"/queue/done/2026-07/{published.FileName}"));
        Assert.Empty(await _store.ListAsync(_layout.ProcessingFolder));
    }

    [Fact]
    public async Task Fail_MovesToFailedWithErrorNote()
    {
        MessageEnvelope published = (await PublishForecastAsync())!;
        ClaimResult claim = await ClaimFirstAsync();

        await _queue.FailAsync(claim.ProcessingPath!, "портал недоступен");

        string failedPath = $"/queue/failed/{published.FileName}";
        Assert.True(await _store.ExistsAsync(failedPath));

        string note = System.Text.Encoding.UTF8.GetString(await _store.DownloadAsync(failedPath + ".error.txt"));
        Assert.Contains("портал недоступен", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WithDeterministicId_DeduplicatesAcrossEveryStage()
    {
        // Прогноз за период порождают и телефон, и watchdog десктопа. Дубль должен
        // схлопываться независимо от того, на какой стадии находится первая задача.
        string id = MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, Period);

        Assert.NotNull(await PublishForecastAsync(id));
        Assert.Null(await PublishForecastAsync(id));

        ClaimResult claim = await ClaimFirstAsync();
        Assert.Null(await PublishForecastAsync(id));

        await _queue.CompleteAsync(claim.Envelope!, claim.ProcessingPath!);
        Assert.Null(await PublishForecastAsync(id));

        Assert.Empty(await _queue.ListPendingAsync(QueueDirection.ToDesktop));
    }

    [Fact]
    public async Task Publish_WithoutExplicitId_AlwaysCreatesNewMessage()
    {
        Assert.NotNull(await PublishForecastAsync());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await PublishForecastAsync());

        Assert.Equal(2, (await _queue.ListPendingAsync(QueueDirection.ToDesktop)).Count);
    }

    [Fact]
    public async Task Claim_PoisonedMessage_QuarantinesInsteadOfBlockingQueue()
    {
        // Битый файл не должен вечно всплывать в очереди и мешать остальным.
        await _store.UploadAsync("/queue/to-desktop/broken.json", "{ это не конверт "u8.ToArray());

        ClaimResult claim = await _queue.TryClaimAsync("/queue/to-desktop/broken.json");

        Assert.Equal(ClaimOutcome.Poisoned, claim.Outcome);
        Assert.NotNull(claim.Error);
        Assert.True(await _store.ExistsAsync("/queue/failed/broken.json"));
        Assert.True(await _store.ExistsAsync("/queue/failed/broken.json.error.txt"));
        Assert.Empty(await _queue.ListPendingAsync(QueueDirection.ToDesktop));
        Assert.Empty(await _store.ListAsync(_layout.ProcessingFolder));
    }

    [Fact]
    public async Task RecoverAbandoned_ReturnsMessagesLeftInProcessing()
    {
        // Имитация падения процесса ровно между claim и complete.
        MessageEnvelope published = (await PublishForecastAsync())!;
        ClaimResult claim = await ClaimFirstAsync();

        IReadOnlyList<ClaimResult> recovered = await _queue.RecoverAbandonedAsync();

        Assert.Single(recovered);
        Assert.Equal(ClaimOutcome.Claimed, recovered[0].Outcome);
        Assert.Equal(published.MessageId, recovered[0].Envelope!.MessageId);
        Assert.Equal(claim.ProcessingPath, recovered[0].ProcessingPath);
    }

    [Fact]
    public async Task RecoverAbandoned_QuarantinesUnreadableLeftovers()
    {
        await _store.UploadAsync("/queue/processing/junk.json", "мусор"u8.ToArray());

        IReadOnlyList<ClaimResult> recovered = await _queue.RecoverAbandonedAsync();

        Assert.Single(recovered);
        Assert.Equal(ClaimOutcome.Poisoned, recovered[0].Outcome);
        Assert.True(await _store.ExistsAsync("/queue/failed/junk.json"));
    }

    [Fact]
    public async Task Complete_IsIdempotent()
    {
        // Повторный вызов после перезапуска не должен падать — путь уже пуст.
        MessageEnvelope published = (await PublishForecastAsync())!;
        ClaimResult claim = await ClaimFirstAsync();

        await _queue.CompleteAsync(claim.Envelope!, claim.ProcessingPath!);
        await _queue.CompleteAsync(claim.Envelope!, claim.ProcessingPath!);

        Assert.True(await _store.ExistsAsync($"/queue/done/2026-07/{published.FileName}"));
    }

    [Fact]
    public async Task ListPending_IgnoresNonJsonFiles()
    {
        await PublishForecastAsync();
        await _store.UploadAsync("/queue/to-desktop/readme.txt", "заметка"u8.ToArray());

        Assert.Single(await _queue.ListPendingAsync(QueueDirection.ToDesktop));
    }

    private Task<MessageEnvelope?> PublishForecastAsync(string? messageId = null) =>
        _queue.PublishAsync(
            MessageType.SubmitForecast,
            Period,
            "pixel-8",
            new SubmitForecastPayload { Reason = "deadline+5" },
            messageId);

    private Task<MessageEnvelope?> PublishProposalAsync() =>
        _queue.PublishAsync(
            MessageType.ReadingsProposed,
            Period,
            "desktop",
            new ReadingsProposedPayload
            {
                ProposalId = "p1",
                SourceMessageId = "m1",
                IsForecast = false,
                Readings = [],
            });

    private async Task<ClaimResult> ClaimFirstAsync()
    {
        RemoteEntry pending = (await _queue.ListPendingAsync(QueueDirection.ToDesktop))[0];
        return await _queue.TryClaimAsync(pending.Path);
    }
}
