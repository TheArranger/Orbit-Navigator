# Reproducible and reviewable builds

Orbit Navigator's managed projects enable deterministic compilation, pin the
exact SDK in `global.json`, pin direct package versions centrally, and commit
NuGet lock files. The public build workflow restores in locked mode, builds
Release, runs the complete suite, and checks release metadata.

## Local build

On Windows 10 or 11 x64, from a clean checkout:

```powershell
.\scripts\Bootstrap-Toolchain.ps1
.\scripts\Build.ps1 -Configuration Release
.\scripts\Test.ps1 -Configuration Release
.\scripts\Test-OpenSourceReadiness.ps1
```

The New Tab asset builders additionally require Python and Pillow 11.3.0.
Runtime assets are checked in, so regenerating artwork is not required to
compile or test the application.

## Installer inputs

The offline installer additionally needs:

- the official x64 Microsoft Edge WebView2 Evergreen Standalone Installer;
- Inno Setup;
- the checked-in Orbit assets; and
- the exact version from `Versions/ProductVersion.props`.

`scripts/Test-PackagingPrerequisites.ps1` validates local prerequisites.
`scripts/Build-Setup.ps1` publishes the self-contained launcher and app, stages
the approved WebView2 prerequisite, verifies expected inputs, and compiles the
offline installer.

Managed assembly outputs are deterministic for identical inputs and toolchain.
A complete `Setup.exe` is not claimed to be byte-for-byte reproducible because
installer metadata and future RFC 3161 signing timestamps may vary. Official
release records must therefore bind the reviewed source commit, tool versions,
input hashes, unsigned payload hashes, signed payload hashes, installer hash,
and verification results.

## Signing boundary

The public build workflow does not possess signing authority. If the project is
accepted by SignPath Foundation, a separate origin-verified signing policy will
accept only artifacts produced from reviewed protected branches. See
`CODE_SIGNING_POLICY.md`.
