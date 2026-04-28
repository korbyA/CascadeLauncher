# Cascade Launcher build script.
# Produces a self-contained, single-file .exe in publish/.
param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

if ($Clean) {
    if (Test-Path "bin") { Remove-Item -Recurse -Force "bin" }
    if (Test-Path "obj") { Remove-Item -Recurse -Force "obj" }
    if (Test-Path "publish") { Remove-Item -Recurse -Force "publish" }
}

Write-Host "Restoring..." -ForegroundColor Cyan
dotnet restore | Out-Host

Write-Host "Publishing single-file exe ($Rid, $Configuration)..." -ForegroundColor Cyan
dotnet publish CascadeLauncher.csproj `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o publish | Out-Host

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $root "publish\CascadeLauncher.exe"
if (Test-Path $exe) {
    Write-Host "OK: $exe" -ForegroundColor Green
} else {
    throw "expected exe not found at $exe"
}
