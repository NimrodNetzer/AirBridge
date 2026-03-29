using System.IO;
using System.Text;

namespace AirBridge.Core;

/// <summary>
/// Minimal append-only log writer for real-device debugging.
/// Writes timestamped lines to <c>%TEMP%\AirBridge\airbridge.log</c>.
/// Safe to call from any thread.
/// </summary>
public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "AirBridge", "airbridge.log");

    private static readonly object _lock = new();

    static AppLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        // Rotate: keep last ~500 KB so the file doesn't grow unbounded.
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 500_000)
                File.WriteAllText(LogPath, string.Empty);
        }
        catch { /* best-effort */ }

        Write("INFO", "===== AirBridge session started =====");
    }

    /// <summary>Writes a DEBUG-level entry.</summary>
    public static void Debug(string message) => Write("DEBUG", message);

    /// <summary>Writes an INFO-level entry.</summary>
    public static void Info(string message)  => Write("INFO ", message);

    /// <summary>Writes a WARN-level entry.</summary>
    public static void Warn(string message)  => Write("WARN ", message);

    /// <summary>Writes an ERROR-level entry, optionally including exception details.</summary>
    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");
        if (ex?.StackTrace is { } st)
            Write("     ", st);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* never throw from a logger */ }
        }
    }
}
