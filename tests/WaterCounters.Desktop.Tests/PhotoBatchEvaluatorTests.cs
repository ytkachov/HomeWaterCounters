using Microsoft.Extensions.Time.Testing;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Photos;

namespace WaterCounters.Desktop.Tests;

public class PhotoBatchEvaluatorTests
{
    private static readonly TimeSpan Settling = TimeSpan.FromMinutes(3);

    private readonly FakeTimeProvider _clock = new(DesktopTestData.Now);
    private readonly PhotoBatchEvaluator _evaluator;

    public PhotoBatchEvaluatorTests() => _evaluator = new PhotoBatchEvaluator(_clock);

    [Fact]
    public void EmptyFolderIsNotABatch()
    {
        PhotoBatchDecision decision = _evaluator.Evaluate([], DesktopTestData.Meters, Settling);

        Assert.Equal(BatchReadiness.Empty, decision.Readiness);
    }

    [Fact]
    public void AllMetersPhotographedIsReadyImmediately()
    {
        // Нормальный случай: реакция почти мгновенная, ждать нечего.
        PhotoBatchDecision decision = _evaluator.Evaluate(
            Photos(_clock.GetUtcNow(), "cold-water.jpg", "hot-water.jpg", "electricity.jpg"),
            DesktopTestData.Meters,
            Settling);

        Assert.True(decision.IsReady);
        Assert.Equal(3, decision.Assignments.Count);
        Assert.Empty(decision.MissingMeters);
        Assert.Contains("все настроенные счётчики", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteBatchWaitsForTheSettlingWindow()
    {
        // Ручная загрузка идёт файл за файлом. Обработать сейчас — значит потерять
        // счётчики, которые ещё дозагружаются.
        PhotoBatchDecision decision = _evaluator.Evaluate(
            Photos(_clock.GetUtcNow(), "cold-water.jpg"),
            DesktopTestData.Meters,
            Settling);

        Assert.Equal(BatchReadiness.Waiting, decision.Readiness);
        Assert.Equal(2, decision.MissingMeters.Count);
    }

    [Fact]
    public void IncompleteBatchBecomesReadyOnceTheFolderGoesQuiet()
    {
        DateTimeOffset uploaded = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(4));

        PhotoBatchDecision decision = _evaluator.Evaluate(
            Photos(uploaded, "cold-water.jpg"),
            DesktopTestData.Meters,
            Settling);

        // «Сняли не все счётчики» или «файл не долетел»: пачка обрабатывается,
        // недостающие счётчики уходят в письмо.
        Assert.True(decision.IsReady);
        Assert.Contains("не пополнялась", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(2, decision.MissingMeters.Count);
    }

    [Fact]
    public void SettlingCountsFromTheNewestFileNotTheOldest()
    {
        DateTimeOffset first = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(4));
        DateTimeOffset second = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(1));

        PhotoBatchDecision decision = _evaluator.Evaluate(
            [Photo("cold-water.jpg", first), Photo("hot-water.jpg", second)],
            DesktopTestData.Meters,
            Settling);

        Assert.Equal(BatchReadiness.Waiting, decision.Readiness);
    }

    [Fact]
    public void UnrecognizedFileNamesAreReportedRatherThanGuessed()
    {
        _clock.Advance(TimeSpan.FromMinutes(10));

        PhotoBatchDecision decision = _evaluator.Evaluate(
            Photos(DesktopTestData.Now, "IMG_1234.jpg", "cold-water.jpg"),
            DesktopTestData.Meters,
            Settling);

        Assert.True(decision.IsReady);
        Assert.Single(decision.Assignments);
        Assert.Single(decision.Unassigned);
        Assert.Equal("/photos/2026-07/IMG_1234.jpg", decision.Unassigned[0].Path);
    }

    [Fact]
    public void NonPhotoFilesAreIgnored()
    {
        PhotoBatchDecision decision = _evaluator.Evaluate(
            Photos(_clock.GetUtcNow(), "notes.txt", "thumbs.db"),
            DesktopTestData.Meters,
            Settling);

        Assert.Equal(BatchReadiness.Empty, decision.Readiness);
    }

    [Fact]
    public void FingerprintChangesOnlyWhenPhotosDo()
    {
        RemoteEntry[] first = Photos(_clock.GetUtcNow(), "cold-water.jpg", "hot-water.jpg", "electricity.jpg");

        string before = _evaluator.Evaluate(first, DesktopTestData.Meters, Settling).Fingerprint;

        _clock.Advance(TimeSpan.FromHours(3));
        string unchanged = _evaluator.Evaluate(first, DesktopTestData.Meters, Settling).Fingerprint;

        RemoteEntry[] reuploaded = [.. first.Select(e =>
            e.Path.EndsWith("cold-water.jpg", StringComparison.Ordinal) ? e with { Revision = "rev-2" } : e)];

        string after = _evaluator.Evaluate(reuploaded, DesktopTestData.Meters, Settling).Fingerprint;

        // Отпечаток держит защиту от бесконечной переобработки: простое ожидание его
        // не меняет, а перезалитая фотография — меняет.
        Assert.Equal(before, unchanged);
        Assert.NotEqual(before, after);
    }

    private static RemoteEntry[] Photos(DateTimeOffset modified, params string[] names) =>
        [.. names.Select(name => Photo(name, modified))];

    private static RemoteEntry Photo(string name, DateTimeOffset modified) => new()
    {
        Path = $"/photos/2026-07/{name}",
        Size = 1024,
        ModifiedUtc = modified,
        Revision = "rev-1",
    };
}

public class MeterMatcherTests
{
    [Theory]
    [InlineData("/photos/2026-07/cold-water.jpg", "cold-water")]
    [InlineData("/photos/2026-07/hot-water.jpeg", "hot-water")]
    [InlineData("/photos/2026-07/electricity.png", "electricity")]
    [InlineData("/photos/2026-07/Cold-Water.JPG", "cold-water")]
    [InlineData("/photos/2026-07/cold_water.jpg", "cold-water")]
    [InlineData("/photos/2026-07/cold-water-2.jpg", "cold-water")]
    [InlineData("/photos/2026-07/cold-water (1).jpg", "cold-water")]
    public void MatchesByFileName(string path, string expected)
    {
        Assert.True(MeterMatcher.TryMatchByFileName(path, DesktopTestData.Meters, out MeterSpec meter));
        Assert.Equal(expected, meter.Key);
    }

    [Theory]
    [InlineData("/photos/2026-07/IMG_1234.jpg")]
    [InlineData("/photos/2026-07/water.jpg")]
    [InlineData("/photos/2026-07/gas.jpg")]
    public void LeavesUnknownNamesUnmatched(string path) =>
        Assert.False(MeterMatcher.TryMatchByFileName(path, DesktopTestData.Meters, out _));
}
