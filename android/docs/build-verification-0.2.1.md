# Local Android verification — 0.2.1 candidate

Date: 2026-10-07. Unpublished; no emulator or physical-device verification.

## Passed on this host

- `scripts/Test-Core.ps1`: 73 browser-model assertions plus 51 independent JVM
  known-answer assertions against the shared public sync fixtures (124 total).
- `scripts/Build-Android.ps1 -Offline -IncludeDevelopmentApk`: Release and Debug
  compilation/package tasks completed using the existing pinned local tools.
- Android lint Release: **No issues found**.
- Android lint Debug: **No issues found**.
- `apksigner verify --verbose --print-certs`: development APK verifies using
  APK Signature Scheme v2. This is a local self-signed development identity,
  not a trusted public publisher or production release identity.
- `aapt dump badging`: debug package `com.orbitnav.navigator.dev`, label
  `Orbit Navigator Dev`, version `0.2.1-dev`, versionCode `2001`, min API 28,
  target/compile API 35, INTERNET permission only; no required touchscreen.
- APK archive inspection confirms offline `assets/licenses/LICENSE` and
  `assets/licenses/NOTICE` are included.

The Java compiler notes deprecated framework APIs retained for the minimum-API
compatibility boundary; lint did not report an error or warning. This is not a
claim that device/rendering/security behavior has been exercised.

## Exact local artifacts

| Artifact | SHA-256 |
| --- | --- |
| `app/build/outputs/apk/debug/app-debug.apk` | `692188930110c54c6219ff6636d42ad3fd33b921a9c78ff8c12b793b3125a5da` |
| `app/build/outputs/apk/release/app-release-unsigned.apk` | `f8f6c9e32679f2a13559df066fb0466ba32f7ab276bf58693b606422a59a5102` |

Development signing certificate SHA-256:
`cc8cc90152d39c7aa6efd4fb2f2e49e259b3e8855d62974254a1344759a83982`.

These hashes identify the exact current bytes, not every future rebuild.
The development signing key is ignored workspace-local test material under
`.tools/debug-signing/`; it must not be published or reused as production
signing material. No production key or signing account was created.

## Not verified / still gated

- Pixel 10 Fold folded/unfolded UI, keyboard, TalkBack, rotation/multi-window,
  API 28/35/36 runtime behavior, WebView redirects/service workers, process
  death, renderer failure and cookie-clear recreation on a real device.
- Android Keystore operations: the guarded platform seam compiled but is not
  composed into an account flow and has not run against a device keystore.
- Private browsing: unavailable by design; no isolated disposable engine yet.
- Account linking and sync: unavailable by design; fixture interoperability
  is not app-to-app/provider end-to-end proof.
- Downloads, uploads, device permissions and full-screen media are unavailable.
- Nothing was installed to a device, uploaded to Portfolio, published to an
  app store, connected to My Orbit, or tested with a real user profile.

See `device-acceptance.md` for the remaining explicit acceptance gates.
