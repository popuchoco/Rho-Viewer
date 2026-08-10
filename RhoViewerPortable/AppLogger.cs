using System.Text;

namespace RhoViewerPortable;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string _logDirectory = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "logs");

    public static string LogDirectory => _logDirectory;

    public static void Initialize(string? configuredDirectory)
    {
        var requested = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : configuredDirectory;
        try
        {
            Directory.CreateDirectory(requested);
            _logDirectory = Path.GetFullPath(requested);
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", "logs");
            try
            {
                Directory.CreateDirectory(fallback);
                _logDirectory = fallback;
            }
            catch
            {
                // Log 失敗不可阻止主程式啟動。
            }
        }
    }

    public static void Info(string action, string message) => Write("INFO", action, message);
    public static void Warning(string action, string message) => Write("WARN", action, message);
    public static void Error(string action, string message, Exception? exception = null)
        => Write("ERROR", action, exception is null ? message : $"{message} | {exception}");

    private static void Write(string level, string action, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{level}\t{action}\t{message}{Environment.NewLine}";
            lock (Sync)
            {
                Directory.CreateDirectory(_logDirectory);
                var file = Path.Combine(_logDirectory, $"RhoViewerPortable_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Log 採 best-effort，不應中斷使用者操作。
        }
    }
}
