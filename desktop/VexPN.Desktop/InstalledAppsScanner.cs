using System.IO;

namespace VexPN.Desktop;

/// <summary>
/// Сканирует ярлыки/папки меню «Пуск» и верхний уровень Program Files в поиске .exe.
/// </summary>
public static class InstalledAppsScanner
{
    private static readonly string[] IgnoredExePrefixes =
    [
        "unins",
        "setup",
        "install",
        "vc_redist",
        "dotnet-"
    ];

    public static Task<List<InstalledAppEntry>> ScanAsync(CancellationToken ct = default) =>
        Task.Run(() => Scan(ct), ct);

    public static List<InstalledAppEntry> Scan(CancellationToken ct = default)
    {
        var map = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string path)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim();
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return;
            if (!File.Exists(path))
                return;

            var fn = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fn))
                return;

            foreach (var p in IgnoredExePrefixes)
            {
                if (fn.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
                return;

            map[path] = new InstalledAppEntry(name, path);
        }

        foreach (var folder in GetStartMenuRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
                continue;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories))
                    TryAdd(exe);
            }
            catch
            {
                // ignore unreadable dirs
            }
        }

        foreach (var root in GetProgramFilesRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                            TryAdd(exe);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return map.Values
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetStartMenuRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
    }

    private static IEnumerable<string> GetProgramFilesRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            yield return pf;
        if (!string.IsNullOrEmpty(pf86) &&
            !pf.Equals(pf86, StringComparison.OrdinalIgnoreCase))
            yield return pf86;
    }
}

public sealed record InstalledAppEntry(string DisplayName, string ExePath);
