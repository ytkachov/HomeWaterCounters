using System.IO;
using System.Security.Cryptography;
using System.Text;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;

namespace WaterCounters.Desktop.Photos;

public enum BatchReadiness
{
    /// <summary>Фотографий за период нет вовсе.</summary>
    Empty = 0,

    /// <summary>Фотографии есть, но пачка ещё может пополниться.</summary>
    Waiting = 1,

    /// <summary>Можно распознавать.</summary>
    Ready = 2,
}

public enum PhotoMatch
{
    /// <summary>Имя файла совпало с ключом счётчика — основной способ при ручной раскладке.</summary>
    ByFileName = 0,

    /// <summary>Счётчик определён по серийному номеру, прочитанному на фото.</summary>
    BySerial = 1,
}

public sealed record PhotoAssignment(MeterSpec Meter, RemoteEntry Entry, PhotoMatch Match);

public sealed record PhotoBatchDecision
{
    public required BatchReadiness Readiness { get; init; }

    public required string Reason { get; init; }

    public IReadOnlyList<PhotoAssignment> Assignments { get; init; } = [];

    /// <summary>Файлы, которые не удалось привязать к счётчику по имени.</summary>
    public IReadOnlyList<RemoteEntry> Unassigned { get; init; } = [];

    /// <summary>Счётчики, для которых фотографии не нашлось. Попадают в письмо как требующие внимания.</summary>
    public IReadOnlyList<MeterSpec> MissingMeters { get; init; } = [];

    /// <summary>
    /// Отпечаток пачки: пути и ревизии файлов. Меняется, когда фотографии
    /// перезаливают, и только тогда — на нём держится защита от бесконечной
    /// переобработки одной и той же неудачной пачки.
    /// </summary>
    public required string Fingerprint { get; init; }

    public bool IsReady => Readiness == BatchReadiness.Ready;
}

/// <summary>
/// Решение о готовности пачки фотографий при ручной раскладке.
///
/// Обрабатывать после первого же файла нельзя — уйдут неполные показания. Поэтому
/// два правила: пачка готова, если сняты все настроенные счётчики (нормальный случай,
/// реакция почти мгновенная), либо если в папке периода ничего не появлялось
/// <c>settlingMinutes</c> минут (случай «сняли не все» или «файл не долетел»).
/// </summary>
public sealed class PhotoBatchEvaluator(TimeProvider? clock = null)
{
    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png"];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public PhotoBatchDecision Evaluate(
        IReadOnlyList<RemoteEntry> entries,
        IReadOnlyList<MeterSpec> meters,
        TimeSpan settling)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(meters);

        List<RemoteEntry> photos =
        [
            .. entries
                .Where(static e => PhotoExtensions.Any(ext => e.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(static e => e.Path, StringComparer.OrdinalIgnoreCase)
        ];

        string fingerprint = Fingerprint(photos);

        if (photos.Count == 0)
        {
            return new PhotoBatchDecision
            {
                Readiness = BatchReadiness.Empty,
                Reason = "фотографий за период нет",
                Fingerprint = fingerprint,
            };
        }

        List<PhotoAssignment> assignments = [];
        List<RemoteEntry> unassigned = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        foreach (RemoteEntry photo in photos)
        {
            if (MeterMatcher.TryMatchByFileName(photo.Path, meters, out MeterSpec? meter) && taken.Add(meter.Key))
            {
                assignments.Add(new PhotoAssignment(meter, photo, PhotoMatch.ByFileName));
            }
            else
            {
                unassigned.Add(photo);
            }
        }

        List<MeterSpec> missing = [.. meters.Where(m => !taken.Contains(m.Key))];

        if (missing.Count == 0 && unassigned.Count == 0)
        {
            return Ready(assignments, unassigned, missing, fingerprint, "сняты все настроенные счётчики");
        }

        DateTimeOffset newest = photos.Max(static p => p.ModifiedUtc);
        TimeSpan quiet = _clock.GetUtcNow() - newest;

        if (quiet < settling)
        {
            TimeSpan left = settling - quiet;

            return new PhotoBatchDecision
            {
                Readiness = BatchReadiness.Waiting,
                Reason = missing.Count > 0
                    ? $"не хватает счётчиков ({string.Join(", ", missing.Select(m => m.Key))}), " +
                      $"ждём ещё {left.TotalMinutes:N0} мин на случай, если файлы дозагружаются"
                    : $"есть непривязанные файлы, ждём ещё {left.TotalMinutes:N0} мин",
                Assignments = assignments,
                Unassigned = unassigned,
                MissingMeters = missing,
                Fingerprint = fingerprint,
            };
        }

        return Ready(
            assignments,
            unassigned,
            missing,
            fingerprint,
            $"папка не пополнялась {quiet.TotalMinutes:N0} мин");
    }

    private static PhotoBatchDecision Ready(
        List<PhotoAssignment> assignments,
        List<RemoteEntry> unassigned,
        List<MeterSpec> missing,
        string fingerprint,
        string reason) => new()
        {
            Readiness = BatchReadiness.Ready,
            Reason = reason,
            Assignments = assignments,
            Unassigned = unassigned,
            MissingMeters = missing,
            Fingerprint = fingerprint,
        };

    /// <summary>Путь и ревизия каждого файла: перезалитая фотография меняет отпечаток, простое ожидание — нет.</summary>
    private static string Fingerprint(IReadOnlyList<RemoteEntry> photos)
    {
        if (photos.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (RemoteEntry photo in photos)
        {
            text.Append(photo.Path.ToLowerInvariant()).Append('|').Append(photo.Revision).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}

/// <summary>
/// Сопоставление фотографии со счётчиком по имени файла — первый и основной способ
/// при ручной раскладке: явный, предсказуемый и не зависящий от качества снимка.
/// Второй способ, по серийному номеру, возможен только после распознавания и живёт
/// в конвейере обработки.
/// </summary>
public static class MeterMatcher
{
    public static bool TryMatchByFileName(
        string path,
        IReadOnlyList<MeterSpec> meters,
        out MeterSpec meter)
    {
        ArgumentNullException.ThrowIfNull(meters);

        string name = Normalize(Path.GetFileNameWithoutExtension(RemotePath.GetFileName(path)));

        foreach (MeterSpec candidate in meters)
        {
            string key = Normalize(candidate.Key);

            // Точное совпадение, либо ключ с хвостом: cold-water-2.jpg, cold-water (1).jpg.
            // Хвост допускается потому, что второй снимок того же счётчика — обычное дело,
            // а вот "cold-water-backup" от "cold-water" отличать незачем.
            if (key.Length > 0 && (name == key || name.StartsWith(key + "-", StringComparison.Ordinal)))
            {
                meter = candidate;
                return true;
            }
        }

        meter = null!;
        return false;
    }

    /// <summary>Приводит имя к виду ключа: нижний регистр, всё несловесное — в дефис.</summary>
    private static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        bool previousDash = false;

        foreach (char symbol in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(symbol))
            {
                text.Append(symbol);
                previousDash = false;
            }
            else if (!previousDash && text.Length > 0)
            {
                text.Append('-');
                previousDash = true;
            }
        }

        return text.ToString().Trim('-');
    }
}
