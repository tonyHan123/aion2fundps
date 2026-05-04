# Build Guide

## Prerequisites

- **.NET 10 SDK** — for the managed projects (Core, Protocol, UI, App, etc.)
- **Visual Studio 2026 with the C++ workload** — required for
  `Aion2FunDps.Engine.vcxproj` (the native packet parser DLL). The .vcxproj
  pins `<PlatformToolset>v145</PlatformToolset>`; install the VS 2026 C++
  build tools via the VS Installer if you don't already have them.
  - VS 2026 Community ships MSBuild at
    `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
- **Npcap (WinPcap-compatible mode)** — for runtime, not strictly for build,
  but the meter won't capture without it. https://npcap.com

## Development (Debug)

The native engine and the .NET projects build separately. Build the native
DLL first (or whenever the C++ sources change) — `Aion2FunDps.App.csproj`
copies `Aion2FunDps.Engine.dll` from `Aion2FunDps.Engine\bin\Debug\` to
the App's output directory via a conditional `<Content>` include, so a stale
or missing native DLL silently degrades to "DllNotFoundException at first
dispatch when UseNativeEngine=true".

```powershell
# Native engine (C++)
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    Aion2FunDps.Engine\Aion2FunDps.Engine.vcxproj `
    /p:Configuration=Debug /p:Platform=x64

# Managed app (.NET)
dotnet build Aion2FunDps.App\Aion2FunDps.App.csproj
```

Run from `Aion2FunDps.App\bin\Debug\net10.0-windows\Aion2FunDps.App.exe`.
No obfuscation, full debug symbols, all diagnostic logs enabled. The
default `App.UseNativeEngine = false` so the meter exercises the managed
parser path; flip the flag in `App.xaml.cs` to test native end-to-end.

### Regression-test the native engine

After C++ changes, run the side-by-side regression to confirm both engines
emit byte-for-byte equivalent event streams:

```powershell
dotnet run --project Aion2FunDps.NativeRegression -c Debug -- 60
```

The argument is capture-duration in seconds. Open the game first; lobby
browse + 1 boss fight covers all opcodes. Output is a per-type event count
diff and a `RESULT: PASS / FAIL` line.

## Release for distribution

The release pipeline produces an obfuscated single-file exe ready to ship
to users. `build-release.ps1` handles four stages:

0. **C++ engine build** — MSBuild on `Aion2FunDps.Engine.vcxproj` (Release|x64)
1. **`dotnet publish`** — multi-file Release with self-contained .NET 10
   runtime; App.csproj's Content include copies `Aion2FunDps.Engine.dll`
   alongside the managed output
2. **ConfuserEx 2** — name + control-flow + constant + resource obfuscation
   per `obfuscation.crproj` (the `Aion2FunDps.Protocol.NativeEngine`
   namespace is excluded from rename so DllImport entry-point names stay
   intact)
3. **Single-file repack** — bundles managed DLLs and the native engine DLL
   into one `.exe`. The .NET single-file extractor unpacks the native DLL
   to a per-user temp dir on first launch; LoadLibrary then finds it.

### One-time setup

ConfuserEx 2 isn't on NuGet. Download once and place anywhere stable:

1. https://github.com/mkaring/ConfuserEx/releases — get `ConfuserEx-CLI.zip`
   (latest)
2. Extract to e.g. `C:\Tools\ConfuserEx\` (any path is fine; defaults to that)

### Build

```powershell
# From repo root
.\build-release.ps1
```

If ConfuserEx is at a non-default location:

```powershell
.\build-release.ps1 -ConfuserCli "D:\my-tools\ConfuserEx\Confuser.CLI.exe"
```

If MSBuild lives somewhere other than the VS 2026 Community default:

```powershell
.\build-release.ps1 -MsBuild "D:\VS\Community\MSBuild\Current\Bin\MSBuild.exe"
```

To produce a non-obfuscated release build (e.g., debugging an obfuscation
regression):

```powershell
.\build-release.ps1 -SkipObfuscation
```

### Output

`Aion2FunDps.App\bin\Release\net10.0-windows\win-x64\publish\Aion2FunDps.App.exe`
— this single file is what you distribute. ~157 MB (includes .NET 10 runtime
+ native engine DLL).

Bundle alongside:

- `THIRD_PARTY_NOTICES.txt` (already auto-copied to publish dir by csproj)

## Why not simpler

- **`PublishSingleFile=true` from the start** doesn't work because ConfuserEx
  can't open the single-file bundle (it's a zip-with-header that .NET
  unpacks at runtime). We obfuscate the loose DLLs first, then repack.
- **Eazfuscator.NET** has a smoother NuGet-only flow but is paid ($399/yr).
  Using free ConfuserEx 2 in trade for one extra script.
- **Native AOT** is not yet supported for WPF in .NET 10
  (dotnet/wpf#11205, closed as duplicate of #3811 in late 2025).

## Sanity check the obfuscated build

Before shipping a fresh release exe, run it once and click through:

- Main meter window shows up
- Class icons render (proves WPF asset binding survived obfuscation)
- Open settings — theme switch works (proves DynamicResource lookups survived)
- Right-click a player row → SkillBreakdownWindow opens (proves
  reflection-by-name on the VM survived)
- Save settings, close, reopen — settings persist (proves AppSettings JSON
  round-trip survived)

If any of those fail, check `obfuscation.crproj`'s rename-exclusion rules —
the offending type/namespace probably needs to be added to a `rename
action="remove"` block.

### Native engine sanity check (when flipping `UseNativeEngine`)

If you're enabling `App.UseNativeEngine = true` for a release build,
also verify:

- `startup.log` (in `%LOCALAPPDATA%\Temp\.net\Aion2FunDps.App\<hash>\`)
  contains `Wiring DiagnosticLogger (engine=native)` — proves
  `Aion2Fun_Dispatcher_Create` P/Invoke fired and DllImport found
  the native DLL extracted from the single-file bundle
- No `DllNotFoundException` appears in startup.log
- Capture works (open game; meter populates with damage rows)
- Run `Aion2FunDps.NativeRegression` against a live capture; expect
  `RESULT: PASS`
