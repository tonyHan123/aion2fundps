#requires -Version 5.1

<#
.SYNOPSIS
  Produces the diagnostic single-file debug exe at a stable path.

.DESCRIPTION
  Mirrors build-release.ps1's "single canonical output location" pattern
  but for the Debug configuration. Why a script and not raw `dotnet
  publish`: the publish output lives at
    Aion2FunDps.App\bin\Debug\net10.0-windows\win-x64\publish\Aion2FunDps.App.exe
  — a deep, version-specific path the user can't easily pin. Past test
  cycles forgot to sync that file to a stable location and the user
  ended up running a stale exe (logs showed v0.1.3 code while the
  source had v0.1.4 changes — diagnosed by inspecting log message
  format strings).

  Always run this before asking the user to retest. The output lives at
    bin\debug-diagnostic\Aion2FunDps.App.exe
  — same path every time, so the user can have a stable shortcut.

  Debug builds enable DEBUG #if blocks: full per-packet diagnostic logs
  (nick-debug, mobspawn-debug, encounter-debug, livestatus-debug,
  bulk-debug, proxy-debug, reset-debug, roster-debug). These are
  required to triage cold-start / boss-detection / proxy state machine
  bugs from end-user test reports. Release builds gate those off for
  capture hot-path perf.

.NOTES
  Run from repo root. No obfuscation step (debug binaries are
  internal-only — never distributed). Native engine .vcxproj is still
  built so the dispatcher seam can wire it up if UseNativeEngine = true
  in settings.
#>
param(
    [string]$MsBuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$appProj = Join-Path $repoRoot "Aion2FunDps.App\Aion2FunDps.App.csproj"
$enginePrj = Join-Path $repoRoot "Aion2FunDps.Engine\Aion2FunDps.Engine.vcxproj"

Write-Host "==> Stage 0: Build native engine (C++ DLL, Debug)" -ForegroundColor Cyan
# Engine MUST build under Configuration=Debug so the App.csproj Content
# include at "Aion2FunDps.Engine\bin\$(Configuration)\Aion2FunDps.Engine.dll"
# resolves under the Debug publish — build-release.ps1 uses Release for
# the engine because the .NET publish there is also Release. Mismatching
# configs silently drops the native DLL from the single-file bundle and
# the meter crashes on first dispatch when UseNativeEngine=true.
if (-not (Test-Path $MsBuild)) {
    throw "MSBuild not found at: $MsBuild`n" +
          "Pass -MsBuild <path> if your VS install lives elsewhere."
}
& $MsBuild $enginePrj /p:Configuration=Debug /p:Platform=x64 /m /v:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native engine build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Stage 1: dotnet publish (Debug single-file)" -ForegroundColor Cyan
# IncludeAllContentForSelfExtract=true bundles mobs.json / skills.json /
# dungeons.json / class icons / native engine DLL into the single-file exe.
# Without this flag the published .exe is missing Data\ content at runtime
# and crashes on startup loading mobs.json (App.xaml.cs:160). build-release.ps1
# sets this in stage 3; we set it here too so debug builds aren't degraded.
& dotnet publish $appProj `
    -c Debug `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:DebugType=embedded `
    | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$publishExe = Join-Path $repoRoot "Aion2FunDps.App\bin\Debug\net10.0-windows\win-x64\publish\Aion2FunDps.App.exe"
if (-not (Test-Path $publishExe)) {
    throw "Publish output missing at: $publishExe"
}

Write-Host ""
Write-Host "==> Stage 2: Sync to bin\debug-diagnostic\" -ForegroundColor Cyan
$distDir = Join-Path $repoRoot "bin\debug-diagnostic"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}
$dstExe = Join-Path $distDir "Aion2FunDps.App.exe"
Copy-Item $publishExe $dstExe -Force

$size = [math]::Round((Get-Item $dstExe).Length / 1MB, 1)
$hash = (Get-FileHash $dstExe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "==> SUCCESS" -ForegroundColor Green
Write-Host "   $dstExe  ($size MB)"
Write-Host "   SHA256: $hash"
Write-Host ""
Write-Host "   Run from: bin\debug-diagnostic\Aion2FunDps.App.exe"
