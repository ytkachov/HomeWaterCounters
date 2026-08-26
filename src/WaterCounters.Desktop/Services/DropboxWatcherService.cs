using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Storage;
using WaterCounters.Desktop.Configuration;

namespace WaterCounters.Desktop.Services;

/// <summary>
/// Сигнал «в Dropbox что-то изменилось». Одна точка, на которую подписываются
/// наблюдатель очереди и наблюдатель фотографий: заводить по longpoll-соединению
/// на каждую папку незачем, изменения приходят одним потоком.
/// </summary>
public sealed class ChangeSignal
{
    private readonly object _gate = new();

    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Raise()
    {
        lock (_gate)
        {
            TaskCompletionSource previous = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    /// <summary>Ждёт изменения или таймаута. Таймаут — не ошибка, а обычный холостой цикл.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task signal;

        lock (_gate)
        {
            signal = _signal.Task;
        }

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);

        try
        {
            await signal.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Истёк таймаут — вызывающий просто идёт на следующий круг.
        }
    }
}

/// <summary>
/// Longpoll по папке приложения.
///
/// Соединение висит подолгу намеренно: это дешевле опроса. При ошибке longpoll
/// служба деградирует до периодического опроса — потеря реакции в минутах лучше,
/// чем остановка обработки до перезапуска.
/// </summary>
public sealed class DropboxWatcherService(
    IRemoteStore store,
    QueueLayout layout,
    ISettingsProvider settings,
    ChangeSignal signal,
    DesktopOptions options,
    ILogger<DropboxWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan LongpollTimeout = TimeSpan.FromSeconds(480);

    private readonly IRemoteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly QueueLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ChangeSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<DropboxWatcherService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? cursor = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cursor ??= await _store.GetCursorAsync(_layout.Root, stoppingToken).ConfigureAwait(false);

                RemoteChanges changes = await _store
                    .WaitForChangesAsync(cursor, LongpollTimeout, stoppingToken)
                    .ConfigureAwait(false);

                cursor = changes.Cursor;

                if (changes.HasChanges)
                {
                    _logger.LogDebug(
                        "Изменения в Dropbox: {Changed} записей, {Deleted} удалений.",
                        changes.ChangedPaths.Count,
                        changes.DeletedPaths.Count);

                    await RefreshSettingsIfTouchedAsync(changes, stoppingToken).ConfigureAwait(false);
                    _signal.Raise();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Курсор сбрасывается: после сетевого сбоя он может оказаться
                // просроченным, и держаться за него — значит зациклиться на ошибке.
                cursor = null;
                _logger.LogWarning(ex, "Longpoll оборвался, переходим на опрос каждые {Interval}.", _options.PollingInterval);

                _signal.Raise();
                await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Настройки редактируются с телефона, поэтому перечитываются по факту правки:
    /// смена вёрстки кабинета или модели распознавания не должна требовать
    /// перезапуска обработчика.
    /// </summary>
    private async Task RefreshSettingsIfTouchedAsync(RemoteChanges changes, CancellationToken ct)
    {
        bool touched = changes.ChangedPaths.Any(path => RemotePath.IsInFolder(path, _layout.ConfigFolder));

        if (!touched)
        {
            return;
        }

        try
        {
            await _settings.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Битые настройки не должны останавливать наблюдение: обработчик
            // продолжает работать на последней успешно прочитанной копии.
            _logger.LogError(ex, "Настройки изменились, но не перечитались.");
        }
    }
}
