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
    public RecognitionProvider Provider { get; set; } = RecognitionProvider.Ollama;

    /// <summary>Адрес VLM-хоста. Может указывать на другую машину в локальной сети.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Модель выбрана замером, а не по описанию: 100 % по целой части на реальных
    /// снимках против 20 % у qwen2.5vl:7b в Q4. Требует около 10 ГБ видеопамяти.
    /// </summary>
    public string Model { get; set; } = "qwen3-vl:8b-instruct-q8_0";

    /// <summary>
    /// Вариант промпта. Короткий выигрывает у подробного и заметно: 100 % против 80 %
    /// по целой части и вчетверо меньше неверных цифр. Правила про красные барабаны и
    /// перекат, которые подробный промпт честно перечисляет, модели скорее мешают.
    /// </summary>
    public PromptVariant Prompt { get; set; } = PromptVariant.Terse;

    /// <summary>Крупная модель на слабой карте отвечает минутами, отсюда запас.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Проходов с разными кропами. 1 — без голосования, 3 — полный ансамбль.</summary>
    public int EnsemblePasses { get; set; } = 3;

    /// <summary>
    /// Сколько минут без новых файлов в папке периода считать признаком того, что
    /// ручная раскладка фотографий закончена. См. docs/recognition-service.md.
    /// </summary>
    public int SettlingMinutes { get; set; } = 3;

    /// <summary>Класть кроп циферблата в Dropbox рядом с предложением — телефон покажет его у поля.</summary>
    public bool UploadCrops { get; set; } = true;

    /// <summary>
    /// Длинная сторона кадра, подаваемого модели. Ужимать нельзя: барабан занимает
    /// малую долю кадра, и на 1280 точность падает вдвое. Значение по умолчанию
    /// оставляет типичный снимок телефона нетронутым.
    /// </summary>
    public int MaxImageDimension { get; set; } = 4000;

    /// <summary>
    /// Размер контекста модели. Умолчание Ollama — 4096 токенов, и два кадра счётчика
    /// в него не помещаются: хост отвечает отказом ещё до распознавания. Больше —
    /// дороже по видеопамяти, поэтому число вынесено сюда, а не зашито в код.
    /// </summary>
    public int ContextTokens { get; set; } = 16384;

    /// <summary>
    /// Читать серийный номер отдельным запросом. Вдвое дольше и того стоит: просьба
    /// заодно прочитать номер сбивает модель с цифр, а номер — единственный способ
    /// понять, какой счётчик на снимке, когда их в квартире несколько одинаковых.
    /// </summary>
    public bool SeparateSerialPass { get; set; } = true;

    /// <summary>Ниже этого порога уверенность модели считается недостаточной и попадает в замечания.</summary>
    public double MinConfidence { get; set; } = 0.80;

    /// <summary>Отключает предобработку OpenCV — на случай, когда она портит конкретные снимки.</summary>
    public bool Preprocess { get; set; } = true;

    /// <summary>
    /// Выравнивание яркости CLAHE на тёмных кадрах. По умолчанию выключено: замер по
    /// фикстурам показал устойчивое ухудшение (80 % против 60 % по целой части) —
    /// счётчики почти всегда сняты в тёмной нише, то есть срабатывало оно всегда, а
    /// вытягивало вместе с цифрами шум и блики на стекле. Включать только если замер
    /// на ваших снимках скажет обратное.
    /// </summary>
    public bool EnhanceDarkFrames { get; set; }
}

public sealed record PortalSettings
{
    /// <summary>
    /// Режим проверки: форма заполняется, кнопка отправки не нажимается.
    /// По умолчанию включён намеренно — отправка показаний необратима.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public bool Enabled { get; set; } = true;

    /// <summary>Видимый браузер нужен один раз — пройти 2FA или капчу вручную.</summary>
    public bool Headless { get; set; } = true;

    public PortalSelectorMap? Selectors { get; set; }
}

public sealed record MailSettings
{
    public bool Enabled { get; set; }

    public string? To { get; set; }

    public string? From { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? UserName { get; set; }
}

/// <summary>Срок сдачи и льготный период. Одни и те же числа у телефона и у обработчика.</summary>
public sealed record ScheduleSettings
{
    public int DeadlineDayOfMonth { get; set; } = 25;

    /// <summary>
    /// Сколько дней после срока ещё имеет смысл ждать фотографии. По истечении
    /// watchdog считает прогноз: лучше приблизительное значение, чем пропуск периода.
    /// </summary>
    public int GraceDays { get; set; } = 3;

    /// <summary>За сколько дней до срока телефон начинает напоминать.</summary>
    public int ReminderDaysBefore { get; set; } = 3;

    /// <summary>
    /// Часовой пояс, в котором считается календарь срока. Обе части обязаны считать
    /// его одинаково, иначе раз в месяц они разойдутся на сутки. Null — пояс машины.
    /// </summary>
    public string? TimeZoneId { get; set; }
}

/// <summary>
/// Несекретные настройки из <c>/config/settings.json</c>. Редактируются с телефона,
/// поэтому смена вёрстки кабинета или модели распознавания не требует пересборки.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Растёт при каждой записи. Сторона с меньшей ревизией не перезаписывает большую.</summary>
    public int Revision { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public IReadOnlyList<MeterSpec> Meters { get; set; } = [];

    public ScheduleSettings Schedule { get; set; } = new();

    public RecognitionSettings Recognition { get; set; } = new();

    public PortalSettings Portal { get; set; } = new();

    public MailSettings Mail { get; set; } = new();

    /// <summary>
    /// Подставляет значения по умолчанию вместо секций, которых нет в файле.
    ///
    /// Нужен потому, что System.Text.Json при десериализации не применяет
    /// инициализаторы свойств: отсутствующая в JSON секция приходит как null, а не
    /// как значение по умолчанию. Файл правится руками и с телефона, не дописать
    /// секцию — обычное дело, и обработчик обязан это пережить, а не упасть на
    /// первом же обращении к настройкам.
    /// </summary>
    public AppSettings WithDefaults() => new()
    {
        SchemaVersion = SchemaVersion == 0 ? CurrentSchemaVersion : SchemaVersion,
        Revision = Revision,
        UpdatedUtc = UpdatedUtc,
        UpdatedBy = UpdatedBy ?? string.Empty,
        Meters = Meters ?? [],
        Schedule = Schedule ?? new ScheduleSettings(),
        Recognition = Recognition ?? new RecognitionSettings(),
        Portal = Portal ?? new PortalSettings(),
        Mail = Mail ?? new MailSettings(),
    };

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
    public string? PortalLogin { get; set; }

    public string? PortalPassword { get; set; }

    public string? SmtpPassword { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppSecrets))]
public sealed partial class ConfigurationJsonContext : JsonSerializerContext;
