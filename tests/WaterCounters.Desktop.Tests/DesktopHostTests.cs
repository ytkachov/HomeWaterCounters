using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.State;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Hosting;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.State;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Tests;

/// <summary>
/// Граф зависимостей хоста. Опечатка в регистрации не ловится компилятором и всплыла
/// бы только при запуске на машине пользователя — там, где обработчик как раз и
/// должен просто работать.
/// </summary>
public class DesktopHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wc-host-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EveryServiceResolves()
    {
        using IHost host = Build();

        Assert.NotNull(host.Services.GetRequiredService<ReadingPipeline>());
        Assert.NotNull(host.Services.GetRequiredService<IMeterRecognizer>());
        Assert.NotNull(host.Services.GetRequiredService<IPortalGateway>());
        Assert.NotNull(host.Services.GetRequiredService<IMailer>());
        Assert.NotNull(host.Services.GetRequiredService<ILocalState>());
        Assert.NotNull(host.Services.GetRequiredService<MessageQueue>());
        Assert.NotNull(host.Services.GetRequiredService<ReadingHistoryStore>());
        Assert.NotNull(host.Services.GetRequiredService<SettingsStore>());
        Assert.NotNull(host.Services.GetRequiredService<SecretsStore>());
        Assert.NotNull(host.Services.GetRequiredService<ISettingsProvider>());
    }

    [Fact]
    public void AllFiveBackgroundServicesAreRegistered()
    {
        using IHost host = Build();

        string[] names = [.. host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name)];

        Assert.Contains("DropboxWatcherService", names);
        Assert.Contains("MessageProcessorService", names);
        Assert.Contains("PhotoBatchService", names);
        Assert.Contains("DeadlineWatchdogService", names);
        Assert.Contains("HealthPublisherService", names);
    }

    [Fact]
    public async Task WarmUpCreatesTheDatabaseAndSeedsSettings()
    {
        var store = new InMemoryRemoteStore();
        using IHost host = Build(store);

        await DesktopHost.WarmUpAsync(host);

        // Первый запуск сам кладёт заготовку настроек: список счётчиков задаёт телефон,
        // которого на первом этапе нет, и начинать иначе не с чего.
        Assert.True(await store.ExistsAsync("/config/settings.json"));

        AppSettings settings = host.Services.GetRequiredService<ISettingsProvider>().Current;
        Assert.Equal(3, settings.Meters.Count);
        Assert.True(settings.Portal.DryRun);

        Assert.True(File.Exists(Path.Combine(_directory, "state.db")));
    }

    private IHost Build(InMemoryRemoteStore? store = null)
    {
        store ??= new InMemoryRemoteStore();

        var options = new DesktopOptions
        {
            DeviceId = "test-desktop",
            DataDirectory = _directory,
        };

        return DesktopHost.Build(options, () => null, _ => store);
    }
}

/// <summary>
/// Модель и адрес VLM-хоста правятся с телефона, и правка обязана вступать в силу
/// без перезапуска обработчика.
/// </summary>
public class RecognizerProviderTests
{
    [Fact]
    public async Task ChangedSettingsTakeEffectWithoutARestart()
    {
        var settings = new StubSettingsProvider(DesktopTestData.Settings() with
        {
            Recognition = new RecognitionSettings { Provider = RecognitionProvider.Stub },
        });

        var provider = new RecognizerProvider(
            settings,
            new SingleClientFactory(),
            new DesktopOptions { FixturesDirectory = Path.Combine(Path.GetTempPath(), "wc-no-fixtures") },
            NullLogger<RecognizerProvider>.Instance);

        // Заглушка без фикстур честно отвечает «не знаю» и ничего не выдумывает.
        RecognitionResult viaStub = await provider.RecognizeAsync(
            DesktopTestData.ColdWater, new byte[] { 1 }, CancellationToken.None);

        Assert.False(viaStub.IsSuccessful);

        settings.Current = settings.Current with
        {
            Recognition = new RecognitionSettings
            {
                Provider = RecognitionProvider.Ollama,
                Endpoint = "http://127.0.0.1:1",
                EnsemblePasses = 1,
                TimeoutSeconds = 10,

                // Без предобработки: тест проверяет пересборку распознавателя, и
                // спотыкаться он должен о закрытый порт, а не о ненастоящий JPEG.
                Preprocess = false,
            },
        };

        // Тот же объект после правки настроек уже ходит по сети — и спотыкается о
        // заведомо закрытый порт. Значит, распознаватель пересобран.
        RecognitionException error = await Assert.ThrowsAsync<RecognitionException>(() =>
            provider.RecognizeAsync(DesktopTestData.ColdWater, new byte[] { 1 }, CancellationToken.None));

        Assert.Contains("127.0.0.1:1", error.Message, StringComparison.Ordinal);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
