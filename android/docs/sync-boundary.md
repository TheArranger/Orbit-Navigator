# Android / My Orbit boundary

Status: byte-level JVM test interoperability only. Account linking and data
sync remain visibly unavailable in the Android UI and make zero provider calls.

The source of truth remains:

- `../../docs/my-orbit-tabs-history-sync-v1.md`
- `../../src/OrbitNavigator.Contracts/Sync/SyncRecords.cs`
- `../../src/OrbitNavigator.Sync/Cryptography/SyncPayloadBinaryCodec.cs`
- `../../src/OrbitNavigator.Sync/Cryptography/InProcessSyncKeyMaterialRegistry.cs`
- `../../src/OrbitNavigator.Sync/Recovery/RecoveryCodeKeyWrapper.cs`
- `../../src/OrbitNavigator.Sync/Transport/MyOrbitOpaqueSyncTransport.cs`
- `../../src/OrbitNavigator.Sync/Transport/MyOrbitSyncBootstrapTransport.cs`

Shared public fixture: `../../tests/OrbitNavigator.Sync.Tests/Interop/v1-vectors.json`.
`InteropContractTests` is a JVM **test-only reference**, not shipped account or
sync functionality. Its primitives independently reproduce fixture results
from the .NET contract and Python reference. `StateCodec` is a separate local
Android format and must never be used as a sync payload codec.

## Names and bytes that must not drift

| Boundary | History | Open tabs |
| --- | --- | --- |
| JSON category | `history` | `open_tabs` |
| Canonical AAD category | `history` | `opentabs` |
| HKDF information category | `history` | `open-tabs` |
| Binary payload category byte | 0 | 2 |

Payload integer fields are little-endian; GUID bytes use network/big-endian
order; UTF-8 strings have signed 32-bit byte-length prefixes. Timestamps are
.NET UTC ticks, not Unix milliseconds. HKDF salt includes keyset GUID bytes and
a big-endian signed 64-bit epoch. Recovery wire enums `recovery_code` and
`pbkdf2_sha256` map to canonical metadata `recoverycode` and `pbkdf2sha256`.

Only normal history/open-tabs are eligible. Private activity, settings,
bookmarks, cookies, passwords, form content, site permissions, downloads,
diagnostics and page contents are not eligible. No new category is inferred
from an existing enum. Private denial must occur before auth/storage/crypto or
network calls. Local and account sync profile IDs must remain separate.

## Blocking native lifecycle work

1. Approve a provider mobile redirect/client contract and prove callback
   ownership on the actual package/signing identity. An exact verified HTTPS
   App Link with matching `assetlinks.json` is the preferred candidate; no
   registration, domain, package fingerprint or client identity is invented
   by this build. The existing provider loopback contract is not presumed to
   provide a production-ready Android lifecycle.
2. Use external-system-browser PAR + Authorization Code + PKCE S256. Because
   Navigator handles broad HTTP(S) intents, a generic ACTION_VIEW fallback
   may select Navigator itself. Explicitly choose an external supported browser
   or fail closed; never run the OAuth flow in Navigator's WebView or ingest a
   My Orbit password/cookie. Prove foreground/background/process-death and
   canceled-callback behavior before composing it.
3. Keep link-only authorization (`orbit.navigator.link`) separate from explicit
   sync consent (`orbit.navigator.link`, `orbit.sync.history`,
   `orbit.sync.open_tabs`, `orbit.sync.devices`). Provider scopes are a set;
   its current emitted order is canonical but must not be mistaken for a
   universal OAuth ordering requirement.
4. Device registration, dedicated cookie-disabled/no-redirect transport,
   monotonic sequence/fence persistence, account-to-local profile binding,
   key bootstrap and recovery must be complete. Use Android Keystore-backed
   protection, not Windows DPAPI or an app-embedded secret. Keystore loss must
   require explicit relink/recovery, not silently generate a replacement key
   and claim existing ciphertext was recovered.
5. Implement authenticated atomic local apply, conflict/tombstone semantics,
   replay protection and crash/restart tests. Byte-level fixture success does
   not supply these adapters.
6. Exercise two actual composed clients, provider revocation/refresh-reuse,
   offline behavior, private zero-call boundaries and zero plaintext in provider
   storage/logs. Never test with the user's real browsing profile or credentials.

References: [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252),
[verified App Links](https://developer.android.com/training/app-links/configure-assetlinks),
[Custom Tabs](https://developer.chrome.com/docs/android/custom-tabs/guide-get-started).
