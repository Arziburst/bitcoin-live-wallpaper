using BitcoinWallpaper.Host.Screensaver;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// D-36: the user's files survive a new installer run over an existing install. Velopack Setup over an existing
/// install renames the whole root (%LOCALAPPDATA%\BitcoinWallpaper, which is also our data folder) to
/// "BitcoinWallpaper.&lt;16 random letters or digits&gt;", installs a fresh root, runs the install hook and only then
/// deletes the renamed folder. The install hook (the new version's exe) copies the user's files back from it here:
/// settings.json, screensaver.json, first-run.done and logs\. A file the new root already has is never overwritten.
/// </summary>
internal static class PreviousInstallData
{
    private const int RandomSuffixLength = 16;

    private static readonly string[] Files =
    [
        Path.GetFileName(FileSettingsStore.DefaultPath),
        Path.GetFileName(ScreensaverSetting.DefaultSavedFilePath),
        Path.GetFileName(FileFirstRunMarker.DefaultPath),
    ];

    private const string LogsFolder = "logs";

    /// <summary>Copies the user's files from the renamed previous root next to <paramref name="root"/>, if any.</summary>
    /// <returns>The folder the files came from, or null when there is no previous root.</returns>
    public static string? Restore(string root)
    {
        var previous = FindPrevious(root);
        if (previous is null)
        {
            return null;
        }

        Directory.CreateDirectory(root);
        foreach (var name in Files)
        {
            var source = Path.Combine(previous, name);
            var target = Path.Combine(root, name);
            if (File.Exists(source) && !File.Exists(target))
            {
                File.Copy(source, target);
            }
        }

        var logs = Path.Combine(previous, LogsFolder);
        var newLogs = Path.Combine(root, LogsFolder);
        if (Directory.Exists(logs) && !Directory.Exists(newLogs))
        {
            Directory.CreateDirectory(newLogs);
            foreach (var file in Directory.GetFiles(logs))
            {
                File.Copy(file, Path.Combine(newLogs, Path.GetFileName(file)));
            }
        }

        return previous;
    }

    /// <summary>The newest sibling "&lt;root name&gt;.&lt;16 letters or digits&gt;" that holds at least one user file.</summary>
    private static string? FindPrevious(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (parent is null || !Directory.Exists(parent))
        {
            return null;
        }

        return new DirectoryInfo(parent)
            .EnumerateDirectories(name + ".*")
            .Where(d => IsRenamedRoot(d.Name, name) && HasUserFiles(d.FullName))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.FullName)
            .FirstOrDefault();
    }

    private static bool IsRenamedRoot(string candidate, string name)
    {
        if (candidate.Length != name.Length + 1 + RandomSuffixLength
            || !candidate.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate[(name.Length + 1)..].All(char.IsAsciiLetterOrDigit);
    }

    private static bool HasUserFiles(string folder) =>
        Files.Any(f => File.Exists(Path.Combine(folder, f))) || Directory.Exists(Path.Combine(folder, LogsFolder));
}
