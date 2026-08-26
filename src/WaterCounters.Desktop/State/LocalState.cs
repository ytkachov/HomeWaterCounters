using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaterCounters.Core.Metering;

namespace WaterCounters.Desktop.State;

/// <summary>
/// Локальный журнал обработчика.
///
/// Источник истины для показаний — <c>/state/history.json</c> в Dropbox; здесь лежит
/// то, что нужно самому обработчику и не имеет смысла для телефона: какие сообщения
/// уже обработаны, чем кончилась попытка по каждой пачке фотографий и что именно
/// отвечала модель.
/// </summary>
public interface ILocalState
{
    Task<bool> WasProcessedAsync(string messageId, CancellationToken ct = default);

    Task MarkProcessedAsync(ProcessedMessage message, CancellationToken ct = default);

    /// <summary>Исход последней попытки по этой же пачке фотографий, если она была.</summary>
    Task<SubmissionRecord?> FindSubmissionAsync(string period, string fingerprint, CancellationToken ct = default);

    Task RecordSubmissionAsync(SubmissionRecord record, CancellationToken ct = default);

    Task RecordRecognitionAsync(RecognitionRun run, CancellationToken ct = default);

    Task RecordReadingsAsync(PeriodKey period, IReadOnlyList<MeterReading> readings, CancellationToken ct = default);
}

public sealed class LocalState(IDbContextFactory<DesktopDbContext> factory) : ILocalState
{
    private readonly IDbContextFactory<DesktopDbContext> _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<bool> WasProcessedAsync(string messageId, CancellationToken ct = default)
    {
        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId, ct).ConfigureAwait(false);
    }

    public async Task MarkProcessedAsync(ProcessedMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == message.MessageId, ct).ConfigureAwait(false))
        {
            return;
        }

        db.ProcessedMessages.Add(message);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<SubmissionRecord?> FindSubmissionAsync(
        string period,
        string fingerprint,
        CancellationToken ct = default)
    {
        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await db.Submissions
            .Where(s => s.Period == period && s.Fingerprint == fingerprint)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task RecordSubmissionAsync(SubmissionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Submissions.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordRecognitionAsync(RecognitionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.RecognitionRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordReadingsAsync(
        PeriodKey period,
        IReadOnlyList<MeterReading> readings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readings);

        await using DesktopDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        string key = period.ToString();

        foreach (MeterReading reading in readings)
        {
            LocalReading? existing = await db.Readings
                .FirstOrDefaultAsync(r => r.Period == key && r.MeterKey == reading.MeterKey, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.Readings.Add(new LocalReading
                {
                    Period = key,
                    MeterKey = reading.MeterKey,
                    Value = reading.Value,
                    Source = reading.Source.ToString(),
                    CreatedUtc = reading.CapturedUtc ?? DateTimeOffset.UtcNow,
                });
            }
            else
            {
                // Замещение, а не второй ряд: повторная обработка периода после сбоя
                // не должна оставлять в журнале два значения за один месяц.
                existing.Value = reading.Value;
                existing.Source = reading.Source.ToString();
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public static class LocalStateRegistration
{
    /// <summary>
    /// Схема создаётся через EnsureCreated, без миграций: база целиком локальна и
    /// восстановима из Dropbox, а тащить инструментарий миграций в приложение для
    /// одной семьи незачем.
    /// </summary>
    public static async Task EnsureCreatedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        IDbContextFactory<DesktopDbContext> factory =
            services.GetRequiredService<IDbContextFactory<DesktopDbContext>>();

        await using DesktopDbContext db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
    }
}
