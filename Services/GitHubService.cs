using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Pulls jars from https://github.com/korbyA/CosmeticsCascade/tree/main/launcher
/// using the GitHub Contents API. The repo is the source of truth for which
/// client + OptiFine jars should be installed.
///
/// API: https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}
/// Returns objects with name/path/size/sha (git blob sha)/download_url.
/// </summary>
public sealed class GitHubService
{
    private const string Owner = "korbyA";
    private const string Repo = "CosmeticsCascade";
    private const string Branch = "main";
    private const string Path = "launcher";

    public async Task<IReadOnlyList<RemoteJar>> ListJarsAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{Path}?ref={Branch}";
        Logger.Info($"GitHub list: {url}");
        var json = await Downloader.GetStringAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var jars = new List<RemoteJar>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();
            if (type != "file") continue;

            var name = el.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

            var sha = el.GetProperty("sha").GetString() ?? "";
            var size = el.GetProperty("size").GetInt64();
            var download = el.GetProperty("download_url").GetString() ?? "";

            jars.Add(new RemoteJar(name, download, sha, size, Classify(name)));
        }
        Logger.Info($"GitHub returned {jars.Count} jar(s)");
        return jars;
    }

    /// <summary>
    /// Heuristic file-name classifier. Anything mentioning OptiFine is OptiFine,
    /// anything mentioning cascade/client is the client mod, otherwise treat
    /// it as an extra mod to drop into /mods.
    /// </summary>
    private static JarKind Classify(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("optifine") || n.Contains("optifabric")) return JarKind.OptiFine;
        if (n.Contains("cascade") || n.Contains("client")) return JarKind.ClientMod;
        return JarKind.Extra;
    }

    /// <summary>
    /// Save a small JSON file alongside the jar to record what we downloaded.
    /// On the next launch we compare the recorded git-blob sha to the remote
    /// to decide whether we need to pull a fresh copy.
    /// </summary>
    public static async Task<bool> SyncJarAsync(RemoteJar jar, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var dest = System.IO.Path.Combine(destDir, jar.FileName);
        var marker = dest + ".gitsha";

        if (File.Exists(dest) && File.Exists(marker))
        {
            var prev = (await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)).Trim();
            if (prev == jar.GitSha)
            {
                Logger.Debug($"skip (git sha unchanged): {jar.FileName}");
                return false;
            }
        }

        // GitHub blob SHA != content SHA-1, so we can't pre-verify; download then trust.
        await Downloader.DownloadAsync(jar.DownloadUrl, dest, expectedSha1: null, ct: ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(marker, jar.GitSha, ct).ConfigureAwait(false);
        Logger.Info($"updated {jar.FileName} -> {jar.GitSha[..Math.Min(7, jar.GitSha.Length)]}");
        return true;
    }
}

public enum JarKind { ClientMod, OptiFine, Extra }

public sealed record RemoteJar(string FileName, string DownloadUrl, string GitSha, long Size, JarKind Kind);
