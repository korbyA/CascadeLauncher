using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Builds the JVM command line for vanilla 1.8.9 + Forge and starts the game.
/// We bypass the official launcher entirely.
///
/// Output is captured to <c>logs/minecraft.log</c> so silent crashes are
/// debuggable. We use <c>java.exe</c> (not javaw) and CreateNoWindow=true so
/// stdout/stderr are available without a console window appearing.
/// </summary>
public sealed class MinecraftLauncher
{
    public sealed record LaunchSpec(
        string JavawPath,
        string GameDir,
        string AssetsRoot,
        string AssetIndexId,
        string NativesDir,
        IReadOnlyList<string> Classpath,
        string MainClass,
        string MinecraftArgsTemplate,
        MinecraftProfile Profile,
        int MaxMemoryMb = 4096,
        int MinMemoryMb = 1024);

    public sealed record LaunchResult(Process Process, string MinecraftLogPath);

    /// <summary>
    /// Spawn the game process. Waits up to ~3s and throws if the process exits
    /// early (catches "java errored out instantly" cases like missing libraries).
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(LaunchSpec spec, CancellationToken ct)
    {
        Directory.CreateDirectory(spec.GameDir);
        var logsDir = Path.Combine(App.LogsDir);
        Directory.CreateDirectory(logsDir);
        var mcLogPath = Path.Combine(logsDir, "minecraft.log");

        // Always run java.exe so we can read stdout/stderr; CreateNoWindow keeps it hidden.
        var javaExe = spec.JavawPath.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)
            ? spec.JavawPath[..^"javaw.exe".Length] + "java.exe"
            : spec.JavawPath;
        if (!File.Exists(javaExe)) javaExe = spec.JavawPath; // fall back to whatever was provided

        // Forge 1.8.9 expects a sane classpath order: forge libs first so they
        // shadow vanilla copies of the libraries Forge patched (ASM, Guava, etc.).
        var classpath = string.Join(';', spec.Classpath.Distinct());

        var args = new List<string>
        {
            $"-Xmx{spec.MaxMemoryMb}M",
            $"-Xms{spec.MinMemoryMb}M",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseG1GC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:MaxGCPauseMillis=50",
            "-XX:G1HeapRegionSize=32M",
            "-Dfml.ignoreInvalidMinecraftCertificates=true",
            "-Dfml.ignorePatchDiscrepancies=true",
            "-Djava.net.preferIPv4Stack=true",
            "-Dfile.encoding=UTF-8",
            $"-Djava.library.path={spec.NativesDir}",
            "-cp",
            classpath,
            spec.MainClass,
        };
        // Each minecraft argument is one token (already split here so paths with
        // spaces survive intact).
        args.AddRange(BuildMinecraftArgs(spec));

        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = spec.GameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // For human-readable launcher.log only — not what the OS sees, since we
        // pass via ArgumentList. Tokens redacted.
        var preview = RedactToken(string.Join(' ', args.Select(QuoteForLog)), spec.Profile.McAccessToken);
        Logger.Info($"launching java: {javaExe}");
        Logger.Info($"args: {preview}");

        // Truncate the per-launch minecraft.log.
        File.WriteAllText(mcLogPath, $"[launcher] {DateTime.Now:yyyy-MM-dd HH:mm:ss} starting {javaExe}{Environment.NewLine}");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrBuf = new StringBuilder();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            try { File.AppendAllText(mcLogPath, "[out] " + e.Data + Environment.NewLine); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            try { File.AppendAllText(mcLogPath, "[err] " + e.Data + Environment.NewLine); } catch { }
            // Keep recent stderr so we can surface it if Java exits early.
            lock (stderrBuf)
            {
                stderrBuf.AppendLine(e.Data);
                if (stderrBuf.Length > 8000) stderrBuf.Remove(0, stderrBuf.Length - 8000);
            }
        };

        if (!p.Start())
            throw new InvalidOperationException("Failed to start java.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Logger.Info($"java pid={p.Id}");

        // Watch for an instant exit. Most failure modes (bad classpath, missing
        // native dll, agent error) crash within the first second or two.
        using var earlyExit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await p.WaitForExitAsync(earlyExit.Token).ConfigureAwait(false);
            // Process exited inside the 3s window -> failure.
            string err;
            lock (stderrBuf) err = stderrBuf.ToString().Trim();
            if (string.IsNullOrEmpty(err))
                err = $"Java exited immediately with code {p.ExitCode}. See {mcLogPath} for details.";
            throw new InvalidOperationException(
                $"Minecraft failed to start (exit code {p.ExitCode}).{Environment.NewLine}{err}");
        }
        catch (OperationCanceledException)
        {
            // Process is still alive after the grace period — assume good.
        }

        return new LaunchResult(p, mcLogPath);
    }

    private static IEnumerable<string> BuildMinecraftArgs(LaunchSpec s)
    {
        // The template comes from version.json, e.g.:
        //   --username ${auth_player_name} --version ${version_name} --gameDir ${game_directory}
        //   --assetsDir ${assets_root} --assetIndex ${assets_index_name}
        //   --uuid ${auth_uuid} --accessToken ${auth_access_token}
        //   --userProperties ${user_properties} --userType ${user_type}
        //   --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker
        //
        // Tokens never contain spaces, so we can split-on-space FIRST, and only
        // then substitute. That keeps "C:\path with spaces\runtime" intact as a
        // single ArgumentList entry — Process knows how to quote it for us.
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = s.Profile.Username,
            ["${version_name}"] = ForgeInstaller.ForgeVersionId,
            ["${game_directory}"] = s.GameDir,
            ["${assets_root}"] = s.AssetsRoot,
            ["${game_assets}"] = s.AssetsRoot, // legacy alias for some 1.8.x args
            ["${assets_index_name}"] = s.AssetIndexId,
            ["${auth_uuid}"] = s.Profile.Uuid.Replace("-", ""),
            ["${auth_access_token}"] = s.Profile.McAccessToken,
            ["${auth_session}"] = "token:" + s.Profile.McAccessToken + ":" + s.Profile.Uuid.Replace("-", ""),
            ["${user_type}"] = s.Profile.UserType,
            ["${user_properties}"] = "{}",
            ["${version_type}"] = "release",
        };

        foreach (var raw in s.MinecraftArgsTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw;
            foreach (var kv in map) token = token.Replace(kv.Key, kv.Value);
            yield return token;
        }
    }

    private static string QuoteForLog(string v)
        => v.Contains(' ') || v.Contains('\t') ? $"\"{v}\"" : v;

    private static string RedactToken(string args, string token)
        => string.IsNullOrEmpty(token) ? args : args.Replace(token, "<redacted>");
}
