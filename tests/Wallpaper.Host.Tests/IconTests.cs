using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using BitcoinWallpaper.Host.Playback;

namespace BitcoinWallpaper.Host.Tests;

// Contract #9, criterion 1: assets/icon/app.ico is built from the owner's PNG (padded to a square, sizes
// 16/20/24/32/40/48/64/256, 32-bit with alpha) and used for the exe; the source PNG is committed next to it (D-14).
//
// SKETCH (written before the code, #237). No product API: the tests read files.
//   assets/icon/app.ico  - found by walking up from the test output to BitcoinWallpaper.sln.
//                          Exactly 8 images: 16, 20, 24, 32, 40, 48, 64, 256 px, each square, each 32 bpp with an
//                          alpha channel. Sizes below 256: 32-bit BI_RGB DIB (BGRA). The 256 image: the same DIB
//                          or PNG (8-bit RGBA, colour type 6, not interlaced). At least one corner pixel of the 256
//                          image is fully transparent (the square padding / the logo's transparent background).
//   assets/icon/app.png  - the owner's source, byte for byte: a 1312x1199 RGBA PNG.
//   The exe (BitcoinWallpaper, src/Wallpaper.Host) carries app.ico as its Win32 icon (ApplicationIcon): its
//   RT_GROUP_ICON resource lists the same 8 sizes at 32 bpp. The test reads the BitcoinWallpaper.dll copied
//   into the test output (the apphost .exe gets its resources from it) as a data file, it does not run it.
//
// Not covered here (owner / review): the tray icon and the Setup.exe use the same icon; the image at each size
// is the same picture.
public class IconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string IconDir => Path.Combine(RepoRoot(), "assets", "icon");
    private static string IcoPath => Path.Combine(IconDir, "app.ico");
    private static string PngPath => Path.Combine(IconDir, "app.png");

    [Fact]
    public void Source_png_is_committed_next_to_the_icon()
    {
        Assert.True(File.Exists(PngPath), $"missing {PngPath}");

        var png = DecodePngHeader(File.ReadAllBytes(PngPath));
        Assert.Equal(1312, png.Width);    // the owner's S-2-app-icon.png, unchanged
        Assert.Equal(1199, png.Height);
        Assert.True(png.ColorType is 4 or 6, $"app.png has no alpha channel (colour type {png.ColorType})");
    }

    [Fact]
    public void Ico_has_a_valid_header()
    {
        var bytes = ReadIco();

        Assert.True(bytes.Length >= 6, "app.ico is shorter than an ICONDIR");
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0)));   // idReserved
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));   // idType: 1 = icon
    }

    [Fact]
    public void Ico_contains_exactly_the_eight_sizes()
    {
        var entries = ReadEntries(ReadIco());

        Assert.Equal(ExpectedSizes.Length, entries.Count);
        Assert.Equal(ExpectedSizes, entries.Select(e => e.Width).OrderBy(w => w).ToArray());
    }

    [Fact]
    public void Every_image_is_square_and_matches_its_directory_entry()
    {
        var bytes = ReadIco();
        foreach (var entry in ReadEntries(bytes))
        {
            Assert.Equal(entry.Width, entry.Height);
            var image = DecodeImage(bytes, entry);
            Assert.Equal(entry.Width, image.Width);
            Assert.Equal(entry.Height, image.Height);
        }
    }

    [Fact]
    public void Every_image_is_32_bpp_and_only_the_256_image_may_be_png()
    {
        var bytes = ReadIco();
        foreach (var entry in ReadEntries(bytes))
        {
            var data = bytes.AsSpan(entry.Offset, entry.Length);
            var isPng = data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

            if (isPng)
            {
                Assert.True(entry.Width == 256, $"the {entry.Width}px image is PNG; only 256 may be");
                var png = DecodePngHeader(data.ToArray());
                Assert.Equal(8, png.BitDepth);
                Assert.Equal(6, png.ColorType);   // RGBA = 32 bpp with alpha
                Assert.True(entry.BitCount is 0 or 32, $"256px entry wBitCount {entry.BitCount}");
            }
            else
            {
                Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(data[14..]));   // biBitCount
                Assert.Equal(32, entry.BitCount);
            }
        }
    }

    [Fact]
    public void Every_image_uses_its_alpha_channel()
    {
        var bytes = ReadIco();
        foreach (var entry in ReadEntries(bytes))
        {
            var image = DecodeImage(bytes, entry);
            var alphas = Enumerable.Range(0, image.Width * image.Height)
                .Select(i => image.Alpha(i % image.Width, i / image.Width)).ToList();

            Assert.True(alphas.Any(a => a < 255), $"the {entry.Width}px image is fully opaque: no alpha");
            Assert.True(alphas.Any(a => a > 0), $"the {entry.Width}px image is fully transparent");
        }
    }

    [Fact]
    public void The_256_image_has_a_transparent_corner()
    {
        var bytes = ReadIco();
        var entry = Assert.Single(ReadEntries(bytes), e => e.Width == 256);
        var image = DecodeImage(bytes, entry);

        var corners = new[]
        {
            image.Alpha(0, 0), image.Alpha(255, 0), image.Alpha(0, 255), image.Alpha(255, 255),
        };
        Assert.Contains((byte)0, corners);
    }

    [Fact]
    public void The_exe_carries_the_icon_with_the_eight_sizes()
    {
        var assembly = typeof(PlaybackMonitor).Assembly.Location;   // BitcoinWallpaper.dll
        var groups = ReadGroupIcons(assembly);

        Assert.True(groups.Count > 0, "BitcoinWallpaper has no Win32 icon (RT_GROUP_ICON); set ApplicationIcon");
        var sizes = groups[0].Select(e => e.Size).OrderBy(s => s).ToArray();
        Assert.Equal(ExpectedSizes, sizes);
        Assert.All(groups[0], e => Assert.Equal(32, e.BitCount));
    }

    // ---- ICO parsing ----

    private sealed record IcoEntry(int Width, int Height, int BitCount, int Length, int Offset);

    private static byte[] ReadIco()
    {
        Assert.True(File.Exists(IcoPath), $"missing {IcoPath}");
        return File.ReadAllBytes(IcoPath);
    }

    private static List<IcoEntry> ReadEntries(byte[] bytes)
    {
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        Assert.True(bytes.Length >= 6 + 16 * count, "ICONDIRENTRY table runs past the end of app.ico");

        var entries = new List<IcoEntry>();
        for (var i = 0; i < count; i++)
        {
            var e = bytes.AsSpan(6 + 16 * i, 16);
            var width = e[0] == 0 ? 256 : e[0];
            var height = e[1] == 0 ? 256 : e[1];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]));
            var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            Assert.True(offset >= 0 && length > 0 && (long)offset + length <= bytes.Length,
                $"entry {i} ({width}px) points outside app.ico");
            entries.Add(new IcoEntry(width, height, bitCount, length, offset));
        }
        return entries;
    }

    private sealed record Rgba(int Width, int Height, byte[] Pixels)   // top-down, 4 bytes per pixel, alpha last
    {
        public byte Alpha(int x, int y) => Pixels[(y * Width + x) * 4 + 3];
    }

    private static Rgba DecodeImage(byte[] bytes, IcoEntry entry)
    {
        var data = bytes.AsSpan(entry.Offset, entry.Length);
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return DecodePng(data.ToArray());
        return DecodeDib(data);
    }

    private static Rgba DecodeDib(ReadOnlySpan<byte> data)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.True(headerSize >= 40, $"DIB header size {headerSize}");
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var doubledHeight = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);   // XOR + AND masks
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        Assert.Equal(32, bitCount);
        Assert.Equal(0u, compression);   // BI_RGB
        var height = doubledHeight / 2;
        Assert.True(width > 0 && height > 0, $"DIB size {width}x{doubledHeight}");

        var stride = width * 4;
        Assert.True(data.Length >= headerSize + stride * height, "DIB pixel data truncated");
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var src = data.Slice(headerSize + (height - 1 - y) * stride, stride);   // bottom-up rows
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = src[x * 4 + 2];
                pixels[o + 1] = src[x * 4 + 1];
                pixels[o + 2] = src[x * 4];
                pixels[o + 3] = src[x * 4 + 3];
            }
        }
        return new Rgba(width, height, pixels);
    }

    // ---- PNG parsing (8-bit RGBA, non-interlaced: enough for the 256 icon image) ----

    private sealed record PngHeader(int Width, int Height, int BitDepth, int ColorType, int Interlace);

    private static PngHeader DecodePngHeader(byte[] png)
    {
        Assert.True(png.Length >= 33 && png.AsSpan(0, 8).SequenceEqual(PngSignature), "not a PNG");
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        return new PngHeader(
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)),
            png[24], png[25], png[28]);
    }

    private static Rgba DecodePng(byte[] png)
    {
        var header = DecodePngHeader(png);
        Assert.Equal(8, header.BitDepth);
        Assert.Equal(6, header.ColorType);
        Assert.Equal(0, header.Interlace);

        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT") idat.Write(png, pos + 8, length);
            if (type == "IEND") break;
            pos += 12 + length;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();

        const int bpp = 4;
        var stride = header.Width * bpp;
        Assert.True(data.Length >= (stride + 1) * header.Height, "PNG image data truncated");
        var pixels = new byte[stride * header.Height];
        for (var y = 0; y < header.Height; y++)
        {
            var filter = data[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? pixels[dst + i - bpp] : 0;
                int b = y > 0 ? pixels[dst - stride + i] : 0;
                int c = y > 0 && i >= bpp ? pixels[dst - stride + i - bpp] : 0;
                int x = data[src + i];
                pixels[dst + i] = (byte)(filter switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + (a + b) / 2,
                    4 => x + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter}"),
                });
            }
        }
        return new Rgba(header.Width, header.Height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ---- Win32 icon resource of the built exe (read as a data file) ----

    private sealed record GroupEntry(int Size, int BitCount);

    private static List<List<GroupEntry>> ReadGroupIcons(string modulePath)
    {
        const uint LoadLibraryAsDatafile = 0x2, LoadLibraryAsImageResource = 0x20;
        var module = LoadLibraryExW(modulePath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        Assert.True(module != IntPtr.Zero, $"cannot open {modulePath} as a data file ({Marshal.GetLastWin32Error()})");
        try
        {
            var groups = new List<List<GroupEntry>>();
            EnumResNameProc proc = (_, _, name, _) => { groups.Add(ReadGroup(module, name)); return true; };
            EnumResourceNamesW(module, RtGroupIcon, proc, IntPtr.Zero);   // false and no callback when there is none
            GC.KeepAlive(proc);
            return groups;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static List<GroupEntry> ReadGroup(IntPtr module, IntPtr name)   // called while `name` is valid
    {
        var entries = new List<GroupEntry>();
        var res = FindResourceW(module, name, RtGroupIcon);
        if (res == IntPtr.Zero) return entries;
        var size = (int)SizeofResource(module, res);
        var ptr = LockResource(LoadResource(module, res));
        var buffer = new byte[size];
        Marshal.Copy(ptr, buffer, 0, size);

        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var e = buffer.AsSpan(6 + 14 * i, 14);   // GRPICONDIRENTRY
            entries.Add(new GroupEntry(e[0] == 0 ? 256 : e[0], BinaryPrimitives.ReadUInt16LittleEndian(e[6..])));
        }
        return entries;
    }

    private static readonly IntPtr RtGroupIcon = 14;

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResNameProc proc, IntPtr param);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr res);

    [DllImport("kernel32")]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr res);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BitcoinWallpaper.sln")))
                return dir.FullName;
        throw new InvalidOperationException("BitcoinWallpaper.sln not found above " + AppContext.BaseDirectory);
    }
}
