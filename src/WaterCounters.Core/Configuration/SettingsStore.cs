using System.Text.Json;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Security;
using WaterCounters.Core.Storage;

namespace WaterCounters.Core.Configuration;

/// <summary>
/// Чтение и запись <c>/config/settings.json</c>.
///
/// Настройки редактируются с телефона и читаются обработчиком, поэтому запись
/// защищена ревизией: сторона, у которой на руках устаревшая копия, не затирает
/// более свежую правку молча.
/// </summary>
public sealed class SettingsStore(IRemoteStore store, QueueLayout layout, TimeProvider? clock = null)
{
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<AppSettings?> TryLoadAsync(CancellationToken ct = default)
    {
        byte[] content;

        try
        {
            content = await _store.DownloadAsync(_layout.SettingsPath, ct).ConfigureAwait(false);
        }
        catch (RemoteNotFoundException)
        {
            return null;
        }

        AppSettings? settings;

        try
        {
            settings = JsonSerializer.Deserialize(content, ConfigurationJsonContext.Default.AppSettings);
        }
        catch (JsonException ex)
        {
            throw new MessageFormatException($"Файл настроек повреждён: {ex.Message}");
        }

        if (settings is null)
        {
            return null;
        }

        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(settings.SchemaVersion);
        }

        return settings;
    }

    /// <summary>
    /// Настройки, а если их ещё нет — заготовка, сразу записанная в Dropbox. Без этого
    /// первый запуск обработчика не с чего начать: список счётчиков задаёт телефон,
    /// которого на первом этапе нет.
    /// </summary>
    public async Task<AppSettings> LoadOrCreateDefaultAsync(string deviceId, CancellationToken ct = default)
    {
        AppSettings? existing = await TryLoadAsync(ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        return await SaveAsync(AppSettings.CreateDefault(), deviceId, ct).ConfigureAwait(false);
    }

    /// <summary>Записывает настройки, подняв ревизию.</summary>
    /// <exception cref="SettingsConflictException">В хранилище лежит более свежая ревизия.</exception>
    public async Task<AppSettings> SaveAsync(AppSettings settings, string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        AppSettings? remote = await TryLoadAsync(ct).ConfigureAwait(false);

        if (remote is not null && remote.Revision > settings.Revision)
        {
            throw new SettingsConflictException(settings.Revision, remote.Revision);
        }

        AppSettings stamped = settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Revision = settings.Revision + 1,
            UpdatedUtc = _clock.GetUtcNow(),
            UpdatedBy = deviceId,
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(stamped, ConfigurationJsonContext.Default.AppSettings);
        await _store.UploadAsync(_layout.SettingsPath, payload, RemoteWriteMode.Overwrite, ct).ConfigureAwait(false);
        return stamped;
    }
}

/// <summary>
/// Чтение и запись <c>/config/secrets.enc</c>. Мастер-пароль вводится один раз на
/// каждом устройстве и в Dropbox не попадает ни в каком виде.
/// </summary>
public sealed class SecretsStore(IRemoteStore store, QueueLayout layout)
{
    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    /// <exception cref="SecretsIntegrityException">Неверный мастер-пароль либо файл подменён.</exception>
    public async Task<AppSecrets?> TryLoadAsync(string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        byte[] envelope;

        try
        {
            envelope = await _store.DownloadAsync(_layout.SecretsPath, ct).ConfigureAwait(false);
        }
        catch (RemoteNotFoundException)
        {
            return null;
        }

        string json = SecretsProtector.UnprotectToString(envelope, passphrase);

        try
        {
            return JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.AppSecrets);
        }
        catch (JsonException ex)
        {
            throw new MessageFormatException($"Расшифрованные секреты не разбираются: {ex.Message}");
        }
    }

    public async Task SaveAsync(AppSecrets secrets, string passphrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        string json = JsonSerializer.Serialize(secrets, ConfigurationJsonContext.Default.AppSecrets);
        byte[] envelope = SecretsProtector.Protect(json, passphrase);

        await _store.UploadAsync(_layout.SecretsPath, envelope, RemoteWriteMode.Overwrite, ct).ConfigureAwait(false);
    }
}

public sealed class SettingsConflictException(int local, int remote)
    : Exception($"В Dropbox лежит более свежая ревизия настроек ({remote}), чем локальная ({local}). Перечитайте настройки.")
{
    public int LocalRevision { get; } = local;

    public int RemoteRevision { get; } = remote;
}
