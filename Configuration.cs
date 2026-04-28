namespace CascadeLauncher;

/// <summary>
/// Build-time configuration baked into the exe.
///
/// Edit <see cref="EmbeddedAzureClientId"/> with your Azure app registration's
/// Application (client) ID and rebuild — every distributed copy of the launcher
/// will sign in against that app, no per-machine setup needed.
///
/// Public-client/native client IDs are NOT secrets; this is the same model
/// PrismLauncher/ATLauncher/etc. use. A user can still override the embedded
/// value by writing a different ID to <c>runtime/auth/client_id.txt</c>.
/// </summary>
public static class Configuration
{
    /// <summary>
    /// Microsoft Entra (Azure AD) Application (client) ID.
    /// Replace the placeholder GUID with the value from your Azure app's Overview page.
    /// Leave as-is to require per-machine setup via the in-app prompt.
    /// </summary>
    public const string EmbeddedAzureClientId = "0d6e9575-72c4-4572-8c9e-1b243157d5ba";
}
