using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RhoViewerPortable;

/// <summary>
/// Holds checked directory components open while updater path-based writes and moves run.
/// On Windows, the target is opened for directory list access without FILE_SHARE_DELETE,
/// allowing share-mode compatibility to reject target rename attempts while held.
/// Attribute-only access does not impose this sharing restriction, and list access
/// requires directory read/list permission unlike the former metadata-only access.
/// Ancestors remain attribute-only and allow delete sharing; they are not pinned against
/// rename. File.Move still resolves names by path, so this narrows the swap window rather
/// than providing handle-relative atomic I/O; non-Windows filesystems receive reparse
/// checks but no equivalent lease guarantee.
/// </summary>
internal sealed class WindowsDirectoryPathLease : IDisposable
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileListDirectory = 0x0001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint ReparsePointAttribute = 0x00000400;
    private const uint DirectoryAttribute = 0x00000010;

    private readonly List<SafeFileHandle> _handles;

    private WindowsDirectoryPathLease(List<SafeFileHandle> handles) => _handles = handles;

    internal static WindowsDirectoryPathLease Acquire(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(fullPath);
            EnsureNoReparsePoints(fullPath);
            return new WindowsDirectoryPathLease(new List<SafeFileHandle>());
        }

        var handles = new List<SafeFileHandle>();
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                throw new InvalidDataException($"更新目錄沒有有效磁碟根目錄：{directoryPath}");

            var remainder = fullPath[root.Length..];
            var segments = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return new WindowsDirectoryPathLease(handles);

            var currentPath = root;
            for (var index = 0; index < segments.Length; index++)
            {
                currentPath = Path.Combine(currentPath, segments[index]);
                Directory.CreateDirectory(currentPath);
                AddDirectoryHandle(handles, currentPath, isTarget: index == segments.Length - 1);
            }

            return new WindowsDirectoryPathLease(handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public void Dispose() => DisposeHandles(_handles);

    private static void AddDirectoryHandle(List<SafeFileHandle> handles, string path, bool isTarget)
    {
        var handle = CreateFileW(
            path,
            isTarget ? FileListDirectory : FileReadAttributes,
            // Attribute-only access is unaffected by share-mode compatibility. Request
            // list access on the target so omitting delete sharing can reject its rename.
            // Ancestors stay metadata-only and allow delete sharing; path-based I/O can
            // still be redirected by an ancestor rename.
            ShareRead | ShareWrite | (isTarget ? 0 : ShareDelete),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"無法固定更新目錄：{path}");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"無法檢查更新目錄：{path}");
        }

        if ((information.FileAttributes & ReparsePointAttribute) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException($"更新路徑包含符號連結或 reparse point，已拒絕：{path}");
        }
        if ((information.FileAttributes & DirectoryAttribute) == 0)
        {
            handle.Dispose();
            throw new InvalidDataException($"更新路徑的目錄元件不是資料夾：{path}");
        }

        handles.Add(handle);
    }

    private static void EnsureNoReparsePoints(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var currentPath = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!Directory.Exists(currentPath))
                continue;
            var attributes = File.GetAttributes(currentPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"更新路徑包含符號連結或 reparse point，已拒絕：{currentPath}");
        }
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
            handles[index].Dispose();
        handles.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
