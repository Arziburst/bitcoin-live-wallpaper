using Windows.Storage;
using Windows.System.UserProfile;

namespace BitcoinWallpaper.Host.LockScreenChart;

/// <summary>
/// The real lock screen (D-34) over Windows.System.UserProfile.LockScreen, as probe #39 proved it on the owner's PC
/// from an unpackaged x64 process: SetImageFileAsync with a new file name per call. Tests never use this class.
/// </summary>
internal sealed class WindowsLockScreenApi : ILockScreenApi
{
    public string? OriginalImageFile
    {
        get
        {
            var image = LockScreen.OriginalImageFile;
            if (image is null)
            {
                return null;
            }

            return image.IsFile ? image.LocalPath : null;
        }
    }

    public async Task SetImageFileAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        await LockScreen.SetImageFileAsync(file);
    }
}

/// <summary>The real file system for <see cref="LockScreenImages"/>.</summary>
internal sealed class SystemLockScreenFiles : ILockScreenFiles
{
    public bool Exists(string path) => File.Exists(path);

    public void CreateDirectory(string folder) => Directory.CreateDirectory(folder);

    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);

    public void Delete(string path) => File.Delete(path);

    public IReadOnlyList<string> List(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
}
