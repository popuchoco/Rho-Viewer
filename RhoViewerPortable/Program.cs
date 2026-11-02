using System.Text;

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
                if (string.Equals(Path.GetExtension(path), ".lpk", StringComparison.OrdinalIgnoreCase))
                {
                    long decodedBytes = 0;
                    foreach (var item in context.Items)
                        decodedBytes = checked(decodedBytes + item.ReadBytes().Length);
                    Console.WriteLine($"  LPK resource integrity OK: {context.Items.Count:N0} entries, {decodedBytes:N0} decoded bytes");
                    continue;
                }

                var image = context.Items.FirstOrDefault(item => item.Extension is ".png" or ".jpg" or ".jpeg");
                if (image is not null)
                {
                    var bytes = image.ReadBytes();
                    var hasValidHeader = image.Extension switch
                    {
                        ".png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                        ".jpg" or ".jpeg" => bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8,
                        _ => false
                    };
                    if (!hasValidHeader)
                    {
                        throw new InvalidDataException($"first image did not have the expected {image.Extension} signature: {image.RelativePath}");
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
