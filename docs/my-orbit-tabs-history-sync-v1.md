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
- `PUT /keysets/recovery-envelope`
- `GET /keysets/recovery-envelope`
- `POST /records/push`
- `GET /records/pull`

The relay validates exact JSON shapes, AES-GCM field sizes, protocol/schema,
profile/device/keyset identity, generation fences, per-device sequence identity,
envelope idempotency, category allowlists, batch limits, cookie-free requests,
scoped bearer authorization, and no-store responses. It stores ciphertext and
canonical AAD only.

The provider candidate passes its full 49-test suite and focused 31-test
authorization/sync suite, but remains local and undeployed. Registration now
accepts only a display name and canonical public identity key, generates a
distinct sync DeviceId, and returns its generation fence. OAuth connection
identifiers are never reused as sync DeviceIds. The recovery endpoints store and
return only an opaque recovery-code-wrapped AES-GCM keyset envelope; My Orbit
never receives a recovery code, derived wrapping key, or unwrapped root key.

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

Navigator now has strict cookie-free/no-redirect adapters for the finalized
provider-generated device registration and recovery-envelope wire shapes. They
reject extra or duplicate JSON properties, non-canonical identifiers/base64url,
contradictory fences and receipts, redirects, `Set-Cookie`, malformed KDF/crypto
shapes, and oversized responses. The sync-device registry deliberately does not
pretend that a standalone device-delete endpoint exists: V1 revocation remains
owned by the account-connection controller.

The local profile mapping rule is explicit and durable. The first device binds
the account sync profile identity to its existing normal local ProfileId. A
joining device stores an immutable local-to-account sync-profile binding; it
does not rewrite its local ProfileId, move profile-scoped files, or touch
private/session state. A conflicting identity cannot silently replace or
reclassify an existing binding. Private calls are rejected before storage.

## Gates before app composition

The following must be completed and security-reviewed before the app may stop
using `OptionalSyncComposition.SignedOut()`:

1. Compose the finalized device-registration and recovery-envelope adapters into
   a separate sync-consent session. Keep account-link-only credentials separate.
2. Carry the immutable account sync-profile binding through encryption,
   transport validation, checkpoints, and authenticated apply without treating
   it as permission to rewrite local profile storage.
3. Add a separate sync-consent authorization flow requesting exactly
   `orbit.navigator.link`, `orbit.sync.history`, `orbit.sync.open_tabs`, and
   `orbit.sync.devices`. The ordinary account-link credential remains link-only.
4. Compose the implemented inventory reconciler and read-side History/OpenTabs
   projector, then implement the authenticated atomic apply target with
   tombstones, conflict handling, replay protection, and restart tests.
5. Add opt-in Settings state and scheduling. Signed-out/provider-unavailable/
   offline states must leave local browsing fully usable.
6. Pass two-client end-to-end tests, revocation/reuse/fence tests, private
   zero-call tests, and a review confirming My Orbit storage/logs contain no
   plaintext browser data.

No provider deployment, public route activation, Beta feed change, package, or
installer is authorized by this implementation slice.
