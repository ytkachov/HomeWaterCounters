using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WaterCounters.Core.Storage.Dropbox;

namespace WaterCounters.DropboxSetup;

/// <summary>
/// Refresh-токен на диске под DPAPI в области текущего пользователя: расшифровать
/// файл сможет только та же учётная запись Windows на той же машине. Файл рядом с
/// приложением, скопированный на другой компьютер, бесполезен.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore(string? path = null) : IRefreshTokenStore
{
    private static readonly byte[] Entropy = "WaterCounters.Dropbox.RefreshToken.v1"u8.ToArray();

    private readonly string _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaterCounters",
        "dropbox-token.dat");

    public string TokenPath => _path;

    public Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(_path);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException)
        {
            // Файл от другого пользователя или другой машины — считаем, что токена нет.
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(_path, protectedBytes);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
