namespace WaterCounters.Core.Storage.Dropbox;

/// <summary>
/// Параметры зарегистрированного приложения Dropbox.
///
/// App key при потоке PKCE не является секретом в криптографическом смысле: он
/// неизбежно попадает в собранное клиентское приложение, а защиту даёт code_verifier,
/// который генерируется на устройстве и никогда его не покидает. App secret не
/// используется вовсе — именно поэтому мобильное приложение ходит в Dropbox напрямую,
/// без своего сервера.
///
/// Тем не менее ключ идентифицирует конкретное приложение, поэтому в репозитории он
/// не хранится: значение приходит из <c>dropbox.local.props</c> или переменной
/// окружения <c>WATERCOUNTERS_DROPBOX_APP_KEY</c> и подставляется в константу на
/// этапе сборки (см. цель GenerateDropboxAppKey).
///
/// Тип приложения в консоли Dropbox — Scoped access → App folder: приложение видит
/// только собственную папку, а не весь диск.
/// </summary>
public static class DropboxAppInfo
{
    private const string EnvironmentVariableName = "WATERCOUNTERS_DROPBOX_APP_KEY";

    /// <summary>
    /// Заглушка из dropbox.local.props.example. Проверяется явно: непустое, но
    /// заведомо нерабочее значение иначе доезжает до Dropbox и возвращается оттуда
    /// как «Invalid client_id» — ошибка, по которой невозможно догадаться, что просто
    /// не заменили строку в конфиге.
    /// </summary>
    public const string Placeholder = "PUT-YOUR-DROPBOX-APP-KEY-HERE";

    /// <summary>
    /// App key. Кидает внятную ошибку, если ключ не задан или остался заглушкой.
    /// </summary>
    public static string AppKey =>
        Resolve() ?? throw new InvalidOperationException(ConfigurationHint);

    /// <summary>Настроен ли ключ. Позволяет показать понятный экран вместо исключения.</summary>
    public static bool IsConfigured => Resolve() is not null;

    /// <summary>Текст с указанием, что именно сделать. Один на все места, где это нужно сказать.</summary>
    public static string ConfigurationHint =>
        "App key Dropbox не задан или остался заглушкой. Скопируйте dropbox.local.props.example " +
        "в dropbox.local.props, подставьте ключ из https://www.dropbox.com/developers/apps " +
        $"и пересоберите решение. Либо задайте переменную окружения {EnvironmentVariableName}.";

    private static string? Resolve()
    {
        // Подстановка на этапе сборки — единственный путь для Android: переменных
        // окружения там нет. Переменная окружения остаётся запасным вариантом для
        // десктопа и утилит.
        string?[] candidates =
        [
            GeneratedDropboxAppKey.Value,
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
        ];

        foreach (string? candidate in candidates)
        {
            if (IsUsable(candidate))
            {
                return candidate!.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Ключи Dropbox — короткие строки из букв и цифр ASCII. Отсечение всего
    /// остального ловит не только известную заглушку, но и любую другую подстановку
    /// вроде «ваш-ключ», не дожидаясь ответа сервера.
    /// </summary>
    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, Placeholder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Разрешения, запрашиваемые при авторизации. Больше не нужно ничего.</summary>
    public static IReadOnlyList<string> Scopes { get; } =
    [
        "files.metadata.read",
        "files.content.read",
        "files.content.write",
    ];

    /// <summary>Схема редиректа для мобильного приложения. Должна совпадать с настройкой в консоли Dropbox.</summary>
    public const string MobileRedirectUri = "wc-app://auth";

    /// <summary>
    /// Десктоп ловит редирект на локальный слушатель. Порт фиксирован, потому что
    /// Dropbox требует точного совпадения redirect URI со списком в консоли.
    /// </summary>
    public const string DesktopRedirectUri = "http://localhost:53682/oauth";
}
