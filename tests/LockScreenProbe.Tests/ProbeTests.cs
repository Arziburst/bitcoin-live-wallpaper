using LockScreenProbe;

namespace LockScreenProbe.Tests;

public class ProbeArgumentsTests
{
    [Theory]
    [InlineData("--status", ProbeAction.Status)]
    [InlineData("--restore", ProbeAction.Restore)]
    [InlineData("--help", ProbeAction.Help)]
    [InlineData("--STATUS", ProbeAction.Status)]
    public void Actions_without_a_file(string arg, ProbeAction expected)
    {
        var command = ProbeCommand.Parse([arg]);

        Assert.Equal(expected, command.Action);
        Assert.Null(command.PngPath);
    }

    [Theory]
    [InlineData("--set", ProbeAction.Set)]
    [InlineData("--set-twice", ProbeAction.SetTwice)]
    public void Set_actions_take_the_png(string arg, ProbeAction expected)
    {
        var command = ProbeCommand.Parse([arg, @"C:\Temp\btc-5120x1440.png"]);

        Assert.Equal(expected, command.Action);
        Assert.Equal(@"C:\Temp\btc-5120x1440.png", command.PngPath);
    }

    [Theory]
    [InlineData]
    [InlineData("--sett")]
    [InlineData("--set")]
    [InlineData("--set", "picture.jpg")]
    [InlineData("--set", "a.png", "b.png")]
    [InlineData("--status", "--restore")]
    public void Wrong_command_lines_are_rejected(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ProbeCommand.Parse(args));
    }
}

public class LockFilesTests
{
    private static readonly DateTime At = new(2026, 10, 31, 14, 30, 5, 123, DateTimeKind.Utc);

    [Fact]
    public void Image_name_is_utc_to_the_millisecond()
    {
        Assert.Equal("lock_20261031T143005123Z.png", LockFiles.ImageFileName(At));
    }

    [Fact]
    public void Local_time_is_converted_to_utc()
    {
        Assert.Equal(LockFiles.ImageFileName(At), LockFiles.ImageFileName(At.ToLocalTime()));
    }

    [Fact]
    public void Two_calls_5_seconds_apart_get_different_names()
    {
        Assert.NotEqual(LockFiles.ImageFileName(At), LockFiles.ImageFileName(At.AddSeconds(5)));
    }

    [Theory]
    [InlineData("jpg", "original.jpg")]
    [InlineData(".PNG", "original.png")]
    [InlineData("", "original.jpg")]
    public void Original_backup_name(string extension, string expected)
    {
        Assert.Equal(expected, LockFiles.OriginalFileName(extension));
    }

    [Fact]
    public void Restore_copy_gets_a_new_name_with_the_original_extension()
    {
        Assert.Equal("restore_20261031T143005123Z.jpg", LockFiles.RestoreFileName(At, ".jpg"));
    }

    [Theory]
    [InlineData(@"file:///C:/Windows/Web/Screen/img100.jpg", ".jpg")]
    [InlineData(@"file:///C:/Users/me/Pictures/desk.PNG", ".PNG")]
    [InlineData(@"file:///C:/Users/me/AppData/Local/x/noext", ".jpg")]
    public void Extension_of_the_current_image(string uri, string expected)
    {
        Assert.Equal(expected, LockFiles.ExtensionOf(new Uri(uri)));
    }

    [Fact]
    public void Find_original_ignores_the_note_and_other_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LockScreenProbe.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(LockFiles.FindOriginal(dir));
            File.WriteAllText(Path.Combine(dir, "original.txt"), "uri");
            File.WriteAllText(Path.Combine(dir, "lock_20261031T143005123Z.png"), "");
            Assert.Null(LockFiles.FindOriginal(dir));

            File.WriteAllText(Path.Combine(dir, "original.jpg"), "");
            Assert.Equal(Path.Combine(dir, "original.jpg"), LockFiles.FindOriginal(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"file:///C:/Users/me/AppData/Local/Packages/Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy/LocalState/Assets/abc", "Spotlight")]
    [InlineData(@"file:///C:/Windows/Web/Screen/img100.jpg", "built-in")]
    [InlineData(@"file:///C:/Users/me/AppData/Local/BitcoinWallpaper/lock/lock_20261031T143005123Z.png", "this probe")]
    [InlineData(@"file:///D:/Photos/desk.jpg", "custom")]
    public void Kind_is_guessed_from_the_image_path(string uri, string expected)
    {
        Assert.Contains(expected, LockScreenKind.Describe(new Uri(uri)));
    }

    [Fact]
    public void Kind_without_an_image_is_not_detectable()
    {
        Assert.StartsWith("not detectable", LockScreenKind.Describe(null));
    }
}
