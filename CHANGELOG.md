# Orbit Navigator changelog

User-visible changes are recorded here for each release. Unreleased changes are
under development and are not yet part of the public installer. This same file
is bundled with the browser for offline reading in Settings.

## Unreleased

No additional unreleased Windows changes.

## 0.2.2 - 2026-10-08

- Combine the Windows improvements from the 0.2.1 development cycle with the
  fullscreen Escape correction. Keyboard-locked web applications can receive
  a short Escape press; normal browser fullscreen exit remains engine-owned.
- Add Settings → Report a problem, opening the Portfolio's private work-order
  form. Reports require your review and submission; no browsing data or
  diagnostics are sent automatically. Including the app version is optional.
- Harden My Orbit account-link protocol validation, callback handling and
  UI-safe status behavior. This does not enable cross-device browsing sync.
- Android and encrypted tab/history sync remain in development, unavailable
  in this Windows release. Live My Orbit provider deployment and real sign-in
  acceptance remain separate requirements; local browsing needs no account.

## 0.1.28 - 2026-10-02

- Replaced the separate Windows title row with integrated minimize, maximize,
  and close controls, retaining window resizing, dragging, and fullscreen.
- Made compact tabs physically smaller, not just text-free, and removed the
  redundant inactive-tab collapse control.
- Simplified the address toolbar and browser menus. Quick View's idle magnifier
  has a transparent surround and darkens on hover, while its mini-browser keeps
  the opaque surface required for reliable web rendering.
- Added rounded, themed browser scrollbars with hover and drag feedback,
  reserved space beside content, and a High Contrast fallback.
- Hardened Settings navigation to reuse one real browser tab instead of a popup.
- Reflowed New Tab at narrow side-tab widths; compact spaces use readable list
  cards without changing the saved Stellar preference.
- Added this offline changelog to Settings.

## 0.1.27 - 2026-10-02

- Fixed available updates disappearing after another update check.
- Recover and reverify an already downloaded update after restarting Orbit.
- Keep a verified, ready-to-install update available during temporary feed
  failures. Missing or damaged packages can be downloaded again.
- Keep signature, version, package-hash, and explicit installation-confirmation
  checks in place.

## 0.1.26 - 2026-10-02

- Added website fullscreen support, including embedded players, with Escape and
  navigation restoring the browser layout.
- Moved Settings into an ordinary browser tab and improved its text contrast.
- Added working download tracking and completion states.
- Improved recovery of a minimized detached tab controller and separated its
  recovery control from Quick View.
- Made the installer close after successful completion.
- Published Orbit Navigator source under the MIT License.

## Release status

Windows installers are not yet Authenticode-signed and may show an
unknown-publisher or reputation warning. The signed update manifest does not
replace Windows publisher signing. Updates do not install silently.

My Orbit account linking and cross-device browsing sync are separate features.
Full cross-device tab and history sync is not enabled in these releases.

## Maintaining this file

Add user-visible changes to Unreleased as they are implemented. At release time,
move the verified changes to a versioned, dated section and use the same notes
for the public release. Record limitations truthfully; do not describe an
unverified change as fixed or released.
