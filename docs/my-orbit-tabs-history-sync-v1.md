# My Orbit tabs and history sync v1

Status: implementation candidate; not deployed or enabled in the app.

## User-visible scope

The first sync release is opt-in and synchronizes only:

- normal-profile browsing history;
- normal-profile open-tab address, title, order, and optional group label.

It does not synchronize private browsing, passwords, form/autofill data, cookies,
site sessions or tokens, permission decisions, protection exceptions, bookmarks,
downloads, Clipboard Shelf data, local diagnostics, page content, screenshots, or
encryption/recovery material. Settings are also excluded from this first release.

Linking a My Orbit account does not by itself enable data sync. The existing
`OptionalSyncComposition.SignedOut()` remains the application default until the
separate sync consent, device registration, key bootstrap, and local adapters are
all available.

## Security boundaries

- Authorization uses external-system-browser PAR plus Authorization Code and
  PKCE S256. Navigator never receives a My Orbit password or browser cookie.
- The sync client uses a dedicated cookie-disabled, no-redirect HTTP handler.
- My Orbit receives canonical routing AAD plus AES-GCM nonce/ciphertext/tag. It
  must never receive decrypted URLs, titles, page content, request metadata, or
  browsing telemetry.
- Every operation is bound to an authenticated My Orbit account connection,
  device identity, profile/keyset, client generation fence, and monotonically
  allocated client sequence.
- Private-mode rejection occurs before authorization, storage, projection,
  encryption, or network work.
- The server accepts only `history` and `open_tabs` in this release. `settings`
  and all unknown categories fail closed.

## Implemented candidate slice

My Orbit now has a local-only candidate opaque relay under
`/navigator-sync/v1`:

- `POST /devices/register`
- `POST /records/push`
- `GET /records/pull`

The relay validates exact JSON shapes, AES-GCM field sizes, protocol/schema,
profile/device/keyset identity, generation fences, per-device sequence identity,
envelope idempotency, category allowlists, batch limits, cookie-free requests,
scoped bearer authorization, and no-store responses. It stores ciphertext and
canonical AAD only.

The provider candidate passes its full 45-test suite and focused 27-test sync
suite, but remains local and undeployed. Its final review confirmed two wire
gaps: provider registration currently expects a client-supplied device id while
`ISyncDeviceRegistry` expects the provider to return one, and there is no opaque
recovery-envelope exchange for onboarding a second device.

Navigator has a corresponding internal `ISyncTransport` candidate that performs
the same validation on both outbound and inbound data, rejects redirects and
`Set-Cookie`, and never accepts Settings or purge traffic in the v1 transport.

Navigator also has a local keyset bootstrap candidate. It generates a 256-bit
root key, binds the protected frame to the normal profile, keyset id, and epoch,
uses the dedicated `SyncKeysetWrappingKey` Windows CurrentUser protection
purpose, persists only the protected envelope, and exposes live material only as
an opaque in-process handle. Private requests are rejected by `SyncOperationGate`
before storage or key protection. Disposal removes and zeroes the registered key.

The local change-catalog candidate durably journals only monotonically ordered
record kind, category, and opaque entity id. It accepts only History/OpenTabs
upserts and tombstones, stores no page address or title, pages through stable
cursors after restart, rejects corrupt/mixed-profile state, and denies private
recording before profile storage.

An App-owned projection adapter can now resolve a journaled entity from the
authoritative normal-profile history facade or live workspace snapshot. It maps
only the requested History/OpenTabs record, preserves tab order/group label,
rejects internal/private/unknown tabs and mixed-profile snapshots, and performs
no network fetch or persistence.

A content-free entity index can now reconcile authoritative History/OpenTabs
revisions into the journal. It appends changes before advancing the index, so a
crash can repeat an idempotent change but cannot silently lose it. The App-owned
inventory source excludes internal/private tabs and supplies only entity ids and
monotonic revisions.

## Gates before app composition

The following must be completed and security-reviewed before the app may stop
using `OptionalSyncComposition.SignedOut()`:

1. Freeze device-registration and first-keyset bootstrap wire shapes against the
   existing `ISyncDeviceRegistry`, recovery, and fencing contracts. The selected
   direction is a provider-generated sync DeviceId kept distinct from the OAuth
   connection id.
2. Define how an upgraded local profile adopts or maps the account-wide sync
   profile identity without moving private/session state or corrupting existing
   profile-scoped storage.
3. Security-review the implemented local protected-key bootstrap and add the
   account-level recovery-code envelope exchange needed to onboard another
   platform without sending raw recovery code or key material to My Orbit.
   Trusted-device exchange is deferred.
4. Add a separate sync-consent authorization flow requesting exactly
   `orbit.navigator.link`, `orbit.sync.history`, `orbit.sync.open_tabs`, and
   `orbit.sync.devices`. The ordinary account-link credential remains link-only.
5. Compose the implemented inventory reconciler and read-side History/OpenTabs
   projector, then implement the authenticated atomic apply target with
   tombstones, conflict handling, replay protection, and restart tests.
6. Add opt-in Settings state and scheduling. Signed-out/provider-unavailable/
   offline states must leave local browsing fully usable.
7. Pass two-client end-to-end tests, revocation/reuse/fence tests, private
   zero-call tests, and a review confirming My Orbit storage/logs contain no
   plaintext browser data.

No provider deployment, public route activation, Beta feed change, package, or
installer is authorized by this implementation slice.
