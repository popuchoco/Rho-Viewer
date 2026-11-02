using System.Buffers.Binary;
using System.Text;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Length > 0 && string.Equals(args[0], "--archive-corpus", StringComparison.OrdinalIgnoreCase))
            return ArchiveCorpusTest.Run(args.Skip(1).ToArray());

        if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.Tests [--archive-corpus dataRoot [dataRoot ...]]");
            return 2;
        }

        var failures = 0;
        failures += RunSuite("resource parser malformed-input tests", ParserInputTests.Run);
        failures += RunSuite("updater security tests", UpdaterSecurityTests.Run);
        failures += RunSuite("CRC and UI mutation-state tests", GeneralTests.Run);
        Console.WriteLine(failures == 0 ? "ALL TEST SUITES PASSED" : $"TEST FAILURES: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int RunSuite(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {name}: {ex}");
            return 1;
        }
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

internal static class ArchiveCorpusTest
{
    private static readonly string[] SupportedExtensions = [".rho", ".rho5", ".lpk"];

    public static int Run(string[] roots)
    {
        if (roots.Length == 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.Tests --archive-corpus dataRoot [dataRoot ...]");
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
                    foreach (var item in context.Items)
                    {
                        try
                        {
                            var bytes = item.ReadBytes();
                            if (bytes.Length != item.Size)
                                throw new InvalidDataException($"declared {item.Size} bytes, decoded {bytes.Length} bytes");
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
