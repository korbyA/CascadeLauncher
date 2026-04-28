using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Locates a Java 8 runtime, or downloads Adoptium Temurin 8 JRE if none is found.
/// Forge 1.8.9 / Minecraft 1.8.9 both refuse to launch on newer Java without
/// patching, so we always prefer Java 8.
/// </summary>
public sealed class JavaProvider
{
    private const string AdoptiumLatestJre8 =
        "https://api.adoptium.net/v3/binary/latest/8/ga/windows/x64/jre/hotspot/normal/eclipse";

    public async Task<string> EnsureJava8Async(string runtimeDir, IProgress<string>? status, CancellationToken ct)
    {
        // 1. Look for an already-provisioned local JRE under runtime/java/.
        var javaRoot = Path.Combine(runtimeDir, "java");
        if (TryFindLocalJavaw(javaRoot, out var existing))
        {
            Logger.Info($"using cached Java: {existing}");
            return existing;
        }

        // 2. Look at the system: JAVA_HOME and PATH. We *only* accept it if it's Java 8.
        if (TryFindSystemJava8(out var systemJava))
        {
            Logger.Info($"using system Java 8: {systemJava}");
            return systemJava;
        }

        // 3. Download Adoptium Temurin 8 JRE.
        Directory.CreateDirectory(javaRoot);
        var zipDest = Path.Combine(javaRoot, "temurin8.zip");
        status?.Report("Downloading Java 8 (Temurin)…");
        await Downloader.DownloadAsync(AdoptiumLatestJre8, zipDest, ct: ct).ConfigureAwait(false);

        status?.Report("Extracting Java…");
        ZipFile.ExtractToDirectory(zipDest, javaRoot, overwriteFiles: true);
        try { File.Delete(zipDest); } catch { }

        if (!TryFindLocalJavaw(javaRoot, out var fresh))
            throw new InvalidOperationException("Java extracted but javaw.exe not found.");
        Logger.Info($"installed Java 8: {fresh}");
        return fresh;
    }

    /// <summary>Walk runtime/java/* looking for bin/javaw.exe.</summary>
    private static bool TryFindLocalJavaw(string root, out string javaw)
    {
        javaw = "";
        if (!Directory.Exists(root)) return false;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var candidate = Path.Combine(dir, "bin", "javaw.exe");
            if (File.Exists(candidate)) { javaw = candidate; return true; }
        }
        return false;
    }

    private static bool TryFindSystemJava8(out string javaw)
    {
        javaw = "";
        // Try JAVA_HOME first.
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var candidate = Path.Combine(home, "bin", "javaw.exe");
            if (File.Exists(candidate) && IsJava8(candidate)) { javaw = candidate; return true; }
        }
        // Then walk PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim(), "javaw.exe");
                if (File.Exists(candidate) && IsJava8(candidate)) { javaw = candidate; return true; }
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Run `java -version` (the matching java.exe — javaw is silent) and check
    /// whether the version string starts with "1.8".
    /// </summary>
    private static bool IsJava8(string javawPath)
    {
        try
        {
            var javaExe = javawPath.Replace("javaw.exe", "java.exe");
            if (!File.Exists(javaExe)) return false;

            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
            // -version prints to stderr like:  java version "1.8.0_412"
            return stderr.Contains("\"1.8") || stderr.Contains(" 1.8.");
        }
        catch { return false; }
    }
}
