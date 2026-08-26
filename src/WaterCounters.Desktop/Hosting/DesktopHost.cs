using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.State;
using WaterCounters.Core.Storage.Dropbox;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Processing;
using WaterCounters.Desktop.Security;
using WaterCounters.Desktop.Services;
using WaterCounters.Desktop.State;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Hosting;

/// <summary>
/// Сборка Generic Host обработчика.
///
/// Внутри WPF-приложения, а не службы Windows: нужен интерактивный сеанс, чтобы при
/// первой настройке пройти вход в кабинет в видимом браузере и привязать Dropbox.
/// </summary>
public static class DesktopHost
{
    /// <param name="storeOverride">
    /// Подмена хранилища. Нужна тестам сборки графа зависимостей: настоящее
    /// хранилище требует привязанного Dropbox, а проверять надо регистрации, и
    /// опечатка в них иначе всплыла бы только на машине пользователя.
    /// </param>
    public static IHost Build(
        DesktopOptions options,
        Func<string?> masterPassword,
        Func<IServiceProvider, IRemoteStore>? storeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(masterPassword);

        options.EnsureDirectories();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("WATERCOUNTERS_");

        ConfigureLogging(builder, options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ChangeSignal>();
        builder.Services.AddSingleton(new QueueLayout(options.DropboxRoot));

        builder.Services.AddDbContextFactory<DesktopDbContext>(db =>
            db.UseSqlite($"Data Source={options.DatabasePath}"));

        builder.Services.AddSingleton<ILocalState, LocalState>();

        builder.Services.AddSingleton(storeOverride ?? (services => CreateStore(services, options)));
        builder.Services.AddSingleton(services => new MessageQueue(
            services.GetRequiredService<IRemoteStore>(),
            services.GetRequiredService<QueueLayout>(),
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton(services => new SettingsStore(
            services.GetRequiredService<IRemoteStore>(),
            services.GetRequiredService<QueueLayout>(),
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton(services => new SecretsStore(
            services.GetRequiredService<IRemoteStore>(),
            services.GetRequiredService<QueueLayout>()));

        builder.Services.AddSingleton(services => new ReadingHistoryStore(
            services.GetRequiredService<IRemoteStore>(),
            services.GetRequiredService<QueueLayout>(),
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<ISettingsProvider>(services => new SettingsProvider(
            services.GetRequiredService<SettingsStore>(),
            services.GetRequiredService<SecretsStore>(),
            options,
            masterPassword,
            services.GetRequiredService<ILogger<SettingsProvider>>()));

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IMeterRecognizer, RecognizerProvider>();

        builder.Services.AddSingleton<IPortalGateway, PlaywrightPortalGateway>();
        builder.Services.AddSingleton<IMailer, SmtpMailer>();
        builder.Services.AddSingleton<ReadingPipeline>();

        builder.Services.AddHostedService<DropboxWatcherService>();
        builder.Services.AddHostedService<MessageProcessorService>();
        builder.Services.AddHostedService<PhotoBatchService>();
        builder.Services.AddHostedService<DeadlineWatchdogService>();
        builder.Services.AddHostedService<HealthPublisherService>();

        return builder.Build();
    }

    /// <summary>Готовит базу и первый раз читает настройки — до запуска фоновых служб.</summary>
    public static async Task WarmUpAsync(IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await LocalStateRegistration.EnsureCreatedAsync(host.Services, ct).ConfigureAwait(false);
        await host.Services.GetRequiredService<ISettingsProvider>().RefreshAsync(ct).ConfigureAwait(false);
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, DesktopOptions options)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(options.LogsDirectory, "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }

    /// <summary>
    /// Хранилище Dropbox на refresh-токене, который положила утилита привязки.
    /// Токен под DPAPI и привязан к учётной записи Windows, поэтому на каждой машине
    /// своя команда <c>login</c> — переносить файл бессмысленно.
    /// </summary>
    private static IRemoteStore CreateStore(IServiceProvider services, DesktopOptions options)
    {
        ILogger<DropboxRemoteStore> logger = services.GetRequiredService<ILogger<DropboxRemoteStore>>();

        var tokens = new DpapiRefreshTokenStore(
            File.Exists(options.DropboxTokenFile) ? options.DropboxTokenFile : DpapiRefreshTokenStore.SetupToolPath);

        string? refreshToken = tokens.GetAsync().GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new DesktopStartupException(
                "Dropbox не привязан. Выполните: dotnet run --project tools/WaterCounters.DropboxSetup -- login");
        }

        logger.LogInformation("Dropbox привязан, корень папки приложения: {Root}.", options.DropboxRoot);
        return DropboxRemoteStore.Create(refreshToken);
    }

}

public sealed class DesktopStartupException(string message) : Exception(message);
