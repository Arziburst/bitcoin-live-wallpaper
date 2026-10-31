using System.Globalization;

namespace BitcoinWallpaper.Host.LockScreenChart;

/// <param name="Ok">The operation did what it had to (or had nothing to do).</param>
/// <param name="Path">The picture now set; null when nothing was set.</param>
/// <param name="Error">Non-empty text when not <paramref name="Ok"/>.</param>
public sealed record LockScreenResult(bool Ok, string? Path, string? Error);

/// <summary>
/// Our snapshot on the lock screen and the way back to the user's own picture (D-34). Everything lives in
/// <c>folder</c> (%LOCALAPPDATA%\BitcoinWallpaper\lock): the snapshots lock_&lt;utc&gt;.png (a new name per set,
/// only the current one kept), the one backup copy original.&lt;ext&gt; with original.txt noting the path Windows
/// reported, and restore_&lt;utc&gt;.&lt;ext&gt; copies of the backup set back when the original file is gone. Nothing
/// is kept only in memory: a new instance over the same folder continues where the old one stopped. No member
/// throws: a failure is logged and returned as a <see cref="LockScreenResult"/> with Ok false.
/// </summary>
public sealed class LockScreenImages(ILockScreenApi api, ILockScreenFiles files, string folder, Action<string> log)
{
    public const string SnapshotPrefix = "lock_";
    public const string RestorePrefix = "restore_";
    public const string BackupBaseName = "original";

    private const string NoteExtension = ".txt";

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "lock");

    private string NotePath => Path.Combine(folder, BackupBaseName + NoteExtension);

    /// <summary>
    /// Backs up the user's picture once, has <paramref name="render"/> write the PNG at a new lock_&lt;utc&gt;.png and
    /// sets it. On success the older snapshots (and restore copies) are deleted; on a failure the new file is.
    /// </summary>
    public async Task<LockScreenResult> SetSnapshotAsync(Func<string, Task> render, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(render);
        string path;
        try
        {
            files.CreateDirectory(folder);
            BackUpOnce();
            path = Path.Combine(folder, SnapshotPrefix + Stamp(utcNow) + ".png");
        }
        catch (Exception e)
        {
            return Fail($"preparing {folder} failed", e);
        }

        try
        {
            await render(path).ConfigureAwait(false);
            await api.SetImageFileAsync(path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            TryDelete(path);
            return Fail("setting the snapshot failed", e);
        }

        foreach (var file in Ours())
        {
            if (!SamePath(file, path))
            {
                TryDelete(file);
            }
        }

        return new LockScreenResult(true, path, null);
    }

    /// <summary>
    /// Puts the user's picture back: the path Windows reported before our first snapshot when that file still exists,
    /// else a restore_ copy of the backup. No backup ever taken: nothing to do (Ok). On success our snapshots are deleted.
    /// </summary>
    public async Task<LockScreenResult> RestoreAsync(DateTimeOffset utcNow)
    {
        string target;
        var copied = false;
        try
        {
            var backup = FindBackup();
            var original = ReadNote();
            if (backup is null && original is null)
            {
                log("lock screen: no backup of the user's picture was ever taken; nothing to restore");
                return new LockScreenResult(true, null, null);
            }

            if (original is not null && files.Exists(original))
            {
                target = original;
            }
            else if (backup is not null)
            {
                target = Path.Combine(folder, RestorePrefix + Stamp(utcNow) + Path.GetExtension(backup));
                files.Copy(backup, target);
                copied = true;
            }
            else
            {
                return Fail($"the user's picture {original} is gone and there is no backup copy in {folder}");
            }
        }
        catch (Exception e)
        {
            return Fail("preparing the restore failed", e);
        }

        try
        {
            await api.SetImageFileAsync(target).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (copied)
            {
                TryDelete(target);
            }

            return Fail($"restoring {target} failed", e);
        }

        foreach (var file in Ours())
        {
            if (!SamePath(file, target))
            {
                TryDelete(file);
            }
        }

        log($"lock screen: restored the user's picture {target}");
        return new LockScreenResult(true, target, null);
    }

    /// <summary>A lock_*.png or restore_* file (the names this class creates), or anything else in our folder.</summary>
    public bool IsOurs(string path)
    {
        var name = Path.GetFileName(path);
        return (name.StartsWith(SnapshotPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
               || name.StartsWith(RestorePrefix, StringComparison.OrdinalIgnoreCase)
               || SamePath(Path.GetDirectoryName(path) ?? "", folder);
    }

    private void BackUpOnce()
    {
        if (FindBackup() is not null || ReadNote() is not null)
        {
            return;
        }

        string? original;
        try
        {
            original = api.OriginalImageFile;
        }
        catch (Exception e)
        {
            log($"lock screen: cannot read the current picture, no backup taken: {Describe(e)}");
            return;
        }

        if (original is null)
        {
            log("lock screen: Windows reports no picture (Spotlight or Slideshow?); no backup taken");
            return;
        }

        if (IsOurs(original))
        {
            return;   // our own snapshot is never the user's picture
        }

        try
        {
            if (!files.Exists(original))
            {
                log($"lock screen: the current picture {original} does not exist; no backup taken");
                return;
            }

            var backup = Path.Combine(folder, BackupBaseName + ExtensionOf(original));
            files.Copy(original, backup);
            files.WriteAllText(NotePath, original);
            log($"lock screen: backed up the user's picture {original} to {backup}");
        }
        catch (Exception e)
        {
            log($"lock screen: backing up {original} failed: {Describe(e)}");
        }
    }

    private string? FindBackup() =>
        files.List(folder)
            .Where(f => Path.GetFileNameWithoutExtension(f).Equals(BackupBaseName, StringComparison.OrdinalIgnoreCase)
                        && !Path.GetExtension(f).Equals(NoteExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private string? ReadNote()
    {
        if (!files.Exists(NotePath))
        {
            return null;
        }

        var text = files.ReadAllText(NotePath).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Our snapshots and restore copies in the folder.</summary>
    private List<string> Ours()
    {
        try
        {
            return files.List(folder).Where(f =>
            {
                var name = Path.GetFileName(f);
                return (name.StartsWith(SnapshotPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                       || name.StartsWith(RestorePrefix, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        catch (Exception e)
        {
            log($"lock screen: listing {folder} failed: {Describe(e)}");
            return [];
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            files.Delete(path);
        }
        catch (Exception e)
        {
            log($"lock screen: deleting {path} failed: {Describe(e)}");
        }
    }

    /// <summary>The log line carries the exception type and HRESULT; the result's Error (the tooltip) stays short.</summary>
    private LockScreenResult Fail(string what, Exception? e = null)
    {
        log("lock screen: " + what + (e is null ? "" : ": " + Describe(e)));
        return new LockScreenResult(false, null, e is null ? what : what + ": " + e.Message.Trim());
    }

    /// <summary>For the log, as probe #39 printed it: "COMException HRESULT 0x80070005: Access is denied."</summary>
    internal static string Describe(Exception e) =>
        string.Create(CultureInfo.InvariantCulture, $"{e.GetType().Name} HRESULT 0x{e.HResult:X8}: {e.Message.Trim()}");

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>20261031T143005123Z: UTC to the millisecond, so two sets never share a name.</summary>
    private static string Stamp(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    private static string ExtensionOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" ? extension : ".jpg";
    }
}
