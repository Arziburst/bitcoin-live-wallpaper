using System.Globalization;

namespace LockScreenProbe;

/// <summary>File names under %LOCALAPPDATA%\BitcoinWallpaper\lock. Each set uses a new name (TrySetLockScreenImageAsync needs one).</summary>
public static class LockFiles
{
    public const string OriginalBaseName = "original";

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "lock");

    /// <summary>lock_20261031T143005123Z.png: UTC to the millisecond, so two sets never share a name.</summary>
    public static string ImageFileName(DateTime utc) => $"lock_{Stamp(utc)}.png";

    /// <summary>restore_20261031T143005123Z.jpg: the original copied under a new name before it is set back.</summary>
    public static string RestoreFileName(DateTime utc, string extension) => $"restore_{Stamp(utc)}{NormalizeExtension(extension)}";

    /// <summary>original.jpg / original.png: the one backup, taken the first time --set runs.</summary>
    public static string OriginalFileName(string extension) => OriginalBaseName + NormalizeExtension(extension);

    /// <summary>The backup in <paramref name="directory"/>, if any (original.* excluding the .txt note).</summary>
    public static string? FindOriginal(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, OriginalBaseName + ".*")
            .Where(path => !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>The image extension of the current lock screen file (from its URI), .jpg when unknown.</summary>
    public static string ExtensionOf(Uri? image)
    {
        if (image is null)
        {
            return ".jpg";
        }

        var extension = Path.GetExtension(image.IsAbsoluteUri ? image.LocalPath : image.OriginalString);
        return extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" ? extension : ".jpg";
    }

    private static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.Length == 0 ? ".jpg" : trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }
}
