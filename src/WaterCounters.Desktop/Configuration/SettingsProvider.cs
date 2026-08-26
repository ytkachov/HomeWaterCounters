using System.IO;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Scheduling;
using WaterCounters.Core.Security;

namespace WaterCounters.Desktop.Configuration;

/// <summary>Текущие настройки и секреты для всех служб обработчика.</summary>
public interface ISettingsProvider
{
    AppSettings Current { get; }

    /// <summary>Расшифрованные секреты, либо null, если мастер-пароль не задан или файла нет.</summary>
    AppSecrets? Secrets { get; }

    SubmissionSchedule Schedule { get; }

    /// <summary>Перечитывает настройки из Dropbox. Вызывается при старте и по сигналу об изменении.</summary>
    Task<AppSettings> RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// Кэш настроек и секретов поверх Dropbox.
///
/// Настройки редактируются с телефона, поэтому перечитываются по сигналу наблюдателя,
/// а не читаются на каждое обращение: смена вёрстки кабинета не должна требовать
/// перезапуска обработчика, но и лишний сетевой запрос на каждый чих не нужен.
/// </summary>
public sealed class SettingsProvider(
    SettingsStore settings,
    SecretsStore secrets,
    DesktopOptions options,
    Func<string?> masterPassword,
    ILogger<SettingsProvider> logger) : ISettingsProvider
{
    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly SecretsStore _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<string?> _masterPassword = masterPassword ?? throw new ArgumentNullException(nameof(masterPassword));
    private readonly ILogger<SettingsProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private AppSettings _current = AppSettings.CreateDefault();
    private AppSecrets? _decrypted;
    private SubmissionSchedule _schedule = new();

    public AppSettings Current => Volatile.Read(ref _current);

    public AppSecrets? Secrets => Volatile.Read(ref _decrypted);

    public SubmissionSchedule Schedule => Volatile.Read(ref _schedule);

    public async Task<AppSettings> RefreshAsync(CancellationToken ct = default)
    {
        AppSettings loaded = await _settings.LoadOrCreateDefaultAsync(_options.DeviceId, ct).ConfigureAwait(false);

        Volatile.Write(ref _current, loaded);
        Volatile.Write(ref _schedule, new SubmissionSchedule(loaded.Schedule));

        _logger.LogInformation(
            "Настройки перечитаны: ревизия {Revision}, счётчиков {Meters}, режим проверки {DryRun}.",
            loaded.Revision,
            loaded.Meters.Count,
            loaded.Portal.DryRun);

        await RefreshSecretsAsync(ct).ConfigureAwait(false);
        return loaded;
    }

    private async Task RefreshSecretsAsync(CancellationToken ct)
    {
        string? passphrase = _masterPassword();

        if (string.IsNullOrEmpty(passphrase))
        {
            Volatile.Write(ref _decrypted, null);
            _logger.LogWarning("Мастер-пароль не задан — доступ к кабинету и SMTP недоступен.");
            return;
        }

        try
        {
            AppSecrets? loaded = await _secrets.TryLoadAsync(passphrase, ct).ConfigureAwait(false);
            Volatile.Write(ref _decrypted, loaded);

            if (loaded is null)
            {
                _logger.LogWarning("Файл секретов ещё не создан — вход в кабинет невозможен.");
            }
        }
        catch (SecretsIntegrityException)
        {
            // Неверный пароль и подменённый файл на этом уровне неразличимы, и это
            // правильно: и то и другое означает «секретами пользоваться нельзя».
            Volatile.Write(ref _decrypted, null);
            _logger.LogError("Секреты не расшифровываются: неверный мастер-пароль либо файл повреждён.");
        }
        catch (Exception ex) when (ex is SecretsFormatException or MessageFormatException)
        {
            Volatile.Write(ref _decrypted, null);
            _logger.LogError(ex, "Файл секретов не разбирается.");
        }
    }
}
