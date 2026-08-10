using System.Text;
using KartLibrary.File;

namespace RhoViewerPortable;

internal sealed class RhoArchiveContext : IDisposable
{
    private readonly RhoArchive _archive;

    private RhoArchiveContext(string path, RhoArchive archive, IReadOnlyList<RhoItem> items, string layerVersion, int folderCount)
    {
        Path = path;
        _archive = archive;
        Items = items;
        LayerVersion = layerVersion;
        FolderCount = folderCount;
    }

    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string LayerVersion { get; }
    public int FolderCount { get; }
    public IReadOnlyList<RhoItem> Items { get; }

    public static RhoArchiveContext Open(string path)
    {
        var archive = new RhoArchive();
        try
        {
            archive.Open(path);
            var layer = ReadLayerVersion(path);
            var items = new List<RhoItem>();
            var context = new RhoArchiveContext(path, archive, items, layer, CountChildFolders(archive.RootFolder));
            Flatten(archive.RootFolder, string.Empty, items, context);
            return context;
        }
        catch
        {
            archive.Dispose();
            throw;
        }

        static void Flatten(RhoFolder folder, string parentPath, List<RhoItem> target, RhoArchiveContext owner)
        {
            foreach (var file in folder.Files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = string.IsNullOrEmpty(parentPath) ? file.Name : $"{parentPath}/{file.Name}";
                target.Add(new RhoItem(owner, file, relativePath));
            }

            foreach (var child in folder.Folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
            {
                var childPath = string.IsNullOrEmpty(parentPath) ? child.Name : $"{parentPath}/{child.Name}";
                Flatten(child, childPath, target, owner);
            }
        }

        static int CountChildFolders(RhoFolder folder)
        {
            var count = 0;
            foreach (var child in folder.Folders)
                count += 1 + CountChildFolders(child);
            return count;
        }
    }

    private static string ReadLayerVersion(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false);
        var raw = reader.ReadBytes(0x40);
        var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return text.Contains("1.1", StringComparison.Ordinal) ? "Rh layer 1.1" : text.Contains("1.0", StringComparison.Ordinal) ? "Rh layer 1.0" : text;
    }

    public void Dispose() => _archive.Dispose();
}

internal sealed class RhoItem
{
    internal RhoItem(RhoArchiveContext owner, RhoFile file, string relativePath)
    {
        Owner = owner;
        File = file;
        RelativePath = relativePath;
    }

    internal RhoArchiveContext Owner { get; }
    internal RhoFile File { get; }
    public string RelativePath { get; }
    public string Name => File.Name;
    public string Extension => System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public int Size => File.Size;
    public string Property => File.FileEncryptionProperty.ToString();

    public byte[] ReadBytes() => File.GetBytes();
}

internal static class RhoReader
{
    public static RhoArchiveContext Open(string path)
    {
        if (!System.IO.File.Exists(path))
            throw new FileNotFoundException("找不到 Rho 檔案。", path);

        return RhoArchiveContext.Open(path);
    }
}
