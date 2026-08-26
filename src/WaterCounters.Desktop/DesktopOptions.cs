using System.IO;
namespace WaterCounters.Desktop;

/// <summary>
/// Локальные настройки машины обработчика: пути, интервалы, режим браузера.
///
/// Сознательно отделены от <c>/config/settings.json</c> в Dropbox. Там живёт то, что
/// одинаково для всех устройств и редактируется с телефона; здесь — то, что у каждой
/// машины своё и телефону неизвестно.
/// </summary>
public sealed record DesktopOptions
{
    public const string SectionName = "Desktop";

    /// <summary>Подпись устройства в сообщениях очереди.</summary>
    public string DeviceId { get; init; } = "desktop-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>Корень папки приложения в Dropbox. Меняется только для прогонов на отдельной ветке.</summary>
    public string DropboxRoot { get; init; } = "/";

    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaterCounters");

    /// <summary>Фикстуры для заглушки распознавания — путь имеет смысл только при provider = Stub.</summary>
    public string FixturesDirectory { get; init; } = Path.Combine("fixtures", "meters");

    /// <summary>Как часто перепроверять папку фотографий, если longpoll молчит.</summary>
    public TimeSpan PhotoScanInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Ежечасно — как в спецификации: срок и льготный период проверяются по календарю.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Пауза перед повторным опросом, когда longpoll недоступен.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Показывать браузер. Нужно один раз — пройти 2FA или капчу вручную; дальше
    /// cookie-сессия живёт в профиле и прогоны идут молча.
    /// </summary>
    public bool ShowBrowser { get; init; }

    /// <summary>
    /// Мастер-пароль от secrets.enc в открытом виде. Только для отладки: обычный путь —
    /// ввести пароль один раз в окне и сохранить под DPAPI.
    /// </summary>
    public string? MasterPassword { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "state.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Профиль браузера: именно он переживает перезапуск и хранит cookie кабинета.</summary>
    public string PortalProfileDirectory => Path.Combine(DataDirectory, "portal-profile");

    /// <summary>Скриншоты и trace падений — без них разбирать сбой автоматики на чужом сайте нечем.</summary>
    public string DiagnosticsDirectory => Path.Combine(DataDirectory, "portal-diagnostics");

    public string MasterPasswordFile => Path.Combine(DataDirectory, "master-password.dat");

    public string DropboxTokenFile => Path.Combine(DataDirectory, "dropbox-token.dat");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
