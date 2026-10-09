# Windows 0.2.2 release scope

The user authorized combining the Windows changes prepared during 0.2.1 with
the fullscreen Escape correction and distributing them as 0.2.2. This supersedes
the split 0.1.29 hotfix plan, not the security gates on unfinished capabilities.

## Included and excluded

- Windows: portfolio problem-report navigation, link-only account protocol and
  launcher hardening, and WebView-origin Escape routing.
- Android source remains developmental and is not a Windows installer payload
  or a public Android release.
- Full browsing sync remains `OptionalSyncComposition.SignedOut()`. The new
  consent/inbox building blocks have no live composition call sites.
- My Orbit provider deployment is not authorized by this Windows publication.
  Real account-link acceptance remains dependent on the provider and a separate
  user-operated sign-in test.
- MIT licensing does not provide Authenticode signing. The unsigned-installer
  warning and explicit installation confirmation remain mandatory.

## Runtime evidence and limits

On 2026-10-08 the combined 0.2.2 build passed with zero warnings/errors; its
solution suite passed 805 tests and WPF suite passed 272 tests. Opt-in runtime
tests are tracked separately rather than silently counted as executed.

The visible disposable BrowserChrome/WebView/native-fullscreen fixture ran on
WebView2 154.0.4258.62. An OS-routed short Escape reached the focused same-origin
iframe (keydown and keyup) while keyboard-locked fullscreen remained active.
An ordinary fullscreen attempt returned to its previous window after Escape.
No permission was auto-granted and no security flags were changed.

The user reported that holding Escape exited fullscreen. The instrumented run
captured only a 96 ms press for that attempt and was ended with Abort; it did not
prove a timed continuous hold. Preserve both observations. Do not label the
aborted manual fixture a passing automated test or claim instrumented long-hold
coverage. Its private WebView profile was deleted and no test process remained.

## Distribution checks

Build from a frozen clean source snapshot, retain its identity, and audit the
published payload for development files or secrets. Keep the existing installer
AppId and production installer behavior unchanged. Do not run the production
AppId installer smoke on the user's host: `/DIR` does not isolate registration.
A direct disposable-profile payload smoke and static installer checks do not
constitute a fresh-machine installer/upgrade/uninstall test.

Before feed publication, verify the staged package and signed manifest through
the client's pinned public key and test an installed 0.1.28/highwater-5 client.
Publish the immutable package before atomically replacing the Primary manifest;
use sequence 6 only if the live feed still has sequence 5. Retain forensic
backup, but never roll clients back to a lower version or sequence. Do not
change Docker, the tunnel, or the user's canonical installation/profile.
