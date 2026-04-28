# Cascade Launcher Alpha

> **Disclaimer.** Cascade Launcher is **not affiliated with, approved by, or
> endorsed by Mojang Studios or Microsoft Corporation**. *Minecraft* is a
> trademark of Mojang Studios. This is an independent, non-commercial,
> open-source community project.
>
> The launcher only authenticates **genuine Microsoft accounts that own
> Minecraft Java Edition**. There is no offline / cracked / unauthenticated
> code path — sign-in goes through Microsoft → Xbox Live → Minecraft Services
> (the official chain), and Mojang's `/minecraft/profile` endpoint is the
> sole arbiter of license verification: a 404 response there causes the
> launcher to refuse to start the game.

Open-source, non-commercial Minecraft Java Edition launcher for **Cascade Client**
(a personal Forge 1.8.9 client mod). Built in C# / WPF as a single self-contained
Windows `.exe`. Maintainer: korbyadams@gmail.com.

## What this app does with Microsoft / Mojang APIs

The launcher uses the standard, documented Microsoft → Xbox Live → Minecraft
Services chain to let users sign in with the Microsoft account they already
own Minecraft on, then launches the official Java client they purchased.

**Authentication endpoints called (and only these):**

| Step | Endpoint | Purpose |
|---|---|---|
| 1 | `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode` + `/token` | Microsoft device-code OAuth flow |
| 2 | `user.auth.xboxlive.com/user/authenticate` | Xbox Live token |
| 3 | `xsts.auth.xboxlive.com/xsts/authorize` | XSTS token (RelyingParty `rp://api.minecraftservices.com/`) |
| 4 | `api.minecraftservices.com/authentication/login_with_xbox` | Minecraft access token |
| 5 | `api.minecraftservices.com/minecraft/profile` | Read username + UUID |

**OAuth scopes requested:** `XboxLive.signin offline_access` — nothing else.

The Microsoft access token is exchanged for a Minecraft token and then
discarded. The Minecraft token is held in memory and persisted to a local
JSON file on disk (`runtime/auth/profile.json`) so the user doesn't have to
sign in on every launch. The MS refresh token is used silently on
subsequent launches via the standard `refresh_token` grant. No tokens are
sent to any third-party server. The launcher does not host an analytics
endpoint, telemetry sink, or remote configuration service.

The full implementation of the auth chain lives in
[Services/AuthService.cs](Services/AuthService.cs) — the file is ~280 lines
and intentionally short for review.

## What this app does NOT do

- Does **not** store or transmit Microsoft passwords (device-code flow; the
  user signs in on Microsoft's own page).
- Does **not** call any Microsoft Graph / Office / Azure Management endpoint.
- Does **not** call any Minecraft Services endpoint other than the two listed
  above.
- Does **not** modify the user's `%APPDATA%\.minecraft` directory; everything
  the launcher writes lives under its own `runtime/` folder next to the exe.
- Does **not** include analytics, telemetry, ad networks, or auto-updaters
  that phone home.
- Does **not** redistribute Minecraft assets, Forge binaries, or OptiFine —
  it downloads them at runtime from their official sources (Mojang's
  piston-meta, Forge's maven, the user's own GitHub repo).

## Launch sequence (non-auth)

When the user presses **Launch** the launcher will:

1. Sync the latest Cascade client jar + OptiFine jar from
   <https://github.com/korbyA/CosmeticsCascade/tree/main/launcher> into `runtime/mods/`,
   skipping anything whose git blob SHA hasn't changed.
2. Resolve Minecraft 1.8.9 from Mojang's `piston-meta` and pull `client.jar`,
   libraries, native DLLs, and asset objects into `runtime/`.
3. Install Forge 1.8.9 (build `11.15.1.2318`) by reading the official
   installer's `install_profile.json` + `version.json` directly (no Swing UI),
   placing the universal jar and resolving Forge's libraries.
4. Locate or download a Java 8 runtime (Adoptium Temurin 8 JRE).
5. Build the JVM command line and start Minecraft straight from `java.exe`,
   bypassing the official launcher.

After the first launch everything is cached. Subsequent launches verify only
SHA-1 / blob hashes and re-download nothing if the files are already correct.

## Folder layout (created on first launch)

```
runtime/
  mods/                 client + OptiFine jars
  versions/             Mojang + Forge version JSONs and client.jar
  libraries/            Minecraft + Forge libraries (maven layout)
  natives/<ver>/        extracted LWJGL / jinput DLLs
  assets/               Mojang asset objects (content-addressed)
  java/                 auto-provisioned Temurin 8 (only if no system Java 8)
  cache/forge/          cached Forge installer jar
  auth/
    profile.json        active Minecraft profile (token + uuid)
    client_id.txt       Azure AD client id (you must create this — see below)
logs/
  launcher.log          rolling launcher log (~1MB cap)
```

## Microsoft account login

Mojang account auth was retired, so login uses Microsoft's device-code OAuth
flow → Xbox Live → XSTS → Minecraft Services. **You must register your own
Azure AD app** and write its Application (client) ID to
`runtime/auth/client_id.txt` before online sign-in will work.

> **Microsoft policy change (2024+):** the "create an app outside a directory"
> shortcut was deprecated. Azure now refuses with:
> *"The ability to create applications outside of a directory has been deprecated.
> You may get a new directory by joining the M365 Developer Program or signing up for Azure."*
>
> You need a tenant. Pick one of:
>
> - **Free Azure account** at <https://azure.microsoft.com/free> — the app
>   registration itself costs nothing, you just need an Azure tenant to host it.
>   Card required for verification but no charges occur for app registration.
> - **Microsoft 365 Developer Program** at
>   <https://developer.microsoft.com/microsoft-365/dev-program> — gives you a
>   free dev tenant, no card required.

Then:

1. <https://entra.microsoft.com> → Applications → App registrations → **New registration**.
2. Name it anything ("Cascade Launcher").
3. Supported account types: **Personal Microsoft accounts only** (or "any org + personal").
4. Skip the redirect URI — we use device code flow.
5. After creating, open **Authentication** and toggle "Allow public client flows" → **Yes**.
6. Open **API permissions** → **Add a permission** → search "Xbox Live" →
   **Delegated** → check `XboxLive.signin`.
7. Copy the **Application (client) ID** from the overview page.
8. Either paste it into [Configuration.cs](Configuration.cs)
   (`EmbeddedAzureClientId`) and rebuild — every distributed copy of the exe
   will use it — **or** drop it into `runtime/auth/client_id.txt` for a single
   machine.
9. **Submit the client ID for Mojang review** at
   <https://aka.ms/mce-reviewappid>. This is mandatory: Mojang gates the
   `login_with_xbox` endpoint behind an allowlist and a brand-new Azure app
   will hit `403 Invalid app registration` until Mojang approves it
   (typically a few days to a couple weeks). The launcher detects this case
   and surfaces the same link in-app.

The launcher does not have an offline / unauthenticated launch path. Sign-in
through Microsoft is required and `/minecraft/profile` must return a valid
license before the game starts.

## Build

Requires **.NET 10 SDK** (verified with `10.0.201`).

```powershell
.\build.ps1                   # builds publish\CascadeLauncher.exe
.\build.ps1 -Clean            # nukes bin/obj/publish first
.\build.ps1 -Configuration Debug
```

Or by hand:

```powershell
dotnet publish CascadeLauncher.csproj `
  -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

The output `CascadeLauncher.exe` bundles the .NET 10 runtime — no install
required on the target machine.

## Architecture

```
CascadeLauncher/
├── App.xaml + App.xaml.cs               app bootstrap, logging, error handling
├── MainWindow.xaml + .cs                main UI (Launch button, progress, footer)
├── Theme/WoodyTheme.xaml                colors lifted from client's WoodyTheme.java
├── Views/LoginWindow.xaml + .cs         Microsoft device-code sign-in dialog
├── ViewModels/MainViewModel.cs          INotifyPropertyChanged glue
├── Services/
│   ├── Logger.cs                        rolling file logger -> /logs
│   ├── Http.cs                          shared HttpClient
│   ├── Downloader.cs                    HTTP GET + SHA-1 verify + skip-if-current
│   ├── GitHubService.cs                 lists + syncs CosmeticsCascade/launcher jars
│   ├── MojangService.cs                 vanilla 1.8.9 client/libs/assets
│   ├── NativeExtractor.cs               extracts LWJGL/jinput DLLs from native jars
│   ├── ForgeInstaller.cs                reads installer's install_profile.json
│   ├── JavaProvider.cs                  finds or auto-installs Adoptium Temurin 8
│   ├── AuthService.cs                   MS OAuth device flow + XBL/XSTS/MC chain
│   ├── MinecraftLauncher.cs             builds the JVM command line, spawns javaw
│   └── LaunchOrchestrator.cs            orchestrates the full launch sequence
```

## Future-proofing hooks

The code already has the seams to extend, even though the features themselves are intentionally not implemented yet:

- **Self-update**: `App.xaml.cs` reads its version from the assembly. A future
  updater can sit in front of `OnStartup` and swap `CascadeLauncher.exe`
  before the WPF app starts (Squirrel-style).
- **Installer / MSI**: the publish output is a single relocatable `.exe`,
  so wrapping it in WiX or MSIX is purely a packaging step.
- **Account system**: `AuthService` already persists `MinecraftProfile` to
  `runtime/auth/profile.json`. Multi-account support is a matter of swapping
  that file for an indexed dictionary and surfacing a profile picker in the UI.

## Tradeoffs / engineering decisions

These are the non-obvious calls I made along the way; flag any you want
changed:

- **Forge install via installer-jar parsing**, not running Forge's installer
  Swing UI. Same approach MultiMC/Prism use; lets the launcher work
  unattended and avoids modifying `%APPDATA%\.minecraft`.
- **GitHub blob SHA used as the cache key** for mod jars, not a content
  SHA-1. The contents API doesn't expose a content hash, so we trust the git
  blob sha for change detection.
- **OptiFine** classification is name-based (`name.Contains("optifine")`).
  Drop OptiFine jars in the repo's `launcher/` folder with that token in the
  filename and they'll be picked up.
- **Stale mods are pruned** from `runtime/mods/` if they're no longer in the
  remote listing (only when the listing succeeded — a failed GitHub call
  preserves whatever's locally cached on the next online launch).
- **Java 8 only**. The system Java is only accepted if `java -version`
  reports `1.8`. Newer JDKs are ignored to avoid the silent ASM/LaunchWrapper
  failures Forge 1.8.9 hits on Java 9+.
- **JVM heap defaults to 4 GB** (`-Xmx4096M -Xms1024M`) with G1GC tuned for
  Minecraft (the same flags Lunar/MultiMC ship by default).
- **Window chrome is custom**. We use `WindowStyle=None` + a transparent
  border so the rounded corners + drop-shadow render correctly. Min/close
  buttons are reimplemented in XAML.
- **No NuGet dependencies**. The whole launcher uses only the BCL — easier
  to audit, smaller publish output.

## Logs

Everything goes to `logs/launcher.log`. Access tokens are redacted before
being logged.

## License

Personal project — all rights reserved unless otherwise specified.
