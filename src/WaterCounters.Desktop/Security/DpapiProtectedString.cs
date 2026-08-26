using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WaterCounters.Core.Storage.Dropbox;

namespace WaterCounters.Desktop.Security;

/// <summary>
/// Строка на диске под DPAPI в области текущего пользователя: расшифровать файл
/// сможет только та же учётная запись Windows на той же машине. Скопированный на
/// другой компьютер файл бесполезен — именно поэтому ни refresh-токен Dropbox, ни
/// мастер-пароль не нужно и не следует переносить между машинами.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtectedString(string path, string purpose)
{
    private readonly string _path = !string.IsNullOrWhiteSpace(path)
        ? path
        : throw new ArgumentException("Путь к файлу обязателен.", nameof(path));

    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), _entropy, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Файл от другого пользователя, с другой машины или повреждён — считаем,
            // что значения нет. Иначе обработчик не запустился бы никогда.
            return null;
        }
    }

    public void Write(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        File.WriteAllBytes(
            _path,
            ProtectedData.Protect(Encoding.UTF8.GetBytes(value), _entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

/// <summary>
/// Refresh-токен Dropbox под DPAPI. Формат файла тот же, что пишет утилита
/// <c>WaterCounters.DropboxSetup</c>: обработчик читает результат её команды
/// <c>login</c>, отдельная авторизация ему не нужна.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiRefreshTokenStore(string path) : IRefreshTokenStore
{
    /// <summary>Энтропия обязана совпадать с DropboxSetup — иначе файл не расшифруется.</summary>
    private const string Purpose = "WaterCounters.Dropbox.RefreshToken.v1";

    private readonly DpapiProtectedString _file = new(path, Purpose);

    /// <summary>Путь, по которому токен кладёт утилита привязки.</summary>
    public static string SetupToolPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaterCounters",
        "dropbox-token.dat");

    public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(_file.Read());

    public Task SaveAsync(string refreshToken, CancellationToken ct = default)
    {
        _file.Write(refreshToken);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _file.Clear();
        return Task.CompletedTask;
    }
}
