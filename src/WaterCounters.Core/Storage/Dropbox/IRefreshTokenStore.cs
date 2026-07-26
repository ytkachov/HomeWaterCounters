namespace WaterCounters.Core.Storage.Dropbox;

/// <summary>
/// Хранилище refresh-токена Dropbox. Реализации платформенные и намеренно вынесены
/// из Core: на Android это Android Keystore через SecureStorage, на Windows — DPAPI.
/// Токен даёт полный доступ к папке приложения, поэтому в обычном файле ему не место.
/// </summary>
public interface IRefreshTokenStore
{
    Task<string?> GetAsync(CancellationToken ct = default);

    Task SaveAsync(string refreshToken, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Реализация в памяти — для тестов и для отладки без платформенного хранилища.</summary>
public sealed class InMemoryRefreshTokenStore(string? initialToken = null) : IRefreshTokenStore
{
    private string? _token = initialToken;

    public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(_token);

    public Task SaveAsync(string refreshToken, CancellationToken ct = default)
    {
        _token = refreshToken;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _token = null;
        return Task.CompletedTask;
    }
}
