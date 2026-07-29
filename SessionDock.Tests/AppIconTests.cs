using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class AppIconTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly int[] ExpectedIconSizes =
        [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [Fact]
    public void SourceArtwork_IsSquareRgbaAndUsesRealTransparency()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Assets",
            "SessionDock.Icon.png");
        var bytes = File.ReadAllBytes(path);

        AssertPngHeader(bytes, 1024);
        AssertAlphaCoverage(DecodePng(bytes), expectTransparentCorners: true);
    }

    [Fact]
    public void WindowsIcon_ContainsTheCompleteReviewedFrameSet()
    {
        var entries = ReadIconEntries(IconPath());

        Assert.Equal(ExpectedIconSizes, entries.Select(entry => entry.Size));
        Assert.Equal(entries.Count, entries.Select(entry => entry.Size).Distinct().Count());
        foreach (var entry in entries)
        {
            Assert.Equal(32, entry.BitCount);
            AssertPngHeader(entry.Payload, entry.Size);
            AssertAlphaCoverage(
                DecodePng(entry.Payload),
                expectTransparentCorners: true);
        }
    }

    [Fact]
    public void ProjectAndReleaseWorkflow_UseTheCompatibleEmbeddedIconContract()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "SessionDock.csproj"));

        Assert.Equal(
            @"Assets\SessionDock.ico",
            project.Descendants("ApplicationIcon").Single().Value);

        var sourceIcon = project.Descendants("None").Single(element =>
            string.Equals(
                (string?)element.Attribute("Update"),
                @"Assets\SessionDock.ico",
                StringComparison.Ordinal));
        AssertNeverCopies(sourceIcon);

        var brandResource = project.Descendants("Resource").Single(element =>
            string.Equals(
                (string?)element.Attribute("Include"),
                @"Assets\SessionDock.Icon.png",
                StringComparison.Ordinal));
        AssertNeverCopies(brandResource);

        var mainWindow = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.Contains(
            mainWindow.Descendants(presentation + "Image"),
            element => string.Equals(
                (string?)element.Attribute("Source"),
                "/SessionDock;component/Assets/SessionDock.Icon.png",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            mainWindow.Descendants(presentation + "TextBlock"),
            element => string.Equals(
                (string?)element.Attribute("Text"),
                "SD",
                StringComparison.Ordinal));

        var workflow = File.ReadAllText(Path.Combine(
                root,
                ".github",
                "workflows",
                "release.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*--icon(?:\s|$)", workflow);
        Assert.DoesNotContain(
            "artifacts/release-input/SessionDock.ico",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Velopack 1.2 adds setup.ico to the full nupkg",
            workflow,
            StringComparison.Ordinal);

        var publishVerification = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Publish.ps1"));
        Assert.Contains(
            "Windows cannot extract the reviewed icon from published " +
            "SessionDock.exe.",
            publishVerification,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledExecutable_EmbedsTheReviewedLargeIconFrame()
    {
        var expectedPayload = ReadIconEntries(IconPath())
            .Single(entry => entry.Size == 256)
            .Payload;
        var executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "SessionDock.exe");

        Assert.True(File.Exists(executablePath));
        Assert.True(
            File.ReadAllBytes(executablePath)
                .AsSpan()
                .IndexOf(expectedPayload) >= 0,
            "The built SessionDock.exe did not contain the reviewed 256px icon frame.");
    }

    private static void AssertNeverCopies(XElement element)
    {
        Assert.Equal(
            "Never",
            (string?)element.Attribute("CopyToOutputDirectory"));
        Assert.Equal(
            "Never",
            (string?)element.Attribute("CopyToPublishDirectory"));
    }

    private static void AssertPngHeader(byte[] bytes, int expectedSize)
    {
        Assert.True(bytes.Length >= 29);
        Assert.True(bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(
            expectedSize,
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(
            expectedSize,
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(6, bytes[25]);
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return Assert.Single(decoder.Frames);
    }

    private static void AssertAlphaCoverage(
        BitmapSource source,
        bool expectTransparentCorners)
    {
        var converted = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgra32,
            null,
            0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        var hasTransparent = false;
        var hasPartial = false;
        var hasOpaque = false;
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            switch (pixels[offset])
            {
                case 0:
                    hasTransparent = true;
                    break;
                case 255:
                    hasOpaque = true;
                    break;
                default:
                    hasPartial = true;
                    break;
            }
        }

        Assert.True(hasTransparent);
        Assert.True(hasPartial);
        Assert.True(hasOpaque);
        if (!expectTransparentCorners)
            return;

        Assert.Equal(0, pixels[3]);
        Assert.Equal(0, pixels[stride - 1]);
        Assert.Equal(0, pixels[pixels.Length - stride + 3]);
        Assert.Equal(0, pixels[^1]);
    }

    private static IReadOnlyList<IconEntry> ReadIconEntries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 22);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        Assert.InRange(count, 1, 64);
        Assert.True(bytes.Length >= 6 + (16 * count));

        var entries = new List<IconEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var directory = bytes.AsSpan(6 + (16 * index), 16);
            var width = directory[0] == 0 ? 256 : directory[0];
            var height = directory[1] == 0 ? 256 : directory[1];
            Assert.Equal(width, height);

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
                directory[8..]);
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                directory[12..]);
            Assert.InRange(payloadLength, 29u, (uint)int.MaxValue);
            Assert.InRange(payloadOffset, 6u, (uint)int.MaxValue);
            Assert.True(
                (ulong)payloadOffset + payloadLength <= (ulong)bytes.Length);

            entries.Add(new IconEntry(
                width,
                BinaryPrimitives.ReadUInt16LittleEndian(directory[6..]),
                bytes.AsSpan((int)payloadOffset, (int)payloadLength).ToArray()));
        }

        return entries;
    }

    private static string IconPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Assets",
            "SessionDock.ico");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }

    private sealed record IconEntry(
        int Size,
        ushort BitCount,
        byte[] Payload);
}
