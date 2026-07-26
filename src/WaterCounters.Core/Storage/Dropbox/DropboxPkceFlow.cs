using System.Security.Cryptography;
using System.Text;
using Dropbox.Api;

namespace WaterCounters.Core.Storage.Dropbox;

/// <summary>
/// Поток авторизации Authorization Code + PKCE.
///
/// Приложение без серверной части не может хранить app secret, поэтому вместо него
/// используется одноразовая пара verifier/challenge: challenge уходит в браузер,
/// verifier остаётся на устройстве, и перехваченный код авторизации сам по себе
/// бесполезен. Запрашивается offline-доступ, чтобы получить refresh-токен —
/// access-токены Dropbox живут около четырёх часов, переавторизация каждый раз
/// сделала бы автоматическую работу невозможной.
/// </summary>
public sealed class DropboxPkceFlow
{
    private readonly string _appKey;
    private readonly string _redirectUri;

    /// <param name="appKey">
    /// Ключ приложения. Null — взять настроенный на этапе сборки
    /// (<see cref="DropboxAppInfo.AppKey"/>); задаётся явно только в тестах.
    /// </param>
    public DropboxPkceFlow(string redirectUri, string? appKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        _appKey = appKey ?? DropboxAppInfo.AppKey;
        _redirectUri = redirectUri;
    }

    /// <summary>Готовит URL авторизации и одноразовые секреты, которые нужно удержать до обмена кода.</summary>
    public DropboxAuthorizationRequest CreateRequest()
    {
        string codeVerifier = CreateCodeVerifier();
        string codeChallenge = CreateCodeChallenge(codeVerifier);
        string state = CreateState();

        Uri authorizeUri = DropboxOAuth2Helper.GetAuthorizeUri(
            OAuthResponseType.Code,
            _appKey,
            _redirectUri,
            state,
            tokenAccessType: TokenAccessType.Offline,
            scopeList: [.. DropboxAppInfo.Scopes],
            includeGrantedScopes: IncludeGrantedScopes.None,
            codeChallenge: codeChallenge);

        return new DropboxAuthorizationRequest(authorizeUri, codeVerifier, state);
    }

    /// <summary>
    /// Обменивает код авторизации на токены. <paramref name="returnedState"/> обязателен:
    /// без сверки state редирект можно подделать и подсунуть чужой код.
    /// </summary>
    public async Task<DropboxTokens> ExchangeAsync(
        DropboxAuthorizationRequest request,
        string code,
        string? returnedState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (!string.Equals(request.State, returnedState, StringComparison.Ordinal))
        {
            throw new DropboxAuthException("Параметр state в ответе не совпадает с отправленным — редирект подделан.");
        }

        ct.ThrowIfCancellationRequested();

        OAuth2Response response;

        try
        {
            response = await DropboxOAuth2Helper.ProcessCodeFlowAsync(
                    code,
                    _appKey,
                    appSecret: null,
                    redirectUri: _redirectUri,
                    codeVerifier: request.CodeVerifier)
                .ConfigureAwait(false);
        }
        catch (OAuth2Exception ex)
        {
            throw new DropboxAuthException($"Dropbox отклонил обмен кода: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            throw new DropboxAuthException(
                "Dropbox не вернул refresh-токен. Убедитесь, что запрошен offline-доступ.");
        }

        return new DropboxTokens(response.AccessToken, response.RefreshToken, response.ExpiresAt);
    }

    /// <summary>Извлекает code и state из URL редиректа.</summary>
    public static DropboxRedirectResult ParseRedirect(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        Dictionary<string, string> parameters = ParseQuery(redirectUri.Query);

        if (parameters.TryGetValue("error", out string? error))
        {
            parameters.TryGetValue("error_description", out string? description);
            return new DropboxRedirectResult(null, null, description ?? error);
        }

        parameters.TryGetValue("code", out string? code);
        parameters.TryGetValue("state", out string? state);

        return code is null
            ? new DropboxRedirectResult(null, state, "В ответе нет параметра code.")
            : new DropboxRedirectResult(code, state, null);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separator]);
            string value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier), hash);
        return Base64Url(hash);
    }

    private static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Подготовленный запрос авторизации. Verifier и state нужно удержать до возврата из браузера.</summary>
public sealed record DropboxAuthorizationRequest(Uri AuthorizeUri, string CodeVerifier, string State);

public sealed record DropboxRedirectResult(string? Code, string? State, string? Error)
{
    public bool IsSuccess => Code is not null && Error is null;
}

public sealed record DropboxTokens(string AccessToken, string RefreshToken, DateTime? ExpiresAt);

public sealed class DropboxAuthException(string message, Exception? inner = null) : Exception(message, inner);
