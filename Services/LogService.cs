using System.Collections.Concurrent;
using System.IO;

namespace Kumo.Services;

public static class LogService
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Pending = new();

    public static event Action<string>? LineWritten;
    public static string LogFilePath { get; private set; } = string.Empty;
    public static string PortableLogFilePath { get; private set; } = string.Empty;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        LogFilePath = Path.Combine(directory, "launcher.log");
        PortableLogFilePath = Path.Combine(AppContext.BaseDirectory, "kumo-debug.log");
        try
        {
            File.WriteAllText(PortableLogFilePath, string.Empty);
        }
        catch
        {
            PortableLogFilePath = string.Empty;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.Message}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (Gate)
        {
            Pending.Enqueue(line);
            if (!string.IsNullOrWhiteSpace(LogFilePath))
            {
                try
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never interrupt a download or game launch.
                }
            }
            if (!string.IsNullOrWhiteSpace(PortableLogFilePath))
            {
                try
                {
                    File.AppendAllText(PortableLogFilePath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never interrupt a download or game launch.
                }
            }
        }
        LineWritten?.Invoke(line);
    }
}
