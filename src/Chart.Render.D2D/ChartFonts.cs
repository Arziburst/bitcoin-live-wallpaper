using Vortice.DirectWrite;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>Inter and JetBrains Mono (both OFL-1.1, assets/fonts) as a private font collection: nothing is
/// installed and the system fonts are never used, so every machine draws the same glyphs.</summary>
public sealed class ChartFonts : IDisposable
{
    public const string Inter = "Inter";
    public const string Mono = "JetBrains Mono";

    private ChartFonts(IDWriteFontCollection1 collection) => Collection = collection;

    public IDWriteFontCollection1 Collection { get; }

    /// <summary>The fonts folder copied next to this assembly by the build.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");

    public static ChartFonts Load(IDWriteFactory5 factory, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        directory ??= DefaultDirectory;
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.ttf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
            : [];
        if (files.Length == 0)
        {
            throw new FileNotFoundException($"No .ttf fonts in {directory}.");
        }

        using var builder = factory.CreateFontSetBuilder();
        foreach (var file in files)
        {
            using var fontFile = factory.CreateFontFileReference(file);
            builder.AddFontFile(fontFile);
        }

        using var set = builder.CreateFontSet();
        var collection = factory.CreateFontCollectionFromFontSet(set);
        foreach (var family in new[] { Inter, Mono })
        {
            if (!collection.FindFamilyName(family, out _))
            {
                collection.Dispose();
                throw new InvalidOperationException($"The font family {family} is missing from {directory}.");
            }
        }

        return new ChartFonts(collection);
    }

    public void Dispose() => Collection.Dispose();
}
