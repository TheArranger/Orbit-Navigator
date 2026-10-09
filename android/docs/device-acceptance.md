# Android acceptance checklist — not yet executed

Only disposable app profiles and synthetic test pages may be used. Installing
or clearing an app on a physical device requires the owner's explicit go-ahead.
No device install, USB requirement, emulator setup or public Portfolio upload
is implied by this checklist. A future user-approved Portfolio test download
must be labeled a development candidate, with its exact hash/signing identity,
known limitations and rollback/uninstall instructions.

## Matrix

| Environment | Required checks | Current evidence |
| --- | --- | --- |
| Installed JDK 17 | Core navigation/state/private gates + shared crypto vectors | Host tests only |
| AGP 8.9.2 / SDK 35 | Compile, package, lint, manifest/signature inspection | Host build only |
| Pixel 10 Fold, folded | Address entry/IME, touch targets, menus, tabs, contrast, TalkBack | Not run |
| Pixel 10 Fold, unfolded | Fold/unfold while loading, settings, cookie clear; 600dp home layout; hinge readability | Not run |
| API 28 phone | Min-SDK runtime APIs, WebView availability, navigation, storage, process restart | Not run |
| API 35/36 phone/tablet | Edge-to-edge, cutouts, gestures, multi-window, rotation, font scaling | Not run |
| No/offline/failed WebView | Honest error state, no crash, no loss of saved state | Not run |

## Scenarios before distribution

1. First launch must remain on the local home screen without a start-page or
   account request. Open synthetic HTTPS pages; search only after Go. Exercise
   back/forward/reload, late titles, renderer crashes and failed requests.
2. Open 16 tabs, switch/close tabs before and after the active tab, close the
   final normal tab and verify a new home tab. Confirm the 17th tab is rejected.
   Multi-window or external link intents must not create conflicting stores.
3. Restart after navigation and restore last addresses only; disable restore
   and verify history/bookmarks survive but old tabs do not reopen. Corrupt a
   disposable state file and confirm preservation/write block until reset.
4. Check unsafe schemes (`file`, `content`, `data`, `javascript`, `intent`), URL
   credentials and invalid TLS certificates. Camera/mic/location/file prompts
   must fail honestly. Test redirects and service workers separately; record
   the HTTPS preference's engine limits rather than claiming network isolation.
5. With a synthetic HTTP LAN site, confirm direct HTTP is blocked by default,
   allowed only after explicit HTTPS-preference opt-out, and mixed content
   remains blocked. Do not use the owner's real PC Remote/session credentials.
6. Cookie clearing: load only disposable cookies/storage; request clearing,
   rotate/fold during the callback, and confirm navigation remains gated until
   cookie completion. The UI must say storage/cache deletion is requested,
   not fully verified. Test process death without claiming crash-time erasure.
7. Private selection must explain unavailability without creating an engine/tab
   or changing the normal profile. Account selection must never issue provider
   requests, prompt for passwords or appear linked. Keystore tests belong to a
   disposable instrumented app; no user key material may be read or changed.
8. Report a problem supplies only project and `platform=android`; no automatic
   version/environment/URL/history attachment or report submission. Copied
   diagnostics contain only the explicitly shown safe version fields.
9. Check 200% font scale, TalkBack focus/labels, 48dp touch targets, small
   landscape windows, fold/unfold and keyboard resize in both chrome themes.
10. The debug APK must use `.dev` package identity, be clearly development-only,
    and never replace the eventual release package. Verify its signature and
    SHA-256 before a user-approved distribution. No production key is generated
    as part of a development build.

Do not mark the Android app production-ready based solely on compilation,
lint, host model tests, cryptographic fixtures or this unexecuted checklist.
