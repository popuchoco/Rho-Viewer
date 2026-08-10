using System.Text;
using System.Text.Json;

namespace RhoViewerPortable;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string RhoDirectory { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public int WindowWidth { get; set; } = 1120;
    public int WindowHeight { get; set; } = 720;
    public double SplitterRatio { get; set; } = 0.20;

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "Config", "RhoViewerPortable.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return CreateDefault();

            var json = File.ReadAllText(ConfigPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefault();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs")
        };
    }

    public void Normalize()
    {
        RhoDirectory = NormalizeDirectory(RhoDirectory);
        LogDirectory = NormalizeDirectory(LogDirectory);
        WindowWidth = Math.Clamp(WindowWidth, 900, 3200);
        WindowHeight = Math.Clamp(WindowHeight, 600, 2400);
        if (double.IsNaN(SplitterRatio) || double.IsInfinity(SplitterRatio))
            SplitterRatio = 0.20;
        SplitterRatio = Math.Clamp(SplitterRatio, 0.18, 0.42);
        if (string.IsNullOrWhiteSpace(LogDirectory))
            LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public bool TrySave(out string error)
    {
        try
        {
            Normalize();
            var directory = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch
        {
            return path.Trim();
        }
    }
}
