using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RhoViewerPortable;

internal enum KartUpdateRegion
{
    Auto,
    Taiwan,
    Korea,
    China
}

internal sealed record KartRegionProfile(
    KartUpdateRegion Region,
    string Code,
    string DisplayName,
    string Host,
    int Port,
    string Note);

internal static class KartRegionCatalog
{
    public static IReadOnlyList<KartRegionProfile> Profiles { get; } = new[]
    {
        new KartRegionProfile(KartUpdateRegion.Auto, "auto", "自動（依 KartRider.pin）", string.Empty, 0, "使用 KartRider.pin 內的伺服器位址與連接埠。"),
        new KartRegionProfile(KartUpdateRegion.Taiwan, "tw", "台灣（TW）", "210.208.95.160", 39311, "依 kart-patcher 公開資料的區域對應。"),
        new KartRegionProfile(KartUpdateRegion.Korea, "kr", "韓國（KR）", "218.153.7.16", 39311, "依 kart-patcher 公開資料的區域對應。"),
        new KartRegionProfile(KartUpdateRegion.China, "cn", "中國（CN）", "61.164.61.66", 39311, "依 kart-patcher 公開資料的區域對應。")
    };

    public static KartRegionProfile FromCode(string? code)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Profiles[0];

    public static KartRegionProfile Get(KartUpdateRegion region)
        => Profiles.First(profile => profile.Region == region);
}

internal sealed record KartPatchFile
{
    public required string RelativePath { get; init; }
    public required uint Crc { get; init; }
    public required long Size { get; init; }
    public required long SizeGzipped { get; init; }
}

internal enum KartPatchFileState
{
    Unchanged,
    Missing,
    Mismatch
}

internal sealed class KartPatchPlanItem
{
    public required KartPatchFile File { get; init; }
    public required string TargetPath { get; init; }
    public required KartPatchFileState State { get; init; }
    public long LocalSize { get; init; }
    public bool NeedsUpdate => State != KartPatchFileState.Unchanged;
}

internal sealed class KartPatchPlan
{
    public required string GameDirectory { get; init; }
    public required string PinPath { get; init; }
    public required int ClientVersion { get; init; }
    public required int ServerVersion { get; init; }
    public required KartUpdateRegion Region { get; init; }
    public required string ServerHost { get; init; }
    public required int ServerPort { get; init; }
    public required string PatchBaseUrl { get; init; }
    public required IReadOnlyList<KartPatchPlanItem> Items { get; init; }

    public IReadOnlyList<KartPatchPlanItem> Changes => Items.Where(item => item.NeedsUpdate).ToArray();
    public long DownloadBytes => Changes.Sum(item => item.File.SizeGzipped);
}

internal sealed class KartPatchProgress
{
    public required string Stage { get; init; }
    public string? RelativePath { get; init; }
    public long CompletedBytes { get; init; }
    public long TotalBytes { get; init; }
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
}

internal sealed class KartPatchResult
{
    public required int ServerVersion { get; init; }
    public required string BackupDirectory { get; init; }
    public required int UpdatedFiles { get; init; }
}

internal sealed class KartPatchService
{
    private const string InternalDirectoryName = ".RhoViewerUpdate";
    internal const int MaxManifestBytes = 16 * 1024 * 1024;
    internal const int MaxManifestFiles = 100_000;
    internal const long MaxPatchFileBytes = 16L * 1024 * 1024 * 1024;
    internal const long MaxPatchPlanBytes = 128L * 1024 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<KartPatchPlan> BuildPlanAsync(string gameDirectory, KartUpdateRegion region, CancellationToken cancellationToken = default)
    {
        gameDirectory = NormalizeExistingDirectory(gameDirectory);
        var connection = await KartPatchDiscovery.DiscoverAsync(gameDirectory, region, cancellationToken).ConfigureAwait(false);
        var patchBaseUrl = CombineUrl(connection.PatchBaseUrl, connection.ServerVersion.ToString());
        var manifestUrl = CombineUrl(patchBaseUrl, "files.nfo2");
        using var response = await SendTrustedGetAsync(manifestUrl, patchBaseUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"更新清單讀取失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var manifest = await ReadBoundedTextAsync(response.Content, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        var files = ParseManifest(manifest);
        var items = new List<KartPatchPlanItem>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long planBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.RelativePath);
            planBytes = checked(planBytes + file.Size + file.SizeGzipped);
            if (planBytes > MaxPatchPlanBytes)
                throw new InvalidDataException("更新清單總暫存/下載量超過安全上限（128 GiB）。");
            if (!seen.Add(relativePath))
                throw new InvalidDataException($"更新清單有重複路徑：{relativePath}");
            if (relativePath.StartsWith(InternalDirectoryName + "/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relativePath, InternalDirectoryName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新清單不可寫入更新工作區：{relativePath}");

            var targetPath = GetSafeChildPath(gameDirectory, relativePath);
            EnsureNoReparsePoints(gameDirectory, relativePath);
            var state = KartPatchFileState.Missing;
            var localSize = 0L;
            if (File.Exists(targetPath))
            {
                localSize = new FileInfo(targetPath).Length;
                var localCrc = await KartCrc.ComputeAsync(targetPath, cancellationToken).ConfigureAwait(false);
                state = localSize == file.Size && localCrc == file.Crc
                    ? KartPatchFileState.Unchanged
                    : KartPatchFileState.Mismatch;
            }

            items.Add(new KartPatchPlanItem
            {
                File = file with { RelativePath = relativePath },
                TargetPath = targetPath,
                State = state,
                LocalSize = localSize
            });
        }

        return new KartPatchPlan
        {
            GameDirectory = gameDirectory,
            PinPath = connection.PinPath,
            ClientVersion = connection.ClientVersion,
            ServerVersion = connection.ServerVersion,
            Region = connection.Region,
            ServerHost = connection.ServerHost,
            ServerPort = connection.ServerPort,
            PatchBaseUrl = patchBaseUrl,
            Items = items
        };
    }

    public async Task<KartPatchResult> ApplyAsync(
        KartPatchPlan plan,
        IProgress<KartPatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.Changes.Count == 0)
        {
            return new KartPatchResult
            {
                ServerVersion = plan.ServerVersion,
                BackupDirectory = string.Empty,
                UpdatedFiles = 0
            };
        }

        var internalDirectory = GetSafeChildPath(plan.GameDirectory, InternalDirectoryName);
        EnsureNoReparsePoints(plan.GameDirectory, InternalDirectoryName);
        var stagingDirectory = Path.Combine(internalDirectory, "staging", Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(internalDirectory, "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var applied = new List<(string Target, string Backup, bool HadOriginal)>();
        var completedBytes = 0L;
        var completedFiles = 0;

        try
        {
            EnsureNoReparsePoints(plan.GameDirectory, Path.GetRelativePath(plan.GameDirectory, stagingDirectory));
            EnsureNoReparsePoints(plan.GameDirectory, Path.GetRelativePath(plan.GameDirectory, backupDirectory));
            Directory.CreateDirectory(stagingDirectory);
            Directory.CreateDirectory(backupDirectory);
            foreach (var item in plan.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = GetSafeChildPath(stagingDirectory, item.File.RelativePath);
                EnsureNoReparsePoints(stagingDirectory, item.File.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                var partialPath = stagedPath + ".part";
                progress?.Report(new KartPatchProgress
                {
                    Stage = "下載與驗證",
                    RelativePath = item.File.RelativePath,
                    CompletedBytes = completedBytes,
                    TotalBytes = plan.DownloadBytes,
                    CompletedFiles = completedFiles,
                    TotalFiles = plan.Changes.Count
                });

                var compressedUrl = CombineUrl(plan.PatchBaseUrl, item.File.RelativePath + ".gz");
                await DownloadAndExtractVerifyAsync(
                    compressedUrl,
                    plan.PatchBaseUrl,
                    partialPath,
                    item.File,
                    bytes =>
                    {
                        completedBytes += bytes;
                        progress?.Report(new KartPatchProgress
                        {
                            Stage = "下載與驗證",
                            RelativePath = item.File.RelativePath,
                            CompletedBytes = completedBytes,
                            TotalBytes = plan.DownloadBytes,
                            CompletedFiles = completedFiles,
                            TotalFiles = plan.Changes.Count
                        });
                    },
                    cancellationToken).ConfigureAwait(false);

                File.Move(partialPath, stagedPath, overwrite: true);
                completedFiles++;
            }

            progress?.Report(new KartPatchProgress
            {
                Stage = "安全套用",
                CompletedBytes = plan.DownloadBytes,
                TotalBytes = plan.DownloadBytes,
                CompletedFiles = 0,
                TotalFiles = plan.Changes.Count
            });

            var appliedFiles = 0;
            foreach (var item in plan.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = GetSafeChildPath(stagingDirectory, item.File.RelativePath);
                var backupPath = GetSafeChildPath(backupDirectory, item.File.RelativePath);
                EnsureNoReparsePoints(plan.GameDirectory, item.File.RelativePath);
                EnsureNoReparsePoints(stagingDirectory, item.File.RelativePath);
                EnsureNoReparsePoints(backupDirectory, item.File.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

                var hadOriginal = File.Exists(item.TargetPath);
                if (hadOriginal)
                {
                    File.SetAttributes(item.TargetPath, FileAttributes.Normal);
                    File.Move(item.TargetPath, backupPath, overwrite: false);
                }

                try
                {
                    File.Move(stagedPath, item.TargetPath, overwrite: false);
                    applied.Add((item.TargetPath, backupPath, hadOriginal));
                    appliedFiles++;
                    progress?.Report(new KartPatchProgress
                    {
                        Stage = "安全套用",
                        RelativePath = item.File.RelativePath,
                        CompletedBytes = plan.DownloadBytes,
                        TotalBytes = plan.DownloadBytes,
                        CompletedFiles = appliedFiles,
                        TotalFiles = plan.Changes.Count
                    });
                }
                catch
                {
                    if (hadOriginal && File.Exists(backupPath) && !File.Exists(item.TargetPath))
                        File.Move(backupPath, item.TargetPath);
                    throw;
                }
            }

            progress?.Report(new KartPatchProgress
            {
                Stage = "完成",
                CompletedBytes = plan.DownloadBytes,
                TotalBytes = plan.DownloadBytes,
                CompletedFiles = plan.Changes.Count,
                TotalFiles = plan.Changes.Count
            });
            return new KartPatchResult
            {
                ServerVersion = plan.ServerVersion,
                BackupDirectory = backupDirectory,
                UpdatedFiles = plan.Changes.Count
            };
        }
        catch
        {
            Rollback(applied);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static async Task DownloadAndExtractVerifyAsync(
        string url,
        string trustedBaseUrl,
        string outputPath,
        KartPatchFile file,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        var compressedPath = outputPath + ".gz";
        try
        {
            if (file.Size < 0 || file.Size > MaxPatchFileBytes || file.SizeGzipped < 0 || file.SizeGzipped > MaxPatchFileBytes)
                throw new InvalidDataException($"更新檔大小超過安全上限：{file.RelativePath}");

            using var response = await SendTrustedGetAsync(url, trustedBaseUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"更新檔下載失敗：{file.RelativePath}；HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > file.SizeGzipped)
                throw new InvalidDataException($"更新檔壓縮大小超出清單宣告：{file.RelativePath}");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var compressed = new FileStream(compressedPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (read > file.SizeGzipped - compressed.Length)
                        throw new InvalidDataException($"更新檔壓縮資料超出清單宣告：{file.RelativePath}");
                    await compressed.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    reportBytes(read);
                }
                await compressed.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (compressed.Length != file.SizeGzipped)
                    throw new InvalidDataException($"更新檔壓縮大小與清單不符：{file.RelativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using (var compressed = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long expanded = 0;
                int read;
                while ((read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (read > file.Size - expanded)
                        throw new InvalidDataException($"更新檔解壓後超出清單宣告大小：{file.RelativePath}");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    expanded += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (expanded != file.Size)
                    throw new InvalidDataException($"更新檔大小與清單不符：{file.RelativePath}");
            }

            var length = new FileInfo(outputPath).Length;
            if (length != file.Size)
                throw new InvalidDataException($"更新檔大小驗證失敗：{file.RelativePath}");
            var crc = await KartCrc.ComputeAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (crc != file.Crc)
                throw new InvalidDataException($"更新檔 CRC 驗證失敗：{file.RelativePath}");
        }
        finally
        {
            try
            {
                if (File.Exists(compressedPath))
                    File.Delete(compressedPath);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("GameUpdateCleanup", $"path={compressedPath}; error={ex.Message}");
            }
        }
    }

    internal static IReadOnlyList<KartPatchFile> ParseManifest(string text)
    {
        if (text.Length > MaxManifestBytes)
            throw new InvalidDataException("NFO200 清單超過 16 MiB 安全上限。");
        if (!text.TrimStart().StartsWith("NFO200", StringComparison.Ordinal))
            throw new InvalidDataException("更新服務回傳的不是 KartRider NFO200 清單。");

        var result = new List<KartPatchFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (line.Length > 16 * 1024)
                throw new InvalidDataException("NFO200 清單單行超過 16 KiB 安全上限。");
            var fields = ParseCsvLine(line);
            if (fields.Count < 12 || fields.Count > 64)
                throw new InvalidDataException("NFO200 清單欄位數量不足。");

            if (result.Count >= MaxManifestFiles)
                throw new InvalidDataException("NFO200 清單項目超過 100,000 個安全上限。");

            var size = ParseInt64(fields[3]);
            var sizeGzipped = ParseInt64(fields[4]);
            var relativePath = NormalizeRelativePath(fields[0]);
            if (!seenPaths.Add(relativePath))
                throw new InvalidDataException($"NFO200 清單有重複路徑：{relativePath}");
            if (size > MaxPatchFileBytes || sizeGzipped > MaxPatchFileBytes)
                throw new InvalidDataException("NFO200 清單檔案大小超過 16 GiB 安全上限。");

            result.Add(new KartPatchFile
            {
                RelativePath = relativePath,
                Crc = ParseUInt32(fields[2]),
                Size = size,
                SizeGzipped = sizeGzipped
            });
        }

        if (result.Count == 0)
            throw new InvalidDataException("NFO200 清單沒有檔案項目。");
        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                    quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(ch);
        }
        if (quoted)
            throw new InvalidDataException("NFO200 清單含有未結束的引號欄位。");
        fields.Add(current.ToString());
        return fields;
    }

    private static uint ParseUInt32(string value)
    {
        var text = value.Trim();
        if (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var unsignedResult))
            return unsignedResult;

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var signedResult))
            return unchecked((uint)signedResult);

        if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException($"NFO200 CRC 欄位無效：{value}");

        throw new InvalidDataException($"NFO200 CRC 欄位超出 32-bit 範圍：{value}");
    }

    private static long ParseInt64(string value)
    {
        if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result) || result < 0)
            throw new InvalidDataException($"NFO200 大小欄位無效：{value}");
        return result;
    }

    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("請指定遊戲目錄。", nameof(path));
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"找不到遊戲目錄：{fullPath}");
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("請選擇遊戲安裝資料夾，不要選擇磁碟機根目錄。");
        return NormalizeRoot(fullPath);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > 2048)
            throw new InvalidDataException("更新清單含有空白或無效路徑。");
        var normalized = path.Trim().Replace('\\', '/');
        var segments = normalized.Split('/');
        if (Path.IsPathRooted(path) || normalized.StartsWith('/') || normalized.Length == 0 ||
            segments.Any(part => part is "" or "." or ".." || part.Contains(':') || part.EndsWith(' ') || part.EndsWith('.')) ||
            segments.Any(part => part.Any(ch => Path.GetInvalidFileNameChars().Contains(ch))) ||
            segments.Any(IsReservedDeviceName))
            throw new InvalidDataException($"更新路徑不可離開遊戲目錄：{path}");
        return normalized;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (baseName.Length == 4 &&
                (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                baseName[3] is >= '1' and <= '9');
    }

    internal static string GetSafeChildPath(string root, string relativePath)
    {
        var normalizedRoot = NormalizeRoot(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路徑超出選定遊戲目錄：{relativePath}");
        return fullPath;
    }

    internal static void EnsureNoReparsePoints(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var current = normalizedRoot;
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"更新路徑包含符號連結或 reparse point，已拒絕：{current}");
        }
    }

    private static string NormalizeRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? root!
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CombineUrl(string baseUrl, string relative)
    {
        var baseUri = ValidateDiscoveredPatchEndpoint(baseUrl);
        var escapedRelative = string.Join("/", relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        var combined = new Uri(new Uri(baseUri.ToString().TrimEnd('/') + "/"), escapedRelative).AbsoluteUri;
        return ValidatePatchUri(combined, baseUrl).AbsoluteUri;
    }

    internal static Uri ValidatePatchUri(string value, string trustedEndpoint)
    {
        var uri = ValidateDiscoveredPatchEndpoint(value);
        var trustedUri = ValidateDiscoveredPatchEndpoint(trustedEndpoint);
        if (!SameOrigin(uri, trustedUri))
            throw new InvalidDataException($"更新請求離開 discovery 回傳的 patch endpoint origin：{uri.GetLeftPart(UriPartial.Authority)}");
        return uri;
    }

    private static Uri ValidateDiscoveredPatchEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException($"KartRider.pin/patch socket 回傳的 endpoint 不是有效 HTTP/HTTPS URL：{value}");
        return uri;
    }

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
           left.Port == right.Port;

    private static async Task<HttpResponseMessage> SendTrustedGetAsync(string value, string trustedEndpoint, CancellationToken cancellationToken)
    {
        var current = ValidatePatchUri(value, trustedEndpoint);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399)
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= 5)
                throw new HttpRequestException("更新服務重新導向無效或超過 5 次。");
            current = ValidatePatchUri(location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(current, location).AbsoluteUri, trustedEndpoint);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new InvalidDataException($"更新清單超過 {maxBytes / (1024 * 1024)} MiB 安全上限。");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (read > maxBytes - output.Length)
                throw new InvalidDataException($"更新清單超過 {maxBytes / (1024 * 1024)} MiB 安全上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var bytes = output.ToArray();
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("更新清單不是有效的 UTF-8 文字。", ex);
        }
    }

    internal static void Rollback(IReadOnlyList<(string Target, string Backup, bool HadOriginal)> applied)
    {
        foreach (var entry in applied.Reverse())
        {
            try
            {
                if (File.Exists(entry.Target))
                    File.Delete(entry.Target);
                if (entry.HadOriginal && File.Exists(entry.Backup))
                    File.Move(entry.Backup, entry.Target);
            }
            catch (Exception ex)
            {
                AppLogger.Error("GameUpdateRollback", $"target={entry.Target}", ex);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warning("GameUpdateCleanup", $"path={path}; error={ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = CreateHttpClientHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RhoViewerPortable", "0.3.2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        return client;
    }

    internal static HttpClientHandler CreateHttpClientHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
    }
}

internal sealed record KartPatchConnection(
    string PinPath,
    int ClientVersion,
    string PatchBaseUrl,
    int ServerVersion,
    KartUpdateRegion Region,
    string ServerHost,
    int ServerPort);

internal static class KartPatchDiscovery
{
    private const int MaxPinFileBytes = 1024 * 1024;
    private const int MaxPinPayloadBytes = 16 * 1024 * 1024;

    public static async Task<KartPatchConnection> DiscoverAsync(string gameDirectory, KartUpdateRegion region, CancellationToken cancellationToken)
    {
        var pinPath = FindPinFile(gameDirectory);
        var pin = await ParsePinAsync(pinPath, cancellationToken).ConfigureAwait(false);
        var selectedProfile = KartRegionCatalog.Get(region);
        var host = region == KartUpdateRegion.Auto ? pin.Host : selectedProfile.Host;
        var port = region == KartUpdateRegion.Auto ? pin.Port : selectedProfile.Port;
        var patchServer = await ConnectPatchSocketAsync(host, port, cancellationToken).ConfigureAwait(false);
        var endpoint = string.IsNullOrWhiteSpace(patchServer.Endpoint) ? pin.OfficialPatchUrl : patchServer.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidDataException("KartRider.pin 沒有可用的更新端點。");
        return new KartPatchConnection(pinPath, pin.ClientVersion, endpoint, patchServer.Version, region, host, port);
    }

    private static string FindPinFile(string gameDirectory)
    {
        var direct = Path.Combine(gameDirectory, "KartRider.pin");
        if (File.Exists(direct))
            return direct;

        try
        {
            var nested = Directory.EnumerateFiles(gameDirectory, "KartRider.pin", SearchOption.AllDirectories).FirstOrDefault();
            if (nested is not null)
                return nested;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("搜尋 KartRider.pin 時沒有足夠權限。", ex);
        }
        throw new FileNotFoundException("找不到 KartRider.pin。請選擇包含遊戲安裝檔的資料夾，而不是單獨的匯出資料夾。", direct);
    }

    private static async Task<PinInfo> ParsePinAsync(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxPinFileBytes)
            throw new InvalidDataException("KartRider.pin 超過 1 MiB 安全上限。");
        var file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var cursor = new BinaryCursor(file);
        var dataLength = cursor.ReadUInt32LittleEndian();
        if (dataLength == 0 || dataLength > cursor.Remaining)
            throw new InvalidDataException("KartRider.pin 的資料長度無效。");
        var data = cursor.ReadBytes(checked((int)dataLength));

        var dataCursor = new BinaryCursor(data);
        if (dataCursor.ReadByte() != 0x53)
            throw new InvalidDataException("KartRider.pin 標頭不正確。");
        var flags = dataCursor.ReadByte();
        dataCursor.Skip(4);
        var key = dataCursor.ReadUInt32LittleEndian();
        dataCursor.Skip(4);
        var payload = dataCursor.ReadBytes(dataCursor.Remaining);
        if ((flags & 2) != 0)
            payload = Decrypt(payload, key);
        if ((flags & 1) != 0)
        {
            payload = InflatePinPayload(payload);
        }
        if (payload.Length > MaxPinPayloadBytes)
            throw new InvalidDataException("KartRider.pin 解壓後超過 16 MiB 安全上限。");

        var reader = new BinaryCursor(payload);
        reader.Skip(9);
        var clientVersion = reader.ReadKartShort();
        reader.Skip(3);
        _ = reader.ReadStringAuto();
        _ = reader.ReadStringAuto();
        var officialPatchUrl = reader.ReadStringAuto();
        reader.Skip(5);
        _ = reader.ReadStringAuto();
        if (reader.ReadByte() != 0)
        {
            _ = reader.ReadStringAuto();
            reader.Skip(8);
            while (!string.IsNullOrEmpty(reader.ReadStringAuto()))
                _ = reader.ReadStringAuto();
        }
        reader.Skip(4);
        var host = $"{reader.ReadByte()}.{reader.ReadByte()}.{reader.ReadByte()}.{reader.ReadByte()}";
        var port = reader.ReadUInt16LittleEndian();
        if (port == 0)
            throw new InvalidDataException("KartRider.pin 沒有有效的更新服務埠號。");
        return new PinInfo(clientVersion, officialPatchUrl, host, port);
    }

    private static byte[] InflatePinPayload(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > MaxPinPayloadBytes - output.Length)
                throw new InvalidDataException("KartRider.pin 解壓後超過 16 MiB 安全上限。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<PatchSocketResponse> ConnectPatchSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var buffer = new byte[16 * 1024];
            var read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (read < 12)
                throw new InvalidDataException("更新服務回應太短。");
            var cursor = new BinaryCursor(buffer.AsSpan(0, read).ToArray());
            cursor.Skip(10);
            var version = cursor.ReadKartShort();
            var endpoint = cursor.ReadStringAuto();
            return new PatchSocketResponse(version, endpoint);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("更新服務連線逾時。");
        }
        catch (SocketException ex)
        {
            throw new IOException($"無法連線更新服務 {host}:{port}。", ex);
        }
    }

    private static byte[] Decrypt(byte[] input, uint key)
    {
        var words = new uint[17];
        var keyBytes = new byte[68];
        words[0] = key ^ 0x8473FBC1;
        for (var i = 1; i < 16; i++)
            words[i] = unchecked(words[i - 1] - 0x7B8C043Fu);
        for (var i = 0; i <= 16; i++)
            BitConverter.GetBytes(words[i]).CopyTo(keyBytes, i * 4);

        var result = input.ToArray();
        var offset = 0;
        while (offset + 64 <= result.Length)
        {
            for (var i = 0; i < 16; i++)
            {
                var value = BitConverter.ToUInt32(result, offset + i * 4);
                BitConverter.GetBytes(words[i] ^ value).CopyTo(result, offset + i * 4);
            }
            offset += 64;
        }
        for (var i = offset; i < result.Length; i++)
            result[i] ^= keyBytes[i - offset];
        return result;
    }

    private sealed record PinInfo(int ClientVersion, string OfficialPatchUrl, string Host, int Port);
    private sealed record PatchSocketResponse(int Version, string Endpoint);
}

internal static class KartCrc
{
    public static async Task<uint> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var buffer = new byte[0x10000];
        ulong low = 0;
        ulong high = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                low += buffer[i];
                high += low;
            }
            low %= 0xFFF1;
            high %= 0xFFF1;
        }
        return (uint)(low | (high << 16));
    }
}

internal sealed class BinaryCursor
{
    private readonly byte[] _data;
    private int _position;

    public BinaryCursor(byte[] data) => _data = data;
    public int Remaining => _data.Length - _position;

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_position++];
    }

    public ushort ReadUInt16LittleEndian()
    {
        Ensure(2);
        var value = BitConverter.ToUInt16(_data, _position);
        _position += 2;
        return value;
    }

    public uint ReadUInt32LittleEndian()
    {
        Ensure(4);
        var value = BitConverter.ToUInt32(_data, _position);
        _position += 4;
        return value;
    }

    public int ReadKartShort()
    {
        var length = ReadUInt16LittleEndian();
        Ensure(checked(length * 2));
        var value = ReadUInt16LittleEndian();
        _position += checked(length * 2 - 2);
        return value;
    }

    public string ReadStringAuto()
    {
        var length = ReadUInt32LittleEndian();
        if (length == 0)
            return string.Empty;
        var byteLength = checked((int)length * 2);
        Ensure(byteLength);
        var value = Encoding.Unicode.GetString(_data, _position, byteLength);
        _position += byteLength;
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        Ensure(count);
        var result = new byte[count];
        Buffer.BlockCopy(_data, _position, result, 0, count);
        _position += count;
        return result;
    }

    public void Skip(int count)
    {
        Ensure(count);
        _position += count;
    }

    private void Ensure(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("KartRider binary data ended unexpectedly.");
    }
}
