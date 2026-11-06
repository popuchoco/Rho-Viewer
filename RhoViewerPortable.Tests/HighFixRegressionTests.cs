using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using KartLibrary.Consts;
using KartLibrary.Data;
using KartLibrary.File;
using KartLibrary.IO;
using KartLibrary.Record;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class HighFixRegressionTests
{
    public static void Run()
    {
        TestArchiveReadsSurviveGarbageCollection();
        TestLpkInflationIsBounded();
        TestRhoPartialEncryptedSecondBlockIsBounded();
        TestArchiveHeaderCountsAndTextLengthsAreBounded();
        TestKsvInnerLengthsAreBounded();
        TestUpdaterTrustBoundaries();
    }

    private static void TestArchiveReadsSurviveGarbageCollection()
    {
        var root = CreateTemporaryDirectory("H1ArchiveHandles");
        try
        {
            var payload = Encoding.UTF8.GetBytes("archive handle remains open after finalization");
            var rhoPath = Path.Combine(root, "synthetic.rho");
            using (var writer = new RhoArchive())
            {
                var file = new RhoFile
                {
                    Name = "sample.bin",
                    DataSource = new ByteArrayDataSource(payload),
                    FileEncryptionProperty = RhoFileProperty.None
                };
                writer.RootFolder.AddFile(file);
                writer.SaveTo(rhoPath);
                writer.getRhoStream()?.Dispose();
            }

            using (var archive = new RhoArchive())
            {
                archive.Open(rhoPath);
                var file = archive.RootFolder.GetFile("sample.bin") ?? throw new InvalidDataException("Synthetic Rho file was not found.");
                AssertBytes(payload, file.GetBytes(), "first Rho read");
                CollectFinalizers();
                AssertBytes(payload, file.GetBytes(), "Rho read after finalizers");
            }

            using (var writer = new Rho5Archive())
            {
                var file = new Rho5File
                {
                    Name = "sample.bin",
                    DataSource = new ByteArrayDataSource(payload)
                };
                writer.RootFolder.AddFile(file);
                writer.Save(root, "synthetic", CountryCode.TW, SavePattern.AlwaysRegeneration);
            }

            using var rho5Archive = new Rho5Archive();
            rho5Archive.Open(root, "synthetic", CountryCode.TW);
            var rho5File = rho5Archive.RootFolder.GetFile("sample.bin") ?? throw new InvalidDataException("Synthetic Rho5 file was not found.");
            AssertBytes(payload, rho5File.GetBytes(), "first Rho5 read");
            CollectFinalizers();
            AssertBytes(payload, rho5File.GetBytes(), "Rho5 read after finalizers");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestLpkInflationIsBounded()
    {
        var exact = Encoding.UTF8.GetBytes("bounded lpk output");
        AssertBytes(exact, LpkArchive.InflateExactly(CompressZlib(exact), exact.Length, "exact.bin"), "exact-size LPK zlib stream");

        var expanded = CompressZlib(new byte[1024 * 1024]);
        ExpectInvalidData(() => LpkArchive.InflateExactly(expanded, 1, "oversized.bin"), "LPK output beyond declared size");
        ExpectInvalidData(() => LpkArchive.InflateExactly(CompressZlib(new byte[] { 1 }), 2, "short.bin"), "LPK output shorter than declared size");
        ExpectInvalidData(() => LpkArchive.InflateExactly(Array.Empty<byte>(), int.MaxValue, "huge.bin"), "oversized LPK declared output");
    }

    private static void TestRhoPartialEncryptedSecondBlockIsBounded()
    {
        Assert(RhoArchive.ValidatePartialEncryptedLength(17, 9) == 26, "Rho partial-encrypted combined size calculation changed.");
        ExpectInvalidData(() => RhoArchive.ValidatePartialEncryptedLength(8, -1), "negative Rho secondary block size");
        ExpectInvalidData(() => RhoArchive.ValidatePartialEncryptedLength(8, 256 * 1024 * 1024 + 1), "oversized Rho secondary block size");
        ExpectInvalidData(() => RhoArchive.ValidatePartialEncryptedLength(200 * 1024 * 1024, 60 * 1024 * 1024), "oversized combined Rho partial-encrypted resource");
    }

    private static void TestArchiveHeaderCountsAndTextLengthsAreBounded()
    {
        ExpectInvalidData(() => ArchiveReadLimits.ValidateEntryCount(0, "Rho"), "empty Rho block table");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateEntryCount(100_001, "Rho"), "oversized Rho block table");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateEntryCount(0, "Rho5"), "empty Rho5 file table");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateEntryCount(-1, "Rho5"), "negative Rho5 file count");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateEntryCount(100_001, "Rho5"), "oversized Rho5 file count");
        ArchiveReadLimits.ValidateEntryCount(100_000, "Rho");

        ExpectInvalidData(() => ArchiveReadLimits.ValidateTableFitsFile(0x100, 1, 0x20, 0x11f, "Rho block"), "truncated Rho block table in tiny file");
        ArchiveReadLimits.ValidateTableFitsFile(0x100, 1, 0x20, 0x120, "Rho block");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateTableFitsFile(0x80, 1, 40, 0xa7, "Rho5 file-info"), "truncated Rho5 table in tiny file");

        using var textStream = new MemoryStream();
        using (var writer = new BinaryWriter(textStream, Encoding.Unicode, leaveOpen: true))
            writer.Write(int.MaxValue);
        textStream.Position = 0;
        using var reader = new BinaryReader(textStream, Encoding.Unicode, leaveOpen: true);
        ExpectInvalidData(() => reader.ReadText(), "huge serialized text length");
    }

    private static void TestKsvInnerLengthsAreBounded()
    {
        using (var stream = new MemoryStream(new byte[4]))
        using (var reader = new BinaryReader(stream))
            ExpectInvalidData(() => reader.ReadKRData(int.MaxValue), "huge KSV outer FileSize");

        var hugeDecompressedSize = new byte[10];
        hugeDecompressedSize[0] = 0x53;
        hugeDecompressedSize[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(hugeDecompressedSize.AsSpan(6), int.MaxValue);
        using (var reader = new BinaryReader(new MemoryStream(hugeDecompressedSize)))
            ExpectInvalidData(() => reader.ReadKRData(hugeDecompressedSize.Length), "huge KSV DecompressSize");

        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
                writer.Write(int.MaxValue);
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
            ExpectInvalidData(() => reader.ReadKRString(), "huge KSV string length");
        }

        ExpectInvalidData(() => ReadSyntheticKsvWithPlayerCount(100_001), "huge KSV playerCount");
        ExpectInvalidData(() => ReadSyntheticKsvWithRecordCount(100_001), "huge KSV recordCount");

        var exactPayload = Encoding.UTF8.GetBytes("exact zlib payload");
        AssertBytes(exactPayload, ReadKrData(BuildKrData(exactPayload, exactPayload.Length)), "exact-size KSV zlib stream");
        ExpectInvalidData(() => ReadKrData(BuildKrData(new byte[64 * 1024], 1)), "KSV zlib stream beyond declared size");
    }

    private static void TestUpdaterTrustBoundaries()
    {
        KartPatchDiscovery.ValidateDiscoveryHost("8.8.8.8");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("127.0.0.1"), "loopback discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("10.20.30.40"), "private discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("169.254.1.2"), "link-local discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("::1"), "IPv6 loopback discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("fe80::1"), "IPv6 link-local discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("fc00::1"), "IPv6 unique-local discovery host");
        ExpectInvalidData(() => KartPatchDiscovery.ValidateDiscoveryHost("patch-server.example.test"), "named discovery host instead of public literal IP");

        const string trustedEndpoint = "https://patch-server.example.test/game/";
        _ = KartPatchService.ValidatePatchUri("https://patch-server.example.test/game/files.nfo2", trustedEndpoint);
        ExpectInvalidData(() => KartPatchService.ValidatePatchUri("https://127.0.0.1/game/files.nfo2", "https://127.0.0.1/game/"), "loopback patch endpoint");
        ExpectInvalidData(() => KartPatchService.ValidatePatchUri("https://192.168.1.20/game/files.nfo2", "https://192.168.1.20/game/"), "private patch endpoint");
        ExpectInvalidData(() => KartPatchService.ValidatePatchUri("https://169.254.10.2/game/files.nfo2", "https://169.254.10.2/game/"), "link-local patch endpoint");
        ExpectInvalidData(() => KartPatchService.ValidatePatchUri("https://localhost/game/files.nfo2", "https://localhost/game/"), "localhost patch endpoint");
        ExpectInvalidData(() => KartPatchService.ValidatePatchUri("http://patch-server.example.test/game/file", "http://patch-server.example.test/game/"), "HTTP endpoint without opt-in");
        _ = KartPatchService.ValidatePatchUri("http://patch-server.example.test/game/file", "http://patch-server.example.test/game/", allowInsecureHttp: true);

        ExpectInvalidData(() => KartPatchDiscovery.RequireSocketEndpoint(string.Empty), "empty socket endpoint must not fall back to KartRider.pin URL");
        ExpectInvalidData(() => KartPatchDiscovery.RequireSocketEndpoint(" \t "), "whitespace socket endpoint");
        Assert(KartPatchDiscovery.RequireSocketEndpoint(trustedEndpoint) == trustedEndpoint, "Valid socket endpoint was changed.");
        Assert(GameUpdateForm.FormatEndpointForDisplay("https://patch-server.example.test:9443/game/42") == "https://patch-server.example.test:9443", "Updater confirmation endpoint omitted scheme, host, or port.");

        Assert(!KartPatchService.IsGloballyRoutableAddress(IPAddress.Parse("192.0.2.10")), "Documentation IPv4 block was accepted as globally routable.");
        Assert(!KartPatchService.IsGloballyRoutableAddress(IPAddress.Parse("2001:db8::1")), "Documentation IPv6 block was accepted as globally routable.");
    }

    private static byte[] ReadSyntheticKsvWithPlayerCount(int playerCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        WriteKsvPrefix(writer);
        writer.Write(playerCount);
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
        _ = reader.ReadKSVInfo();
        return stream.ToArray();
    }

    private static byte[] ReadSyntheticKsvWithRecordCount(int recordCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        WriteKsvPrefix(writer);
        writer.Write(0); // playerCount
        writer.Write(KSVStructVersion.GetRecordClassIdentifier(0));
        writer.Write(recordCount);
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
        _ = reader.ReadKSVInfo();
        return stream.ToArray();
    }

    private static void WriteKsvPrefix(BinaryWriter writer)
    {
        writer.Write(KSVStructVersion.GetHeaderClassIdentifier(11));
        WriteKsvString(writer, string.Empty); // title
        writer.Write((short)CountryCode.TW);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(0u);
        writer.Write(0u);
        WriteKsvString(writer, string.Empty); // account
        WriteKsvString(writer, string.Empty); // recorder
        writer.Write((ushort)0); // date
        writer.Write((ushort)0); // time
        writer.Write(0u); // record checksum
        writer.Write((byte)0); // official
        WriteKsvString(writer, string.Empty); // description
        WriteKsvString(writer, string.Empty); // track
        writer.Write(0); // unknown3
        writer.Write(0); // best time
        WriteKsvString(writer, string.Empty); // contest image
        writer.Write(0); // unknown4
        writer.Write(0); // unknown5
        writer.Write((byte)0); // unknown6
        writer.Write((byte)0); // speed
    }

    private static void WriteKsvString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static byte[] BuildKrData(byte[] expandedData, int declaredSize)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(expandedData);

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)0x53);
            writer.Write((byte)1); // zlib compressed, not encrypted
            writer.Write(Adler.Adler32(0, expandedData, 0, expandedData.Length));
            writer.Write(declaredSize);
            writer.Write(compressed.ToArray());
        }
        return output.ToArray();
    }

    private static byte[] ReadKrData(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        return reader.ReadKRData(bytes.Length);
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(input);
        return output.ToArray();
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CollectFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidDataException($"{label}: returned bytes differ from the synthetic payload.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void ExpectInvalidData(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException($"{label} was not rejected.");
    }
}
