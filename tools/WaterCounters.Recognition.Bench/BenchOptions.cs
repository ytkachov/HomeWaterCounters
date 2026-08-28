using System.Globalization;
using WaterCounters.Core.Configuration;
using WaterCounters.Recognition.Vlm;

namespace WaterCounters.Recognition.Bench;

/// <summary>Что кладём в запрос: полный кадр, кроп циферблата или оба.</summary>
public enum BenchImageSet
{
    Both = 0,
    FullFrame = 1,
    DialCrop = 2,
}

/// <summary>Одна замеряемая комбинация: модель × промпт × препроцессинг × кадры × проходы.</summary>
public sealed record BenchCombination(
    string Model,
    PromptVariant Prompt,
    bool Preprocess,
    bool Enhance,
    BenchImageSet Images,
    bool SerialPass,
    int Passes)
{
    public override string ToString() =>
        $"{Model} / {Prompt} / {(Preprocess ? "prep" : "raw")} / " +
        $"{(Enhance ? "clahe" : "noclahe")} / {Images} / " +
        $"{(SerialPass ? "2pass" : "1pass")} / ×{Passes}";
}

public sealed record BenchOptions
{
    public string Fixtures { get; init; } = Path.Combine("fixtures", "meters");

    public string Endpoint { get; init; } = "http://localhost:11434";

    public RecognitionProvider Provider { get; init; } = RecognitionProvider.Ollama;

    public IReadOnlyList<string> Models { get; init; } = ["qwen2.5vl:7b"];

    public IReadOnlyList<PromptVariant> Prompts { get; init; } = [PromptVariant.Russian];

    public IReadOnlyList<bool> Preprocess { get; init; } = [true];

    public IReadOnlyList<int> Passes { get; init; } = [1];

    /// <summary>Какие кадры класть в запрос. Проверяется замером: две картинки помогают не всегда.</summary>
    public IReadOnlyList<BenchImageSet> Images { get; init; } = [BenchImageSet.Both];

    /// <summary>
    /// CLAHE на тёмных кадрах — отдельным измерением, а не вместе с поиском панели.
    /// Счётчики почти всегда сняты в тёмной нише, то есть выравнивание яркости на них
    /// срабатывает всегда, и его вклад обязан быть измерен отдельно от кропа.
    /// </summary>
    public IReadOnlyList<bool> Enhance { get; init; } = [false];

    /// <summary>Читать серийный номер отдельным запросом — замеряется наравне с остальным.</summary>
    public IReadOnlyList<bool> SerialPass { get; init; } = [true];

    /// <summary>
    /// Сколько испорченных вариантов делать из каждой фикстуры. Мера устойчивости к
    /// съёмке в других условиях, а не способ увеличить выборку: варианты одного кадра
    /// не независимы, и отчёт это оговаривает отдельной строкой.
    /// </summary>
    public int Augment { get; init; }

    public int TimeoutSeconds { get; init; } = 180;

    public int MaxImageDimension { get; init; } = 1280;

    /// <summary>Размер контекста модели — умолчание Ollama в 4096 токенов мало для двух кадров.</summary>
    public int ContextTokens { get; init; } = 8192;

    public string? CsvPath { get; init; }

    /// <summary>
    /// Куда сложить кадры ровно в том виде, в каком они уходят модели.
    ///
    /// Без этого разбор «почему не читается» сводится к гаданию: метрика говорит,
    /// что модель ошиблась, но не говорит, что она вообще видела — нашёлся ли
    /// циферблат, не срезал ли кроп половину барабана, хватает ли разрешения.
    /// </summary>
    public string? DumpDirectory { get; init; }

    /// <summary>Показать каждое расхождение отдельно — из них растут регрессионные фикстуры.</summary>
    public bool Verbose { get; init; } = true;

    public IReadOnlyList<BenchCombination> Combinations =>
    [
        .. from model in Models
           from prompt in Prompts
           from preprocess in Preprocess
           from enhance in Enhance
           from images in Images
           from serialPass in SerialPass
           from passes in Passes
           select new BenchCombination(model, prompt, preprocess, enhance, images, serialPass, passes),
    ];

    public static BenchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new BenchOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];

            if (key is "--help" or "-h")
            {
                throw new BenchUsageException(null);
            }

            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new BenchUsageException($"Не разобран аргумент '{key}'.");
            }

            string value = args[++i];

            options = key switch
            {
                "--fixtures" => options with { Fixtures = value },
                "--endpoint" => options with { Endpoint = value },
                "--provider" => options with { Provider = ParseEnum<RecognitionProvider>(key, value) },
                "--models" => options with { Models = Split(value) },
                "--prompts" => options with { Prompts = [.. Split(value).Select(v => ParseEnum<PromptVariant>(key, v))] },
                "--preprocess" => options with { Preprocess = [.. Split(value).Select(ParseFlag)] },
                "--passes" => options with { Passes = [.. Split(value).Select(v => ParseInt(key, v))] },
                "--images" => options with { Images = [.. Split(value).Select(v => ParseEnum<BenchImageSet>(key, v))] },
                "--enhance" => options with { Enhance = [.. Split(value).Select(ParseFlag)] },
                "--serial-pass" => options with { SerialPass = [.. Split(value).Select(ParseFlag)] },
                "--augment" => options with { Augment = ParseInt(key, value) },
                "--timeout" => options with { TimeoutSeconds = ParseInt(key, value) },
                "--max-dimension" => options with { MaxImageDimension = ParseInt(key, value) },
                "--context" => options with { ContextTokens = ParseInt(key, value) },
                "--csv" => options with { CsvPath = value },
                "--dump" => options with { DumpDirectory = value },
                "--verbose" => options with { Verbose = ParseFlag(value) },
                _ => throw new BenchUsageException($"Неизвестный ключ '{key}'."),
            };
        }

        return options;
    }

    public const string Usage = """
        Замер распознавания на размеченных фикстурах.

          dotnet run --project tools/WaterCounters.Recognition.Bench -- [ключи]

        Фикстуры: fixtures/meters/<meterKey>_<ожидаемое>_<серийник>.jpg
        Например: cold-water_01234.567_12-345-678.jpg
        Разрядность берётся из самой разметки, настройки не нужны.

          --fixtures       папка с фикстурами (по умолчанию fixtures/meters)
          --endpoint       адрес VLM-хоста (по умолчанию http://localhost:11434)
          --provider       Ollama | OpenAiCompatible (по умолчанию Ollama)
          --models         список через запятую: qwen2.5vl:7b,gemma3:12b
          --prompts        Russian | English | Terse, через запятую
          --preprocess     on,off — с предобработкой OpenCV и без неё
          --passes         1,3 — число проходов ансамбля
          --images         Both | FullFrame | DialCrop — какие кадры кладём в запрос
          --enhance        on,off — выравнивание яркости CLAHE (по умолчанию off: замер показал вред)
          --serial-pass    on,off — читать серийный номер отдельным запросом
          --augment        сколько испорченных вариантов делать из каждой фикстуры
                           (темнее, светлее, поворот, наклон, смаз, шум, блик)
          --timeout        секунд на один запрос (по умолчанию 180)
          --max-dimension  длинная сторона кадра (по умолчанию 1280)
          --context        размер контекста модели в токенах (по умолчанию 8192)
          --csv            куда выгрузить таблицу результатов
          --dump           куда сложить подготовленные кадры — то, что видит модель
          --verbose        on|off — печатать каждое расхождение (по умолчанию on)
        """;

    private static IReadOnlyList<string> Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool ParseFlag(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => throw new BenchUsageException($"Значение '{value}' не похоже на on/off."),
    };

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new BenchUsageException($"Значение ключа {key} должно быть целым числом, получено '{value}'.");

    private static TEnum ParseEnum<TEnum>(string key, string value)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : throw new BenchUsageException(
                $"Значение ключа {key} должно быть одним из: {string.Join(", ", Enum.GetNames<TEnum>())}.");
}

public sealed class BenchUsageException(string? message) : Exception(message ?? string.Empty);
