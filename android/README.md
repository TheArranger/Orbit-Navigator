# Orbit Navigator for Android — unpublished 0.2.1 foundation

This is a native Android application, not a WPF port or a wrapper around a
hosted UI. It uses Android widgets and the installed Android System WebView.
The pure Java `core` module owns navigation policy, tab/session state, bounded
history/bookmarks and the versioned local format. The `app` module owns the
Android lifecycle, renderer, atomic storage and uncomposed Keystore seam.

The product version is read from `../Versions/ProductVersion.props`; there is
no separate Android version string to drift. Version 0.2.1 maps to Android
versionCode 2001 (`major * 1,000,000 + minor * 1,000 + patch`).

## Implemented, but not device-verified

- Native address/search bar, back/forward/reload, up to 16 tabs, a local home
  screen, bookmarks, bounded history (latest 250 distinct addresses), and settings.
- DuckDuckGo, Google or Bing search; no search suggestions or requests while
  typing. Home is local and does not load a remote start page.
- Normal tabs restore their last address/title, not renderer back stacks,
  forms, scroll position or page content. Background restored tabs remain
  unloaded until selected. Restore can be turned off independently of history.
- Light/dark chrome and an adaptive home layout: cards stack on phones and sit
  side-by-side at 600dp+. The activity is resizable with no orientation lock.
  Window insets account for system bars and display cutouts.
- Normal-only, bounded, atomic app-internal persistence. Unknown or corrupt
  state is preserved and blocks subsequent writes until the user explicitly
  chooses Reset saved browser state. No automatic Android backup or transfer.
- Explicitly denied camera, microphone, location, file/content navigation,
  file uploads, certificate-error bypass and third-party cookies. No JavaScript
  bridge or WebView remote-debugging interface. Mixed content is blocked.
- A default HTTPS **address/navigation preference**, with deliberate direct-HTTP
  opt-out for LAN sites. This is **not a network firewall** or a claim that every
  redirect is intercepted. WebView and service-worker request gates share the
  preference; worker file/content access is disabled. Background tabs can
  still execute scripts and make website requests: `WebView.onPause()` is not
  a network suspension mechanism.
- Cookie clearing with an asynchronous completion callback and a process-wide
  recreation-safe navigation gate while that callback is pending. Web storage
  and cache deletion are requested separately; the UI explicitly does not
  attest their completion, comprehensive erasure or forensic deletion.
- Help, a user-initiated Portfolio report form, and optional copy-to-clipboard
  diagnostics containing only app version, API level, WebView version and
  unavailable feature status. No automatic report submission or upload.
- Offline MIT license/notice viewer with the repository's actual license files
  staged into the APK at build time.
- No browser-owned telemetry, automatic update checks or account requests.
  Website requests and the WebView provider's security services are separate.
- The launcher uses the existing first-party Orbit logo, copied unchanged from
  `../assets/branding/windows/orbit-navigator-256.png`.

## Explicit release gates

**Private browsing is unavailable.** Selecting it explains why and does not
open a normal tab. The model has private lifecycle/persistence rejection tests,
but there is no private WebView composition. Disabling history, clearing cache
or setting `LOAD_NO_CACHE` would not provide a verified ephemeral engine
profile. Before enabling private mode, implement and device-test a separately
isolated engine/process/profile, secure snapshot handling, termination,
crash cleanup and zero normal-profile/account/storage calls. Private model
closing its final tab destroys the session and cannot be reopened/reused.

**My Orbit linking and sync are unavailable.** There is no pretend account
switch or fabricated synchronized state. The provider's approved redirect
contract, separate consent/scopes, registration, recovery, local/account profile
binding, authenticated atomic apply, scheduling and revocation lifecycle must
all be finished and tested before enabling them. See
[sync-boundary.md](docs/sync-boundary.md). `KeystoreSecretStore` is an uncomposed
AndroidKeyStore AES-GCM seam: scope/profile-bound AAD and filenames, distinct
link/sync/keyset purposes, no-backup storage, private rejection before any
platform access. It never uses Windows DPAPI and has not been device-verified.

Downloads, uploads, full-screen media, camera/microphone/location access,
password management, per-site exceptions, desktop extensions and store/update
distribution are not implemented. The source is currently English-only.
This is not feature parity with the Windows browser.

## Compatibility and prerequisites

- Android 9 / API 28 minimum; compile/target API 35.
- Java 17, Gradle 8.11.1, Android Gradle Plugin 8.9.2, build-tools 35.0.0.
- No external runtime library dependencies; Android supplies the renderer.
- Phone/foldable/tablet layout intent is not proof of compatibility with every
  device. Pixel 10 Fold folded/unfolded behavior, API 36 behavior and API 28
  compatibility require the device acceptance matrix below. No store-policy
  compliance is claimed by the target version.

This machine already had Java 17.0.19, the above cached Gradle/AGP pair,
`C:\Android\android_sdk` (platforms 35/36 and build-tools 35.0.0), and accepted
SDK license files. These were reused; no SDK package was installed and no
license was accepted. No emulator package, system image or AVD was found in the
inspected standard SDK/AVD roots. No physical device was connected or installed.

## Build and verification

From this directory in PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-Core.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-Android.ps1 -Offline -IncludeDevelopmentApk
```

The execution-policy choice applies only to that script process. The script
supports explicit `-SdkRoot`, `-Gradle` and `-GradleCache` paths on another
machine. Without `-Offline`, Gradle may obtain pinned build dependencies from
the official configured repositories; SDK auto-download remains disabled.
It does not install SDK packages, accept licenses, start an emulator, install
to a device, publish, or register accounts. Tool metadata and the optional
debug key are scoped under ignored `android/.tools/`, not a user's Android
profile. The shared existing Gradle cache is used only as a dependency/tool cache.

Outputs:

- `app/build/outputs/apk/release/app-release-unsigned.apk`: unsigned, **not
  installable** until deliberately signed; no production key has been created.
- `app/build/outputs/apk/debug/app-debug.apk`: only with
  `-IncludeDevelopmentApk`; installable development candidate with package
  `com.orbitnav.navigator.dev` and version `0.2.1-dev`. It is signed by the
  task-local debug key, not a release identity, and cannot update a production
  package. Keep the same local debug key for future development updates. Do not
  publish the debug key or mistake its public standard password for a secret.
- `app/build/reports/lint-results-{release,debug}.html`: Android lint reports.

`Test-Core.ps1` compiles/runs the Java model tests without Android or Gradle,
then verifies the shared **public synthetic** cryptographic vectors on the JVM
(canonical AAD, HKDF, AES-GCM in both directions, tamper rejection, binary
payloads/Unicode and PBKDF2 recovery). This tests byte-level interoperability,
not Android Keystore, provider authentication or real-device cross-platform sync.

See [the acceptance checklist](docs/device-acceptance.md) before any wider
distribution. Successful compilation and lint do not certify rendered UI,
folding transitions, WebView behavior or device privacy boundaries.

## Primary references

- [AGP/Gradle compatibility](https://developer.android.com/build/releases/about-agp)
- [WebViewClient callback boundaries](https://developer.android.com/reference/android/webkit/WebViewClient)
- [ServiceWorkerController](https://developer.android.com/reference/android/webkit/ServiceWorkerController)
- [WebView lifecycle](https://developer.android.com/reference/android/webkit/WebView)
- [Android Keystore](https://developer.android.com/privacy-and-security/keystore)
