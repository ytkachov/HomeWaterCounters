using System.Text.Json;
using System.Text.Json.Serialization;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;

namespace WaterCounters.Core.State;

/// <summary>Факт закрытия периода. Именно он не даёт обработать один период дважды.</summary>
public sealed record SubmittedPeriod
{
    public required PeriodKey Period { get; init; }

    public required DateTimeOffset SubmittedUtc { get; init; }

    /// <summary>true — прогон был в режиме проверки, в кабинет ничего не ушло.</summary>
    public required bool WasDryRun { get; init; }

    /// <summary>true — значения не с фотографий, а посчитаны прогнозом.</summary>
    public required bool WasForecast { get; init; }

    public string? Note { get; init; }
}

/// <summary>Содержимое <c>/state/history.json</c>.</summary>
public sealed record ReadingHistory
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset UpdatedUtc { get; init; }

    public IReadOnlyList<MeterReading> Readings { get; init; } = [];

    public IReadOnlyList<SubmittedPeriod> Periods { get; init; } = [];

    public static ReadingHistory Empty { get; } = new();

    /// <summary>
    /// Период уже закрыт. Режим проверки закрытием не считается: показания в кабинет
    /// не ушли, и в следующем запуске период обязан обработаться заново.
    /// </summary>
    public bool IsClosed(PeriodKey period) =>
        Periods.Any(p => p.Period == period && !p.WasDryRun);

    public SubmittedPeriod? PeriodRecord(PeriodKey period) => Periods.FirstOrDefault(p => p.Period == period);

    public MeterReading? Latest(string meterKey) =>
        Readings
            .Where(r => string.Equals(r.MeterKey, meterKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Period)
            .FirstOrDefault();

    public IReadOnlyList<MeterReading> For(string meterKey) =>
        [.. Readings.Where(r => string.Equals(r.MeterKey, meterKey, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Period)];

    /// <summary>
    /// Добавляет показания периода, замещая уже записанные за тот же период и счётчик.
    /// Замещение, а не добавление: иначе повторная обработка периода после сбоя
    /// оставила бы в истории два значения и сломала расчёт дельт.
    /// </summary>
    public ReadingHistory With(IEnumerable<MeterReading> readings, SubmittedPeriod? period, DateTimeOffset updatedUtc)
    {
        ArgumentNullException.ThrowIfNull(readings);

        List<MeterReading> incoming = [.. readings];
        HashSet<(string Meter, PeriodKey Period)> replaced =
            [.. incoming.Select(r => (r.MeterKey.ToLowerInvariant(), r.Period))];

        List<MeterReading> merged =
        [
            .. Readings.Where(r => !replaced.Contains((r.MeterKey.ToLowerInvariant(), r.Period))),
            .. incoming,
        ];

        merged.Sort(static (a, b) =>
        {
            int byPeriod = a.Period.CompareTo(b.Period);
            return byPeriod != 0 ? byPeriod : string.CompareOrdinal(a.MeterKey, b.MeterKey);
        });

        List<SubmittedPeriod> periods = [.. Periods];

        if (period is not null)
        {
            periods.RemoveAll(p => p.Period == period.Period);
            periods.Add(period);
            periods.Sort(static (a, b) => a.Period.CompareTo(b.Period));
        }

        return this with
        {
            Readings = merged,
            Periods = periods,
            UpdatedUtc = updatedUtc,
        };
    }
}

/// <summary>
/// Чтение и запись <c>/state/history.json</c>. Единственный писатель — обработчик,
/// поэтому блокировок нет; телефон файл только читает.
/// </summary>
public sealed class ReadingHistoryStore(IRemoteStore store, QueueLayout layout, TimeProvider? clock = null)
{
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ReadingHistory> LoadAsync(CancellationToken ct = default)
    {
        byte[] content;

        try
        {
            content = await _store.DownloadAsync(_layout.HistoryPath, ct).ConfigureAwait(false);
        }
        catch (RemoteNotFoundException)
        {
            return ReadingHistory.Empty;
        }

        ReadingHistory? history;

        try
        {
            history = JsonSerializer.Deserialize(content, StateJsonContext.Default.ReadingHistory);
        }
        catch (JsonException ex)
        {
            throw new MessageFormatException($"История показаний повреждена: {ex.Message}");
        }

        if (history is null)
        {
            return ReadingHistory.Empty;
        }

        if (history.SchemaVersion > ReadingHistory.CurrentSchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(history.SchemaVersion);
        }

        return history;
    }

    public async Task<ReadingHistory> SaveAsync(ReadingHistory history, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);

        ReadingHistory stamped = history with { UpdatedUtc = _clock.GetUtcNow() };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(stamped, StateJsonContext.Default.ReadingHistory);

        await _store.UploadAsync(_layout.HistoryPath, payload, RemoteWriteMode.Overwrite, ct).ConfigureAwait(false);
        return stamped;
    }

    /// <summary>Читает, применяет изменение и записывает обратно — обычный путь обработчика.</summary>
    public async Task<ReadingHistory> AppendAsync(
        IEnumerable<MeterReading> readings,
        SubmittedPeriod? period,
        CancellationToken ct = default)
    {
        ReadingHistory current = await LoadAsync(ct).ConfigureAwait(false);
        ReadingHistory updated = current.With(readings, period, _clock.GetUtcNow());
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ReadingHistory))]
public sealed partial class StateJsonContext : JsonSerializerContext;
