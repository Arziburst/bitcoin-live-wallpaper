namespace LockScreenProbe;

/// <summary>
/// A best guess of the lock screen mode from the current image's path only (no registry is read).
/// Windows Spotlight images live in the ContentDeliveryManager / IrisService folders; built-in pictures under \Windows\Web\.
/// </summary>
public static class LockScreenKind
{
    public static string Describe(Uri? image)
    {
        if (image is null)
        {
            return "not detectable (Windows reports no image file - Windows Spotlight or Slideshow, or no access)";
        }

        var path = image.IsAbsoluteUri ? image.LocalPath : image.OriginalString;
        if (path.Contains("ContentDeliveryManager", StringComparison.OrdinalIgnoreCase)
            || path.Contains("IrisService", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Spotlight", StringComparison.OrdinalIgnoreCase))
        {
            return "probably Windows Spotlight (the image is in a Spotlight folder)";
        }

        if (path.Contains(@"\BitcoinWallpaper\lock\", StringComparison.OrdinalIgnoreCase))
        {
            return "Picture - one set by this probe";
        }

        if (path.Contains(@"\Windows\Web\", StringComparison.OrdinalIgnoreCase))
        {
            return "Picture - a built-in Windows picture";
        }

        return "Picture (or Slideshow) - a custom image";
    }
}
