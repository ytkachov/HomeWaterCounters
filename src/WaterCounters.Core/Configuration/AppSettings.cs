using System.Text.Json.Serialization;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Configuration;

/// <summary>Реализация распознавания, которую поднимает обработчик.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecognitionProvider>))]
public enum RecognitionProvider
{
    /// <summary>Локальная Ollama: POST /api/chat, схема в поле format.</summary>
    Ollama = 0,

    /// <summary>LM Studio, llama.cpp, vLLM: POST /v1/chat/completions.</summary>
    OpenAiCompatible = 1,

    /// <summary>Заглушка по фикстурам — разработка и прогоны без GPU.</summary>
    Stub = 2,
}

public sealed record RecognitionSettings
{
    public RecognitionProvider Provider { get; init; } = RecognitionProvider.Ollama;

    /// <summary>Адрес VLM-хоста. Может указывать на другую машину в локальной сети.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "qwen2.5vl:7b";

    /// <summary>Крупная модель на слабой карте отвечает минутами, отсюда запас.</summary>
    public int TimeoutSeconds { get; init; } = 180;

    /// <summary>Проходов с разными кропами. 1 — без голосования, 3 — полный ансамбль.</summary>
    public int EnsemblePasses { get; init; } = 3;

    /// <summary>
    /// Сколько минут без новых файлов в папке периода считать признаком того, что
    /// ручная раскладка фотографий закончена. См. docs/recognition-service.md.
    /// </summary>
    public int SettlingMinutes { get; init; } = 3;

    /// <summary>Класть кроп циферблата в Dropbox рядом с предложением — телефон покажет его у поля.</summary>
    public bool UploadCrops { get; init; } = true;

    /// <summary>Длинная сторона кадра, подаваемого модели. Больше — медленнее и без выигрыша в точности.</summary>
    public int MaxImageDimension { get; init; } = 1280;

    /// <summary>Ниже этого порога уверенность модели считается недостаточной и попадает в замечания.</summary>
    public double MinConfidence { get; init; } = 0.80;

    /// <summary>Отключает предобработку OpenCV — на случай, когда она портит конкретные снимки.</summary>
    public bool Preprocess { get; init; } = true;
}

public sealed record PortalSettings
{
    /// <summary>
    /// Режим проверки: форма заполняется, кнопка отправки не нажимается.
    /// По умолчанию включён намеренно — отправка показаний необратима.
    /// </summary>
    public bool DryRun { get; init; } = true;

    public bool Enabled { get; init; } = true;

    /// <summary>Видимый браузер нужен один раз — пройти 2FA или капчу вручную.</summary>
    public bool Headless { get; init; } = true;

    public PortalSelectorMap? Selectors { get; init; }
}

public sealed record MailSettings
{
    public bool Enabled { get; init; }

    public string? To { get; init; }

    public string? From { get; init; }

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; } = 587;

    public bool UseStartTls { get; init; } = true;

    public string? UserName { get; init; }
}

/// <summary>Срок сдачи и льготный период. Одни и те же числа у телефона и у обработчика.</summary>
public sealed record ScheduleSettings
{
    public int DeadlineDayOfMonth { get; init; } = 25;

    /// <summary>
    /// Сколько дней после срока ещё имеет смысл ждать фотографии. По истечении
    /// watchdog считает прогноз: лучше приблизительное значение, чем пропуск периода.
    /// </summary>
    public int GraceDays { get; init; } = 3;

    /// <summary>За сколько дней до срока телефон начинает напоминать.</summary>
    public int ReminderDaysBefore { get; init; } = 3;

    /// <summary>
    /// Часовой пояс, в котором считается календарь срока. Обе части обязаны считать
    /// его одинаково, иначе раз в месяц они разойдутся на сутки. Null — пояс машины.
    /// </summary>
    public string? TimeZoneId { get; init; }
}

/// <summary>
/// Несекретные настройки из <c>/config/settings.json</c>. Редактируются с телефона,
/// поэтому смена вёрстки кабинета или модели распознавания не требует пересборки.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Растёт при каждой записи. Сторона с меньшей ревизией не перезаписывает большую.</summary>
    public int Revision { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public string UpdatedBy { get; init; } = string.Empty;

    public IReadOnlyList<MeterSpec> Meters { get; init; } = [];

    public ScheduleSettings Schedule { get; init; } = new();

    public RecognitionSettings Recognition { get; init; } = new();

    public PortalSettings Portal { get; init; } = new();

    public MailSettings Mail { get; init; } = new();

    public MeterSpec? MeterByKey(string key) =>
        Meters.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Счётчики в порядке съёмки — в этом же порядке они показываются и в письме.</summary>
    public IReadOnlyList<MeterSpec> OrderedMeters => [.. Meters.OrderBy(m => m.SortOrder).ThenBy(m => m.Key, StringComparer.Ordinal)];

    /// <summary>
    /// Заготовка для первого запуска: три типовых счётчика и режим проверки.
    /// Ключи совпадают с ожидаемыми именами файлов при ручной раскладке фотографий.
    /// </summary>
    public static AppSettings CreateDefault() => new()
    {
        Meters =
        [
            new MeterSpec
            {
                Key = "cold-water",
                DisplayName = "Холодная вода",
                Kind = MeterKind.ColdWater,
                Unit = "м³",
                IntegerDigits = 5,
                FractionDigits = 3,
                SortOrder = 0,
            },
            new MeterSpec
            {
                Key = "hot-water",
                DisplayName = "Горячая вода",
                Kind = MeterKind.HotWater,
                Unit = "м³",
                IntegerDigits = 5,
                FractionDigits = 3,
                SortOrder = 1,
            },
            new MeterSpec
            {
                Key = "electricity",
                DisplayName = "Электричество",
                Kind = MeterKind.Electricity,
                Unit = "кВт·ч",
                IntegerDigits = 6,
                FractionDigits = 1,
                SortOrder = 2,
            },
        ],
    };
}

/// <summary>
/// Секреты из <c>/config/secrets.enc</c>. В открытом виде существуют только в памяти
/// процесса: в Dropbox уезжает AES-256-GCM (см. <c>SecretsProtector</c>).
/// </summary>
public sealed record AppSecrets
{
    public string? PortalLogin { get; init; }

    public string? PortalPassword { get; init; }

    public string? SmtpPassword { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppSecrets))]
public sealed partial class ConfigurationJsonContext : JsonSerializerContext;
