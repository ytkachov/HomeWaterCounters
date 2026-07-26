using System.Text;
using WaterCounters.Core.Security;

namespace WaterCounters.Core.Tests;

public class SecretsProtectorTests
{
    private const string Passphrase = "правильный-пароль-42";

    // Полные 600k итераций на каждый тест — это секунды впустую; криптостойкость
    // проверяется не числом итераций, а тем, что заголовок входит в AAD.
    private const int FastIterations = 100_000;

    private const string Secret = """{"portalLogin":"user","portalPassword":"пароль","smtpPassword":"s3cret"}""";

    [Fact]
    public void RoundTrip_RestoresOriginal()
    {
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);

        Assert.Equal(Secret, SecretsProtector.UnprotectToString(blob, Passphrase));
    }

    [Fact]
    public void Protect_DoesNotLeakPlaintext()
    {
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);
        string asText = Encoding.UTF8.GetString(blob);

        Assert.DoesNotContain("пароль", asText, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_ProducesDifferentBlobsForSameInput()
    {
        // Соль и nonce случайны на каждую запись — одинаковый шифротекст выдал бы
        // тому, кто видит папку Dropbox, факт «секреты не менялись».
        byte[] first = SecretsProtector.Protect(Secret, Passphrase, FastIterations);
        byte[] second = SecretsProtector.Protect(Secret, Passphrase, FastIterations);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Unprotect_WrongPassphrase_Throws()
    {
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);

        Assert.Throws<SecretsIntegrityException>(
            () => SecretsProtector.UnprotectToString(blob, "неверный-пароль"));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);
        blob[^1] ^= 0xFF;

        Assert.Throws<SecretsIntegrityException>(() => SecretsProtector.Unprotect(blob, Passphrase));
    }

    [Fact]
    public void Unprotect_TamperedIterationCount_Throws()
    {
        // Заголовок входит в associated data: понизить стоимость перебора,
        // подправив число итераций в файле, не получится.
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);
        blob[5] ^= 0x01;

        Assert.ThrowsAny<Exception>(() => SecretsProtector.Unprotect(blob, Passphrase));
    }

    [Fact]
    public void Unprotect_ForeignBlob_ReportsFormatError()
    {
        Assert.Throws<SecretsFormatException>(
            () => SecretsProtector.Unprotect(new byte[64], Passphrase));
    }

    [Fact]
    public void Unprotect_TruncatedBlob_ReportsFormatError()
    {
        byte[] blob = SecretsProtector.Protect(Secret, Passphrase, FastIterations);

        Assert.Throws<SecretsFormatException>(
            () => SecretsProtector.Unprotect(blob.AsSpan(0, 10), Passphrase));
    }

    [Fact]
    public void Protect_EmptyPayloadIsSupported()
    {
        byte[] blob = SecretsProtector.Protect(string.Empty, Passphrase, FastIterations);

        Assert.Equal(string.Empty, SecretsProtector.UnprotectToString(blob, Passphrase));
    }

    [Fact]
    public void Protect_RejectsWeakIterationCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SecretsProtector.Protect(Secret, Passphrase, iterations: 1000));
    }

    [Fact]
    public void Protect_RejectsEmptyPassphrase()
    {
        Assert.Throws<ArgumentException>(() => SecretsProtector.Protect(Secret, string.Empty));
    }
}
