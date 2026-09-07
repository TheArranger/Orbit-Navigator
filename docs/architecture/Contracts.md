# Contracts v1.1 status

Status: frozen Phase 1 boundary; final privacy actual-file recheck accepted.

Privacy accepted RC4 and the previously accepted UI-facing portions were not
changed. The compiled source and invariant tests are foundation-owned and
immutable to dependent lanes. Privacy, UX, and sync inspect and consume these
files; all requested changes are routed through the coordinator.

The boundary contains:

- neutral profile, session, window, tab, request, device, and opaque-auth
  identifiers plus trusted exact-origin identities and structurally safe
  controller results;
- strict site protection, typed permission brokerage, private clipboard
  isolation, reading preferences, local Orbit-password sensitive actions, and
  Windows user-presence proof contracts;
- foundation adapters for Windows key protection, profile storage, private
  WebView profile lifecycle, local clearing, clock/network/lifecycle hooks, and
  signed-out browsing readiness;
- browser-data facades for bookmarks, history, downloads, settings, private
  windows, and revisioned durable tab-group metadata;
- the exact History/Settings/OpenTabs sync allowlist, explicit exclusions,
  encrypted record/tombstone/purge transport shapes, canonical AAD, device and
  recovery flows, reset fencing, local/synced deletion contracts, and
  server-signed deletion acknowledgement/status verification through the
  pinned/rotatable My Orbit signing-key boundary.

Verification baseline after privacy's actual-file correction: Release contract
build with zero warnings and zero errors; 110 contract tests passed. The host
completion invariant requires `SaveInProfile` to remain false for Once,
Session, and Persistent Orbit permission rules. Popups and Autoplay use the
same typed, expiring, default-deny permission lifecycle as other capabilities.
Remote tombstones have an additive authenticated-consumption codec operation
whose typed receipt must exactly match the canonical AAD identity. Deletion
request digests cover the fence plus every ordered record's canonical AAD and
encrypted bytes. Remote deletion receipts/statuses are accepted only after a
pinned-key server signature verifies the complete request, idempotency, and
fence bindings; an untrusted service never receives the user sync key. Privacy
accepted the compiled server-signature and canonical-AAD digest boundary after
its final actual-file recheck.
