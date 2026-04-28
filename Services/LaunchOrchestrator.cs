using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// End-to-end "click Launch" flow. Each step reports a one-line status string
/// to <paramref name="status"/> so the UI can render progress.
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly string _runtimeDir;
    private readonly AuthService _auth;

    public LaunchOrchestrator(string runtimeDir, AuthService auth)
    {
        _runtimeDir = runtimeDir;
        _auth = auth;
    }

    public async Task LaunchAsync(MinecraftProfile profile, IProgress<string> status, CancellationToken ct)
    {
        // 0. Directory layout (idempotent).
        var modsDir = Path.Combine(_runtimeDir, "mods");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(Path.Combine(_runtimeDir, "versions"));
        Directory.CreateDirectory(Path.Combine(_runtimeDir, "libraries"));

        // 1. Sync the GitHub-hosted client + OptiFine jars into /runtime/mods.
        status.Report("Checking client mods…");
        var gh = new GitHubService();
        IReadOnlyList<RemoteJar> jars;
        try
        {
            jars = await gh.ListJarsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Don't fail the launch if GitHub is unreachable — fall back to whatever's
            // already in /mods so a transient outage doesn't block launching with the
            // last-known-good mod set.
            Logger.Warn($"GitHub list failed: {ex.Message}; using cached mods");
            jars = Array.Empty<RemoteJar>();
        }

        foreach (var jar in jars)
        {
            status.Report($"Mod: {jar.FileName}");
            await GitHubService.SyncJarAsync(jar, modsDir, ct).ConfigureAwait(false);
        }

        // Prune client mod jars that aren't on the remote anymore (only when we have a
        // valid listing — never prune blindly when GitHub failed).
        if (jars.Count > 0)
        {
            var keep = new HashSet<string>(jars.Select(j => j.FileName), StringComparer.OrdinalIgnoreCase);
            foreach (var existing in Directory.EnumerateFiles(modsDir, "*.jar"))
            {
                if (!keep.Contains(Path.GetFileName(existing)))
                {
                    Logger.Info($"removing stale mod: {existing}");
                    try { File.Delete(existing); File.Delete(existing + ".gitsha"); } catch { }
                }
            }
        }

        // 2. Vanilla Minecraft 1.8.9.
        var mojang = new MojangService();
        var vanilla = await mojang.EnsureVanillaAsync(MojangService.TargetVersion, _runtimeDir, status, ct).ConfigureAwait(false);

        // 3. Forge.
        status.Report("Setting up Forge 1.8.9…");
        var forge = await new ForgeInstaller().EnsureInstalledAsync(_runtimeDir, status, ct).ConfigureAwait(false);

        // 4. Java 8.
        var javaw = await new JavaProvider().EnsureJava8Async(_runtimeDir, status, ct).ConfigureAwait(false);

        // 5. Build classpath. Order matters: Forge libs first so they shadow vanilla copies
        //    of conflicting libraries (Forge 1.8.9 ships with patched ASM/Guava versions).
        var classpath = new List<string>();
        classpath.AddRange(forge.Libraries);
        classpath.AddRange(vanilla.Libraries);
        classpath.Add(vanilla.ClientJar);

        // 6. Launch.
        status.Report("Starting Minecraft…");
        var spec = new MinecraftLauncher.LaunchSpec(
            JavawPath: javaw,
            GameDir: _runtimeDir,
            AssetsRoot: vanilla.AssetsRoot,
            AssetIndexId: vanilla.AssetIndexId,
            NativesDir: vanilla.NativesDir,
            Classpath: classpath,
            MainClass: forge.MainClass,
            MinecraftArgsTemplate: string.IsNullOrWhiteSpace(forge.MinecraftArguments)
                ? vanilla.MinecraftArguments
                : forge.MinecraftArguments,
            Profile: profile);

        var result = await new MinecraftLauncher().LaunchAsync(spec, ct).ConfigureAwait(false);
        Logger.Info($"Minecraft pid={result.Process.Id}, log={result.MinecraftLogPath}");
        status.Report("Minecraft launched.");
    }
}
