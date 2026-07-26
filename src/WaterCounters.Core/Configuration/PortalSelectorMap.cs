using System.Text.Json.Serialization;

namespace WaterCounters.Core.Configuration;

/// <summary>
/// Карта селекторов конкретного кабинета. Живёт в настройках, а не в коде: когда
/// поставщик поменяет вёрстку, правка делается с телефона и не требует пересборки
/// и переустановки десктопа.
///
/// В шаблонах доступна подстановка <c>{portalId}</c> — идентификатор счётчика.
/// </summary>
public sealed record PortalSelectorMap
{
    public required string Name { get; init; }

    public required string LoginUrl { get; init; }

    /// <summary>Страница ввода показаний. Если пусто — считаем, что она открывается сама после входа.</summary>
    public string? ReadingsUrl { get; init; }

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

    public string ReadingInputFor(string portalId) =>
        ReadingInput.Replace("{portalId}", portalId, StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalSelectorMap))]
public sealed partial class PortalJsonContext : JsonSerializerContext;
