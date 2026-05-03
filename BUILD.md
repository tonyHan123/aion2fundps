# Build Guide

## Development (Debug)

```powershell
dotnet build Aion2FunDps.App/Aion2FunDps.App.csproj
```

Then run from `Aion2FunDps.App/bin/Debug/net10.0-windows/Aion2FunDps.App.exe`. No
obfuscation, full debug symbols, all diagnostic logs enabled.

## Release for distribution

The release pipeline produces an obfuscated single-file exe ready to ship to
users. It involves three stages handled by `build-release.ps1`:

1. **`dotnet publish`** — multi-file Release with self-contained .NET 10
   runtime
2. **ConfuserEx 2** — name + control-flow + constant + resource obfuscation
   per `obfuscation.crproj`
3. **Single-file repack** — bundles obfuscated DLLs into one `.exe`

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

To produce a non-obfuscated release build (e.g., debugging an obfuscation
regression):

```powershell
.\build-release.ps1 -SkipObfuscation
```

### Output

`Aion2FunDps.App\bin\Release\net10.0-windows\win-x64\publish\Aion2FunDps.App.exe`
— this single file is what you distribute. ~120 MB (includes .NET 10 runtime).

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
