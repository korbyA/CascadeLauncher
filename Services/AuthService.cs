using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CascadeLauncher.Services;

/// <summary>
/// Microsoft account login for Minecraft. Mojang accounts have been retired,
/// so we go through the full chain:
///
///   1. Microsoft OAuth (device code flow — no embedded browser, no redirect listener)
///   2. Xbox Live (XBL) auth
///   3. XSTS token
///   4. Minecraft Services login -&gt; Minecraft access token
///   5. Minecraft profile -&gt; UUID + username
///
/// The MS access token is refreshed silently on subsequent launches using the
/// stored refresh_token. Refresh failure falls back to a fresh device-code login.
///
/// SETUP REQUIRED: Register a public-client/native Azure AD app at
///   https://portal.azure.com -&gt; Microsoft Entra ID -&gt; App registrations
/// with redirect type "Public client/native" and the "XboxLive.signin" delegated
/// permission. Drop the Application (client) ID into runtime/auth/client_id.txt
/// (one-time, per-distribution). Tenant is "consumers".
/// </summary>
public sealed class AuthService
{
    // Azure AD application (client) ID. Override at runtime via <runtimeDir>/auth/client_id.txt.
    // Cannot be hardcoded universally — each launcher distribution registers its own.
    private const string DefaultClientIdPlaceholder = "PUT-YOUR-AZURE-CLIENT-ID-HERE";

    private readonly string _runtimeDir;
    private readonly string _authDir;
    private readonly string _profilePath;

    public AuthService(string runtimeDir)
    {
        _runtimeDir = runtimeDir;
        _authDir = Path.Combine(runtimeDir, "auth");
        Directory.CreateDirectory(_authDir);
        _profilePath = Path.Combine(_authDir, "profile.json");
    }

    private string ClientIdPath => Path.Combine(_authDir, "client_id.txt");

    /// <summary>True if a Microsoft client ID is available (embedded or per-machine override).</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ReadFileOverride()) || IsRealGuid(Configuration.EmbeddedAzureClientId);

    /// <summary>Path of the client_id.txt file, surfaced for UI hints.</summary>
    public string ClientIdConfigPath => ClientIdPath;

    /// <summary>Persist a Microsoft client ID override. Validates that it looks like a GUID.</summary>
    public void SetClientId(string clientId)
    {
        var trimmed = (clientId ?? "").Trim();
        if (!Guid.TryParse(trimmed, out _))
            throw new ArgumentException("Client ID must be a GUID like 00000000-0000-0000-0000-000000000000.");
        File.WriteAllText(ClientIdPath, trimmed);
    }

    private string? ReadFileOverride()
    {
        try
        {
            if (File.Exists(ClientIdPath))
            {
                var v = File.ReadAllText(ClientIdPath).Trim();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Resolves the active client ID. Priority:
    ///   1. <c>runtime/auth/client_id.txt</c> (per-machine override)
    ///   2. <see cref="Configuration.EmbeddedAzureClientId"/> (compiled into the exe)
    ///   3. placeholder (login dialog will prompt)
    /// </summary>
    private string ClientId
    {
        get
        {
            var fileOverride = ReadFileOverride();
            if (!string.IsNullOrWhiteSpace(fileOverride)) return fileOverride;
            if (IsRealGuid(Configuration.EmbeddedAzureClientId)) return Configuration.EmbeddedAzureClientId;
            return DefaultClientIdPlaceholder;
        }
    }

    private static bool IsRealGuid(string s)
        => Guid.TryParse(s, out var g) && g != Guid.Empty;

    /// <summary>Loads the persisted Microsoft profile, if any. Returns null if no prior sign-in.</summary>
    public MinecraftProfile? LoadStoredProfile()
    {
        if (!File.Exists(_profilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MinecraftProfile>(File.ReadAllText(_profilePath));
        }
        catch { return null; }
    }

    public void SaveProfile(MinecraftProfile p) =>
        File.WriteAllText(_profilePath, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));

    public void ClearProfile()
    {
        try { if (File.Exists(_profilePath)) File.Delete(_profilePath); } catch { }
    }

    /// <summary>
    /// Refresh the Minecraft access token using the saved MS refresh_token.
    /// Returns the same profile with a fresh MC token, or null on failure.
    /// </summary>
    public async Task<MinecraftProfile?> TryRefreshAsync(MinecraftProfile p, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.MsRefreshToken)) return null;
        if (ClientId == DefaultClientIdPlaceholder) return null;
        try
        {
            var msToken = await RefreshMsTokenAsync(p.MsRefreshToken, ct).ConfigureAwait(false);
            return await CompleteFromMsTokenAsync(msToken.AccessToken, msToken.RefreshToken ?? p.MsRefreshToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"token refresh failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Begin a device-code login. Returns the user-visible code/URL the UI should display
    /// while we poll Microsoft in the background. Resolves to a Minecraft profile when
    /// the user finishes signing in (or throws on timeout/denial).
    /// </summary>
    public async Task<DeviceCodeStart> StartDeviceLoginAsync(CancellationToken ct)
    {
        if (ClientId == DefaultClientIdPlaceholder)
            throw new InvalidOperationException(
                "Microsoft login is not configured.\n\n" +
                "Microsoft requires an Azure tenant to register apps. Either:\n" +
                "  1. Sign up for a free Azure account at https://azure.microsoft.com/free, or\n" +
                "  2. Join the Microsoft 365 Developer Program at https://developer.microsoft.com/microsoft-365/dev-program\n\n" +
                "Then register a public-client/native app, enable 'Allow public client flows',\n" +
                "and paste the Application (client) ID into:\n" +
                $"  {Path.Combine(_authDir, "client_id.txt")}");

        // Step 1: ask Microsoft for a device code.
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "XboxLive.signin offline_access",
        });
        var resp = await Http.Client.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            content, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"devicecode failed: {json}");
        var dc = JsonSerializer.Deserialize<DeviceCodeResponse>(json)!;
        return new DeviceCodeStart(dc.UserCode, dc.VerificationUri, dc.DeviceCode, dc.Interval, dc.ExpiresIn);
    }

    /// <summary>Poll the token endpoint until the user finishes the device-code login.</summary>
    public async Task<MinecraftProfile> CompleteDeviceLoginAsync(DeviceCodeStart start, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, start.IntervalSeconds));

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct).ConfigureAwait(false);

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId,
                ["device_code"] = start.DeviceCode,
            });
            var resp = await Http.Client.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var ok = JsonSerializer.Deserialize<MsTokenResponse>(body)!;
                return await CompleteFromMsTokenAsync(ok.AccessToken, ok.RefreshToken ?? "", ct).ConfigureAwait(false);
            }

            // Microsoft returns the OAuth error contract as JSON, but a raw 400
            // with HTML/empty body is also possible (intermediary, throttling, etc.).
            string? err = null, errDesc = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString();
                if (doc.RootElement.TryGetProperty("error_description", out var d)) errDesc = d.GetString();
            }
            catch
            {
                Logger.Warn($"non-JSON {(int)resp.StatusCode} from token endpoint: {body}");
            }

            switch (err)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                case "authorization_declined":
                    throw new InvalidOperationException("Login cancelled or expired.");
                case "invalid_client":
                    throw new InvalidOperationException(
                        "Microsoft rejected the client ID.\n\n" +
                        "Check the Application (client) ID is correct, and that 'Allow public client flows' is set to Yes in Azure → App registration → Authentication.");
                case "invalid_grant":
                case "invalid_request":
                    throw new InvalidOperationException(
                        $"Microsoft rejected the sign-in: {err}\n\n" +
                        (errDesc ?? "Most often this means the Azure app's account-type setting doesn't match the account you signed in with, or 'Allow public client flows' is off."));
                case "unauthorized_client":
                    throw new InvalidOperationException(
                        "Azure app is not authorized for the device-code flow.\n" +
                        "Open Azure → App registration → Authentication and turn on 'Allow public client flows'.");
                default:
                    var detail = errDesc ?? err ?? body;
                    if (string.IsNullOrWhiteSpace(detail)) detail = $"HTTP {(int)resp.StatusCode}";
                    Logger.Warn($"token endpoint failure: status={(int)resp.StatusCode} body={body}");
                    throw new InvalidOperationException($"Login failed: {detail}");
            }
        }
        throw new TimeoutException("Device-code login timed out.");
    }

    // ---- internals ----

    private async Task<MsTokenResponse> RefreshMsTokenAsync(string refreshToken, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "XboxLive.signin offline_access",
        });
        var resp = await Http.Client.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MsTokenResponse>(body)!;
    }

    /// <summary>
    /// JSON options for Xbox Live / XSTS. The PascalCase property names
    /// matter — XBL silently rejects camelCase with an empty 400 body.
    /// HttpClient.PostAsJsonAsync defaults to camelCase, so we serialize by
    /// hand and ship as raw StringContent.
    /// </summary>
    private static readonly JsonSerializerOptions XboxJsonOptions = new()
    {
        PropertyNamingPolicy = null, // preserve PascalCase
    };

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value, XboxJsonOptions),
            Encoding.UTF8, "application/json");

    private async Task<MinecraftProfile> CompleteFromMsTokenAsync(string msAccessToken, string msRefreshToken, CancellationToken ct)
    {
        // 2. Xbox Live auth.
        var xblBody = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msAccessToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        var xblResp = await Http.Client.PostAsync(
            "https://user.auth.xboxlive.com/user/authenticate", JsonBody(xblBody), ct).ConfigureAwait(false);
        var xblBodyText = await xblResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!xblResp.IsSuccessStatusCode)
            throw TranslateXboxError("Xbox Live", xblResp, xblBodyText);

        using var xblDoc = JsonDocument.Parse(xblBodyText);
        var xblToken = xblDoc.RootElement.GetProperty("Token").GetString()!;
        var uhs = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        // 3. XSTS — same JSON casing requirements as XBL.
        var xstsBody = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        var xstsResp = await Http.Client.PostAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize", JsonBody(xstsBody), ct).ConfigureAwait(false);
        var xstsBodyText = await xstsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!xstsResp.IsSuccessStatusCode)
            throw TranslateXboxError("XSTS", xstsResp, xstsBodyText);

        using var xstsDoc = JsonDocument.Parse(xstsBodyText);
        var xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString()!;

        // 4. Minecraft services. (camelCase here — Mojang's API actually wants it.)
        var mcBody = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        var mcResp = await Http.Client.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox", mcBody, ct).ConfigureAwait(false);
        if (!mcResp.IsSuccessStatusCode)
        {
            var mcErr = await mcResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.Warn($"Minecraft auth {(int)mcResp.StatusCode}: {mcErr}");

            // Mojang gates custom Azure apps behind an allowlist. The error
            // body literally contains the string "Invalid app registration"
            // when this is the cause, with a Microsoft URL pointing at the
            // self-service review form.
            if (mcErr.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "This Azure app hasn't been approved by Mojang yet.\n\n" +
                    "The OAuth chain works — Microsoft, Xbox Live, and XSTS all signed off — " +
                    "but Mojang's Minecraft Services API only accepts Azure apps that have been " +
                    "submitted for review.\n\n" +
                    "Submit your Application (client) ID here:\n" +
                    "  https://aka.ms/mce-reviewappid\n\n" +
                    "Approval typically takes a few days to a couple weeks.");
            }

            throw new InvalidOperationException($"Minecraft authentication failed (HTTP {(int)mcResp.StatusCode}). {mcErr}");
        }
        using var mcDoc = JsonDocument.Parse(await mcResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var mcAccess = mcDoc.RootElement.GetProperty("access_token").GetString()!;

        // 5. Profile (UUID + username). 404 here = the MS account doesn't own Minecraft.
        using var profReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccess);
        var profResp = await Http.Client.SendAsync(profReq, ct).ConfigureAwait(false);
        if (!profResp.IsSuccessStatusCode)
            throw new InvalidOperationException("This Microsoft account does not own Minecraft Java Edition.");
        using var profDoc = JsonDocument.Parse(await profResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var rawUuid = profDoc.RootElement.GetProperty("id").GetString()!; // 32 chars, no dashes
        var username = profDoc.RootElement.GetProperty("name").GetString()!;
        var uuid = FormatUuid(rawUuid);

        var profile = new MinecraftProfile
        {
            Username = username,
            Uuid = uuid,
            McAccessToken = mcAccess,
            UserType = "msa",
            MsRefreshToken = msRefreshToken,
        };
        SaveProfile(profile);
        return profile;
    }

    /// <summary>
    /// Converts an Xbox Live or XSTS error response to an actionable exception.
    /// Both endpoints return a JSON body with an XErr code — the codes below
    /// are documented at https://wiki.vg/Microsoft_Authentication_Scheme.
    /// </summary>
    private static InvalidOperationException TranslateXboxError(string stage, HttpResponseMessage resp, string body)
    {
        long xerr = 0;
        string? redirect = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var x))
            {
                if (x.ValueKind == JsonValueKind.Number) xerr = x.GetInt64();
                else if (x.ValueKind == JsonValueKind.String && long.TryParse(x.GetString(), out var v)) xerr = v;
            }
            if (doc.RootElement.TryGetProperty("Redirect", out var r)) redirect = r.GetString();
        }
        catch { /* not JSON */ }

        Logger.Warn($"{stage} {(int)resp.StatusCode}: XErr={xerr} body={body}");

        var detail = xerr switch
        {
            2148916227 => "This Microsoft account has been banned by Xbox.",
            2148916233 =>
                "This Microsoft account does not have an Xbox profile yet.\n\n" +
                "Sign in once at https://www.xbox.com (or open the Xbox app) with this account to create one, then try again.",
            2148916235 => "Xbox Live is not available in your country/region.",
            2148916236 or 2148916237 =>
                "This account needs adult verification on Xbox before it can sign in.",
            2148916238 =>
                "This is a child account. An adult must add it to a family group before it can use Xbox Live.\n" +
                "https://account.microsoft.com/family",
            2148916262 => "This account is signed up for Xbox but the profile setup wasn't finished. Open the Xbox app and complete it.",
            _ when xerr != 0 => $"Xbox Live rejected the sign-in (XErr {xerr}).\n{(redirect != null ? "Microsoft suggests: " + redirect : "")}",
            _ => $"{stage} returned HTTP {(int)resp.StatusCode}. Body: {(string.IsNullOrWhiteSpace(body) ? "<empty>" : body)}",
        };
        return new InvalidOperationException(detail);
    }

    private static string FormatUuid(string raw32)
    {
        if (raw32.Length != 32) return raw32;
        return $"{raw32[..8]}-{raw32.Substring(8,4)}-{raw32.Substring(12,4)}-{raw32.Substring(16,4)}-{raw32[20..]}";
    }
}

public sealed class MinecraftProfile
{
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string McAccessToken { get; set; } = "";
    public string UserType { get; set; } = "msa";
    public string MsRefreshToken { get; set; } = "";
}

public sealed record DeviceCodeStart(string UserCode, string VerificationUri, string DeviceCode, int IntervalSeconds, int ExpiresIn);

internal sealed class DeviceCodeResponse
{
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("interval")] public int Interval { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}

internal sealed class MsTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}
