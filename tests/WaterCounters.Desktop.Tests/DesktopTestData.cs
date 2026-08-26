using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Scheduling;
using WaterCounters.Core.State;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.State;
using WaterCounters.Portal;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Tests;

internal static class DesktopTestData
{
    public static readonly DateTimeOffset Now = new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    public static readonly PeriodKey Period = new(2026, 7);

    public static MeterSpec ColdWater { get; } = new()
    {
        Key = "cold-water",
        DisplayName = "Холодная вода",
        Kind = MeterKind.ColdWater,
        Unit = "м³",
        IntegerDigits = 5,
        FractionDigits = 3,
        SerialNumber = "12-345-678",
        PortalId = "W-1",
        SortOrder = 0,
    };

    public static MeterSpec HotWater { get; } = new()
    {
        Key = "hot-water",
        DisplayName = "Горячая вода",
        Kind = MeterKind.HotWater,
        Unit = "м³",
        IntegerDigits = 5,
        FractionDigits = 3,
        SerialNumber = "98-765-432",
        PortalId = "W-2",
        SortOrder = 1,
    };

    public static MeterSpec Electricity { get; } = new()
    {
        Key = "electricity",
        DisplayName = "Электричество",
        Kind = MeterKind.Electricity,
        Unit = "кВт·ч",
        IntegerDigits = 6,
        FractionDigits = 1,
        PortalId = "E-1",
        SortOrder = 2,
    };

    public static IReadOnlyList<MeterSpec> Meters { get; } = [ColdWater, HotWater, Electricity];

    public static AppSettings Settings(bool dryRun = true) => new()
    {
        Meters = Meters,
        Portal = new PortalSettings
        {
            DryRun = dryRun,
            Selectors = new PortalSelectorMap
            {
                Name = "тест",
                LoginUrl = "http://localhost/login",
                LoginInput = "#login",
                PasswordInput = "#password",
                SubmitLoginButton = "#submit",
                LoggedInMarker = "#account",
                ReadingInput = "#meter-{portalId}",
                SubmitReadingsButton = "#send",
                SuccessMarker = "#done",
            },
        },
    };

    /// <summary>История подряд идущих месяцев, заканчивающаяся месяцем перед <see cref="Period"/>.</summary>
    public static ReadingHistory HistoryFor(MeterSpec meter, params decimal[] values)
    {
        PeriodKey start = Period.AddMonths(-values.Length);

        List<MeterReading> readings = [];

        foreach (decimal value in values)
        {
            readings.Add(new MeterReading
            {
                MeterKey = meter.Key,
                Period = start,
                Value = value,
                Source = ReadingSource.Manual,
            });

            start = start.Next();
        }

        return new ReadingHistory { Readings = readings };
    }

    public static ReadingHistory Merge(params ReadingHistory[] histories) => new()
    {
        Readings = [.. histories.SelectMany(static h => h.Readings)],
    };
}

/// <summary>Настройки и секреты, задаваемые тестом напрямую.</summary>
internal sealed class StubSettingsProvider(AppSettings settings, AppSecrets? secrets = null) : ISettingsProvider
{
    public AppSettings Current { get; set; } = settings;

    public AppSecrets? Secrets { get; set; } = secrets ?? new AppSecrets
    {
        PortalLogin = "user",
        PortalPassword = "pass",
    };

    public SubmissionSchedule Schedule { get; set; } = new(settings.Schedule);

    public Task<AppSettings> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Current);
}

/// <summary>Распознаватель, отвечающий по ключу счётчика заранее заданными значениями.</summary>
internal sealed class ScriptedRecognizer : IMeterRecognizer
{
    private readonly Dictionary<string, RecognitionResult> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    public RecognitionException? Throws { get; set; }

    public ScriptedRecognizer Answer(MeterSpec meter, decimal? value, string? serial = null, double confidence = 0.95)
    {
        _answers[meter.Key] = new RecognitionResult(serial, value, confidence, "{}", []);
        return this;
    }

    public Task<RecognitionResult> RecognizeAsync(MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        Calls.Add(meter.Key);

        if (Throws is { } error)
        {
            throw error;
        }

        return Task.FromResult(_answers.TryGetValue(meter.Key, out RecognitionResult? answer)
            ? answer
            : RecognitionResult.Failed("сценарий теста не задал ответ"));
    }
}

/// <summary>Кабинет, который считает нажатия и запоминает, что именно в него отправляли.</summary>
internal sealed class FakePortalGateway : IPortalGateway
{
    /// <summary>Сколько раз показания реально уходили в кабинет. При dryRun обязан остаться нулём.</summary>
    public int SubmitCount { get; private set; }

    public int CallCount { get; private set; }

    public List<PortalReading> LastReadings { get; private set; } = [];

    public SubmissionStatus Status { get; set; } = SubmissionStatus.Submitted;

    public string? Error { get; set; }

    public Task<PortalOutcome> SubmitAsync(
        PeriodKey period,
        IReadOnlyList<PortalReading> readings,
        bool dryRun,
        CancellationToken ct = default)
    {
        CallCount++;
        LastReadings = [.. readings];

        if (!dryRun && Error is null)
        {
            SubmitCount++;
        }

        return Task.FromResult(new PortalOutcome
        {
            Status = Error is not null ? SubmissionStatus.DryRun : dryRun ? SubmissionStatus.DryRun : Status,
            Message = "макет кабинета",
            Error = Error,
        });
    }
}

/// <summary>Собранный конвейер со всеми зависимостями на месте — общая заготовка для тестов.</summary>
internal sealed class PipelineHarness : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"wc-desktop-{Guid.NewGuid():N}.db");

    private readonly DbContextOptions<DesktopDbContext> _dbOptions;

    public PipelineHarness(AppSettings? settings = null)
    {
        Settings = new StubSettingsProvider(settings ?? DesktopTestData.Settings());
        Clock = new FakeTimeProvider(DesktopTestData.Now);
        Store = new InMemoryRemoteStore(Clock);
        Layout = new QueueLayout();
        Queue = new MessageQueue(Store, Layout, Clock);
        History = new ReadingHistoryStore(Store, Layout, Clock);

        _dbOptions = new DbContextOptionsBuilder<DesktopDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using (var db = new DesktopDbContext(_dbOptions))
        {
            db.Database.EnsureCreated();
        }

        Local = new LocalState(new PooledFactory(_dbOptions));

        Pipeline = new ReadingPipeline(
            Settings,
            Recognizer,
            Store,
            Queue,
            History,
            Portal,
            Mailer,
            Local,
            new DesktopOptions { DeviceId = "test-desktop" },
            NullLogger<ReadingPipeline>.Instance,
            Clock);
    }

    public StubSettingsProvider Settings { get; }

    public FakeTimeProvider Clock { get; }

    public InMemoryRemoteStore Store { get; }

    public QueueLayout Layout { get; }

    public MessageQueue Queue { get; }

    public ReadingHistoryStore History { get; }

    public ScriptedRecognizer Recognizer { get; } = new();

    public FakePortalGateway Portal { get; } = new();

    public NullMailer Mailer { get; } = new();

    public ILocalState Local { get; }

    public ReadingPipeline Pipeline { get; }

    public async Task SeedHistoryAsync(ReadingHistory history) => await History.SaveAsync(history);

    /// <summary>Готовая пачка: по фотографии на каждый перечисленный счётчик.</summary>
    public async Task<PhotoBatchDecision> UploadPhotosAsync(params MeterSpec[] meters)
    {
        List<PhotoAssignment> assignments = [];

        foreach (MeterSpec meter in meters)
        {
            string path = Layout.PhotoPath(DesktopTestData.Period, $"{meter.Key}.jpg");
            RemoteEntry entry = await Store.UploadAsync(path, new byte[] { 1, 2, 3 }, RemoteWriteMode.Overwrite);
            assignments.Add(new PhotoAssignment(meter, entry, PhotoMatch.ByFileName));
        }

        return new PhotoBatchDecision
        {
            Readiness = BatchReadiness.Ready,
            Reason = "тест",
            Assignments = assignments,
            MissingMeters = [.. DesktopTestData.Meters.Where(m => meters.All(x => x.Key != m.Key))],
            Fingerprint = "fp-1",
        };
    }

    public async Task<IReadOnlyList<MessageEnvelope>> ToMobileAsync()
    {
        IReadOnlyList<RemoteEntry> entries = await Queue.ListPendingAsync(QueueDirection.ToMobile);
        List<MessageEnvelope> envelopes = [];

        foreach (RemoteEntry entry in entries)
        {
            envelopes.Add(MessageCodec.Decode(await Store.DownloadAsync(entry.Path)));
        }

        return envelopes;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class PooledFactory(DbContextOptions<DesktopDbContext> options)
        : IDbContextFactory<DesktopDbContext>
    {
        public DesktopDbContext CreateDbContext() => new(options);
    }
}
