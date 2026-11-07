namespace RhoViewerPortable;

/// <summary>
/// Rho package browsing stays a folder picker. KartRider.pin selection is a file picker;
/// the updater then uses the directory that contains the selected pin.
/// </summary>
internal static class LocationDialogs
{
    internal const string PinFileName = "KartRider.pin";

    internal static FolderBrowserDialog CreateRhoDirectoryDialog(string? currentDirectory)
    {
        return new FolderBrowserDialog
        {
            Description = "選擇要掃描 .rho、.rho5 與 .lpk 封包的資料夾（包含子資料夾）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(currentDirectory) ? currentDirectory : string.Empty
        };
    }

    internal static OpenFileDialog CreateKartRiderPinDialog(string? currentGameDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇 KartRider.pin",
            Filter = "KartRider.pin|KartRider.pin|所有檔案 (*.*)|*.*",
            FileName = PinFileName,
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        var initialDirectory = InitialDirectoryForPinDialog(currentGameDirectory);
        if (initialDirectory.Length > 0)
            dialog.InitialDirectory = initialDirectory;
        return dialog;
    }

    private static string InitialDirectoryForPinDialog(string? currentGameDirectory)
    {
        if (string.IsNullOrWhiteSpace(currentGameDirectory))
            return string.Empty;

        var candidate = currentGameDirectory.Trim().Trim('"');
        if (Directory.Exists(candidate))
            return candidate;
        if (File.Exists(candidate))
            return Path.GetDirectoryName(candidate) ?? string.Empty;
        return string.Empty;
    }
}
