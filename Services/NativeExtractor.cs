using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace CascadeLauncher.Services;

/// <summary>
/// Native libraries (LWJGL, jinput) ship as jars containing .dll/.so files.
/// Java needs them unpacked into a flat directory passed via -Djava.library.path.
/// </summary>
public static class NativeExtractor
{
    public static void ExtractTo(string nativeJar, string destDir, JsonElement libElement)
    {
        // Per Mojang format, "extract.exclude" can list paths to skip (e.g., META-INF/).
        var exclude = new List<string>();
        if (libElement.TryGetProperty("extract", out var ex)
            && ex.TryGetProperty("exclude", out var arr))
        {
            foreach (var s in arr.EnumerateArray())
            {
                var v = s.GetString();
                if (!string.IsNullOrEmpty(v)) exclude.Add(v);
            }
        }

        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(nativeJar);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dirs
            if (exclude.Any(p => entry.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            // Only DLLs are useful on Windows; ignore .so/.dylib if present.
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var dest = Path.Combine(destDir, entry.Name);
            if (File.Exists(dest)) continue; // assume already extracted; same source jar
            entry.ExtractToFile(dest, overwrite: false);
        }
    }
}
