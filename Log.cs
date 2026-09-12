using System.IO;
using System.Text;

namespace AgentLimits;

/// <summary>
/// A simple file log: one file per day under %LOCALAPPDATA%\AgentLimits\logs,
/// anything older than RetentionDays is deleted on startup. Needed to figure
/// out "why did this row go blank" after the fact, instead of having to catch
/// it live.
/// </summary>
public static class Log
{
    public const int RetentionDays = 3;

    private static readonly object Gate = new();
    private static string LogDir => Path.Combine(AppConfig.Dir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>Deletes logs older than RetentionDays. Call on startup.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogDir, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* cleanup must never crash the app */ }
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch { /* logging is never more important than the app running */ }
    }
}
