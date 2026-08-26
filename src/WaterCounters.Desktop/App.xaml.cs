using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WaterCounters.Desktop.Hosting;
using WaterCounters.Desktop.Security;
using WaterCounters.Desktop.Services;

namespace WaterCounters.Desktop;

/// <summary>
/// Оболочка обработчика: иконка в трее и Generic Host с фоновыми службами внутри.
///
/// Приложение, а не служба Windows, потому что интерактивный сеанс нужен по существу:
/// при первой настройке вход в кабинет проходит в видимом браузере, а привязка Dropbox
/// открывает браузер для OAuth.
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Вторая копия обработчика не сломала бы очередь — её защищает атомарный Move, —
    /// но удвоила бы обращения к модели и к кабинету. Мьютекс нужен не ради
    /// корректности, а чтобы не жечь GPU и не стучаться дважды на чужой сайт.
    /// </summary>
    private readonly Mutex _single = new(initiallyOwned: false, @"Global\WaterCounters.Desktop");

    private bool _isOnlyInstance;

    private IHost? _host;
    private TrayPresenter? _tray;
    private DesktopOptions _options = new();
    private DpapiProtectedString? _passwordFile;
    private string? _masterPassword;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _isOnlyInstance = _single.WaitOne(TimeSpan.Zero, exitContext: false);

        if (!_isOnlyInstance)
        {
            System.Windows.MessageBox.Show(
                "Обработчик уже запущен — иконка в области уведомлений.",
                "WaterCounters",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown(0);
            return;
        }

        try
        {
            _options = LoadOptions();
            _passwordFile = new DpapiProtectedString(_options.MasterPasswordFile, "WaterCounters.MasterPassword.v1");
            _masterPassword = _options.MasterPassword ?? _passwordFile.Read() ?? AskForMasterPassword();

            _host = DesktopHost.Build(_options, () => _masterPassword);
            _tray = new TrayPresenter(_options, RequestScan, ExitApplication);

            await DesktopHost.WarmUpAsync(_host, _shutdown.Token);
            await _host.StartAsync(_shutdown.Token);

            _tray.ShowStarted();
        }
        catch (Exception ex)
        {
            // Ошибка старта в приложении без окна не видна вообще никак, поэтому
            // она показывается явно, а не только пишется в журнал.
            Log.Fatal(ex, "Обработчик не запустился.");

            System.Windows.MessageBox.Show(
                ex is DesktopStartupException ? ex.Message : ex.ToString(),
                "WaterCounters — обработчик не запустился",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Остановка хоста прошла не чисто.");
            }

            _host.Dispose();
        }

        _tray?.Dispose();

        if (_isOnlyInstance)
        {
            _single.ReleaseMutex();
        }

        _single.Dispose();
        _shutdown.Dispose();
        await Log.CloseAndFlushAsync();

        base.OnExit(e);
    }

    private static DesktopOptions LoadOptions()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("WATERCOUNTERS_")
            .Build();

        return configuration.GetSection(DesktopOptions.SectionName).Get<DesktopOptions>() ?? new DesktopOptions();
    }

    private string? AskForMasterPassword()
    {
        var window = new MasterPasswordWindow();

        if (window.ShowDialog() != true || window.EnteredPassword is not { } password)
        {
            return null;
        }

        if (window.ShouldRemember)
        {
            _passwordFile!.Write(password);
        }

        return password;
    }

    /// <summary>Ручная проверка из меню трея — на случай «не жди минуту, посмотри сейчас».</summary>
    private void RequestScan()
    {
        _host?.Services.GetRequiredService<ChangeSignal>().Raise();
        _tray?.Notify("Проверка запущена", "Обработчик перечитывает Dropbox.");
    }

    private void ExitApplication() => Shutdown(0);
}
