namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// D-36: the user's files survive a new installer run over an existing install. Velopack Setup over an existing
/// install renames the whole root (%LOCALAPPDATA%\BitcoinWallpaper, which is also our data folder) to
/// "BitcoinWallpaper.&lt;16 random letters or digits&gt;", installs a fresh root, runs the install hook and only then
/// deletes the renamed folder. The install hook (the new version's exe) copies the user's files back from it here:
/// every top-level *.json, first-run.done, and the folders logs\ and lock\ merged file by file. Velopack's own entries
/// are never copied, a file the new root already has is never overwritten, and each file is copied on its own (one
/// failure does not stop the rest).
/// </summary>
internal static class PreviousInstallData
{
    private const int RandomSuffixLength = 16;

    private static readonly string[] TopLevelPatterns = ["*.json", "first-run.done"];

    private static readonly string[] Folders = ["logs", "lock"];

    /// <summary>Velopack's entries in the install root; never restored even if a pattern matched them.</summary>
    private static readonly HashSet<string> VelopackEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "current", "packages", "Update.exe", "sq.version", "RELEASES", ".betaId", ".dead", "Squirrel.exe",
    };

    /// <summary>What a restore did: the folder it came from (null: none), the copied files and the failures.</summary>
    public sealed record Result(string? From, IReadOnlyList<string> Restored, IReadOnlyList<string> Failed);

    /// <summary>Copies the user's files from the renamed previous root next to <paramref name="root"/>, if any.</summary>
    public static Result Restore(string root)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        var previous = FindPrevious(root);
        if (previous is not null) return new Result(previous, restored, failed); // DELIBERATE BREAK: generic copy off
        if (previous is null)
        {
            return new Result(null, restored, failed);
        }

        Directory.CreateDirectory(root);
        foreach (var source in TopLevelPatterns.SelectMany(p => SafeFiles(previous, p, SearchOption.TopDirectoryOnly, failed))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!VelopackEntries.Contains(Path.GetFileName(source)))
            {
                CopyOne(source, Path.Combine(root, Path.GetFileName(source)), restored, failed);
            }
        }

        foreach (var folder in Folders)
        {
            var from = Path.Combine(previous, folder);
            if (!Directory.Exists(from))
            {
                continue;
            }

            foreach (var source in SafeFiles(from, "*", SearchOption.AllDirectories, failed))
            {
                CopyOne(source, Path.Combine(root, folder, Path.GetRelativePath(from, source)), restored, failed);
            }
        }

        return new Result(previous, restored, failed);
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, SearchOption option, List<string> failed)
    {
        try
        {
            return Directory.GetFiles(folder, pattern, option);
        }
        catch (Exception e)
        {
            failed.Add($"{Path.Combine(folder, pattern)}: {e.Message}");
            return [];
        }
    }

    private static void CopyOne(string source, string target, List<string> restored, List<string> failed)
    {
        try
        {
            if (File.Exists(target))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            restored.Add(target);
        }
        catch (Exception e)
        {
            failed.Add($"{source}: {e.Message}");
        }
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
        TopLevelPatterns.Any(p => Directory.EnumerateFiles(folder, p).Any())
        || Folders.Any(f => Directory.Exists(Path.Combine(folder, f)));
}
