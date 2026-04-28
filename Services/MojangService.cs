using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Resolves vanilla Minecraft 1.8.9 from Mojang's piston-meta service:
///   1. version_manifest_v2.json -> URL of the 1.8.9 version JSON
///   2. version JSON -> client.jar, libraries[], assetIndex
///   3. assetIndex -> objects/&lt;hashprefix&gt;/&lt;hash&gt; under assets/objects
///
/// Each artifact carries a SHA-1 in the manifest, which the Downloader uses to
/// skip files that are already correct on disk.
/// </summary>
public sealed class MojangService
{
    public const string TargetVersion = "1.8.9";
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public sealed record VersionInfo(string Id, string Url, string Sha1);

    public async Task<VersionInfo> GetVersionInfoAsync(string version, CancellationToken ct)
    {
        var json = await Downloader.GetStringAsync(VersionManifestUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == version)
            {
                return new VersionInfo(
                    version,
                    v.GetProperty("url").GetString()!,
                    v.GetProperty("sha1").GetString()!);
            }
        }
        throw new InvalidOperationException($"Version {version} not in Mojang manifest");
    }

    /// <summary>
    /// Download (or skip) the version JSON itself. Returns the parsed JsonDocument.
    /// Caller owns the document and must dispose it.
    /// </summary>
    public async Task<JsonDocument> DownloadVersionJsonAsync(VersionInfo info, string runtimeDir, CancellationToken ct)
    {
        var dir = Path.Combine(runtimeDir, "versions", info.Id);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, info.Id + ".json");
        await Downloader.DownloadAsync(info.Url, dest, info.Sha1, ct: ct).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(dest, ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    /// <summary>
    /// Walk the version JSON and ensure all required artifacts (client jar,
    /// non-native + native libraries, asset index, asset objects) are on disk.
    /// </summary>
    public async Task<MinecraftFiles> EnsureVanillaAsync(
        string version,
        string runtimeDir,
        IProgress<string>? status,
        CancellationToken ct)
    {
        status?.Report($"Resolving Minecraft {version}…");
        var info = await GetVersionInfoAsync(version, ct).ConfigureAwait(false);
        using var verDoc = await DownloadVersionJsonAsync(info, runtimeDir, ct).ConfigureAwait(false);
        var root = verDoc.RootElement;

        // --- client.jar ---
        var client = root.GetProperty("downloads").GetProperty("client");
        var clientJar = Path.Combine(runtimeDir, "versions", version, version + ".jar");
        status?.Report("Downloading Minecraft client…");
        await Downloader.DownloadAsync(
            client.GetProperty("url").GetString()!,
            clientJar,
            client.GetProperty("sha1").GetString(),
            ct: ct).ConfigureAwait(false);

        // --- libraries (jars + natives) ---
        var libs = new List<string>();
        var nativesDir = Path.Combine(runtimeDir, "natives", version);
        Directory.CreateDirectory(nativesDir);

        var libArr = root.GetProperty("libraries");
        int total = libArr.GetArrayLength(), idx = 0;
        foreach (var lib in libArr.EnumerateArray())
        {
            idx++;
            if (!RuleMatches(lib)) continue;
            status?.Report($"Libraries {idx}/{total}…");

            // Standard artifact (e.g., guava, lwjgl jar)
            if (lib.TryGetProperty("downloads", out var dl)
                && dl.TryGetProperty("artifact", out var art))
            {
                var libPath = Path.Combine(runtimeDir, "libraries",
                    art.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
                await Downloader.DownloadAsync(
                    art.GetProperty("url").GetString()!,
                    libPath,
                    art.GetProperty("sha1").GetString(),
                    ct: ct).ConfigureAwait(false);
                libs.Add(libPath);
            }

            // Natives (lwjgl-platform, jinput-platform, etc.) — extract into natives/<version>/
            if (lib.TryGetProperty("natives", out var natMap)
                && natMap.TryGetProperty("windows", out var winClassifierEl))
            {
                var classifier = winClassifierEl.GetString();
                if (classifier != null
                    && lib.GetProperty("downloads").TryGetProperty("classifiers", out var classifiers)
                    && classifiers.TryGetProperty(classifier, out var natArt))
                {
                    var natPath = Path.Combine(runtimeDir, "libraries",
                        natArt.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
                    await Downloader.DownloadAsync(
                        natArt.GetProperty("url").GetString()!,
                        natPath,
                        natArt.GetProperty("sha1").GetString(),
                        ct: ct).ConfigureAwait(false);
                    NativeExtractor.ExtractTo(natPath, nativesDir, lib);
                }
            }
        }

        // --- asset index + assets ---
        var assetIdx = root.GetProperty("assetIndex");
        var assetIdxId = assetIdx.GetProperty("id").GetString()!;
        var assetIdxPath = Path.Combine(runtimeDir, "assets", "indexes", assetIdxId + ".json");
        status?.Report("Downloading asset index…");
        await Downloader.DownloadAsync(
            assetIdx.GetProperty("url").GetString()!,
            assetIdxPath,
            assetIdx.GetProperty("sha1").GetString(),
            ct: ct).ConfigureAwait(false);

        await EnsureAssetObjectsAsync(assetIdxPath, runtimeDir, status, ct).ConfigureAwait(false);

        return new MinecraftFiles(
            ClientJar: clientJar,
            Libraries: libs,
            NativesDir: nativesDir,
            AssetIndexId: assetIdxId,
            AssetsRoot: Path.Combine(runtimeDir, "assets"),
            MainClass: root.GetProperty("mainClass").GetString()!,
            MinecraftArguments: root.TryGetProperty("minecraftArguments", out var ma) ? ma.GetString() ?? "" : "");
    }

    /// <summary>
    /// Mojang asset objects are content-addressed: stored at assets/objects/&lt;XX&gt;/&lt;HASH&gt;
    /// </summary>
    private static async Task EnsureAssetObjectsAsync(string indexPath, string runtimeDir, IProgress<string>? status, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(indexPath, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        var objects = doc.RootElement.GetProperty("objects");
        int total = 0; foreach (var _ in objects.EnumerateObject()) total++;
        int i = 0;
        foreach (var prop in objects.EnumerateObject())
        {
            i++;
            var hash = prop.Value.GetProperty("hash").GetString()!;
            var prefix = hash.Substring(0, 2);
            var url = $"https://resources.download.minecraft.net/{prefix}/{hash}";
            var dest = Path.Combine(runtimeDir, "assets", "objects", prefix, hash);
            if (i % 50 == 0) status?.Report($"Assets {i}/{total}…");
            await Downloader.DownloadAsync(url, dest, hash, ct: ct).ConfigureAwait(false);
        }
        status?.Report($"Assets {total}/{total}.");
    }

    /// <summary>
    /// Mojang library entries can carry "rules" gating them by OS. For 1.8.9 the
    /// supported rules are simple allow/disallow with osName=windows/linux/osx.
    /// </summary>
    private static bool RuleMatches(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules)) return true;
        bool allow = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.GetProperty("action").GetString();
            bool osMatch = true;
            if (rule.TryGetProperty("os", out var os) && os.TryGetProperty("name", out var name))
                osMatch = name.GetString() == "windows";
            if (osMatch) allow = action == "allow";
        }
        return allow;
    }
}

public sealed record MinecraftFiles(
    string ClientJar,
    IReadOnlyList<string> Libraries,
    string NativesDir,
    string AssetIndexId,
    string AssetsRoot,
    string MainClass,
    string MinecraftArguments);
