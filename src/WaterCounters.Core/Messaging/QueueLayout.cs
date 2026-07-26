using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;

namespace WaterCounters.Core.Messaging;

public enum QueueDirection
{
    ToDesktop = 0,
    ToMobile = 1,
}

/// <summary>Раскладка папок в удалённом хранилище. Единственное место, где зашиты имена.</summary>
public sealed class QueueLayout
{
    public const string DefaultRoot = "/";

    public QueueLayout(string root = DefaultRoot) => Root = RemotePath.Normalize(root);

    public string Root { get; }

    public string ConfigFolder => RemotePath.Combine(Root, "config");

    public string SettingsPath => RemotePath.Combine(ConfigFolder, "settings.json");

    public string SecretsPath => RemotePath.Combine(ConfigFolder, "secrets.enc");

    public string StateFolder => RemotePath.Combine(Root, "state");

    public string HistoryPath => RemotePath.Combine(StateFolder, "history.json");

    public string PhotosFolder => RemotePath.Combine(Root, "photos");

    public string QueueFolder => RemotePath.Combine(Root, "queue");

    public string ToDesktopFolder => RemotePath.Combine(QueueFolder, "to-desktop");

    public string ToMobileFolder => RemotePath.Combine(QueueFolder, "to-mobile");

    public string ProcessingFolder => RemotePath.Combine(QueueFolder, "processing");

    public string DoneFolder => RemotePath.Combine(QueueFolder, "done");

    public string FailedFolder => RemotePath.Combine(QueueFolder, "failed");

    public string PhotosFolderFor(PeriodKey period) => RemotePath.Combine(PhotosFolder, period.ToString());

    public string DoneFolderFor(PeriodKey period) => RemotePath.Combine(DoneFolder, period.ToString());

    public string PendingFolder(QueueDirection direction) =>
        direction == QueueDirection.ToDesktop ? ToDesktopFolder : ToMobileFolder;

    public string PendingPath(QueueDirection direction, string fileName) =>
        RemotePath.Combine(PendingFolder(direction), fileName);

    public string ProcessingPath(string fileName) => RemotePath.Combine(ProcessingFolder, fileName);

    public string DonePath(PeriodKey period, string fileName) => RemotePath.Combine(DoneFolderFor(period), fileName);

    public string FailedPath(string fileName) => RemotePath.Combine(FailedFolder, fileName);

    public string PhotoPath(PeriodKey period, string fileName) => RemotePath.Combine(PhotosFolderFor(period), fileName);

    public static QueueDirection DirectionOf(MessageType type) =>
        type.IsToDesktop() ? QueueDirection.ToDesktop : QueueDirection.ToMobile;
}
