using System.Text.Json.Serialization;

namespace WaterCounters.Core.Metering;

/// <summary>
/// Вид счётчика. Сериализуется строкой: settings.json правится руками и с телефона,
/// а перепутать 0 и 1 — это перепутать холодную воду с горячей, то есть получить
/// два неверных показания сразу. Написание менять нельзя — сломается совместимость
/// с уже лежащими настройками.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeterKind>))]
public enum MeterKind
{
    ColdWater = 0,
    HotWater = 1,
    Electricity = 2,
}

/// <summary>
/// Описание одного физического счётчика. Задаётся в настройках с телефона и
/// используется распознаванием (сколько разрядов ждать), валидацией и порталом.
/// </summary>
public sealed record MeterSpec
{
    /// <summary>Стабильный идентификатор, например "cold-water". Ключ во всех сообщениях.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required MeterKind Kind { get; init; }

    /// <summary>Единица измерения для отображения, например "м³" или "кВт·ч".</summary>
    public required string Unit { get; init; }

    /// <summary>Разрядов до запятой (обычно 5 для воды, 6 для электричества).</summary>
    public required int IntegerDigits { get; init; }

    /// <summary>Разрядов после запятой. У воды это красные барабаны, обычно 3.</summary>
    public required int FractionDigits { get; init; }

    /// <summary>
    /// Серийный номер с корпуса. Не идентификатор счётчика в системе, а перекрёстная
    /// проверка распознавания: несовпадение почти всегда значит перепутанные счётчики.
    /// </summary>
    public string? SerialNumber { get; init; }

    /// <summary>Идентификатор счётчика в личном кабинете, если он отличается от серийника.</summary>
    public string? PortalId { get; init; }

    /// <summary>Порядок в списке съёмки на телефоне.</summary>
    public int SortOrder { get; init; }

    /// <summary>Максимальное значение до переполнения барабана, например 99999.999.</summary>
    public decimal MaxValue => (decimal)Math.Pow(10, IntegerDigits) - SmallestIncrement;

    /// <summary>Цена деления: 0.001 при трёх дробных разрядах.</summary>
    public decimal SmallestIncrement => 1m / (decimal)Math.Pow(10, FractionDigits);

    /// <summary>Округление вниз до цены деления — используется прогнозом.</summary>
    public decimal RoundDown(decimal value)
    {
        decimal step = SmallestIncrement;
        return Math.Floor(value / step) * step;
    }
}
