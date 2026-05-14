#requires -Version 5.1

<#
.SYNOPSIS
  Produces the obfuscated, single-file alpha release exe.

.DESCRIPTION
  Three-stage pipeline so the obfuscation step sees individual DLLs
  (single-file publish embeds them as a zip, which ConfuserEx can't open):

    1. dotnet publish multi-file Release  → bin\Release\publish\
    2. ConfuserEx 2 obfuscates each DLL   → bin\obfuscated\
    3. Repack as single-file               → bin\release-final\Aion2Fun.exe

  Configuration lives in obfuscation.crproj (rules per module, preserving
  WPF binding targets, JSON-serialized settings, etc.).

.PARAMETER ConfuserCli
  Path to Confuser.CLI.exe. ConfuserEx 2 is not on NuGet; download the
  latest release from https://github.com/mkaring/ConfuserEx/releases and
  point this at the extracted Confuser.CLI.exe.

.PARAMETER MsBuild
  Path to MSBuild.exe (used to build the C++ engine .vcxproj).
  VS 2026 Community ships at C:\Program Files\Microsoft Visual Studio\18\
  Community\MSBuild\Current\Bin\MSBuild.exe — adjust if you have a
  different VS edition.

.PARAMETER SkipObfuscation
  Skip the obfuscation step — useful for dry-run packaging tests before
  a real release. The output will be the same as a vanilla publish.

.NOTES
  Run from the repo root (where this file lives) in PowerShell. Requires
  .NET 10 SDK on PATH and SharpPcap-compatible Npcap on the build host
  (only because the test build links against it; not redistributed).
#>
param(
    [string]$ConfuserCli = "C:\Tools\ConfuserEx\Confuser.CLI.exe",
    [string]$MsBuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    [switch]$SkipObfuscation
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$appProj = Join-Path $repoRoot "Aion2FunDps.App\Aion2FunDps.App.csproj"
$enginePrj = Join-Path $repoRoot "Aion2FunDps.Engine\Aion2FunDps.Engine.vcxproj"

Write-Host "==> Stage 0: Build native engine (C++ DLL)" -ForegroundColor Cyan
# The dotnet publish stage below copies Aion2FunDps.Engine.dll alongside
# the .NET output via App.csproj's <None> include. That copy is
# conditioned on the DLL existing, so we must build the .vcxproj FIRST
# in matching Release|x64 config — otherwise the publish silently skips
# the copy and the resulting bundle has no native engine.
if (-not (Test-Path $MsBuild)) {
    throw "MSBuild not found at: $MsBuild`n" +
          "Pass -MsBuild <path> if your VS install lives elsewhere."
}
& $MsBuild $enginePrj /p:Configuration=Release /p:Platform=x64 /m /v:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native engine build failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Stage 1: dotnet publish (multi-file Release)" -ForegroundColor Cyan
& dotnet publish $appProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

if (-not $SkipObfuscation) {
    Write-Host ""
    Write-Host "==> Stage 2: ConfuserEx 2 obfuscation" -ForegroundColor Cyan
    if (-not (Test-Path $ConfuserCli)) {
        throw "ConfuserEx CLI not found at: $ConfuserCli`n" +
              "Download from https://github.com/mkaring/ConfuserEx/releases " +
              "and either place it at that path or pass -ConfuserCli <path>."
    }
    $crproj = Join-Path $repoRoot "obfuscation.crproj"
    & $ConfuserCli -n $crproj | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ConfuserEx failed (exit $LASTEXITCODE)" }
} else {
    Write-Host ""
    Write-Host "==> Stage 2: SKIPPED (--SkipObfuscation)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> Stage 3: Repack single-file" -ForegroundColor Cyan
# Re-publish with single-file using the obfuscated DLLs as input. The
# trick: copy obfuscated DLLs over Release\ outputs, then publish with
# PublishSingleFile=true; dotnet picks up the modified DLLs and bundles
# them. This avoids needing a separate single-file packaging tool.
if (-not $SkipObfuscation) {
    $obfDir = Join-Path $repoRoot "bin\obfuscated"
    if (-not (Test-Path $obfDir)) {
        throw "Obfuscation output dir not found: $obfDir"
    }
    # Mirror obfuscated DLLs back into the projects' bin\Release\ before
    # the single-file repack. ConfuserEx outputs a flat directory; we
    # need to map back to the per-project bin layout for dotnet to pick
    # them up.
    Get-ChildItem -Path $obfDir -Filter *.dll | ForEach-Object {
        $name = $_.Name
        switch -wildcard ($name) {
            'Aion2FunDps.Core.dll'      { Copy-Item $_.FullName "Aion2FunDps.Core\bin\Release\net10.0\$name" -Force }
            'Aion2FunDps.Protocol.dll'  { Copy-Item $_.FullName "Aion2FunDps.Protocol\bin\Release\net10.0\$name" -Force }
            'Aion2FunDps.Capture.dll'   { Copy-Item $_.FullName "Aion2FunDps.Capture\bin\Release\net10.0\$name" -Force }
            'Aion2FunDps.Storage.dll'   { Copy-Item $_.FullName "Aion2FunDps.Storage\bin\Release\net10.0\$name" -Force }
            'Aion2FunDps.UI.dll'        { Copy-Item $_.FullName "Aion2FunDps.UI\bin\Release\net10.0-windows\$name" -Force }
            'Aion2FunDps.App.dll'       { Copy-Item $_.FullName "Aion2FunDps.App\bin\Release\net10.0-windows\$name" -Force }
        }
    }
}

# Single-file repack — uses the (now-obfuscated) DLLs as inputs.
& dotnet publish $appProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    --no-build `
    | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Single-file repack failed (exit $LASTEXITCODE)" }

$publishDir = "Aion2FunDps.App\bin\Release\net10.0-windows\win-x64\publish"
$finalExe = Join-Path $publishDir "Aion2FunDps.App.exe"

if (-not (Test-Path $finalExe)) {
    Write-Host ""
    Write-Host "==> Final exe not found at expected path: $finalExe" -ForegroundColor Red
    Write-Host "   Check publish output above for clues."
    exit 1
}

Write-Host ""
Write-Host "==> Stage 4: Assemble distribution folder (bin\release-final)" -ForegroundColor Cyan
# The publish folder contains the final exe but also .pdb sidecars and any
# stale companion files the SDK leaves behind. Uploading that whole folder
# would leak debugging symbols and confuse users. Mirror only the user-facing
# assets into bin\release-final\ so the release upload step has an obvious
# single source for "what goes into the GitHub Release".
$distDir = "bin\release-final"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir | Out-Null

Copy-Item $finalExe (Join-Path $distDir "Aion2FunDps.App.exe") -Force

$notice = "THIRD_PARTY_NOTICES.txt"
if (Test-Path $notice) {
    Copy-Item $notice (Join-Path $distDir $notice) -Force
} else {
    Write-Host "   WARNING: THIRD_PARTY_NOTICES.txt not found at repo root" -ForegroundColor Yellow
}

$size = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)
$hash = (Get-FileHash $finalExe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "==> SUCCESS" -ForegroundColor Green
Write-Host "   Distribution folder: $distDir"
Get-ChildItem $distDir | ForEach-Object {
    $kb = [math]::Round($_.Length / 1MB, 2)
    Write-Host "     $($_.Name)  ($kb MB)"
}
Write-Host ""
Write-Host "   SHA256: $hash"
Write-Host "   Upload everything in $distDir to the GitHub Release."
