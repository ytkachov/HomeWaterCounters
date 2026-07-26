namespace WaterCounters.Core.Storage;

/// <summary>
/// Пути в удалённом хранилище: всегда с ведущим слэшем, прямые слэши, без хвостового.
/// Dropbox регистронезависим, поэтому сравнение путей идёт по <see cref="Comparer"/>.
/// </summary>
public static class RemotePath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalized = path.Replace('\\', '/').Trim();

        if (!normalized.StartsWith('/'))
        {
            normalized = '/' + normalized;
        }

        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        if (normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Путь '{path}' содержит пустой сегмент.", nameof(path));
        }

        if (normalized.Contains("/../", StringComparison.Ordinal) || normalized.EndsWith("/..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Путь '{path}' содержит переход вверх по дереву.", nameof(path));
        }

        return normalized;
    }

    public static string Combine(string folder, params string[] segments)
    {
        string result = Normalize(folder);

        foreach (string segment in segments)
        {
            string trimmed = segment.Replace('\\', '/').Trim('/');

            if (trimmed.Length == 0)
            {
                continue;
            }

            result = result == "/" ? '/' + trimmed : result + '/' + trimmed;
        }

        return Normalize(result);
    }

    public static string GetFileName(string path)
    {
        string normalized = Normalize(path);
        int slash = normalized.LastIndexOf('/');
        return normalized[(slash + 1)..];
    }

    public static string GetFolder(string path)
    {
        string normalized = Normalize(path);
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    /// <summary>Лежит ли <paramref name="path"/> непосредственно или вложенно внутри <paramref name="folder"/>.</summary>
    public static bool IsInFolder(string path, string folder)
    {
        string normalizedPath = Normalize(path);
        string normalizedFolder = Normalize(folder);

        if (normalizedFolder == "/")
        {
            return true;
        }

        return normalizedPath.Length > normalizedFolder.Length
            && normalizedPath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase)
            && normalizedPath[normalizedFolder.Length] == '/';
    }
}
