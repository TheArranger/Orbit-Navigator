# Orbit Navigator

Orbit Navigator is a Windows 10/11 x64 desktop browser foundation built around
Microsoft WebView2. The product is designed to run locally without a My Orbit
account and without browser-owned telemetry.

Orbit Navigator is published by **Orbit Nav Pub** and maintained by **Paradox
(aka TheArranger or TheCodeArranger)**. Individual contributors, if any join
later, will be identified through the public repository history.

Orbit Navigator is open-source software under the standard **MIT License**.
You may use, modify, redistribute, and sell copies, including for commercial
use, while retaining the license and copyright notice. First-party artwork
is covered to the extent Orbit Nav Pub holds rights in it. Third-party
material keeps its own license. See [LICENSE](LICENSE), [NOTICE](NOTICE),
[TRADEMARKS.md](TRADEMARKS.md), and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Project stewardship is documented in [GOVERNANCE.md](GOVERNANCE.md).

Released MIT versions remain available under MIT even if the project owner
chooses a different license for future versions. Donations are optional and
do not change anyone's license rights.

Project links: [Paradox portfolio](https://iamtheparadox.com/) and
[support Orbit Navigator on Ko-fi](https://ko-fi.com/paradoxthecreator).

## Download and update

[Download Orbit Navigator 0.1.25 for Windows x64](https://orbit-nav-updater.snap-it.cc/primary/OrbitNavigator-0.1.25.exe).
The installer includes the offline WebView2 prerequisite. Install over an
existing copy to retain the local browser profile. Close Orbit before updating.

The browser uses one Primary update feed, with signed manifest, version, and
package-hash checks. Download and installation remain user-controlled. This
release is **not Authenticode-signed**: Windows may show an unknown-publisher or
reputation warning. An update-manifest signature is not Windows publisher trust.
See the [Code signing policy](CODE_SIGNING_POLICY.md). No paid signing service
or subscription has been enabled.

Phase 1 contains the foundation-owned launcher, WPF/WebView2 App composition,
build/toolchain scaffold, frozen Contracts v1.1 project, browser facades,
tests, and offline installer authoring. The App composes the separately owned
privacy, sync, and presentation assemblies while keeping signed-out local
browsing available. Secure My Orbit account linking is available, while
cross-platform tab and history synchronization remains fail-closed until its
encrypted transport, device/key lifecycle, and client adapters are complete.

## Build entry points

Website fullscreen controls (including embedded remote-desktop players) expand
the active tab to the display and temporarily hide browser chrome. Escape or
the website's exit control restores the prior window and workspace layout.
Tab changes, navigation, and renderer failures also leave fullscreen.

The isolated WebView2 fullscreen smoke is opt-in: set
`ORBIT_RUN_WEBVIEW_FULLSCREEN_SMOKE=1` when running the App tests. It uses a
disposable private profile and an in-memory iframe fixture, never a real stream.

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
