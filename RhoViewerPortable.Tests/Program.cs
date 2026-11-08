using System.Buffers.Binary;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Length > 0 && string.Equals(args[0], "--archive-corpus", StringComparison.OrdinalIgnoreCase))
            return ArchiveCorpusTest.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "--bml-corpus", StringComparison.OrdinalIgnoreCase))
            return BmlCorpusTest.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && string.Equals(args[0], "--find-pin", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: RhoViewerPortable.Tests --find-pin <gameDirectory>");
                return 2;
            }

            try
            {
                Console.WriteLine(KartPatchDiscovery.FindPinFile(args[1]));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.Tests [--archive-corpus [--sha256] dataRoot [dataRoot ...] | --bml-corpus path [path ...] | --find-pin gameDirectory]");
            return 2;
        }

        var failures = 0;
        failures += RunSuite("resource parser malformed-input tests", ParserInputTests.Run);
        failures += RunSuite("BML binary XML-like parser tests", BmlAnalyzerTests.Run);
        failures += RunSuite("updater security tests", UpdaterSecurityTests.Run);
        failures += RunSuite("H1-H6 and M1-M3 offline archive regressions", HighFixRegressionTests.Run);
        failures += RunSuite("M4 non-DDS image header limits", NonDdsImageHeaderTests.Run);
        failures += RunSuite("optional null-terminated text cap", NullTerminatedTextLimitTests.Run);
        failures += RunSuite("CRC and UI mutation-state tests", GeneralTests.Run);
        failures += RunSuite("large virtual resource grid tests", VirtualGridTests.Run);
        failures += RunSuite("M5 debounce, folder tree, selection, and preview gate regressions", UiPerformanceHelperTests.Run);
        failures += RunSuite("M5 virtual game update grid test", GameUpdateGridTests.Run);
        failures += RunSuite("bounded preview byte-cache tests", PreviewByteCacheTests.Run);
        failures += RunSuite("language selection and config persistence tests", LanguagePersistenceTests.Run);
        failures += RunSuite("DDS decoding and malformed-input tests", DdsDecoderTests.Run);
        failures += RunSuite("KartRider.pin file picker and shown virtual-grid lifecycle", PickerAndGridLifecycleTests.Run);
        Console.WriteLine(failures == 0 ? "ALL TEST SUITES PASSED" : $"TEST FAILURES: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int RunSuite(string name, Action test)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:O}] START {name}");
        var status = "FAIL";
        try
        {
            test();
            status = "PASS";
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {name}: {ex}");
            return 1;
        }
        finally
        {
            Console.WriteLine($"[{DateTimeOffset.Now:O}] END {name} {status}");
        }
    }
}

internal static class GameUpdateGridTests
{
    public static void Run()
    {
        const int itemCount = 100_000;
        var file = new KartPatchFile
        {
            RelativePath = "Data/sample.bin",
            Crc = 0x12345678,
            Size = 12,
            SizeGzipped = 8
        };
        var item = new KartPatchPlanItem
        {
            File = file,
            TargetPath = Path.Combine("Data", "sample.bin"),
            State = KartPatchFileState.Mismatch,
            LocalSize = 10
        };
        var plan = new KartPatchPlan
        {
            GameDirectory = Path.GetTempPath(),
            PinPath = Path.Combine(Path.GetTempPath(), "KartRider.pin"),
            ClientVersion = 1,
            ServerVersion = 2,
            Region = KartUpdateRegion.Auto,
            ServerHost = "localhost",
            ServerPort = 39311,
            PatchBaseUrl = "https://example.invalid/patch/",
            AllowInsecureHttp = false,
            Items = Enumerable.Repeat(item, itemCount).ToArray()
        };

        using var form = new GameUpdateForm(AppSettings.CreateDefault());
        form.RenderPlan(plan);
        if (!form.IsUpdateGridVirtualized)
            throw new InvalidDataException("GameUpdateForm update grid must use DataGridView virtual mode.");
        if (form.UpdateGridRowCount != itemCount)
            throw new InvalidDataException($"virtual update grid should expose all {itemCount:N0} items; got {form.UpdateGridRowCount:N0}.");
    }
}

internal static class DdsDecoderTests
{
    public static void Run()
    {
        TestBc1AndTransparency();
        TestBc2AndBc3();
        TestBc4AndBc5();
        TestDx10RgbaAndLegacyPitch();
        TestInvalidAndUnsupportedInputs();
        TestUnsupportedTextureLayouts();
    }

    private static void TestBc1AndTransparency()
    {
        var solidRed = new byte[8];
        WriteUInt16(solidRed, 0, 0xf800);
        WriteUInt16(solidRed, 2, 0x0000);
        using (var decoded = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "DXT1", solidRed)))
        {
            AssertColor(decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 255, 0, 0), "BC1/DXT1 red endpoint");
            Assert(decoded.Bitmap.Width == 4 && decoded.Bitmap.Height == 4, "BC1 dimensions were not preserved");
            Assert(decoded.Format.Contains("BC1", StringComparison.Ordinal), "BC1 format label was not returned");
        }

        WriteUInt16(solidRed, 2, 0xf800);
        BinaryPrimitives.WriteUInt32LittleEndian(solidRed.AsSpan(4), uint.MaxValue);
        using var transparent = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "DXT1", solidRed));
        Assert(transparent.Bitmap.GetPixel(0, 0).A == 0, "BC1 three-color mode did not preserve transparent index 3");
    }

    private static void TestBc2AndBc3()
    {
        var bc2 = new byte[16];
        Array.Fill(bc2, (byte)0xff, 0, 8);
        WriteUInt16(bc2, 8, 0xf800);
        WriteUInt16(bc2, 10, 0x0000);
        using (var decoded = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "DXT3", bc2)))
            AssertColor(decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 255, 0, 0), "BC2/DXT3 explicit alpha");

        var bc3 = new byte[16];
        bc3[0] = 255;
        bc3[1] = 0;
        WriteUInt16(bc3, 8, 0xf800);
        WriteUInt16(bc3, 10, 0x0000);
        using var bc3Decoded = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "DXT5", bc3));
        AssertColor(bc3Decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 255, 0, 0), "BC3/DXT5 interpolated alpha");
    }

    private static void TestBc4AndBc5()
    {
        var bc4 = new byte[8];
        bc4[0] = bc4[1] = 64;
        using (var decoded = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "ATI1", bc4)))
            AssertColor(decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 64, 64, 64), "BC4 grayscale channel");

        var bc5 = new byte[16];
        bc5[0] = bc5[1] = 255;
        bc5[8] = bc5[9] = 128;
        using var bc5Decoded = DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "ATI2", bc5));
        AssertColor(bc5Decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 255, 128, 0), "BC5 two-channel preview");
    }

    private static void TestDx10RgbaAndLegacyPitch()
    {
        var rgba = new byte[] { 12, 34, 56, 78, 90, 123, 200, 255 };
        using (var decoded = DdsImageDecoder.Decode(BuildDx10Dds(2, 1, 28, rgba)))
        {
            AssertColor(decoded.Bitmap.GetPixel(0, 0), Color.FromArgb(78, 12, 34, 56), "DX10 RGBA channel order");
            AssertColor(decoded.Bitmap.GetPixel(1, 0), Color.FromArgb(255, 90, 123, 200), "DX10 RGBA second pixel");
        }

        var pixelFormat = new byte[32];
        WriteUInt32(pixelFormat, 0, 32);
        WriteUInt32(pixelFormat, 4, 0x40);
        WriteUInt32(pixelFormat, 12, 24);
        WriteUInt32(pixelFormat, 16, 0x00ff0000);
        WriteUInt32(pixelFormat, 20, 0x0000ff00);
        WriteUInt32(pixelFormat, 24, 0x000000ff);
        var pitched = BuildMaskedDds(1, 1, pixelFormat, 4, new byte[] { 255, 0, 0, 0 });
        using var legacy = DdsImageDecoder.Decode(pitched);
        AssertColor(legacy.Bitmap.GetPixel(0, 0), Color.FromArgb(255, 0, 0, 255), "24-bit RGB masks with padded row pitch");
    }

    private static void TestInvalidAndUnsupportedInputs()
    {
        ExpectInvalid(() => DdsImageDecoder.Decode(new byte[127]), "truncated DDS header");
        ExpectInvalid(() => DdsImageDecoder.Decode(BuildFourCcDds(4, 4, "DXT1", new byte[7])), "truncated BC1 block");

        var missingHeaderFlags = BuildFourCcDds(4, 4, "DXT1", new byte[8]);
        WriteUInt32(missingHeaderFlags, 8, 0);
        ExpectInvalid(() => DdsImageDecoder.Decode(missingHeaderFlags), "missing required DDS header flags");

        var missingTextureCaps = BuildFourCcDds(4, 4, "DXT1", new byte[8]);
        WriteUInt32(missingTextureCaps, 108, 0);
        ExpectInvalid(() => DdsImageDecoder.Decode(missingTextureCaps), "missing DDS texture caps");

        var huge = BuildFourCcDds(4, 4, "DXT1", new byte[8]);
        WriteUInt32(huge, 12, uint.MaxValue);
        ExpectInvalid(() => DdsImageDecoder.Decode(huge), "oversized dimensions");

        var unsupported = BuildFourCcDds(4, 4, "DXT9", new byte[16]);
        try
        {
            using var _ = DdsImageDecoder.Decode(unsupported);
        }
        catch (NotSupportedException)
        {
            return;
        }
        throw new InvalidDataException("Unsupported DDS FourCC was not rejected.");
    }

    private static void TestUnsupportedTextureLayouts()
    {
        var legacyCubeFace = BuildFourCcDds(4, 4, "DXT1", new byte[8]);
        WriteUInt32(legacyCubeFace, 112, 0x400); // A cube face flag without the main cube-map flag is malformed too.
        ExpectUnsupported(() => DdsImageDecoder.Decode(legacyCubeFace), "legacy cube-map face");

        var dx10Array = BuildDx10Dds(1, 1, 28, new byte[4]);
        WriteUInt32(dx10Array, 140, 2);
        ExpectUnsupported(() => DdsImageDecoder.Decode(dx10Array), "DX10 texture array");

        var dx10Cube = BuildDx10Dds(1, 1, 28, new byte[4]);
        WriteUInt32(dx10Cube, 136, 0x4);
        ExpectUnsupported(() => DdsImageDecoder.Decode(dx10Cube), "DX10 cube map");
    }

    private static byte[] BuildFourCcDds(int width, int height, string fourCc, byte[] payload)
    {
        var result = CreateBaseHeader(width, height, 0x4, 0, 0, 0, 0, 0, 0, payload.Length);
        WriteUInt32(result, 84, FourCc(fourCc));
        payload.CopyTo(result, 128);
        return result;
    }

    private static byte[] BuildDx10Dds(int width, int height, uint dxgiFormat, byte[] payload)
    {
        var rowPitch = dxgiFormat is 28 or 29 or 87 or 91 or 88 or 93 ? (uint)(width * 4) : 0;
        var result = CreateBaseHeader(width, height, 0x4, 0, 0, 0, 0, 0, rowPitch, payload.Length + 20);
        WriteUInt32(result, 84, FourCc("DX10"));
        Array.Resize(ref result, 148 + payload.Length);
        WriteUInt32(result, 128, dxgiFormat);
        WriteUInt32(result, 132, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteUInt32(result, 140, 1); // array size
        payload.CopyTo(result, 148);
        return result;
    }

    private static byte[] BuildMaskedDds(int width, int height, byte[] pixelFormat, int rowPitch, byte[] payload)
    {
        var result = CreateBaseHeader(width, height, 0x40, 0, 0, 0, 0, 0, (uint)rowPitch, payload.Length);
        pixelFormat.CopyTo(result, 76);
        payload.CopyTo(result, 128);
        return result;
    }

    private static byte[] CreateBaseHeader(int width, int height, uint pixelFlags, uint fourCc, uint bits, uint red, uint green, uint blue, uint rowPitch, int payloadBytes)
    {
        var result = new byte[128 + payloadBytes];
        "DDS "u8.CopyTo(result);
        WriteUInt32(result, 4, 124);
        WriteUInt32(result, 8, 0x1007 | (rowPitch > 0 ? 0x8u : 0)); // CAPS, HEIGHT, WIDTH, optional PITCH, PIXELFORMAT
        WriteUInt32(result, 12, (uint)height);
        WriteUInt32(result, 16, (uint)width);
        WriteUInt32(result, 20, rowPitch);
        WriteUInt32(result, 76, 32);
        WriteUInt32(result, 80, pixelFlags);
        WriteUInt32(result, 84, fourCc);
        WriteUInt32(result, 88, bits);
        WriteUInt32(result, 92, red);
        WriteUInt32(result, 96, green);
        WriteUInt32(result, 100, blue);
        WriteUInt32(result, 108, 0x1000); // DDSCAPS_TEXTURE
        return result;
    }

    private static uint FourCc(string value) =>
        (uint)value[0] | (uint)value[1] << 8 | (uint)value[2] << 16 | (uint)value[3] << 24;

    private static void WriteUInt16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void AssertColor(Color actual, Color expected, string label)
    {
        if (actual.ToArgb() != expected.ToArgb())
            throw new InvalidDataException($"{label}: expected {expected}, got {actual}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void ExpectInvalid(Func<DdsPreview> decode, string label)
    {
        try
        {
            using var decoded = decode();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException($"Expected invalid-data rejection for {label}.");
    }

    private static void ExpectUnsupported(Func<DdsPreview> decode, string label)
    {
        try
        {
            using var decoded = decode();
        }
        catch (NotSupportedException)
        {
            return;
        }
        throw new InvalidDataException($"Expected unsupported-layout rejection for {label}.");
    }
}

internal static class LanguagePersistenceTests
{
    public static void Run()
    {
        TestLanguagePackageSelection();
        TestSelectedLanguageSurvivesConfigRoundTrip();
    }

    private static void TestLanguagePackageSelection()
    {
        var packages = LanguageManager.GetPackages();
        Assert(packages.Count == 4, $"expected four language packages, found {packages.Count}");

        LanguageManager.Initialize("en-US.xml");
        Assert(LanguageManager.CurrentFileName == "en-US.xml", "English selection was not loaded");
        Assert(LanguageManager.Translate("about.title", "fallback") == "About Rho Viewer Portable", "English package translation was not applied");

        LanguageManager.Initialize("../../unexpected.xml");
        Assert(LanguageManager.CurrentFileName == "zh-Hant.xml", "unsupported package names must fall back to the default language");
    }

    private static void TestSelectedLanguageSurvivesConfigRoundTrip()
    {
        var path = AppSettings.ConfigPath;
        var directory = Path.GetDirectoryName(path)!;
        var directoryExisted = Directory.Exists(directory);
        var fileExisted = File.Exists(path);
        var original = fileExisted ? File.ReadAllBytes(path) : null;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.UiLanguage = "ko-KR.xml";
            settings.GameRegion = "cn";
            Assert(settings.TrySave(out var error), $"could not save settings: {error}");

            var loaded = AppSettings.Load();
            Assert(loaded.UiLanguage == "ko-KR.xml", "selected UI language was not persisted in config");
            Assert(loaded.GameRegion == "cn", "saving another setting unexpectedly reset unrelated config data");

            loaded.UiLanguage = "../../outside.xml";
            loaded.Normalize();
            Assert(loaded.UiLanguage == "zh-Hant.xml", "invalid language package name was not normalized safely");
        }
        finally
        {
            if (fileExisted && original is not null)
                File.WriteAllBytes(path, original);
            else if (File.Exists(path))
                File.Delete(path);

            if (!directoryExisted && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal static class ParserInputTests
{
    public static void Run()
    {
        TestFlacBoundaries();
        TestOggBoundaries();
        TestDeterministicMalformedCorpus();
        TestAnalysisSizeLimit();
        TestFallbackAndClassification();
        TestArchivePathNormalization();
    }

    private static void TestFlacBoundaries()
    {
        var valid = BuildMinimalFlac();
        var summary = ResourceAnalyzers.AnalyzeExtension(".flac", valid);
        Assert(summary.Contains("FLAC metadata 分析", StringComparison.Ordinal), "valid FLAC STREAMINFO was not recognized");
        Assert(summary.Contains("MD5：", StringComparison.Ordinal), "valid FLAC STREAMINFO MD5 was not reported");

        AssertMalformedFlac("fLaC"u8.ToArray(), "signature-only FLAC");
        AssertMalformedFlac(BuildFlacBlock(0x80, 0, 0, 33, new byte[33]), "wrong STREAMINFO length");
        AssertMalformedFlac(BuildFlacBlock(0x80, 0xff, 0xff, 0xff, Array.Empty<byte>()), "oversized FLAC metadata declaration");
        AssertMalformedFlac(BuildFlacBlock(0x00, 0, 0, 0, Array.Empty<byte>()), "missing final FLAC metadata block");

        var wrongOrder = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(wrongOrder);
        wrongOrder[4] = 0x80 | 1;
        wrongOrder[7] = 34;
        AssertMalformedFlac(wrongOrder, "STREAMINFO not first");

        var malformedComments = BuildFlacWithCommentBlock(new byte[] { 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0 });
        AssertMalformedFlac(malformedComments, "FLAC Vorbis vendor length overflow");

        var atBlockLimit = ResourceAnalyzers.AnalyzeExtension(".flac", BuildFlacWithPaddingBlocks(ResourceAnalyzers.MaxFlacMetadataBlocks));
        Assert(atBlockLimit.Contains($"metadata blocks：{ResourceAnalyzers.MaxFlacMetadataBlocks}", StringComparison.Ordinal),
            "FLAC parser did not accept the exact metadata-block limit");
        Assert(!atBlockLimit.Contains("區塊順序、長度或數量無效", StringComparison.Ordinal),
            "FLAC parser rejected the exact metadata-block limit");

        var aboveBlockLimit = ResourceAnalyzers.AnalyzeExtension(".flac", BuildFlacWithPaddingBlocks(ResourceAnalyzers.MaxFlacMetadataBlocks + 1));
        Assert(aboveBlockLimit.Contains("區塊順序、長度或數量無效", StringComparison.Ordinal),
            "FLAC parser did not reject metadata blocks above its configured limit");
    }

    private static void AssertMalformedFlac(byte[] bytes, string label)
    {
        var summary = ResourceAnalyzers.AnalyzeExtension(".flac", bytes);
        Assert(summary.Contains("無效", StringComparison.Ordinal) ||
               summary.Contains("不是標準", StringComparison.Ordinal) ||
               summary.Contains("找不到有效 STREAMINFO", StringComparison.Ordinal),
            $"{label} did not produce a bounded malformed-input result");
    }

    private static void TestOggBoundaries()
    {
        var invalid = ResourceAnalyzers.AnalyzeExtension(".ogg", "bad"u8.ToArray());
        Assert(invalid.Contains("不是標準 OggS", StringComparison.Ordinal), "invalid OGG signature was not rejected");

        var truncatedTable = new byte[28];
        "OggS"u8.CopyTo(truncatedTable);
        truncatedTable[26] = 2;
        AssertMalformedOgg(truncatedTable, "truncated OGG segment table");

        var truncatedPayload = new byte[28];
        "OggS"u8.CopyTo(truncatedPayload);
        truncatedPayload[26] = 1;
        truncatedPayload[27] = 255;
        AssertMalformedOgg(truncatedPayload, "truncated OGG payload");

        var invalidVersion = BuildOggPage(Array.Empty<byte>());
        invalidVersion[4] = 1;
        AssertMalformedOgg(invalidVersion, "unsupported OGG page version");

        var trailingBytes = BuildOggPage(Array.Empty<byte>()).Concat(new byte[] { 0x55 }).ToArray();
        AssertMalformedOgg(trailingBytes, "trailing bytes after OGG page");

        var comments = BuildVorbisComments(0, 10_001);
        var commentsSummary = ResourceAnalyzers.AnalyzeExtension(".ogg", BuildOggPage(BuildVorbisIdentification(), comments));
        Assert(commentsSummary.Contains("數量或長度欄位無效", StringComparison.Ordinal), "OGG excessive comment count was not flagged");

        var oversizedVendor = BuildVorbisComments(uint.MaxValue, 0);
        _ = ResourceAnalyzers.AnalyzeExtension(".ogg", BuildOggPage(BuildVorbisIdentification(), oversizedVendor));

        var atPacketLimit = ResourceAnalyzers.AnalyzeExtension(".ogg", BuildOggPagesWithPacketLength(ResourceAnalyzers.MaxOggHeaderPacketBytes));
        Assert(!atPacketLimit.Contains("超過標頭分析上限", StringComparison.Ordinal),
            "OGG parser rejected a packet exactly at its configured limit");

        var abovePacketLimit = ResourceAnalyzers.AnalyzeExtension(".ogg", BuildOggPagesWithPacketLength(ResourceAnalyzers.MaxOggHeaderPacketBytes + 1));
        Assert(abovePacketLimit.Contains("超過標頭分析上限", StringComparison.Ordinal),
            "OGG parser did not reject a packet above its configured limit");
    }

    private static void AssertMalformedOgg(byte[] bytes, string label)
    {
        var summary = ResourceAnalyzers.AnalyzeExtension(".ogg", bytes);
        Assert(summary.Contains("截斷或無效", StringComparison.Ordinal) || summary.Contains("不是標準", StringComparison.Ordinal),
            $"{label} did not produce a bounded malformed-input result");
    }

    private static void TestDeterministicMalformedCorpus()
    {
        var random = new Random(0x5EED);
        for (var index = 0; index < 2_000; index++)
        {
            var bytes = new byte[random.Next(0, 2_048)];
            random.NextBytes(bytes);
            if (bytes.Length >= 4)
            {
                if ((index & 1) == 0)
                    "OggS"u8.CopyTo(bytes);
                else
                    "fLaC"u8.CopyTo(bytes);
            }

            var extension = (index & 1) == 0 ? ".ogg" : ".flac";
            var result = ResourceAnalyzers.AnalyzeExtension(extension, bytes);
            Assert(result is not null, $"{extension} parser returned a null result for deterministic mutation {index}");
        }
    }

    private static void TestAnalysisSizeLimit()
    {
        var oversized = new byte[ResourceAnalyzers.MaxAnalysisBytes + 1];
        var result = ResourceAnalyzers.AnalyzeExtension(".ogg", oversized);
        Assert(result.Contains("資源過大", StringComparison.Ordinal), "OGG analyzer did not enforce its input cap");
    }

    private static void TestFallbackAndClassification()
    {
        Assert(ResourceAnalyzers.AnalyzeExtension(".ksv", "not-a-ksv"u8.ToArray()).Contains("KSV", StringComparison.Ordinal), "KSV fallback changed");
        var oneS = ResourceAnalyzers.AnalyzeExtension(".1s", BuildMinimalOneS());
        Assert(oneS.Contains("FILE_HEADER：1 個", StringComparison.Ordinal) &&
               oneS.Contains("候選宣告模組數：2", StringComparison.Ordinal) &&
               oneS.Contains("VERTEX_COORDINATES_HEADER：1 個", StringComparison.Ordinal), ".1s marker probe regression");
        Assert(ResourceAnalyzers.AnalyzeExtension(".1s", Array.Empty<byte>()).Contains("不能僅憑此結果判定檔案無效", StringComparison.Ordinal), ".1s unknown variant fallback regression");
        Assert(ResourceClassifier.Classify(".ksv") == ResourceKind.Recording &&
               ResourceClassifier.Classify(".1s") == ResourceKind.Model &&
               ResourceClassifier.Classify(".ogg") == ResourceKind.Audio, "resource categories changed");
    }

    private static void TestArchivePathNormalization()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "ArchivePathSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rho5First = Path.Combine(directory, "DataPack7_00000.rho5");
            var rho5Second = Path.Combine(directory, "DataPack7_00001.rho5");
            var lpkFirst = Path.Combine(directory, "DataPack8_00000.lpk");
            var lpkSecond = Path.Combine(directory, "DataPack8_00001.lpk");
            File.WriteAllBytes(rho5First, Array.Empty<byte>());
            File.WriteAllBytes(rho5Second, Array.Empty<byte>());
            File.WriteAllBytes(lpkFirst, Array.Empty<byte>());
            File.WriteAllBytes(lpkSecond, Array.Empty<byte>());

            Assert(string.Equals(RhoReader.GetCanonicalPath(rho5Second), rho5First, StringComparison.OrdinalIgnoreCase), "Rho5 parts were not grouped");
            Assert(string.Equals(RhoReader.GetCanonicalPath(lpkSecond), lpkSecond, StringComparison.OrdinalIgnoreCase), "independent LPK packages were grouped");
            ExpectInvalid(() => RhoReader.Open(lpkFirst), "empty LPK package");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] BuildMinimalFlac()
    {
        var result = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(result);
        result[4] = 0x80;
        result[7] = 34;
        var streamInfo = result.AsSpan(8, 34);
        streamInfo[0] = 0x10;
        streamInfo[1] = 0x00;
        streamInfo[2] = 0x10;
        streamInfo[3] = 0x00;
        streamInfo[10] = 0x0A;
        streamInfo[11] = 0xC4;
        streamInfo[12] = 0x42;
        streamInfo[13] = 0xF0;
        streamInfo[17] = 0x01;
        for (var i = 18; i < 34; i++)
            streamInfo[i] = (byte)i;
        return result;
    }

    private static byte[] BuildFlacBlock(byte firstHeader, byte lengthHigh, byte lengthMiddle, byte lengthLow, byte[] payload)
    {
        var result = new byte[4 + 4 + payload.Length];
        "fLaC"u8.CopyTo(result);
        result[4] = firstHeader;
        result[5] = lengthHigh;
        result[6] = lengthMiddle;
        result[7] = lengthLow;
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] BuildFlacWithCommentBlock(byte[] commentBlock)
    {
        var streamInfo = BuildMinimalFlac();
        var result = new byte[streamInfo.Length + 4 + commentBlock.Length];
        streamInfo.AsSpan(0, streamInfo.Length).CopyTo(result);
        var offset = streamInfo.Length;
        result[offset] = 0x80 | 4;
        result[offset + 1] = (byte)(commentBlock.Length >> 16);
        result[offset + 2] = (byte)(commentBlock.Length >> 8);
        result[offset + 3] = (byte)commentBlock.Length;
        commentBlock.CopyTo(result, offset + 4);
        result[4] &= 0x7F;
        return result;
    }

    private static byte[] BuildFlacWithPaddingBlocks(int blockCount)
    {
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount));

        var streamInfo = BuildMinimalFlac();
        var result = new byte[streamInfo.Length + (blockCount - 1) * 4];
        streamInfo.CopyTo(result, 0);
        result[4] = blockCount == 1 ? (byte)0x80 : (byte)0;
        var offset = streamInfo.Length;
        for (var index = 1; index < blockCount; index++)
        {
            result[offset] = (byte)((index == blockCount - 1 ? 0x80 : 0) | 1); // zero-length padding block
            offset += 4;
        }
        return result;
    }

    private static byte[] BuildMinimalOneS()
    {
        var result = new byte[40];
        new byte[] { 0xAA, 0x47, 0x49, 0x02, 0x8C, 0x07 }.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), 2);
        new byte[] { 0xAA, 0x47, 0x3C, 0x03, 0x07, 0x0E }.CopyTo(result, 16);
        new byte[] { 0xAA, 0x47, 0x46, 0x04, 0x2A, 0x19 }.CopyTo(result, 22);
        new byte[] { 0xAA, 0x27 }.CopyTo(result, 32);
        return result;
    }

    private static byte[] BuildOggPage(params byte[][] packets)
    {
        var lacing = new List<byte>();
        var payload = new List<byte>();
        foreach (var packet in packets)
        {
            var offset = 0;
            while (packet.Length - offset >= 255)
            {
                lacing.Add(255);
                payload.AddRange(packet.AsSpan(offset, 255).ToArray());
                offset += 255;
            }
            lacing.Add((byte)(packet.Length - offset));
            if (offset < packet.Length)
                payload.AddRange(packet.AsSpan(offset).ToArray());
        }
        if (lacing.Count > byte.MaxValue)
            throw new InvalidOperationException("Synthetic OGG test page exceeds the segment-table limit.");

        var result = new byte[27 + lacing.Count + payload.Count];
        "OggS"u8.CopyTo(result);
        result[4] = 0;
        result[5] = 2;
        result[26] = (byte)lacing.Count;
        lacing.CopyTo(result, 27);
        payload.CopyTo(result, 27 + lacing.Count);
        return result;
    }

    private static byte[] BuildOggPagesWithPacketLength(int packetLength)
    {
        if (packetLength < 0)
            throw new ArgumentOutOfRangeException(nameof(packetLength));

        var fullSegments = packetLength / byte.MaxValue;
        var finalSegmentLength = packetLength % byte.MaxValue;
        var lacing = new byte[fullSegments + 1];
        Array.Fill(lacing, byte.MaxValue);
        lacing[^1] = (byte)finalSegmentLength; // a zero-length lace terminates exact multiples of 255

        using var output = new MemoryStream();
        var lacingOffset = 0;
        var pageNumber = 0;
        while (lacingOffset < lacing.Length)
        {
            var segmentCount = Math.Min(byte.MaxValue, lacing.Length - lacingOffset);
            var payloadLength = 0;
            for (var index = 0; index < segmentCount; index++)
                payloadLength += lacing[lacingOffset + index];

            var page = new byte[27 + segmentCount + payloadLength];
            "OggS"u8.CopyTo(page);
            page[5] = (byte)(pageNumber == 0 ? 0x02 : 0x01); // BOS on first page, continuation thereafter
            if (lacingOffset + segmentCount == lacing.Length)
                page[5] |= 0x04; // EOS
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), (uint)pageNumber);
            page[26] = (byte)segmentCount;
            lacing.AsSpan(lacingOffset, segmentCount).CopyTo(page.AsSpan(27));
            output.Write(page);

            lacingOffset += segmentCount;
            pageNumber++;
        }

        return output.ToArray();
    }

    private static byte[] BuildVorbisIdentification()
    {
        var packet = new byte[30];
        packet[0] = 1;
        "vorbis"u8.CopyTo(packet.AsSpan(1));
        packet[8] = 0;
        packet[11] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), 48_000);
        packet[28] = 0xB8;
        packet[29] = 1;
        return packet;
    }

    private static byte[] BuildVorbisComments(uint vendorLength, uint commentCount)
    {
        var packet = new byte[7 + 4 + 4];
        "vorbis"u8.CopyTo(packet);
        packet[6] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), vendorLength);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11, 4), commentCount);
        return packet;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException($"Expected invalid-data rejection for {label}.");
    }
}

internal static class BmlAnalyzerTests
{
    public static void Run()
    {
        TestSyntheticTreeAndEscaping();
        TestMalformedInputs();
        TestDepthAndSizeLimits();
        TestPreviewLimit();
        TestDeterministicRandomInputs();
    }

    private static void TestSyntheticTreeAndEscaping()
    {
        var bytes = BuildDocument(new BmlNode("CaptionWindow", "", [new("title", "R&D <Race>")],
        [
            new("Label", "繁體中文 & <text>", [], []),
            new("Panel", "", [], [new("TextButton", "Start", [new("id", "go")], [])])
        ]));

        var result = ResourceAnalyzers.AnalyzeExtension(".bml", bytes);
        Assert(result.Contains("BML 二進位 XML-like 結構解析成功", StringComparison.Ordinal), "valid BML tree was not recognized");
        Assert(result.Contains("根節點：CaptionWindow", StringComparison.Ordinal), "BML root tag was not reported");
        Assert(result.Contains("節點：4", StringComparison.Ordinal), "BML node count was incorrect");
        Assert(result.Contains("R&amp;D &lt;Race&gt;", StringComparison.Ordinal), "BML attribute was not escaped in generated XML");
        Assert(result.Contains("繁體中文 &amp; &lt;text&gt;", StringComparison.Ordinal), "BML text was not escaped in generated XML");
        Assert(ResourceClassifier.Classify(".bml") == ResourceKind.StructuredData, "BML classification changed");
    }

    private static void TestMalformedInputs()
    {
        AssertMalformed(Array.Empty<byte>(), "empty BML");
        AssertMalformed(new byte[3], "truncated BML string length");

        var negativeStringLength = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(negativeStringLength, -1);
        AssertMalformed(negativeStringLength, "negative BML string length");

        var truncatedString = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(truncatedString, 2);
        AssertMalformed(truncatedString, "truncated BML UTF-16 string");

        var invalidUtf16 = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(invalidUtf16, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(invalidUtf16.AsSpan(4), 0xD800);
        Assert(ResourceAnalyzers.AnalyzeExtension(".bml", invalidUtf16).Contains("不是有效的 UTF-16LE", StringComparison.Ordinal),
            "invalid UTF-16 surrogate was not rejected");

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            WriteBmlString(writer, "root");
            WriteBmlString(writer, "");
            writer.Write(-1);
        }
        AssertMalformed(stream.ToArray(), "negative BML attribute count");

        var trailing = BuildDocument(new BmlNode("root", "", [], [] )).Concat(new byte[] { 0x7f }).ToArray();
        Assert(ResourceAnalyzers.AnalyzeExtension(".bml", trailing).Contains("尾隨資料", StringComparison.Ordinal),
            "trailing BML bytes were not rejected");
    }

    private static void TestDepthAndSizeLimits()
    {
        var nested = new BmlNode("n", "", [], []);
        for (var depth = 1; depth < BmlAnalyzer.MaxDepth + 1; depth++)
            nested = new BmlNode("n", "", [], [nested]);
        Assert(ResourceAnalyzers.AnalyzeExtension(".bml", BuildDocument(nested)).Contains("深度超過上限", StringComparison.Ordinal),
            "BML maximum nesting depth was not enforced");

        var oversized = new byte[BmlAnalyzer.MaxBmlBytes + 1];
        Assert(ResourceAnalyzers.AnalyzeExtension(".bml", oversized).Contains("資源過大", StringComparison.Ordinal),
            "BML analyzer did not enforce its input cap");
    }

    private static void TestPreviewLimit()
    {
        var longText = new string('x', BmlAnalyzer.MaxPreviewChars * 2);
        var result = ResourceAnalyzers.AnalyzeExtension(".bml", BuildDocument(new BmlNode("root", longText, [], [])));
        Assert(result.Contains("XML 預覽已截斷", StringComparison.Ordinal), "oversized BML preview text was not truncated");
        Assert(result.Length < BmlAnalyzer.MaxPreviewChars + 1_000, "BML preview output exceeded its bounded size");
    }

    private static void TestDeterministicRandomInputs()
    {
        var random = new Random(0x011A_2026);
        for (var index = 0; index < 300; index++)
        {
            var bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);
            var result = ResourceAnalyzers.AnalyzeExtension(".bml", bytes);
            Assert(!string.IsNullOrWhiteSpace(result), $"BML parser returned no result for deterministic mutation {index}");
        }
    }

    private static void AssertMalformed(byte[] bytes, string label)
    {
        var result = ResourceAnalyzers.AnalyzeExtension(".bml", bytes);
        Assert(result.Contains("無法解析", StringComparison.Ordinal) || result.Contains("不是有效的 UTF-16LE", StringComparison.Ordinal),
            $"{label} did not produce a bounded parse failure");
    }

    private static byte[] BuildDocument(BmlNode root)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
            WriteNode(writer, root);
        return stream.ToArray();
    }

    private static void WriteNode(BinaryWriter writer, BmlNode node)
    {
        WriteBmlString(writer, node.Name);
        WriteBmlString(writer, node.Text);
        writer.Write(node.Attributes.Length);
        foreach (var attribute in node.Attributes)
        {
            WriteBmlString(writer, attribute.Name);
            WriteBmlString(writer, attribute.Value);
        }
        writer.Write(node.Children.Length);
        foreach (var child in node.Children)
            WriteNode(writer, child);
    }

    private static void WriteBmlString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private sealed record BmlNode(string Name, string Text, BmlAttribute[] Attributes, BmlNode[] Children);
    private sealed record BmlAttribute(string Name, string Value);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

internal static class UpdaterSecurityTests
{
    public static void Run()
    {
        TestBrotliDecompressionDisabled();
        TestPatchEndpointConfinement();
        TestManifestValidation();
        TestPatchPathValidation();
        TestRollback();
    }

    private static void TestBrotliDecompressionDisabled()
    {
        using var handler = KartPatchService.CreateHttpClientHandler();
        if (handler.AutomaticDecompression != System.Net.DecompressionMethods.None)
            throw new InvalidDataException("HttpClient automatic content decompression must remain disabled.");
    }

    private static void TestPatchEndpointConfinement()
    {
        const string discoveredEndpoint = "https://patch-server.example.test/game/";
        _ = KartPatchService.ValidatePatchUri("https://patch-server.example.test/game/files.nfo2", discoveredEndpoint);
        _ = KartPatchService.ValidatePatchUri("https://patch-server.example.test:9443/game/files.nfo2", "https://patch-server.example.test:9443/game/");
        ExpectInvalid(() => KartPatchService.ValidatePatchUri("https://another.example.test/game/files.nfo2", discoveredEndpoint), "cross-origin patch URL");
        ExpectInvalid(() => KartPatchService.ValidatePatchUri("https://user:pass@patch-server.example.test/game/file", discoveredEndpoint), "URL credentials");
        ExpectInvalid(() => KartPatchService.ValidatePatchUri("http://patch-server.example.test/game/file", discoveredEndpoint), "scheme-changing patch URL");
    }

    private static void TestManifestValidation()
    {
        const string header = "NFO200\r\n";
        const string fields = ",unused,123,3,4,5,6,7,8,9,10,11";
        var files = KartPatchService.ParseManifest(header + "\"Data,One.bin\"" + fields + "\r\n");
        if (files.Count != 1 || files[0].RelativePath != "Data,One.bin" || files[0].Crc != 123 || files[0].Size != 3 || files[0].SizeGzipped != 4)
            throw new InvalidDataException("NFO200 quoted CSV parsing test failed.");
        ExpectInvalid(() => KartPatchService.ParseManifest(header + "\"unterminated" + fields + "\r\n"), "unclosed manifest quote");
        ExpectInvalid(() => KartPatchService.ParseManifest(header + "Data.bin,unused,123,-1,4,5,6,7,8,9,10,11\r\n"), "negative manifest size");
        ExpectInvalid(() => KartPatchService.ParseManifest(header + "Data.bin,unused,123,3,4,5,6,7,8,9,10,11\r\ndata.bin,unused,123,3,4,5,6,7,8,9,10,11\r\n"), "duplicate manifest paths");
        ExpectInvalid(() => KartPatchService.ParseManifest(header + "short,row\r\n"), "short manifest row");
    }

    private static void TestPatchPathValidation()
    {
        if (KartPatchService.NormalizeRelativePath("Data\\safe.bin") != "Data/safe.bin")
            throw new InvalidDataException("Valid relative update path normalization failed.");
        ExpectInvalid(() => KartPatchService.NormalizeRelativePath("../outside.bin"), "parent traversal");
        ExpectInvalid(() => KartPatchService.NormalizeRelativePath("C:\\outside.bin"), "absolute drive path");
        ExpectInvalid(() => KartPatchService.NormalizeRelativePath("/outside.bin"), "rooted path");
        ExpectInvalid(() => KartPatchService.NormalizeRelativePath("Data/NUL.txt"), "reserved Windows device name");
        ExpectInvalid(() => KartPatchService.NormalizeRelativePath("Data/file."), "trailing dot");

        var root = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "PathSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var safe = KartPatchService.GetSafeChildPath(root, "Data/safe.bin");
            if (!safe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Safe child path escaped its root.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "RollbackSelfTest", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "Data", "patched.bin");
        var backup = Path.Combine(root, "backup", "Data", "patched.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        try
        {
            File.WriteAllText(target, "new");
            File.WriteAllText(backup, "old");
            KartPatchService.Rollback(new[] { (target, backup, true) });
            if (!File.Exists(target) || File.ReadAllText(target) != "old" || File.Exists(backup))
                throw new InvalidDataException("Update rollback did not restore the original file.");

            var newFile = Path.Combine(root, "Data", "new-only.bin");
            File.WriteAllText(newFile, "new");
            KartPatchService.Rollback(new[] { (newFile, Path.Combine(root, "unused.bak"), false) });
            if (File.Exists(newFile))
                throw new InvalidDataException("Update rollback did not remove a newly created file.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException($"Expected invalid-data rejection for {label}.");
    }
}

internal static class GeneralTests
{
    public static void Run()
    {
        TestKartCrc();
        TestUiMutationScopes();
    }

    private static void TestKartCrc()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "KartCrcSelfTest", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sample.bin");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, "abc"u8.ToArray());
            var result = KartCrc.ComputeAsync(path).GetAwaiter().GetResult();
            const uint expected = 0x024A0126;
            if (result != expected)
                throw new InvalidDataException($"Kart CRC mismatch: expected 0x{expected:X8}, got 0x{result:X8}");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestUiMutationScopes()
    {
        var state = new UiMutationTracker();
        if (state.IsActive(UiMutationKind.FilterControls))
            throw new InvalidDataException("UI mutation state starts active.");

        using (state.Enter(UiMutationKind.FilterControls))
        {
            if (!state.IsActive(UiMutationKind.FilterControls))
                throw new InvalidDataException("UI mutation state was not entered.");
            using (state.Enter(UiMutationKind.FilterControls))
            {
                if (!state.IsActive(UiMutationKind.FilterControls))
                    throw new InvalidDataException("Nested UI mutation state was not entered.");
            }
            if (!state.IsActive(UiMutationKind.FilterControls))
                throw new InvalidDataException("Nested UI scope cleared the outer scope.");
        }
        if (state.IsActive(UiMutationKind.FilterControls))
            throw new InvalidDataException("UI mutation state was not restored after disposal.");

        try
        {
            using var ignored = state.Enter(UiMutationKind.ResourceTreeRebuild);
            throw new InvalidOperationException("expected scope unwind");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected scope unwind")
        {
        }
        if (state.IsActive(UiMutationKind.ResourceTreeRebuild))
            throw new InvalidDataException("UI mutation state was not restored after exception unwind.");
    }
}

internal static class VirtualGridTests
{
    public static void Run()
    {
        using var grid = new BufferedDataGridView
        {
            VirtualMode = true,
            AllowUserToAddRows = false,
            ReadOnly = true
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path" });
        grid.CreateControl();
        _ = grid.Handle;

        grid.BeginUpdate();
        try
        {
            grid.RowCount = 275_507;
        }
        finally
        {
            grid.EndUpdate();
        }

        Assert(grid.RowCount == 275_507, "virtual grid should expose the full resource count without adding row objects");
        grid.RowCount = 0;
        Assert(grid.RowCount == 0, "virtual grid should clear rows by setting RowCount to zero");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal static class PreviewByteCacheTests
{
    public static void Run()
    {
        TestByteCapacityAndLeastRecentlyUsedEviction();
        TestEntryLimitAndOversizedValues();
        TestReplacementAndClear();
    }

    private static void TestByteCapacityAndLeastRecentlyUsedEviction()
    {
        var cache = new BoundedByteCache<object>(capacityBytes: 10, maximumEntries: 8);
        var first = new object();
        var second = new object();
        var third = new object();
        cache.Add(first, new byte[4]);
        cache.Add(second, new byte[4]);

        Assert(cache.TryGetValue(first, out var firstValue) && firstValue.Length == 4,
            "cache should return a previously added byte array");
        cache.Add(third, new byte[5]);

        Assert(cache.TryGetValue(first, out _), "a recent cache hit should protect the entry from eviction");
        Assert(!cache.TryGetValue(second, out _), "the least-recently-used entry should be evicted first");
        Assert(cache.TryGetValue(third, out _), "the newly added entry should remain cached");
        Assert(cache.CachedBytes == 9, "cache byte accounting should match retained arrays");

        static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidDataException(message);
        }
    }

    private static void TestEntryLimitAndOversizedValues()
    {
        var cache = new BoundedByteCache<object>(capacityBytes: 100, maximumEntries: 2);
        var first = new object();
        var second = new object();
        var third = new object();
        cache.Add(first, new byte[] { 1 });
        cache.Add(second, new byte[] { 2 });
        cache.Add(third, new byte[] { 3 });

        Assert(cache.Count == 2, "cache should enforce its entry-count limit");
        Assert(!cache.TryGetValue(first, out _), "entry-count pressure should evict the oldest entry");
        Assert(cache.TryGetValue(second, out _) && cache.TryGetValue(third, out _),
            "the two most recent entries should remain cached");

        var oversized = new object();
        cache.Add(oversized, new byte[101]);
        Assert(!cache.TryGetValue(oversized, out _), "an item larger than the byte budget should not be cached");
        Assert(cache.Count == 2 && cache.CachedBytes == 2, "oversized values should not alter cache accounting");

        static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidDataException(message);
        }
    }

    private static void TestReplacementAndClear()
    {
        var cache = new BoundedByteCache<string>(capacityBytes: 16, maximumEntries: 4);
        cache.Add("key", new byte[3]);
        cache.Add("key", new byte[5]);
        Assert(cache.Count == 1 && cache.CachedBytes == 5, "replacing a key should update count and byte accounting");
        Assert(cache.TryGetValue("key", out var value) && value.Length == 5, "replacement value should be returned");

        cache.Clear();
        Assert(cache.Count == 0 && cache.CachedBytes == 0 && !cache.TryGetValue("key", out _),
            "clear should release all cached entries and bytes");

        static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidDataException(message);
        }
    }
}

internal static class BmlCorpusTest
{
    public static int Run(string[] paths)
    {
        if (paths.Length == 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.Tests --bml-corpus path [path ...]");
            return 2;
        }

        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                if (string.Equals(Path.GetExtension(path), ".bml", StringComparison.OrdinalIgnoreCase))
                    files.Add(Path.GetFullPath(path));
                else
                    Console.Error.WriteLine($"SKIP non-BML file: {path}");
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                             .Where(file => string.Equals(Path.GetExtension(file), ".bml", StringComparison.OrdinalIgnoreCase)))
                    files.Add(Path.GetFullPath(file));
            }
            else
            {
                Console.Error.WriteLine($"FAIL missing BML corpus path: {path}");
                return 2;
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("FAIL no .bml files found");
            return 2;
        }

        var failures = 0;
        long totalBytes = 0;
        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                totalBytes += bytes.Length;
                var summary = ResourceAnalyzers.AnalyzeExtension(".bml", bytes);
                if (summary.StartsWith("BML 二進位 XML-like 結構解析成功", StringComparison.Ordinal))
                    Console.WriteLine($"PASS {file} ({bytes.Length:N0} bytes)");
                else
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {file}: {summary.Split(Environment.NewLine)[0]}");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {file}: {ex.Message}");
            }
        }

        Console.WriteLine($"BML corpus: files={files.Count:N0}, bytes={totalBytes:N0}, failures={failures:N0}");
        return failures == 0 ? 0 : 1;
    }
}

internal static class ArchiveCorpusTest
{
    private static readonly string[] SupportedExtensions = [".rho", ".rho5", ".lpk"];

    public static int Run(string[] roots)
    {
        var includeSha256 = roots.Length > 0 && string.Equals(roots[0], "--sha256", StringComparison.OrdinalIgnoreCase);
        if (includeSha256)
            roots = roots.Skip(1).ToArray();

        if (roots.Length == 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.Tests --archive-corpus [--sha256] dataRoot [dataRoot ...]");
            return 2;
        }

        var totals = SupportedExtensions.ToDictionary(extension => extension, _ => new CorpusTotals(), StringComparer.OrdinalIgnoreCase);
        var failedArchives = 0;
        var failedEntries = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"FAIL missing archive root: {root}");
                failedArchives++;
                continue;
            }

            string[] paths;
            try
            {
                var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                paths = candidates.Select(path => Path.GetExtension(path).Equals(".rho5", StringComparison.OrdinalIgnoreCase)
                        ? RhoReader.GetCanonicalPath(path)
                        : Path.GetFullPath(path))
                    .Where(unique.Add)
                    .ToArray();
                Console.WriteLine($"DATA {Path.GetFullPath(root)}: {candidates.Length:N0} physical archive files; {paths.Length:N0} archives/groups to open.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL enumerate {root}: {ex}");
                failedArchives++;
                continue;
            }

            var rootCounts = SupportedExtensions.ToDictionary(extension => extension, _ => new CorpusTotals(), StringComparer.OrdinalIgnoreCase);
            using var corpusHash = includeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var processed = 0;
            foreach (var path in paths)
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var bucket = rootCounts[extension];
                RhoArchiveContext? context = null;
                try
                {
                    context = RhoReader.Open(path, "auto");
                    bucket.Archives++;
                    totals[extension].Archives++;
                    if (corpusHash is not null)
                    {
                        var relativeArchivePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                        AppendHashRecord(corpusHash, 0xA1, Encoding.UTF8.GetBytes(relativeArchivePath));
                    }
                    foreach (var item in context.Items)
                    {
                        try
                        {
                            var bytes = item.ReadBytes();
                            if (bytes.Length != item.Size)
                                throw new InvalidDataException($"declared {item.Size} bytes, decoded {bytes.Length} bytes");
                            if (corpusHash is not null)
                            {
                                AppendHashRecord(corpusHash, 0xE1, Encoding.UTF8.GetBytes(item.RelativePath.Replace('\\', '/')));
                                AppendHashRecord(corpusHash, 0xE2, bytes);
                            }
                            bucket.Entries++;
                            totals[extension].Entries++;
                            bucket.DecodedBytes += bytes.LongLength;
                            totals[extension].DecodedBytes += bytes.LongLength;
                        }
                        catch (Exception ex)
                        {
                            bucket.EntryFailures++;
                            totals[extension].EntryFailures++;
                            failedEntries++;
                            if (failedEntries <= 100)
                                Console.Error.WriteLine($"FAIL ENTRY {path} :: {item.RelativePath} :: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    bucket.ArchiveFailures++;
                    totals[extension].ArchiveFailures++;
                    failedArchives++;
                    if (failedArchives <= 100)
                        Console.Error.WriteLine($"FAIL ARCHIVE {path} :: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    context?.Dispose();
                }

                processed++;
                if (processed % 100 == 0)
                {
                    Console.WriteLine($"Progress {Path.GetFileName(root)}: {processed:N0}/{paths.Length:N0} archives; {bucket.Entries:N0} {extension} resources decoded.");
                    GC.Collect();
                }
            }

            Console.WriteLine($"RESULT {root}: {FormatTotals(rootCounts)}");
            if (corpusHash is not null)
            {
                var label = rootCounts.Values.Any(value => value.ArchiveFailures > 0 || value.EntryFailures > 0)
                    ? "PARTIAL_SHA256"
                    : "SHA256";
                Console.WriteLine($"{label} decoded corpus {Path.GetFullPath(root)}: {Convert.ToHexString(corpusHash.GetHashAndReset()).ToLowerInvariant()}");
            }
        }

        Console.WriteLine($"TOTAL {FormatTotals(totals)}; archive failures={failedArchives:N0}; entry failures={failedEntries:N0}");
        if (totals.Values.Any(total => total.Archives == 0))
        {
            Console.Error.WriteLine("FAIL at least one format (.rho, .rho5, .lpk) had no real archive in the supplied corpus.");
            failedArchives++;
        }
        Console.WriteLine(failedArchives == 0 && failedEntries == 0
            ? "REAL DATA READ/DECODE VALIDATION PASSED"
            : "REAL DATA READ/DECODE VALIDATION FAILED");
        return failedArchives == 0 && failedEntries == 0 ? 0 : 1;
    }

    private static void AppendHashRecord(IncrementalHash hash, byte kind, ReadOnlySpan<byte> value)
    {
        Span<byte> recordHeader = stackalloc byte[9];
        recordHeader[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(recordHeader[1..], (ulong)value.Length);
        hash.AppendData(recordHeader);
        hash.AppendData(value);
    }

    private static string FormatTotals(IReadOnlyDictionary<string, CorpusTotals> totals)
        => string.Join("; ", SupportedExtensions.Select(extension =>
        {
            var value = totals[extension];
            return $"{extension}: archives={value.Archives:N0}, entries={value.Entries:N0}, bytes={value.DecodedBytes:N0}, archiveFailures={value.ArchiveFailures:N0}, entryFailures={value.EntryFailures:N0}";
        }));

    private sealed class CorpusTotals
    {
        public int Archives { get; set; }
        public int ArchiveFailures { get; set; }
        public long Entries { get; set; }
        public int EntryFailures { get; set; }
        public long DecodedBytes { get; set; }
    }
}

internal static class Assertions
{
    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
