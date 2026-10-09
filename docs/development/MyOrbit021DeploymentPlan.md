# My Orbit provider deployment plan for Orbit Navigator 0.2.1

Preparation only. The user authorized this plan and requires another approval
before applying any production change. Do not restart services, run production
migrations, alter public routes, upload an APK, or modify the Orbit update feed.

## Verified cause of the sign in blocker

On 2026-10-07 the configured public origin `https://my-orbit.snap-it.cc`
returned 404 for GET `/oauth2/authorize` and the recovery-envelope endpoint.
The running Docker service `myorbit-my-orbit-1` uses image
`sha256:68b583e2e1c737730d137fc8157e01ad412d7fe7f49b905762e803f58698d739`.
Its `/app/myorbit/app.py` SHA-256 is
`D78FF42522865C9CB0FA3CE18160B6F73750DA929E935F2D23A977087B754B41`;
it contains neither Navigator route integration nor the later OIDC integration.
The current Compose selection is the base `docker-compose.yml`, not either
later overlay. Recheck these identities immediately before any proposed change.

The working My Orbit tree is dirty with unrelated mobile/UI changes and
untracked Navigator provider code. A blanket image rebuild from that directory
would deploy more than the authorized account capability and is not acceptable.
Existing overlay files are not evidence that those overlays are active.

## Minimal reviewed candidate

1. Preserve the active image and Compose selection. Build a disposable candidate
   from the exact active-image digest, not a floating tag or whole dirty tree.
2. Extract and review only Navigator authorization/router/templates/schema and
   necessary integration hooks: cookie-free middleware, consent/login return,
   connection list/revoke, rate limits, and password-reset/account deletion
   revocation. Do not copy the entire modified `app.py` without a reviewed diff.
3. Keep the Windows public client `orbit-navigator` link-only for the first live
   test. Full encrypted-sync scopes/relay stay separate from that approval.
4. Verify against disposable SQLite and PostgreSQL fixtures, including migration
   from the active schema, consent/cancel, PKCE/state/code replay, refresh rotation,
   revocation, login/CSRF, no-store headers and log redaction. Preserve unrelated
   account, Portfolio and Schedule Manager behavior; check which existing
   integrations should be present before choosing a deployment baseline.
5. Prepare a reviewed, reproducible image digest and persistent Compose override,
   an encrypted production-data backup plan, a bounded maintenance window, and
   exact rollback command/previous image. Never export secrets into this plan,
   logs, source or artifacts. Rollback must not drop new tables or existing data.

## Approval and live verification

Submit the exact source delta, image digest, migration result, baseline comparison
and rollback plan to the user. Only after approval, update the one My Orbit app
service in place without changing DNS/tunnel, database exposure, unrelated
services or Orbit feeds. Ensure the selected Compose configuration survives a
restart rather than reverting silently to the old base image.

Verify public TLS and route behavior, then ask the user to approve a disposable
account in the external browser. Navigator must observe the matching callback,
safe Connected state, restart restoration, disconnect and remote revocation.
Do not record credentials, PAR handles, auth URLs, codes, PKCE verifier/state,
tokens or real account identifiers in the evidence. Local browsing remains usable
if the provider is offline or the user cancels.

## Android gate

The current provider has one hard-coded Windows client and loopback redirect.
A distinct Android registration needs review across PAR, code exchange, refresh,
revoke, sync access checks and audit identity; it is not a one-line allowlist
addition. Prefer a verified HTTPS App Link bound to the final Android package and
release-signing certificate, with a separate development identity. No client
secret belongs in an APK. The external browser chooser must exclude Navigator
itself; callback state must survive safely or fail closed after process death.
The user must approve the callback hostname and release-key custody before
registration. Do not treat existing format interoperability tests as Android
account-link or native sync acceptance.
