using BitcoinWallpaper.Host.LockScreenChart;

namespace BitcoinWallpaper.Host.Tests;

// Contract 42, criterion 2 (D-34 step 2): backing up and restoring the user's own lock screen picture, and setting
// our snapshot, against a fake lock screen API and a fake file system (no real LockScreen call, #42 non-goals).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.LockScreenChart,
// src/Wallpaper.Host/LockScreenChart/ (e.g. LockScreenImages.cs, ILockScreenApi.cs, ILockScreenFiles.cs).
//
//   // Production: Windows.System.UserProfile.LockScreen (OriginalImageFile.Path, SetImageFileAsync).
//   public interface ILockScreenApi
//   {
//       string? OriginalImageFile { get; }                 // path of the picture Windows reports; null: none. May throw.
//       Task SetImageFileAsync(string path);               // throws when Windows refuses
//   }
//
//   // Production: System.IO. Paths are full paths; comparison is case-insensitive.
//   public interface ILockScreenFiles
//   {
//       bool Exists(string path);
//       void CreateDirectory(string folder);
//       void Copy(string source, string destination);      // overwrites
//       void Delete(string path);                          // no-op when absent
//       IReadOnlyList<string> List(string folder);         // full paths of the files directly in folder
//       string ReadAllText(string path);
//       void WriteAllText(string path, string text);
//   }
//
//   public sealed record LockScreenResult(bool Ok, string? Path, string? Error);   // Error: non-empty text when !Ok
//
//   // `folder`: %LOCALAPPDATA%\BitcoinWallpaper\lock. Its own state (the backup copy and a note of the original
//   // path) lives in that folder under names that do not match lock_*.png or restore_*; a NEW instance over the same
//   // files continues where the old one stopped (nothing is kept only in memory). No member throws: every failure
//   // is logged through `log` and returned as LockScreenResult(false, null, message).
//   public sealed class LockScreenImages(ILockScreenApi api, ILockScreenFiles files, string folder, Action<string> log)
//   {
//       // 1. If no backup exists yet: take api.OriginalImageFile; when it is non-null, exists, and its file name is
//       //    not lock_*.png / restore_* (our own), copy it into folder and remember its path. Else no backup.
//       // 2. path = folder\lock_<utc>.png (unique per utcNow; <utc> is any file-name-safe rendering of the time);
//       //    await render(path) (it writes the PNG through `files`); await api.SetImageFileAsync(path).
//       // 3. On success only: every other lock_*.png in folder is deleted, the new one is kept.
//       // 4. On a failure of render or set: the previous lock_*.png files and the backup stay; the new file is gone;
//       //    result is (false, null, message containing the exception message).
//       public Task<LockScreenResult> SetSnapshotAsync(Func<string, Task> render, DateTimeOffset utcNow);
//
//       // No backup was ever taken: Ok, calls nothing (the user never lost anything).
//       // The remembered original path exists -> api.SetImageFileAsync(that path).
//       // Else the backup copy exists -> api.SetImageFileAsync(a path whose content is the backup's, e.g. a restore_<utc>
//       //   copy; never one of our lock_*.png).
//       // Neither exists, or the set throws -> (false, null, message); nothing is deleted.
//       public Task<LockScreenResult> RestoreAsync(DateTimeOffset utcNow);
//   }
public sealed class LockScreenBackupTests
{
    private const string Folder = @"C:\Users\Owner\AppData\Local\BitcoinWallpaper\lock";
    private const string Original = @"C:\Users\Owner\Pictures\mine.jpg";
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeApi : ILockScreenApi
    {
        public string? Original { get; set; }
        public Exception? ThrowOnSet { get; set; }
        public Exception? ThrowOnOriginal { get; set; }
        public List<string> Sets { get; } = new();
        public FakeFiles? Files { get; set; }
        public List<string?> SetContents { get; } = new();

        string? ILockScreenApi.OriginalImageFile => ThrowOnOriginal is { } e ? throw e : Original;

        public Task SetImageFileAsync(string path)
        {
            if (ThrowOnSet is { } e)
            {
                throw e;
            }

            Sets.Add(path);
            SetContents.Add(Files is not null && Files.Exists(path) ? Files.ReadAllText(path) : null);
            // Like Windows: afterwards the "original" is whatever we set.
            Original = path;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFiles : ILockScreenFiles
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Content.ContainsKey(path);
        public void CreateDirectory(string folder) { }
        public void Copy(string source, string destination) => Content[destination] = Content[source];
        public void Delete(string path) => Content.Remove(path);
        public string ReadAllText(string path) => Content[path];
        public void WriteAllText(string path, string text) => Content[path] = text;

        public IReadOnlyList<string> List(string folder) =>
            Content.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private readonly FakeApi _api = new();
    private readonly FakeFiles _files = new();
    private readonly List<string> _log = new();

    public LockScreenBackupTests()
    {
        _api.Files = _files;
        _files.Content[Original] = "ORIGINAL-PICTURE";
        _api.Original = Original;
    }

    private LockScreenImages New() => new(_api, _files, Folder, _log.Add);

    private static Func<string, Task> Render(FakeFiles files, string content = "SNAPSHOT") => path =>
    {
        files.Content[path] = content;
        return Task.CompletedTask;
    };

    private static bool IsOurs(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("lock_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("restore_", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> LockFiles() =>
        _files.List(Folder).Where(f => Path.GetFileName(f).StartsWith("lock_", StringComparison.OrdinalIgnoreCase)
                                       && f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();

    private List<string> BackupCopies() =>
        _files.List(Folder).Where(f => !IsOurs(f) && _files.Content[f] == "ORIGINAL-PICTURE").ToList();

    // ---- set ----

    [Fact]
    public async Task Set_backs_up_the_original_then_sets_a_unique_lock_png_in_the_folder()
    {
        var result = await New().SetSnapshotAsync(Render(_files), T0);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        var set = Assert.Single(_api.Sets);
        Assert.Equal(set, result.Path);
        Assert.Equal(Folder, Path.GetDirectoryName(set), ignoreCase: true);
        Assert.Matches(@"^lock_.+\.png$", Path.GetFileName(set));
        Assert.Equal("SNAPSHOT", _api.SetContents[0]);   // the file existed when it was set
        Assert.Single(BackupCopies());
        Assert.Equal("ORIGINAL-PICTURE", _files.Content[Original]);   // never touched
    }

    [Fact]
    public async Task The_backup_is_taken_once_and_never_replaced_by_our_own_picture()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files), T0);
        var backup = Assert.Single(BackupCopies());
        // Windows now reports our lock_*.png as the current image.
        Assert.True(IsOurs(_api.Original!));

        await images.SetSnapshotAsync(Render(_files, "SNAPSHOT-2"), T0.AddMinutes(30));
        await New().SetSnapshotAsync(Render(_files, "SNAPSHOT-3"), T0.AddMinutes(60));   // a new run of the app

        Assert.Equal(backup, Assert.Single(BackupCopies()));
        Assert.Equal("ORIGINAL-PICTURE", _files.Content[backup]);
        Assert.All(_files.List(Folder).Where(f => !IsOurs(f)), f => Assert.NotEqual("SNAPSHOT-2", _files.Content[f]));
    }

    [Fact]
    public async Task A_lock_png_reported_as_the_original_is_never_backed_up()
    {
        var leftover = Path.Combine(Folder, "lock_20261030T100000Z.png");
        _files.Content[leftover] = "OLD-SNAPSHOT";
        _api.Original = leftover;

        var result = await New().SetSnapshotAsync(Render(_files), T0);

        Assert.True(result.Ok);
        Assert.DoesNotContain(_files.List(Folder), f => !IsOurs(f) && _files.Content[f] == "OLD-SNAPSHOT");
        Assert.DoesNotContain(_files.List(Folder), f => !IsOurs(f));   // no backup at all
    }

    [Fact]
    public async Task A_restore_png_reported_as_the_original_is_never_backed_up()
    {
        var leftover = Path.Combine(Folder, "restore_20261030T100000Z.jpg");
        _files.Content[leftover] = "OLD-COPY";
        _api.Original = leftover;

        await New().SetSnapshotAsync(Render(_files), T0);

        Assert.DoesNotContain(_files.List(Folder), f => !IsOurs(f));
    }

    [Fact]
    public async Task No_original_picture_means_no_backup_but_the_snapshot_is_still_set()
    {
        _api.Original = null;

        var result = await New().SetSnapshotAsync(Render(_files), T0);

        Assert.True(result.Ok);
        Assert.Single(_api.Sets);
        Assert.DoesNotContain(_files.List(Folder), f => !IsOurs(f));
    }

    [Fact]
    public async Task Different_times_give_different_file_names_and_only_the_current_one_stays()
    {
        var images = New();
        var first = (await images.SetSnapshotAsync(Render(_files, "A"), T0)).Path!;
        var second = (await images.SetSnapshotAsync(Render(_files, "B"), T0.AddMinutes(30))).Path!;
        var third = (await images.SetSnapshotAsync(Render(_files, "C"), T0.AddMinutes(60))).Path!;

        Assert.Equal(3, new[] { first, second, third }.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(third, Assert.Single(LockFiles()), ignoreCase: true);
        Assert.Equal("C", _files.Content[third]);
    }

    [Fact]
    public async Task Older_lock_pngs_from_an_earlier_run_are_deleted_after_a_successful_set()
    {
        var older = Path.Combine(Folder, "lock_20261029T080000Z.png");
        _files.Content[older] = "OLD";

        var result = await New().SetSnapshotAsync(Render(_files), T0);

        Assert.False(_files.Exists(older));
        Assert.Equal(result.Path, Assert.Single(LockFiles()), ignoreCase: true);
    }

    [Fact]
    public async Task A_failed_set_keeps_the_previous_files_and_reports_the_error()
    {
        var images = New();
        var good = (await images.SetSnapshotAsync(Render(_files, "GOOD"), T0)).Path!;
        _api.ThrowOnSet = new InvalidOperationException("Spotlight is on");

        var result = await images.SetSnapshotAsync(Render(_files, "NEW"), T0.AddMinutes(30));

        Assert.False(result.Ok);
        Assert.Null(result.Path);
        Assert.Contains("Spotlight is on", result.Error);
        Assert.Equal(good, Assert.Single(LockFiles()), ignoreCase: true);   // the new file is gone, the old one stays
        Assert.Equal("GOOD", _files.Content[good]);
        Assert.Single(BackupCopies());
        Assert.NotEmpty(_log);
    }

    [Fact]
    public async Task A_failed_render_sets_nothing_deletes_nothing_and_reports_the_error()
    {
        var images = New();
        var good = (await images.SetSnapshotAsync(Render(_files, "GOOD"), T0)).Path!;

        var result = await images.SetSnapshotAsync(_ => throw new IOException("disk full"), T0.AddMinutes(30));

        Assert.False(result.Ok);
        Assert.Contains("disk full", result.Error);
        Assert.Single(_api.Sets);
        Assert.Equal(good, Assert.Single(LockFiles()), ignoreCase: true);
    }

    [Fact]
    public async Task An_unreadable_original_does_not_throw_and_does_not_stop_the_snapshot()
    {
        _api.ThrowOnOriginal = new InvalidOperationException("no access");

        var result = await Record.ExceptionAsync(() => New().SetSnapshotAsync(Render(_files), T0));

        Assert.Null(result);
        Assert.Single(_api.Sets);
        Assert.NotEmpty(_log);
    }

    // ---- restore ----

    [Fact]
    public async Task Restore_sets_the_original_path_when_it_still_exists()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files), T0);

        var result = await images.RestoreAsync(T0.AddMinutes(5));

        Assert.True(result.Ok);
        Assert.Equal(2, _api.Sets.Count);
        Assert.Equal(Original, _api.Sets[1], ignoreCase: true);
        Assert.Equal("ORIGINAL-PICTURE", _files.Content[Original]);
    }

    [Fact]
    public async Task Restore_uses_the_backup_copy_when_the_original_is_gone()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files), T0);
        _files.Delete(Original);

        var result = await New().RestoreAsync(T0.AddMinutes(5));   // also after an app restart

        Assert.True(result.Ok);
        Assert.Equal(2, _api.Sets.Count);
        Assert.NotEqual(Original, _api.Sets[1], StringComparer.OrdinalIgnoreCase);
        Assert.False(Path.GetFileName(_api.Sets[1]).StartsWith("lock_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ORIGINAL-PICTURE", _api.SetContents[1]);
    }

    [Fact]
    public async Task Restore_without_any_backup_is_ok_and_calls_nothing()
    {
        var result = await New().RestoreAsync(T0);

        Assert.True(result.Ok);
        Assert.Empty(_api.Sets);
    }

    [Fact]
    public async Task Restore_with_neither_original_nor_backup_reports_the_error_and_deletes_nothing()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files), T0);
        _files.Delete(Original);
        foreach (var backup in BackupCopies())
        {
            _files.Delete(backup);
        }

        var result = await Record.ExceptionAsync(() => images.RestoreAsync(T0.AddMinutes(5)));

        Assert.Null(result);
        Assert.Single(_api.Sets);
        Assert.Single(LockFiles());   // the snapshot stays: it is all the user has
    }

    [Fact]
    public async Task A_failed_restore_reports_the_error_and_does_not_throw()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files), T0);
        _api.ThrowOnSet = new InvalidOperationException("denied");

        var result = await images.RestoreAsync(T0.AddMinutes(5));

        Assert.False(result.Ok);
        Assert.Contains("denied", result.Error);
        Assert.Single(LockFiles());
        Assert.Single(BackupCopies());
    }

    [Fact]
    public async Task After_a_restore_a_new_snapshot_can_be_set_again_and_the_backup_is_still_the_original()
    {
        var images = New();
        await images.SetSnapshotAsync(Render(_files, "A"), T0);
        await images.RestoreAsync(T0.AddMinutes(1));
        _api.Original = Original;   // Windows reports the restored picture

        var result = await images.SetSnapshotAsync(Render(_files, "B"), T0.AddMinutes(2));

        Assert.True(result.Ok);
        Assert.Equal(result.Path, Assert.Single(LockFiles()), ignoreCase: true);
        Assert.Equal("ORIGINAL-PICTURE", _files.Content[Assert.Single(BackupCopies())]);
    }
}