using System.Globalization;
using System.Text.Json.Serialization;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Configuration;

/// <summary>
/// Карта селекторов конкретного кабинета. Живёт в настройках, а не в коде: когда
/// поставщик поменяет вёрстку, правка делается с телефона и не требует пересборки
/// и переустановки десктопа.
///
/// В шаблонах доступны подстановки: <c>{portalId}</c> — идентификатор счётчика,
/// <c>{value}</c> — отправляемое значение уже в формате кабинета, <c>{yyyy}</c>,
/// <c>{MM}</c>, <c>{M}</c> и <c>{yyyy-MM}</c> — период. Период нужен потому, что
/// «уже сдано» во многих кабинетах не отдельный элемент, а строка истории с датой
/// начала периода: отличить её от прошлого месяца можно только по этой дате.
/// </summary>
public sealed record PortalSelectorMap
{
    public required string Name { get; init; }

    public required string LoginUrl { get; init; }

    /// <summary>Страница ввода показаний. Если пусто — считаем, что она открывается сама после входа.</summary>
    public string? ReadingsUrl { get; init; }

    /// <summary>
    /// Страница одного счётчика — обычно содержит <c>{portalId}</c>.
    ///
    /// Заданное значение переключает адаптер на поштучную сдачу: кабинет принимает
    /// не всю квартиру одной формой, а по счётчику за визит. Тогда цикл «открыть
    /// страницу → проверить, не сдано ли → заполнить единственное поле → нажать
    /// кнопку» повторяется столько раз, сколько счётчиков, и каждый из них
    /// получает свой исход.
    /// </summary>
    public string? MeterPageUrl { get; init; }

    public required string LoginInput { get; init; }

    public required string PasswordInput { get; init; }

    public required string SubmitLoginButton { get; init; }

    /// <summary>Элемент, наличие которого доказывает, что вход выполнен.</summary>
    public required string LoggedInMarker { get; init; }

    /// <summary>Элемент с текстом ошибки входа. Позволяет отличить неверный пароль от смены вёрстки.</summary>
    public string? LoginErrorMarker { get; init; }

    /// <summary>Поле ввода показания. Обычно содержит <c>{portalId}</c>.</summary>
    public required string ReadingInput { get; init; }

    public required string SubmitReadingsButton { get; init; }

    public required string SuccessMarker { get; init; }

    /// <summary>Признак «период уже закрыт» — повторно отправлять нельзя.</summary>
    public string? AlreadySubmittedMarker { get; init; }

    /// <summary>Элемент с текстом ошибки валидации формы показаний.</summary>
    public string? ValidationErrorMarker { get; init; }

    public int NavigationTimeoutMs { get; init; } = 30_000;

    public int ActionTimeoutMs { get; init; } = 15_000;

    /// <summary>
    /// Разделитель дробной части, который ждёт форма. Российские кабинеты сплошь и рядом
    /// требуют запятую и молча отбрасывают дробную часть при точке — а это ошибка,
    /// которую заметишь только по следующему счёту.
    /// </summary>
    public string DecimalSeparator { get; init; } = ".";

    /// <summary>
    /// Формат числа для формы (обычные форматные строки .NET: <c>"0"</c>, <c>"0.000"</c>).
    /// Пусто — значение печатается как есть, но без хвостовых нулей: <c>decimal</c>
    /// помнит разрядность источника, и 919,000 в поле кабинета, который ждёт целые
    /// кубометры, — это отказ формы или неверное показание.
    /// </summary>
    public string? ValueFormat { get; init; }

    /// <summary>
    /// Сколько дробных разрядов принимает кабинет. Лишние <b>отбрасываются</b>, а не
    /// округляются: 926,603 кубометра — это 926 полных, и округление вверх до 927
    /// означало бы заявить непотреблённый кубометр. Разрядность самого счётчика при
    /// этом остаётся полной — распознавание обязано читать красные барабаны, иначе
    /// модель припишет их к целой части.
    /// </summary>
    public int? ValueDecimals { get; init; }

    /// <summary>Кабинет принимает показания по одному счётчику за визит.</summary>
    [JsonIgnore]
    public bool IsPerMeter => !string.IsNullOrWhiteSpace(MeterPageUrl);

    public string ReadingInputFor(string portalId) => Expand(ReadingInput, portalId);

    public string MeterPageUrlFor(string portalId) =>
        Expand(MeterPageUrl ?? throw new InvalidOperationException("MeterPageUrl не задан."), portalId);

    /// <summary>Значение в том виде, в каком его ждёт форма кабинета.</summary>
    public string FormatValue(decimal value)
    {
        if (ValueDecimals is { } decimals and >= 0)
        {
            decimal scale = 1m;

            for (int i = 0; i < decimals; i++)
            {
                scale *= 10m;
            }

            value = Math.Truncate(value * scale) / scale;
        }

        string text = ValueFormat is { Length: > 0 } format
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : Normalize(value).ToString(CultureInfo.InvariantCulture);

        return text.Replace(".", DecimalSeparator, StringComparison.Ordinal);
    }

    /// <summary>Убирает хвостовые нули, сохраняя значение: 919,000 → 919, а 919,500 → 919,5.</summary>
    private static decimal Normalize(decimal value)
    {
        decimal trimmed = value / 1.000000000000000000000000000000000m;
        return trimmed == 0m ? 0m : trimmed;
    }

    /// <summary>
    /// Подставляет в шаблон селектора всё, что известно о конкретной отправке.
    /// Неизвестные на этот момент подстановки остаются как есть: селектор, которому
    /// нужно значение, до заполнения формы всё равно не используется.
    /// </summary>
    public string Expand(string template, string? portalId = null, PeriodKey? period = null, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        string result = template;

        if (portalId is not null)
        {
            result = result.Replace("{portalId}", portalId, StringComparison.Ordinal);
        }

        if (value is not null)
        {
            result = result.Replace("{value}", value, StringComparison.Ordinal);
        }

        if (period is { } p)
        {
            result = result
                .Replace("{yyyy-MM}", p.ToString(), StringComparison.Ordinal)
                .Replace("{yyyy}", p.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{MM}", p.Month.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{M}", p.Month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return result;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalSelectorMap))]
public sealed partial class PortalJsonContext : JsonSerializerContext;
