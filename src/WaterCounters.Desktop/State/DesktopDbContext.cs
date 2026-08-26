using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WaterCounters.Desktop.State;

/// <summary>Чем закончилась работа с периодом. Хранится строкой — журнал переживает смену версии.</summary>
public enum SubmissionOutcome
{
    /// <summary>Показания приняты кабинетом.</summary>
    Submitted = 0,

    /// <summary>Режим проверки: форма заполнена, кнопка не нажималась.</summary>
    DryRun = 1,

    /// <summary>Период был закрыт ещё до нашего прихода.</summary>
    AlreadySubmitted = 2,

    /// <summary>Критическое замечание валидатора удержало отправку — ждём человека.</summary>
    HeldForReview = 3,

    /// <summary>Показания уехали на подтверждение на телефон.</summary>
    AwaitingConfirmation = 4,

    /// <summary>Сбой, который имеет смысл повторить: кабинет недоступен, модель не ответила.</summary>
    Failed = 5,
}

/// <summary>Обработанное сообщение очереди. Ключ идемпотентности на случай повторной доставки.</summary>
public sealed class ProcessedMessage
{
    public required string MessageId { get; set; }

    public required string Type { get; set; }

    public required string Period { get; set; }

    public required DateTimeOffset ProcessedUtc { get; set; }

    public required string Outcome { get; set; }
}

/// <summary>
/// Попытка закрыть период. Именно эта таблица не даёт зациклиться на одной и той же
/// пачке фотографий: отпечаток пачки запоминается вместе с исходом.
/// </summary>
public sealed class SubmissionRecord
{
    public int Id { get; set; }

    public required string Period { get; set; }

    /// <summary>Отпечаток пачки фотографий. Пусто для прогноза — у него фотографий нет.</summary>
    public required string Fingerprint { get; set; }

    public required SubmissionOutcome Outcome { get; set; }

    public required DateTimeOffset CreatedUtc { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Журнал обращений к модели. Ведётся ради подбора модели и роста фикстур: каждая
/// неверно распознанная фотография должна становиться регрессионным тестом, а для
/// этого надо знать, что именно и с какой уверенностью было прочитано.
/// </summary>
public sealed class RecognitionRun
{
    public int Id { get; set; }

    public required string Period { get; set; }

    public required string MeterKey { get; set; }

    public required string PhotoPath { get; set; }

    public required string Model { get; set; }

    public decimal? Value { get; set; }

    public string? Serial { get; set; }

    public double Confidence { get; set; }

    public long ElapsedMs { get; set; }

    public string Warnings { get; set; } = string.Empty;

    public required DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>Локальная копия сданных показаний. История в Dropbox — источник истины, эта — быстрый доступ.</summary>
public sealed class LocalReading
{
    public int Id { get; set; }

    public required string Period { get; set; }

    public required string MeterKey { get; set; }

    public required decimal Value { get; set; }

    public required string Source { get; set; }

    public required DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// Локальное состояние обработчика в SQLite.
///
/// Схема создаётся вызовом EnsureCreated, без миграций: база целиком локальна,
/// восстанавливается из Dropbox и не переживает смену схемы намеренно — тащить
/// в приложение для одной семьи инструментарий миграций незачем.
/// </summary>
public sealed class DesktopDbContext(DbContextOptions<DesktopDbContext> options) : DbContext(options)
{
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<SubmissionRecord> Submissions => Set<SubmissionRecord>();

    public DbSet<RecognitionRun> RecognitionRuns => Set<RecognitionRun>();

    public DbSet<LocalReading> Readings => Set<LocalReading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // SQLite не знает типа decimal и по умолчанию хранил бы показания в double.
        // Для чисел вида 01234.567 это тихая потеря точности в последнем разряде,
        // поэтому значения лежат строкой в инвариантной культуре.
        var decimalToString = new ValueConverter<decimal, string>(
            value => value.ToString(CultureInfo.InvariantCulture),
            text => decimal.Parse(text, CultureInfo.InvariantCulture));

        var nullableDecimalToString = new ValueConverter<decimal?, string?>(
            value => value == null ? null : value.Value.ToString(CultureInfo.InvariantCulture),
            text => text == null ? null : decimal.Parse(text, CultureInfo.InvariantCulture));

        modelBuilder.Entity<ProcessedMessage>().HasKey(m => m.MessageId);

        modelBuilder.Entity<SubmissionRecord>(entity =>
        {
            entity.HasIndex(s => new { s.Period, s.Fingerprint });
            entity.Property(s => s.Outcome).HasConversion<string>();
        });

        modelBuilder.Entity<RecognitionRun>(entity =>
        {
            entity.HasIndex(r => new { r.Period, r.MeterKey });
            entity.Property(r => r.Value).HasConversion(nullableDecimalToString);
        });

        modelBuilder.Entity<LocalReading>(entity =>
        {
            entity.HasIndex(r => new { r.Period, r.MeterKey }).IsUnique();
            entity.Property(r => r.Value).HasConversion(decimalToString);
        });
    }
}
