using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CascadeLauncher.Services;

/// <summary>
/// Single shared HttpClient. Disposing per-request causes socket exhaustion;
/// for a launcher with bursts of small downloads we want one long-lived client.
/// </summary>
public static class Http
{
    public const string UserAgent = "CascadeLauncher/0.1 (+https://github.com/korbyA/CosmeticsCascade)";

    public static HttpClient Client { get; } = Build();

    private static HttpClient Build()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return c;
    }
}
