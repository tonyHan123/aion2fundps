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
    [switch]$SkipObfuscation
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$appProj = Join-Path $repoRoot "Aion2FunDps.App\Aion2FunDps.App.csproj"

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

Write-Host ""
if (Test-Path $finalExe) {
    $size = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)
    Write-Host "==> SUCCESS" -ForegroundColor Green
    Write-Host "   Output: $finalExe ($size MB)"
    Write-Host "   Bundle this exe + THIRD_PARTY_NOTICES.txt for distribution."
} else {
    Write-Host "==> Final exe not found at expected path: $finalExe" -ForegroundColor Red
    Write-Host "   Check publish output above for clues."
}
