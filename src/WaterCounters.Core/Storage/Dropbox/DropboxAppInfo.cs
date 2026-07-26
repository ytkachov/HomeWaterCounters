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
    /// App key. Кидает внятную ошибку, если сборка прошла без настроенного ключа —
    /// иначе Dropbox ответил бы невразумительным «invalid client_id» на первом же входе.
    /// </summary>
    public static string AppKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(GeneratedDropboxAppKey.Value))
            {
                return GeneratedDropboxAppKey.Value;
            }

            // Запасной путь для десктопа и CLI: на Android переменных окружения нет,
            // там работает только подстановка на этапе сборки.
            string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);

            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            throw new InvalidOperationException(
                "App key Dropbox не задан. Скопируйте dropbox.local.props.example в " +
                $"dropbox.local.props и укажите ключ, либо задайте переменную окружения {EnvironmentVariableName}.");
        }
    }

    /// <summary>Настроен ли ключ. Позволяет показать понятный экран вместо исключения.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GeneratedDropboxAppKey.Value) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariableName));

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
