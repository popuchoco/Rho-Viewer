using System.Text;
using KartLibrary.File;

namespace RhoViewerPortable;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return SelfTest.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && string.Equals(args[0], "--smoke-load", StringComparison.OrdinalIgnoreCase))
        {
            return UiSmokeTest.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && string.Equals(args[0], "--format-self-test", StringComparison.OrdinalIgnoreCase))
        {
            return FormatSelfTest.Run();
        }

        if (args.Length > 0 && string.Equals(args[0], "--kart-crc-self-test", StringComparison.OrdinalIgnoreCase))
        {
            return KartCrcSelfTest.Run();
        }

        if (args.Length > 0 && string.Equals(args[0], "--kart-crc-file-test", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: RhoViewerPortable.exe --kart-crc-file-test file");
                return 2;
            }

            return KartCrcSelfTest.RunFile(args[1]);
        }

        if (args.Length > 0 && string.Equals(args[0], "--patch-discovery-test", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: RhoViewerPortable.exe --patch-discovery-test gameDirectory [auto|tw|kr|cn]");
                return 2;
            }

            var region = args.Length >= 3
                ? KartRegionCatalog.FromCode(args[2]).Region
                : KartUpdateRegion.Auto;
            return PatchDiscoverySelfTest.Run(args[1], region).GetAwaiter().GetResult();
        }

        if (args.Length > 0 && string.Equals(args[0], "--patch-plan-test", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: RhoViewerPortable.exe --patch-plan-test gameDirectory [auto|tw|kr|cn]");
                return 2;
            }

            var region = args.Length >= 3
                ? KartRegionCatalog.FromCode(args[2]).Region
                : KartUpdateRegion.Auto;
            return PatchPlanSelfTest.Run(args[1], region).GetAwaiter().GetResult();
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

internal static class UiSmokeTest
{
    public static int Run(string[] paths)
    {
        if (paths.Length == 0)
            return 2;

        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        var exitCode = 0;
        form.Shown += async (_, _) =>
        {
            try
            {
                await form.LoadPathsForSmokeAsync(paths);
            }
            catch (Exception ex)
            {
                exitCode = 1;
                Console.Error.WriteLine(ex);
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        return exitCode;
    }
}

internal static class KartCrcSelfTest
{
    public static int Run()
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
            Console.WriteLine($"OK Kart CRC: 0x{result:X8}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Kart CRC: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for the temporary self-test directory.
            }
        }
    }

    public static int RunFile(string path)
    {
        try
        {
            var result = KartCrc.ComputeAsync(path).GetAwaiter().GetResult();
            Console.WriteLine($"OK Kart CRC: {Path.GetFileName(path)} = 0x{result:X8} ({result})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Kart CRC: {ex.Message}");
            return 1;
        }
    }
}

internal static class PatchDiscoverySelfTest
{
    public static async Task<int> Run(string gameDirectory, KartUpdateRegion region)
    {
        try
        {
            var result = await KartPatchDiscovery.DiscoverAsync(gameDirectory, region, CancellationToken.None);
            Console.WriteLine($"OK patch discovery: region={result.Region}; pin={result.PinPath}; client={result.ClientVersion}; server={result.ServerVersion}; socket={result.ServerHost}:{result.ServerPort}; endpoint={result.PatchBaseUrl}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL patch discovery: {ex.Message}");
            return 1;
        }
    }
}

internal static class PatchPlanSelfTest
{
    public static async Task<int> Run(string gameDirectory, KartUpdateRegion region)
    {
        try
        {
            var plan = await new KartPatchService().BuildPlanAsync(gameDirectory, region, CancellationToken.None);
            Console.WriteLine($"OK patch plan: region={plan.Region}; server={plan.ServerVersion}; files={plan.Items.Count}; changes={plan.Changes.Count}; downloadBytes={plan.DownloadBytes}; base={plan.PatchBaseUrl}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL patch plan: {ex.Message}");
            return 1;
        }
    }
}

internal static class SelfTest
{
    public static int Run(string[] paths)
    {
        if (paths.Length == 0)
        {
            Console.Error.WriteLine("Usage: RhoViewerPortable.exe --self-test file1.rho [file2.rho ...]");
            return 2;
        }

        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                using var context = RhoReader.Open(path);
                Console.WriteLine($"OK {Path.GetFileName(path)}: {context.Items.Count} files, layer {context.LayerVersion}");
                var image = context.Items.FirstOrDefault(item => item.Extension is ".png" or ".jpg" or ".jpeg");
                if (image is not null)
                {
                    var bytes = image.ReadBytes();
                    if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                    {
                        throw new InvalidDataException($"first image did not decode as PNG: {image.RelativePath}");
                    }
                    Console.WriteLine($"  image OK: {image.RelativePath} ({bytes.Length:N0} bytes)");
                }

                var audio = context.Items.FirstOrDefault(item => item.Extension is ".ogg" or ".flac" or ".wav" or ".mp3");
                if (audio is not null)
                {
                    var bytes = audio.ReadBytes();
                    var valid = audio.Extension == ".ogg"
                        ? bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("OggS"u8)
                        : audio.Extension == ".flac"
                            ? bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8)
                            : bytes.Length >= 4;
                    if (!valid)
                        throw new InvalidDataException($"first audio did not decode as {audio.Extension}: {audio.RelativePath}");
                    Console.WriteLine($"  audio extract OK: {audio.RelativePath} ({bytes.Length:N0} bytes)");
                    var analysis = ResourceAnalyzers.Analyze(audio, bytes);
                    if (string.IsNullOrWhiteSpace(analysis))
                        throw new InvalidDataException($"audio analyzer returned no result: {audio.RelativePath}");
                    Console.WriteLine("  audio analysis OK");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {path}: {ex.Message}");
            }
        }

        return failed == 0 ? 0 : 1;
    }
}

internal static class FormatSelfTest
{
    public static int Run()
    {
        try
        {
            var flac = BuildMinimalFlac();
            var flacSummary = ResourceAnalyzers.AnalyzeExtension(".flac", flac);
            if (!flacSummary.Contains("FLAC metadata 分析", StringComparison.Ordinal) ||
                !flacSummary.Contains("MD5：", StringComparison.Ordinal))
                throw new InvalidDataException("FLAC analyzer synthetic test failed.");

            var invalidOggSummary = ResourceAnalyzers.AnalyzeExtension(".ogg", "bad"u8.ToArray());
            if (!invalidOggSummary.Contains("不是標準 OggS", StringComparison.Ordinal))
                throw new InvalidDataException("OGG invalid-header test failed.");

            var ksvSummary = ResourceAnalyzers.AnalyzeExtension(".ksv", "not-a-ksv"u8.ToArray());
            if (!ksvSummary.Contains("KSV", StringComparison.Ordinal))
                throw new InvalidDataException("KSV fallback test failed.");

            Console.WriteLine("OK format analyzers: FLAC synthetic, OGG rejection, KSV fallback");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL format analyzers: {ex.Message}");
            return 1;
        }
    }

    private static byte[] BuildMinimalFlac()
    {
        var result = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(result);
        result[4] = 0x80;
        result[5] = 0;
        result[6] = 0;
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
}
