using System;
using System.IO;

namespace CascadeLauncher.Services;

/// <summary>
/// Tiny thread-safe file logger. Writes to /logs/launcher.log.
/// Console output is dropped — this is a windowed app and there's no console.
/// </summary>
public static class Logger
{
    private static readonly object _gate = new();
    private static string? _path;

    public static void Initialize(string path)
    {
        _path = path;
        try
        {
            // Roll the file at ~1MB so it doesn't grow forever.
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 1_000_000)
            {
                var bak = path + ".old";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg) => Write("WARN", msg, null);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", msg, ex);
    public static void Debug(string msg) => Write("DBG ", msg, null);

    private static void Write(string level, string msg, Exception? ex)
    {
        if (_path is null) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {msg}";
        if (ex != null) line += Environment.NewLine + ex;
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* swallow */ }
    }
}
