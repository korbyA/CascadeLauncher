using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Installs Forge 1.8.9 (build 11.15.1.2318) by parsing the official installer JAR
/// directly — same approach MultiMC/Prism/PolyMC use. We do NOT run the installer's
/// Swing UI; instead we read its <c>install_profile.json</c> + <c>version.json</c>,
/// extract the bundled forge universal jar, and download the additional libraries
/// it asks for.
///
/// The 1.8.9 build is hardcoded because that's what Cascade Client targets.
/// </summary>
public sealed class ForgeInstaller
{
    public const string ForgeBuild = "1.8.9-11.15.1.2318-1.8.9";
    public static string ForgeVersionId => $"{ForgeBuild.Split('-')[0]}-Forge{ForgeBuild}";

    private const string InstallerUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/" + ForgeBuild +
        "/forge-" + ForgeBuild + "-installer.jar";

    public sealed record ForgeFiles(
        string VersionJsonPath,
        string MainClass,
        string MinecraftArguments,
        IReadOnlyList<string> Libraries);

    public async Task<ForgeFiles> EnsureInstalledAsync(string runtimeDir, IProgress<string>? status, CancellationToken ct)
    {
        var versionDir = Path.Combine(runtimeDir, "versions", ForgeVersionId);
        var versionJsonPath = Path.Combine(versionDir, ForgeVersionId + ".json");

        var librariesRoot = Path.Combine(runtimeDir, "libraries");
        var installerCache = Path.Combine(runtimeDir, "cache", "forge");
        Directory.CreateDirectory(installerCache);
        var installerJar = Path.Combine(installerCache, $"forge-{ForgeBuild}-installer.jar");

        // 1. Cache the installer (only re-download if missing).
        if (!File.Exists(installerJar))
        {
            status?.Report("Downloading Forge installer…");
            await Downloader.DownloadAsync(InstallerUrl, installerJar, ct: ct).ConfigureAwait(false);
        }
        else
        {
            Logger.Debug($"forge installer cached: {installerJar}");
        }

        // 2. Extract install_profile.json + version.json + the universal jar.
        Directory.CreateDirectory(versionDir);
        using var zip = ZipFile.OpenRead(installerJar);

        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new InvalidOperationException("install_profile.json missing from Forge installer");
        using var profileStream = profileEntry.Open();
        using var profileDoc = await JsonDocument.ParseAsync(profileStream, cancellationToken: ct).ConfigureAwait(false);
        var profile = profileDoc.RootElement;

        // version_info / versionInfo lives in install_profile under "versionInfo" for 1.8.9-era installers.
        JsonElement versionInfo;
        if (profile.TryGetProperty("versionInfo", out var vi)) versionInfo = vi;
        else versionInfo = profile.GetProperty("install").GetProperty("version") /* newer */ ;

        // Persist version.json to disk (canonical .minecraft layout).
        if (!File.Exists(versionJsonPath))
        {
            await using var fs = File.Create(versionJsonPath);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            versionInfo.WriteTo(w);
        }

        // 3. Place the bundled forge universal jar where Forge expects.
        //    install_profile -> install.path is a Maven coord like "net.minecraftforge:forge:1.8.9-11.15.1.2318-1.8.9"
        //    install.filePath names the entry inside the installer jar.
        var install = profile.GetProperty("install");
        var forgeMaven = install.GetProperty("path").GetString()!;
        var bundledFile = install.GetProperty("filePath").GetString()!;
        var forgeJarDest = MavenToPath(librariesRoot, forgeMaven);
        Directory.CreateDirectory(Path.GetDirectoryName(forgeJarDest)!);
        if (!File.Exists(forgeJarDest))
        {
            var bundled = zip.GetEntry(bundledFile)
                ?? throw new InvalidOperationException($"{bundledFile} missing from Forge installer");
            using var src = bundled.Open();
            await using var dst = File.Create(forgeJarDest);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            Logger.Info($"placed forge jar at {forgeJarDest}");
        }

        // 4. Walk version.json libraries and fetch each from its declared maven (or Forge's mirror).
        var libs = new List<string> { forgeJarDest };
        var libArr = versionInfo.GetProperty("libraries");
        int total = libArr.GetArrayLength(), i = 0;
        foreach (var lib in libArr.EnumerateArray())
        {
            i++;
            var name = lib.GetProperty("name").GetString()!;
            // Skip the forge entry itself — already placed above.
            if (name == forgeMaven) continue;

            // "clientreq" defaults to true; only skip if explicitly false.
            if (lib.TryGetProperty("clientreq", out var cr) && cr.ValueKind == JsonValueKind.False) continue;

            var path = MavenToPath(librariesRoot, name);
            if (File.Exists(path)) { libs.Add(path); continue; }

            status?.Report($"Forge libs {i}/{total}…");
            string baseUrl = lib.TryGetProperty("url", out var urlEl)
                ? (urlEl.GetString() ?? "https://libraries.minecraft.net/")
                : "https://libraries.minecraft.net/";
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var url = baseUrl + MavenToRelative(name);
            try
            {
                await Downloader.DownloadAsync(url, path, ct: ct).ConfigureAwait(false);
                libs.Add(path);
            }
            catch (Exception ex)
            {
                // Some 1.8.9-era libs (e.g., legacy jline) may need a fallback to the Forge maven.
                Logger.Warn($"primary fetch failed for {name}: {ex.Message}; trying forge mirror");
                var forgeUrl = "https://maven.minecraftforge.net/" + MavenToRelative(name);
                await Downloader.DownloadAsync(forgeUrl, path, ct: ct).ConfigureAwait(false);
                libs.Add(path);
            }
        }

        var mainClass = versionInfo.GetProperty("mainClass").GetString()!;
        var args = versionInfo.TryGetProperty("minecraftArguments", out var mae) ? mae.GetString() ?? "" : "";
        return new ForgeFiles(versionJsonPath, mainClass, args, libs);
    }

    /// <summary>"group:artifact:version[:classifier]" -> libraries/group/artifact/version/artifact-version[-classifier].jar</summary>
    public static string MavenToPath(string librariesRoot, string maven)
        => Path.Combine(librariesRoot, MavenToRelative(maven).Replace('/', Path.DirectorySeparatorChar));

    public static string MavenToRelative(string maven)
    {
        var parts = maven.Split(':');
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        var ext = "jar";
        // Some maven coords include "@zip" etc. as a packaging hint.
        if (version.Contains('@'))
        {
            var split = version.Split('@');
            version = split[0];
            ext = split[1];
        }
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }
}
