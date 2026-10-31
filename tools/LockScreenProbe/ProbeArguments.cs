namespace LockScreenProbe;

public enum ProbeAction
{
    Help,
    Status,
    Set,
    SetTwice,
    Restore,
}

/// <summary>The parsed command line: one action, and the PNG for --set / --set-twice.</summary>
public sealed record ProbeCommand(ProbeAction Action, string? PngPath = null)
{
    public const string Usage =
        "usage: LockScreenProbe --status | --set <png> | --set-twice <png> | --restore\n" +
        "  --status            read-only: what the lock screen shows now and what this PC supports\n" +
        "  --set <png>         back up the current lock screen image once, then set <png> as the lock screen\n" +
        "  --set-twice <png>   like --set, then again 5 seconds later under a new file name (repeated refresh)\n" +
        "  --restore           set the backed-up original image back";

    /// <summary>Parses the arguments; a wrong command line throws <see cref="ArgumentException"/> with a plain-English reason.</summary>
    public static ProbeCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("no action given.");
        }

        var name = args[0].ToLowerInvariant();
        var (action, needsPng) = name switch
        {
            "-h" or "--help" or "/?" => (ProbeAction.Help, false),
            "--status" => (ProbeAction.Status, false),
            "--set" => (ProbeAction.Set, true),
            "--set-twice" => (ProbeAction.SetTwice, true),
            "--restore" => (ProbeAction.Restore, false),
            _ => throw new ArgumentException($"unknown argument {args[0]}."),
        };

        var expected = needsPng ? 2 : 1;
        if (args.Count < expected)
        {
            throw new ArgumentException($"{args[0]} needs the path of a PNG file.");
        }

        if (args.Count > expected)
        {
            throw new ArgumentException($"unexpected argument {args[expected]} after {args[0]}; give one action at a time.");
        }

        if (!needsPng)
        {
            return new ProbeCommand(action);
        }

        var png = args[1];
        if (!png.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{png} is not a .png file.");
        }

        return new ProbeCommand(action, png);
    }
}
