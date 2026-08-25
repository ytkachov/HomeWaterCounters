namespace WaterCounters.Portal.Tests;

/// <summary>
/// Подготовка окружения перед первым тестом: браузер Playwright и уборка мусора
/// от прошлых прогонов.
///
/// Без автоустановки на свежей машине весь набор падает с «Executable doesn't exist»,
/// и причина выглядит как поломка адаптера, хотя дело в незакачанном браузере.
/// Полагаться на инструкцию нельзя: команду установки можно выполнить только после
/// сборки, то есть ровно тогда, когда `dotnet test` уже запущен и уже упал.
/// </summary>
public sealed class PlaywrightBrowsersFixture
{
    /// <summary>Общий корень для профилей браузера, создаваемых тестами.</summary>
    public static string ProfileRoot { get; } = Path.Combine(Path.GetTempPath(), "wc-portal-tests");

    public PlaywrightBrowsersFixture()
    {
        EnsureBrowsersInstalled();
        RemoveStaleProfiles();
    }

    private static void EnsureBrowsersInstalled()
    {
        // Проверка наличия стоит миллисекунды, а прогон установщика — около четырёх
        // секунд даже вхолостую. На машине разработчика это цена каждого запуска тестов.
        if (BrowsersPresent())
        {
            return;
        }

        // chromium тянет за собой и chrome-headless-shell, которым запускается
        // headless-режим — отдельно его ставить не нужно.
        int exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Не удалось установить браузер Playwright (код {exitCode}). " +
                "Выполните вручную: tests/WaterCounters.Portal.Tests/bin/Debug/net8.0/playwright.ps1 install chromium");
        }
    }

    private static bool BrowsersPresent()
    {
        string root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

        if (!Directory.Exists(root))
        {
            return false;
        }

        // Нужны обе части: headless-режим запускается через chrome-headless-shell,
        // и именно его отсутствие даёт «Executable doesn't exist» при полном chromium.
        return Directory.EnumerateDirectories(root, "chromium-*").Any()
            && Directory.EnumerateDirectories(root, "chromium_headless_shell-*").Any();
    }

    /// <summary>
    /// Профиль удаляется в Dispose теста, но браузер может ещё держать файлы, и
    /// удаление тихо не проходит. За сотню прогонов это накапливается, поэтому
    /// подчищаем остатки на старте, когда точно ничего не запущено.
    /// </summary>
    private static void RemoveStaleProfiles()
    {
        if (!Directory.Exists(ProfileRoot))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(ProfileRoot))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занят другим процессом — не наша забота, попробуем в следующий раз.
            }
        }
    }
}

/// <summary>Общая на весь набор: подготовка выполняется один раз, а не перед каждым тестом.</summary>
[CollectionDefinition(Name)]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightBrowsersFixture>
{
    public const string Name = "playwright";
}
