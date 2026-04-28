using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// File download helper with SHA-1 verification (Mojang/Forge use SHA-1 in manifests)
/// and "skip if already correct" semantics so re-launches don't re-fetch.
/// </summary>
public static class Downloader
{
    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="dest"/>.
    /// If <paramref name="expectedSha1"/> is supplied and the local file already matches, skip.
    /// </summary>
    /// <returns>true if a download actually happened, false if the file was already current.</returns>
    public static async Task<bool> DownloadAsync(
        string url,
        string dest,
        string? expectedSha1 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (File.Exists(dest) && expectedSha1 != null)
        {
            var existingSha = await ComputeSha1Async(dest, ct).ConfigureAwait(false);
            if (string.Equals(existingSha, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug($"skip (sha1 match): {dest}");
                return false;
            }
            Logger.Info($"sha1 mismatch, redownloading: {dest}");
        }

        Logger.Info($"GET {url}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = dest + ".part";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    progress?.Report(new DownloadProgress(read, total));
                }
            }

            if (expectedSha1 != null)
            {
                var actual = await ComputeSha1Async(tmp, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new IOException($"SHA-1 mismatch for {url} (expected {expectedSha1}, got {actual})");
                }
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>GET a URL as a string (for small JSON manifests).</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        Logger.Debug($"GET (string) {url}");
        using var resp = await Http.Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string> ComputeSha1Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        using var sha1 = SHA1.Create();
        var bytes = await sha1.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public readonly record struct DownloadProgress(long BytesRead, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesRead / TotalBytes : 0;
}
