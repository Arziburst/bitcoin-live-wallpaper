using System.Globalization;
using System.Runtime.InteropServices;
using LockScreenProbe;
using Windows.Storage;
using Windows.System.UserProfile;

// Spike for task #39 (D-34 step 1): can an unpackaged per-user app set the Windows lock screen image?
// --status only reads. --set, --set-twice and --restore change the lock screen of the user who runs them.
ProbeCommand command;
try
{
    command = ProbeCommand.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"{e.Message}\n{ProbeCommand.Usage}");
    return 2;
}

if (command.Action == ProbeAction.Help)
{
    Console.WriteLine(ProbeCommand.Usage);
    return 0;
}

var directory = LockFiles.DefaultDirectory();
var log = new Log(command.Action == ProbeAction.Status ? null : Path.Combine(directory, "probe.log"));
try
{
    log.Line($"LockScreenProbe {command.Action} at {DateTime.UtcNow:u}");
    return command.Action switch
    {
        ProbeAction.Status => Status(),
        ProbeAction.Set => await SetAsync(command.PngPath!) ? 0 : 1,
        ProbeAction.SetTwice => await SetTwiceAsync(command.PngPath!) ? 0 : 1,
        ProbeAction.Restore => await RestoreAsync() ? 0 : 1,
        _ => 2,
    };
}
catch (Exception e)
{
    log.Line($"Stopped by an unexpected error: {Describe(e)}");
    return 1;
}
finally
{
    log.Dispose();
}

int Status()
{
    log.Line($"Windows {Environment.OSVersion.Version}, process {RuntimeInformation.ProcessArchitecture} on {RuntimeInformation.OSArchitecture}.");
    log.Line(HasPackageIdentity() ? "This process has a package identity (packaged)." : "This process has no package identity (unpackaged), as the product would.");
    log.Line($"UserProfilePersonalizationSettings.IsSupported(): {IsFallbackSupported()}");
    var image = ReadBack();
    log.Line($"Lock screen kind (best guess from the image path): {LockScreenKind.Describe(image)}");
    try
    {
        using var stream = LockScreen.GetImageStream();
        log.Line(stream is null ? "LockScreen.GetImageStream(): no stream." : $"LockScreen.GetImageStream(): {stream.Size:N0} bytes readable.");
    }
    catch (Exception e)
    {
        log.Line($"LockScreen.GetImageStream() failed: {Describe(e)}");
    }

    var original = LockFiles.FindOriginal(directory);
    log.Line(original is null ? $"No backup yet in {directory} (--set makes one)." : $"Backup of the original image: {original}");
    log.Line("Nothing was changed.");
    return 0;
}

async Task<bool> SetAsync(string png)
{
    if (!File.Exists(png))
    {
        log.Line($"The file {Path.GetFullPath(png)} does not exist.");
        return false;
    }

    Directory.CreateDirectory(directory);
    await BackUpOriginalOnceAsync();
    return await SetCopyAsync(png, LockFiles.ImageFileName(DateTime.UtcNow));
}

async Task<bool> SetTwiceAsync(string png)
{
    log.Line("First call:");
    var first = await SetAsync(png);
    log.Line("Waiting 5 seconds before the second call (new file name)...");
    await Task.Delay(TimeSpan.FromSeconds(5));
    log.Line("Second call:");
    var second = await SetCopyAsync(png, LockFiles.ImageFileName(DateTime.UtcNow));
    log.Line($"First call {(first ? "succeeded" : "failed")}, second call {(second ? "succeeded" : "failed")}.");
    return first && second;
}

async Task<bool> RestoreAsync()
{
    var original = LockFiles.FindOriginal(directory);
    if (original is null)
    {
        log.Line($"No backup found in {directory}; nothing to restore. Pick the picture again in Settings > Personalization > Lock screen.");
        return false;
    }

    log.Line($"Restoring the backup {original}.");
    return await SetCopyAsync(original, LockFiles.RestoreFileName(DateTime.UtcNow, Path.GetExtension(original)));
}

async Task BackUpOriginalOnceAsync()
{
    var existing = LockFiles.FindOriginal(directory);
    if (existing is not null)
    {
        log.Line($"A backup of the original image already exists ({existing}); keeping it.");
        return;
    }

    var image = ReadBack();
    using var stream = LockScreen.GetImageStream();
    if (stream is null)
    {
        log.Line("Could not back up the current image: Windows gave no image stream. --restore will not be able to set it back.");
        return;
    }

    var target = Path.Combine(directory, LockFiles.OriginalFileName(LockFiles.ExtensionOf(image)));
    await using (var source = stream.AsStreamForRead())
    await using (var file = File.Create(target))
    {
        await source.CopyToAsync(file);
    }

    await File.WriteAllTextAsync(Path.Combine(directory, LockFiles.OriginalBaseName + ".txt"), $"{image}\n");
    log.Line($"Backed up the current lock screen image to {target}.");
}

async Task<bool> SetCopyAsync(string source, string fileName)
{
    var target = Path.Combine(directory, fileName);
    File.Copy(source, target, overwrite: false);
    log.Line($"Copied {Path.GetFullPath(source)} to {target}.");
    var file = await StorageFile.GetFileFromPathAsync(target);

    var done = false;
    try
    {
        log.Line("Calling LockScreen.SetImageFileAsync...");
        await LockScreen.SetImageFileAsync(file);
        log.Line("LockScreen.SetImageFileAsync succeeded.");
        done = true;
    }
    catch (Exception e)
    {
        log.Line($"LockScreen.SetImageFileAsync failed: {Describe(e)}");
    }

    if (!done)
    {
        if (!IsFallbackSupported())
        {
            log.Line("Fallback skipped: UserProfilePersonalizationSettings.IsSupported() is false on this PC.");
        }
        else
        {
            try
            {
                log.Line("Calling UserProfilePersonalizationSettings.TrySetLockScreenImageAsync...");
                done = await UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(file);
                log.Line($"TrySetLockScreenImageAsync returned {done}.");
            }
            catch (Exception e)
            {
                log.Line($"TrySetLockScreenImageAsync failed: {Describe(e)}");
            }
        }
    }

    var now = ReadBack();
    if (now is not null && now.IsFile)
    {
        log.Line(string.Equals(now.LocalPath, target, StringComparison.OrdinalIgnoreCase)
            ? "Read-back matches the file just set."
            : "Read-back is a different file (Windows may keep its own copy; check the lock screen with Win+L).");
    }

    log.Line(done ? "Result: the lock screen image was set. Press Win+L to look." : "Result: the lock screen image was NOT set.");
    return done;
}

Uri? ReadBack()
{
    try
    {
        var image = LockScreen.OriginalImageFile;
        log.Line($"LockScreen.OriginalImageFile: {(image is null ? "(none)" : image.ToString())}");
        return image;
    }
    catch (Exception e)
    {
        log.Line($"Reading LockScreen.OriginalImageFile failed: {Describe(e)}");
        return null;
    }
}

bool IsFallbackSupported()
{
    try
    {
        return UserProfilePersonalizationSettings.IsSupported();
    }
    catch (Exception e)
    {
        log.Line($"UserProfilePersonalizationSettings.IsSupported() failed: {Describe(e)}");
        return false;
    }
}

static bool HasPackageIdentity()
{
    try
    {
        _ = Windows.ApplicationModel.Package.Current.Id;
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
    catch (COMException)
    {
        return false;
    }
}

static string Describe(Exception e) =>
    string.Create(CultureInfo.InvariantCulture, $"{e.GetType().Name} HRESULT 0x{e.HResult:X8}: {e.Message.Trim()}");

/// <summary>Every step goes to the console; for actions that change something also to probe.log in the lock folder.</summary>
internal sealed class Log(string? path) : IDisposable
{
    private StreamWriter? _file;

    public void Line(string text)
    {
        Console.WriteLine(text);
        if (path is null)
        {
            return;
        }

        if (_file is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: true) { AutoFlush = true };
        }

        _file.WriteLine(text);
    }

    public void Dispose() => _file?.Dispose();
}
