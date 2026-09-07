# Orbit Navigator

Orbit Navigator is a Windows 10/11 x64 desktop browser foundation built around
Microsoft WebView2. The product is designed to run locally without a My Orbit
account and without browser-owned telemetry.

Orbit Navigator is published by **Orbit Nav Pub** and maintained by **Paradox
(aka TheArranger or TheCodeArranger)**. Individual contributors, if any join
later, will be identified through the public repository history.

Orbit Navigator is open-source software licensed under the Mozilla Public
License 2.0. The license covers the original source, scripts, documentation,
and project-created artwork in this repository unless a file is identified as
third-party material. The license does not grant permission to present a fork
as the official Orbit Navigator product or to use the Orbit Navigator name or
logo as its identity. See [LICENSE](LICENSE), [NOTICE](NOTICE),
[TRADEMARKS.md](TRADEMARKS.md), and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Project stewardship is documented in [GOVERNANCE.md](GOVERNANCE.md).

Phase 1 contains the foundation-owned launcher, WPF/WebView2 App composition,
build/toolchain scaffold, frozen Contracts v1.1 project, browser facades,
tests, and offline installer authoring. The App composes the separately owned
privacy, sync, and presentation assemblies while keeping signed-out local
browsing available; remote sync stays fail-closed until real My Orbit adapters
are supplied. Final visual assets remain a separate owned lane.

## Build entry points

- `scripts\Bootstrap-Toolchain.ps1` stages the pinned workspace-local .NET SDK.
- `scripts\Build.ps1` builds the solution.
- `scripts\Test.ps1` runs the solution tests.
- `scripts\Test-WebView2Runtime.ps1` initializes the published WPF WebView2
  control against the installed runtime and verifies fail-closed host settings.
- `scripts\Test-NormalLaunch.ps1` launches through the root executable and
  requires a visible, WebView2-initialized FoundationWindow before closing it.
- `scripts\Publish-Launcher.ps1` publishes the root `Orbit Navigator.exe`.
- `scripts\Build-Setup.ps1` publishes and compiles the offline installer after
  the pinned WebView2 prerequisite and installer compiler are staged. Signing
  is optional for private unsigned builds.
- `scripts\Test-PackagingPrerequisites.ps1` reports whether the offline
  packaging inputs are ready without changing the machine.

See `docs\architecture\Ownership.md` before modifying a project.

Successful publishing produces the root `Orbit Navigator.exe` plus its
organized `app` payload. Successful installer compilation produces
`dist\Setup.exe` with the pinned x64 WebView2 Evergreen Standalone Installer
embedded for fully offline first-time installation.

## Contributing and reporting problems

Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing a change. Security
issues must follow [SECURITY.md](SECURITY.md); ordinary defects can use the
repository's structured bug-report template. [SUPPORT.md](SUPPORT.md) explains
the support boundary. Never include browsing history,
private-window activity, passwords, tokens, page contents, or other sensitive
information in a report.

## Privacy and signing status

[PRIVACY.md](PRIVACY.md) describes the data boundaries of the current app.
Official public signing through SignPath Foundation has not yet been approved;
current installers remain unsigned unless their release notes explicitly say
otherwise. The proposed release controls are documented in
[CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).
