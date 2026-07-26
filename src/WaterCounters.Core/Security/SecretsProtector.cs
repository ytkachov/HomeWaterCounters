using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WaterCounters.Core.Security;

/// <summary>
/// Шифрование блоба секретов (доступ к кабинету, пароль SMTP) перед записью в Dropbox.
///
/// AES-256-GCM с ключом из PBKDF2-SHA256. Заголовок входит в associated data, поэтому
/// подмена числа итераций или версии формата ломает проверку тега так же, как подмена
/// шифротекста — Dropbox не видит ни секретов, ни возможности незаметно их подправить.
/// </summary>
public static class SecretsProtector
{
    public const int CurrentVersion = 1;
    public const int DefaultIterations = 600_000;

    private const int MagicLength = 4;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = MagicLength + 1 + 4;

    /// <summary>Сигнатура "WaterCounters Secrets Container", ровно <see cref="MagicLength"/> байт.</summary>
    private static ReadOnlySpan<byte> Magic => "WCSC"u8;

    public static byte[] Protect(ReadOnlySpan<byte> plaintext, string passphrase, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 100_000);

        Span<byte> salt = stackalloc byte[SaltLength];
        Span<byte> nonce = stackalloc byte[NonceLength];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        byte[] result = new byte[HeaderLength + SaltLength + NonceLength + TagLength + plaintext.Length];
        WriteHeader(result, iterations);

        salt.CopyTo(result.AsSpan(HeaderLength));
        nonce.CopyTo(result.AsSpan(HeaderLength + SaltLength));

        Span<byte> tag = result.AsSpan(HeaderLength + SaltLength + NonceLength, TagLength);
        Span<byte> ciphertext = result.AsSpan(HeaderLength + SaltLength + NonceLength + TagLength);

        Span<byte> key = stackalloc byte[32];

        try
        {
            DeriveKey(passphrase, salt, iterations, key);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, result.AsSpan(0, HeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return result;
    }

    public static byte[] Protect(string plaintext, string passphrase, int iterations = DefaultIterations) =>
        Protect(Encoding.UTF8.GetBytes(plaintext), passphrase, iterations);

    /// <exception cref="SecretsFormatException">Блоб повреждён или не той версии.</exception>
    /// <exception cref="SecretsIntegrityException">Неверный пароль либо содержимое подменено.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> envelope, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        int minimum = HeaderLength + SaltLength + NonceLength + TagLength;

        if (envelope.Length < minimum)
        {
            throw new SecretsFormatException($"Блоб короче минимального размера ({minimum} байт).");
        }

        if (!envelope[..MagicLength].SequenceEqual(Magic[..MagicLength]))
        {
            throw new SecretsFormatException("Неверная сигнатура — это не блоб секретов WaterCounters.");
        }

        byte version = envelope[MagicLength];

        if (version != CurrentVersion)
        {
            throw new SecretsFormatException($"Версия формата {version} не поддерживается (ожидалась {CurrentVersion}).");
        }

        int iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(MagicLength + 1, 4));

        if (iterations is < 1 or > 10_000_000)
        {
            throw new SecretsFormatException($"Некорректное число итераций KDF: {iterations}.");
        }

        ReadOnlySpan<byte> salt = envelope.Slice(HeaderLength, SaltLength);
        ReadOnlySpan<byte> nonce = envelope.Slice(HeaderLength + SaltLength, NonceLength);
        ReadOnlySpan<byte> tag = envelope.Slice(HeaderLength + SaltLength + NonceLength, TagLength);
        ReadOnlySpan<byte> ciphertext = envelope[(HeaderLength + SaltLength + NonceLength + TagLength)..];

        byte[] plaintext = new byte[ciphertext.Length];
        Span<byte> key = stackalloc byte[32];

        try
        {
            DeriveKey(passphrase, salt, iterations, key);
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, envelope[..HeaderLength]);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SecretsIntegrityException(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    public static string UnprotectToString(ReadOnlySpan<byte> envelope, string passphrase) =>
        Encoding.UTF8.GetString(Unprotect(envelope, passphrase));

    private static void WriteHeader(Span<byte> destination, int iterations)
    {
        Magic[..MagicLength].CopyTo(destination);
        destination[MagicLength] = CurrentVersion;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(MagicLength + 1, 4), iterations);
    }

    private static void DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations, Span<byte> key) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            key,
            iterations,
            HashAlgorithmName.SHA256);
}

public sealed class SecretsFormatException(string message) : Exception(message);

public sealed class SecretsIntegrityException(Exception? inner = null)
    : Exception("Не удалось расшифровать секреты: неверный пароль либо файл был изменён.", inner);
