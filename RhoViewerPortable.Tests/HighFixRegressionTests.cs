using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using KartLibrary.Consts;
using KartLibrary.Data;
using KartLibrary.Encrypt;
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
        TestLpkEntrySizesAreBounded();
        TestRhoPartialEncryptedSecondBlockIsBounded();
        TestArchiveHeaderCountsAndTextLengthsAreBounded();
        TestRhoFolderTraversalIsBounded();
        TestStoredResourceSizesAreBounded();
        TestArchiveDataRangesAreBounded();
        TestFailedArchiveOpenDisposesStreams();
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
            File.WriteAllBytes(rhoPath, CreateSyntheticRhoFixture(payload));

            using (var archive = new RhoArchive())
            {
                archive.Open(rhoPath);
                var file = archive.RootFolder.GetFile("sample.bin") ?? throw new InvalidDataException("Synthetic Rho file was not found.");
                AssertBytes(payload, file.GetBytes(), "first Rho read");
                CollectFinalizers();
                AssertBytes(payload, file.GetBytes(), "Rho read after finalizers");
            }

            // Save and read on the still-open writer to exercise the shared parent
            // stream: Dispose clears the logical tree and defeats this GC check.
            using var rho5Archive = new Rho5Archive();
            var newFile = new Rho5File
            {
                Name = "sample.bin",
                DataSource = new ByteArrayDataSource(payload)
            };
            rho5Archive.RootFolder.AddFile(newFile);
            rho5Archive.Save(root, "synthetic", CountryCode.TW, SavePattern.AlwaysRegeneration);
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

    private static byte[] CreateSyntheticRhoFixture(byte[] payload)
    {
        const string archiveName = "synthetic";
        const string resourceName = "sample";
        const string resourceExtension = "bin";
        const uint dataInfoWhiteningKey = 0x3A9213AC;
        const int blockInfoSize = 0x20;
        const int headerSize = 0x100;

        var rhoKey = RhoKey.GetRhoKey(archiveName);
        var extensionBytes = Encoding.ASCII.GetBytes(resourceExtension);
        var extensionNumberBytes = new byte[sizeof(uint)];
        Array.Copy(extensionBytes, extensionNumberBytes, extensionBytes.Length);
        var extensionNumber = BinaryPrimitives.ReadUInt32LittleEndian(extensionNumberBytes);
        var resourceNameChecksum = Adler.Adler32(0, Encoding.Unicode.GetBytes(resourceName), 0, resourceName.Length * sizeof(char));
        var resourceDataIndex = resourceNameChecksum + extensionNumber;

        byte[] rootData;
        using (var rootDataStream = new MemoryStream())
        using (var rootDataWriter = new BinaryWriter(rootDataStream, Encoding.Unicode, leaveOpen: true))
        {
            rootDataWriter.Write(0); // child folder count
            rootDataWriter.Write(1); // file count
            rootDataWriter.Write(Encoding.Unicode.GetBytes(resourceName));
            rootDataWriter.Write((short)0);
            rootDataWriter.Write(extensionNumber);
            rootDataWriter.Write((int)RhoFileProperty.None);
            rootDataWriter.Write(resourceDataIndex);
            rootDataWriter.Write(payload.Length);
            rootData = rootDataStream.ToArray();
        }

        var encryptedRootData = (byte[])rootData.Clone();
        RhoEncrypt.EncryptData(RhoKey.GetDirectoryDataKey(rhoKey), encryptedRootData, 0, encryptedRootData.Length);

        var dataInfoSize = ((2 * blockInfoSize + 0xFF) & 0x7FFFFF00);
        var dataBeginOffset = headerSize + dataInfoSize;
        var rootDataOffset = dataBeginOffset;
        var fileDataOffset = dataBeginOffset + ((rootData.Length + 0xFF) & 0x7FFFFF00);
        var fixture = new byte[fileDataOffset + payload.Length];

        Encoding.Unicode.GetBytes("Rh layer spec 1.1").CopyTo(fixture, 0);
        Encoding.Unicode.GetBytes("KartRider (veblush & dew)").CopyTo(fixture, 0x40);

        var header = new byte[0x80];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 0x10001);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), dataInfoWhiteningKey);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), unchecked(rhoKey - 0x397E40C3));
        var dataHash = Adler.Adler32Combine(0, encryptedRootData, 0, encryptedRootData.Length);
        dataHash = Adler.Adler32Combine(dataHash, payload, 0, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), dataHash);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 0xFC1F9778);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), 0x7E);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Adler.Adler32(0, header, 4, header.Length - 4));
        RhoEncrypt.EncryptHeaderInfo(header, rhoKey).CopyTo(fixture, 0x80);

        var blockInfoKey = dataInfoWhiteningKey ^ rhoKey;
        WriteBlockInfo(fixture, 0x100, 0xFFFFFFFF, rootDataOffset, encryptedRootData.Length, (int)RhoBlockProperty.FullEncrypted,
            Adler.Adler32(0, rootData, 0, rootData.Length), blockInfoKey);
        WriteBlockInfo(fixture, 0x100 + blockInfoSize, resourceDataIndex, fileDataOffset, payload.Length, (int)RhoBlockProperty.None,
            0, blockInfoKey + 1);

        encryptedRootData.CopyTo(fixture, rootDataOffset);
        payload.CopyTo(fixture, fileDataOffset);
        return fixture;
    }

    private static void WriteBlockInfo(byte[] fixture, int offset, uint index, int dataOffset, int dataSize,
        int blockProperty, uint checksum, uint encryptionKey)
    {
        var blockInfo = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(blockInfo, index);
        BinaryPrimitives.WriteInt32LittleEndian(blockInfo.AsSpan(4), dataOffset >> 8);
        BinaryPrimitives.WriteInt32LittleEndian(blockInfo.AsSpan(8), dataSize);
        BinaryPrimitives.WriteInt32LittleEndian(blockInfo.AsSpan(12), dataSize);
        BinaryPrimitives.WriteInt32LittleEndian(blockInfo.AsSpan(16), blockProperty);
        BinaryPrimitives.WriteUInt32LittleEndian(blockInfo.AsSpan(20), checksum);
        RhoEncrypt.EncryptHeaderInfo(blockInfo, encryptionKey).CopyTo(fixture, offset);
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

    private static void TestLpkEntrySizesAreBounded()
    {
        const long maxEntrySize = 256L * 1024 * 1024;
        LpkArchive.ValidateEntrySizeLimits(maxEntrySize, maxEntrySize);
        ExpectInvalidData(() => LpkArchive.ValidateEntrySizeLimits(maxEntrySize + 1, 1), "oversized LPK decoded entry size");
        ExpectInvalidData(() => LpkArchive.ValidateEntrySizeLimits(1, maxEntrySize + 1), "oversized LPK stored entry size");
        ExpectInvalidData(() => LpkArchive.ValidateEntrySizeLimits(-1, 1), "negative LPK decoded entry size");
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

    private static void TestRhoFolderTraversalIsBounded()
    {
        ArchiveReadLimits.ValidateFolderChildCount(0, "Rho folder");
        ArchiveReadLimits.ValidateFolderChildCount(ArchiveReadLimits.MaxEntryCount, "Rho file");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateFolderChildCount(-1, "Rho folder"), "negative Rho folder count");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateFolderChildCount(ArchiveReadLimits.MaxEntryCount + 1, "Rho file"), "oversized per-folder Rho file count");

        Assert(ArchiveReadLimits.ValidateFolderNodeCount(1, 2) == 3, "Rho total folder-node count changed.");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateFolderNodeCount(ArchiveReadLimits.MaxFolderNodes, 1), "oversized total Rho folder-node count");

        var visitedFolderDataIndices = new HashSet<uint>();
        ArchiveReadLimits.RegisterFolderDataIndex(visitedFolderDataIndices, 0xFFFFFFFF);
        ArchiveReadLimits.RegisterFolderDataIndex(visitedFolderDataIndices, 7);
        ExpectInvalidData(() => ArchiveReadLimits.RegisterFolderDataIndex(visitedFolderDataIndices, 7), "repeated Rho folder dataIndex cycle");
    }

    private static void TestStoredResourceSizesAreBounded()
    {
        ArchiveReadLimits.ValidateResourceSizes(ArchiveReadLimits.MaxResourceBytes, ArchiveReadLimits.MaxResourceBytes, "synthetic archive");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateResourceSizes(ArchiveReadLimits.MaxResourceBytes + 1L, 1, "synthetic archive"), "oversized stored resource");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateResourceSizes(1, ArchiveReadLimits.MaxResourceBytes + 1L, "synthetic archive"), "oversized decoded resource");
    }

    private static void TestArchiveDataRangesAreBounded()
    {
        using var stream = new MemoryStream(new byte[32]);
        ArchiveReadLimits.ValidateDataRange(stream, 16, 16, "synthetic archive");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateDataRange(stream, 17, 16, "synthetic archive"), "Rho block range beyond tiny file");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateDataRange(stream, -1, 1, "synthetic archive"), "negative Rho block offset");
        ExpectInvalidData(() => ArchiveReadLimits.ValidateDataRange(stream, 0, -1, "synthetic archive"), "negative Rho block size");
    }

    private static void TestFailedArchiveOpenDisposesStreams()
    {
        var root = CreateTemporaryDirectory("H4FailedOpenHandles");
        try
        {
            var rhoPath = Path.Combine(root, "short.rho");
            File.WriteAllBytes(rhoPath, new byte[0x7F]);
            var rhoArchive = new RhoArchive();
            ExpectThrows<InvalidOperationException>(() => rhoArchive.Open(rhoPath), "short Rho header");
            rhoArchive.Dispose();
            using (var exclusiveRho = new FileStream(rhoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(exclusiveRho.CanRead && exclusiveRho.CanWrite, "Failed Rho Open retained its file handle after Dispose.");

            var rho5Path = Path.Combine(root, "synthetic_00000.rho5");
            File.WriteAllBytes(rho5Path, new byte[1]);
            var rho5Archive = new Rho5Archive();
            ExpectThrows<EndOfStreamException>(() => rho5Archive.Open(root, "synthetic", CountryCode.TW), "short Rho5 header");
            rho5Archive.Dispose();
            using (var exclusiveRho5 = new FileStream(rho5Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(exclusiveRho5.CanRead && exclusiveRho5.CanWrite, "Failed Rho5 Open retained its file handle after Dispose.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static void ExpectThrows<TException>(Action action, string label) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidDataException($"{label} was not rejected.");
    }
}
