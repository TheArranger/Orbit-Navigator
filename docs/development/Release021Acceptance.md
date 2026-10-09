# Orbit Navigator 0.2.1 acceptance

0.2.1 is an unpublished overhaul target, not a completed release. The existing
Windows release and public update feed must remain unchanged until the following
gates have evidence tied to one candidate. Automated test success is not a
substitute for real app, account, device, or installation evidence.

## Scope and release gates

| Area | Required proof | Current boundary |
| --- | --- | --- |
| Windows | Full Release build, unit and WPF tests; isolated-profile navigation, tabs/groups, downloads, permissions, fullscreen, settings and support tests | Source version is 0.2.1; canonical installation and feed unchanged |
| Android | Buildable native app, supported Android range, responsive phone/foldable/tablet UI, real navigation and restart; Pixel 10 Fold folded/unfolded acceptance | Android lane is new; APK compilation alone cannot certify device behavior |
| My Orbit linking | Real system-browser consent, cancellation, callback, status after restart, disconnect and revocation; no credentials in UI/logs | Candidate protocol tests pass locally; configured production authorization route returned 404 in the readiness probe |
| Encrypted sync | Two actual clients with separate local profiles; recovery-code setup/join; tabs/history apply; reconnect, tombstone semantics, conflicts, revocation, private zero-call | Protocol/reference-client evidence is separate from native-app composition; live sync stays disabled |
| Bug reporting | Exact portfolio route, optional version, sign-in continuation, user-reviewed test report delivered as a private work order, cancellation and fallback | No automatic diagnostics, browsing data, or attachments; no silent form submission |
| Windows trust | Provider approval, reviewed-build origin, signed and timestamped first-party binaries/installer, independent signature verification | MIT is already present; signing service approval is external; no warning-free guarantee |
| Release delivery | Candidate hashes, clean install/update preserving profile, cross-PC update regression, changelog/privacy notices, physical Android test | No publication, production deployment, or installer execution is part of this source-work checkpoint |

## Sync application semantics

The received-record inbox is one atomic local document containing authenticated
records, tombstones, page receipts, and replay identities. It loads the immutable
local-to-account profile binding before accepting data. It never reclassifies a
local profile, silently opens a remote URL, or replaces the current browser's
tabs. It accepts History and OpenTabs only; purge and settings are unsupported.

Remote items remain device-scoped. A tombstone removes that origin's item; this
does not yet establish cross-device user-deletion convergence. An explicit UI
and policy for restoring remote tabs and merging or deleting history must be
reviewed before enabling sync. The bounded inbox fails closed when full; a safe
compaction/retention policy is required for sustained production operation.

Account-link credentials stay link-only. Sync requires its own explicit consent
and protected credential lifecycle. Android must use platform-backed secret
protection and an approved mobile redirect; Windows DPAPI storage cannot be
copied to Android. Reference crypto vectors prove format compatibility, not
secure native credential lifecycle or real user sign-in.

## Test isolation and evidence

Use an explicit disposable profile root and fresh acceptance run ID; verify the
root attestation before WebView activity. Never use a canonical profile for
test data, close a user-owned process, or launch a profile-sharing instance.
Record source revision/dirty state, runtime hashes, tool versions, commands,
counts, screenshots, and exact limitations. Stop test-owned processes and remove
only resolved disposable roots after retaining non-sensitive evidence.

Real account steps are performed by the user in the external browser. Do not
record passwords, codes, verifier/state, tokens, recovery codes, auth URIs,
cookies, browsing URLs or page content in diagnostics. Use disposable accounts
and synthetic browsing fixtures for end-to-end tests.

The user has already verified cross-PC updates and fullscreen PC Remote usage.
Keep those as regression coverage rather than describing them as unresolved
failures. The Pixel 10 Fold is the requested physical Android acceptance device;
distribution through the portfolio comes only after an explicitly approved test
candidate, without replacing the public Windows release.

## 2026-10-07 source checkpoint

Evidence root: `artifacts/verification/overhaul-0.2.1-20261007/`.

- Final combined Release build: zero warnings/errors (`solution-build-verified2.log`).
- Final solution tests: 805 passed, zero failed, one existing opt-in real-runtime
  fullscreen test skipped (`solution-tests-verified/`). WPF: 270/270 passed
  (`wpf-tests-verified/`). An earlier WPF run exposed fake keyboard devices
  installing duplicate text-composition managers; the test seam now uses the real
  WPF keyboard device. One intervening narrow-focus timeout remains recorded;
  two subsequent complete WPF runs passed, not a guarantee of no future flakiness.
- Independent Python reference clients passed local provider-router E2E with
  fixture identity; Java and .NET consume the same public synthetic crypto
  vectors. This is not Windows-to-Android application sync proof.
- Android: both APK variants build, lint reports no issues, and 124 JVM assertions
  pass. The development APK is debug-signed, not a production release. Private
  WebView, account/sync, downloads/uploads and fullscreen media remain gated or
  unimplemented; physical device acceptance has not run.
- Actual source-built Windows smoke used attested run
  `fcbafdb6-e797-4d95-96fd-f444f4860a9f`, PID 44272, in an isolated Temp root.
  Physical menu interaction opened Settings as a tab. The visible Report a
  problem control opened the exact Portfolio route in a third tab, without the
  optional version by default. The protected work-order page rendered. No login,
  account linking, submission or attachment was attempted. My Orbit Settings
  displayed Not connected and account-link-only/no-sync copy.
- Initial PowerShell shell launch stalled before creating Orbit; its exact
  test-owned launcher PID 40188 was stopped. Direct bundled-runtime invocation
  then worked and the app exited normally with code zero. No product fix was
  inferred from the shell-launch failure.
- All test-owned app/WebView processes are gone. The disposable profile folder
  remains because the tool policy rejected its exact recursive cleanup request;
  no alternate deletion method was attempted. Exact root and attestation are in
  `checkpoint.json` and `smoke-root-attestation.json`. It contains synthetic local
  test state only, not user credentials. Do not treat cleanup as fully complete.
- Canonical profile content inventory before/after is identical: 2,651 files,
  445,814,322 bytes; aggregate SHA-256
  `9F360B1F56B9B50B5864B3772C1AFDEA59CC5013AA694FCC88EF52D66B785898`.
  No canonical install, production provider deployment, public release or update
  feed mutation was performed.

Provider work remains preparation only; see `MyOrbit021DeploymentPlan.md`.
The user must review the exact candidate/rollback evidence and approve applying
it before the live sign-in test. No Portfolio chat interruption is needed for
the existing report route; authenticated work-order delivery still needs a
user-reviewed disposable test.
