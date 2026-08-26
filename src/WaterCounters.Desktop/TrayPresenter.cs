using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WaterCounters.Desktop;

/// <summary>
/// Иконка в области уведомлений: единственный видимый след обработчика.
///
/// NotifyIcon из WinForms, а не сторонняя библиотека для WPF: своей иконки в трее у
/// WPF нет, а тащить ради одного элемента постороннюю зависимость незачем.
/// </summary>
public sealed class TrayPresenter : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly DesktopOptions _options;

    public TrayPresenter(DesktopOptions options, Action scanNow, Action exit)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scanNow);
        ArgumentNullException.ThrowIfNull(exit);

        _options = options;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Проверить сейчас", null, (_, _) => scanNow());
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenFolder(_options.LogsDirectory));
        menu.Items.Add("Открыть папку данных", null, (_, _) => OpenFolder(_options.DataDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WaterCounters — обработчик",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void ShowStarted() => Notify(
        "Обработчик запущен",
        $"Следит за папкой Dropbox. Журнал: {_options.LogsDirectory}");

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
