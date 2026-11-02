using System.Text;
using System.Text.RegularExpressions;
using KartLibrary.Consts;
using KartLibrary.File;

namespace RhoViewerPortable;

internal sealed class RhoArchiveContext : IDisposable
{
    private readonly IDisposable _archive;

    private RhoArchiveContext(
        string path,
        string displayName,
        IDisposable archive,
        IReadOnlyList<RhoItem> items,
        string layerVersion,
        int folderCount,
        long storedSize)
    {
        Path = path;
        DisplayName = displayName;
        _archive = archive;
        Items = items;
        LayerVersion = layerVersion;
        FolderCount = folderCount;
        StoredSize = storedSize;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public string LayerVersion { get; }
    public int FolderCount { get; }
    public long StoredSize { get; }
    public IReadOnlyList<RhoItem> Items { get; }

    public static RhoArchiveContext Open(string path, string regionCode = "auto")
    {
        var extension = System.IO.Path.GetExtension(path);
        if (string.Equals(extension, ".rho5", StringComparison.OrdinalIgnoreCase))
            return OpenRho5(path, regionCode);

        if (string.Equals(extension, ".lpk", StringComparison.OrdinalIgnoreCase))
            return OpenLpk(path);

        return OpenRho(path);
    }

    private static RhoArchiveContext OpenLpk(string path)
    {
        var archive = LpkArchive.Open(path);
        try
        {
            var items = new List<RhoItem>(archive.Entries.Count);
            var context = new RhoArchiveContext(
                path,
                System.IO.Path.GetFileName(path),
                archive,
                items,
                "LPK v4",
                CountLpkFolders(archive.Entries),
                new FileInfo(path).Length);
            foreach (var entry in archive.Entries)
            {
                var name = System.IO.Path.GetFileName(entry.RelativePath);
                var property = $"LPK v4 flags={entry.Flags}" + ((entry.Flags & 1) != 0 ? "（ZLib 壓縮）" : "（未壓縮）");
                items.Add(new RhoItem(context, name, entry.RelativePath, entry.Size, property, () => archive.Read(entry)));
            }
            return context;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static int CountLpkFolders(IEnumerable<LpkArchive.LpkEntry> entries)
        => entries.SelectMany(entry =>
                entry.RelativePath.Split('/').SkipLast(1)
                    .Select((_, index) => string.Join("/", entry.RelativePath.Split('/').Take(index + 1))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static RhoArchiveContext OpenRho(string path)
    {
        var archive = new RhoArchive();
        try
        {
            archive.Open(path);
            var layer = ReadLayerVersion(path);
            var items = new List<RhoItem>();
            var context = new RhoArchiveContext(
                path,
                System.IO.Path.GetFileName(path),
                archive,
                items,
                layer,
                CountChildFolders(archive.RootFolder),
                new FileInfo(path).Length);
            Flatten(archive.RootFolder, string.Empty, items, context);
            return context;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static RhoArchiveContext OpenRho5(string path, string regionCode)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var fileName = System.IO.Path.GetFileName(fullPath);
        var match = Regex.Match(fileName, @"^(?<pack>.+)_(?<part>\d{5})\.rho5$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException("Rho5 檔名必須符合 DataPackN_00000.rho5 這類 5 位數分卷格式。");

        var packName = match.Groups["pack"].Value;
        var directory = System.IO.Path.GetDirectoryName(fullPath)!;
        var countryCode = ResolveRho5Region(fullPath, regionCode);
        var archive = new Rho5Archive();
        try
        {
            archive.Open(directory, packName, countryCode);
            var items = new List<RhoItem>();

            var partFiles = Directory.EnumerateFiles(directory, $"{packName}_*.rho5", SearchOption.TopDirectoryOnly)
                .Where(candidate => Regex.IsMatch(
                    System.IO.Path.GetFileName(candidate),
                    $"^{Regex.Escape(packName)}_\\d{{5}}\\.rho5$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
            if (partFiles.Length == 0)
                throw new FileNotFoundException($"找不到 {packName}_00000.rho5 分卷。");

            var identityPath = partFiles.OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).First();
            var context = new RhoArchiveContext(
                identityPath,
                $"{packName}（Rho5）",
                archive,
                items,
                $"Rho5 v2 / {countryCode}",
                CountChildFolders(archive.RootFolder),
                partFiles.Sum(candidate => new FileInfo(candidate).Length));
            Flatten(archive.RootFolder, string.Empty, items, context);
            return context;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static CountryCode ResolveRho5Region(string path, string regionCode)
    {
        var explicitRegion = regionCode.Trim().ToLowerInvariant() switch
        {
            "tw" => CountryCode.TW,
            "kr" => CountryCode.KR,
            "cn" => CountryCode.CN,
            _ => CountryCode.None
        };
        if (explicitRegion != CountryCode.None)
            return explicitRegion;

        for (var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "KartRiderTW.ini")))
                return CountryCode.TW;
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "KartRiderKR.ini")))
                return CountryCode.KR;
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "KartRiderCN.ini")))
                return CountryCode.CN;
        }

        throw new InvalidOperationException(
            "Rho5 解密需要 TW/KR/CN 區域混合字串。請在遊戲更新設定中選擇遊戲區域，或將 Rho5 放在含 KartRiderTW.ini、KartRiderKR.ini 或 KartRiderCN.ini 的遊戲資料夾中以便自動判斷。");
    }

    private static string ReadLayerVersion(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false);
        var raw = reader.ReadBytes(0x40);
        var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return text.Contains("1.1", StringComparison.Ordinal) ? "Rh layer 1.1" : text.Contains("1.0", StringComparison.Ordinal) ? "Rh layer 1.0" : text;
    }

    private static void Flatten(RhoFolder folder, string parentPath, List<RhoItem> target, RhoArchiveContext owner)
    {
        foreach (var file in folder.Files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = string.IsNullOrEmpty(parentPath) ? file.Name : $"{parentPath}/{file.Name}";
            target.Add(new RhoItem(owner, file.Name, relativePath, file.Size, file.FileEncryptionProperty.ToString(), file.GetBytes));
        }

        foreach (var child in folder.Folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            var childPath = string.IsNullOrEmpty(parentPath) ? child.Name : $"{parentPath}/{child.Name}";
            Flatten(child, childPath, target, owner);
        }
    }

    private static void Flatten(Rho5Folder folder, string parentPath, List<RhoItem> target, RhoArchiveContext owner)
    {
        foreach (var file in folder.Files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = string.IsNullOrEmpty(parentPath) ? file.Name : $"{parentPath}/{file.Name}";
            target.Add(new RhoItem(owner, file.Name, relativePath, file.Size, "Rho5 加密資源（讀取時解密）", file.GetBytes));
        }

        foreach (var child in folder.Folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            var childPath = string.IsNullOrEmpty(parentPath) ? child.Name : $"{parentPath}/{child.Name}";
            Flatten(child, childPath, target, owner);
        }
    }

    private static int CountChildFolders(RhoFolder folder)
        => folder.Folders.Sum(child => 1 + CountChildFolders(child));

    private static int CountChildFolders(Rho5Folder folder)
        => folder.Folders.Sum(child => 1 + CountChildFolders(child));

    public void Dispose() => _archive.Dispose();
}

internal sealed class RhoItem
{
    private readonly Func<byte[]> _readBytes;

    internal RhoItem(RhoArchiveContext owner, string name, string relativePath, int size, string property, Func<byte[]> readBytes)
    {
        Owner = owner;
        Name = name;
        RelativePath = relativePath;
        Size = size;
        Property = property;
        _readBytes = readBytes;
    }

    internal RhoArchiveContext Owner { get; }
    public string RelativePath { get; }
    public string Name { get; }
    public string Extension => System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public ResourceKind Kind => ResourceClassifier.Classify(Extension);
    public int Size { get; }
    public string Property { get; }

    public byte[] ReadBytes() => _readBytes();

}

internal static class RhoReader
{
    private static readonly Regex Rho5PartPattern = new(@"^(?<pack>.+)_(?<part>\d{5})\.rho5$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string GetCanonicalPath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var extension = System.IO.Path.GetExtension(fullPath);
        var pattern = extension.Equals(".rho5", StringComparison.OrdinalIgnoreCase) ? Rho5PartPattern : null;
        if (pattern is null)
            return fullPath;

        var match = pattern.Match(System.IO.Path.GetFileName(fullPath));
        if (!match.Success)
            return fullPath;

        var directory = System.IO.Path.GetDirectoryName(fullPath)!;
        var packName = match.Groups["pack"].Value;
        var candidatePattern = Rho5PartPattern;
        return Directory.EnumerateFiles(directory, $"{packName}_*{extension}", SearchOption.TopDirectoryOnly)
            .Where(candidate => candidatePattern.IsMatch(System.IO.Path.GetFileName(candidate)) &&
                                string.Equals(candidatePattern.Match(System.IO.Path.GetFileName(candidate)).Groups["pack"].Value, packName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => System.IO.Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? fullPath;
    }

    public static RhoArchiveContext Open(string path, string regionCode = "auto")
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 Rho 封包。", path);

        return RhoArchiveContext.Open(path, regionCode);
    }
}
