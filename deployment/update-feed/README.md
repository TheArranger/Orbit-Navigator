# Orbit Navigator update feed (not deployed)

This is a static, download-only service boundary. It must never share a container,
volume, listener, or tunnel route with My Orbit or another application.

## Fixed local boundary

- Compose service/container: `orbit-navigator-updates`
- Container listener: `8080`
- Only host publication: `127.0.0.1:8789:8080`
- Public hostname reserved by the user: `orbit-nav-updater.snap-it.cc`
- Cloudflare Tunnel origin: `http://127.0.0.1:8789`

The host port was observed unused on 2026-08-15. Recheck before deployment.

## Artifact directory

Create two dedicated directories containing only their own channel artifacts:

```text
primary/
  manifest.json
  OrbitNavigator-X.Y.Z.exe
beta/
  manifest.json
  OrbitNavigator-X.Y.Z-beta.N.exe
```

Use `C:\ProgramData\OrbitNavigator\UpdateFeed\Primary` and
`C:\ProgramData\OrbitNavigator\UpdateFeed\Beta` as the host roots. Mount them with
`ORBIT_PRIMARY_UPDATE_ROOT` and `ORBIT_BETA_UPDATE_ROOT`; Compose maps each to its
own read-only channel root. Grant a dedicated release operator write access only
while staging a release, and grant Docker read access. Do not locate either root
under a Windows user profile, network share, source tree, Docker socket directory,
or any directory containing secrets.

The nginx configuration is baked into an image whose upstream base is pinned by
digest; this avoids exposing the `Z:` project share to Docker. Compose mounts only
the dedicated artifact directory read-only, runs nginx as UID/GID 101 with all
capabilities dropped, uses a read-only root filesystem, and places the service on
an isolated internal Docker network.
It exposes no upload, directory-listing, admin, proxy, CGI, or dynamic route.

## Cloudflare Tunnel ingress (guidance only)

Do not add this route until separately authorized. Add one exact hostname rule to
the existing named tunnel, ahead of the final catch-all:

```yaml
ingress:
  - hostname: orbit-nav-updater.snap-it.cc
    service: http://127.0.0.1:8789
  - service: http_status:404
```

Do not add a wildcard hostname, LAN CIDR route, dashboard/admin route, or a fallback
to another local service. The connector remains outbound-only. Public clients must
use `https://orbit-nav-updater.snap-it.cc`; HTTP at the Cloudflare edge must redirect
to HTTPS. Set minimum TLS 1.2 or newer. The loopback hop is confined to the machine
and is carried outward only inside the authenticated tunnel.

At the edge, bypass caching independently for `/primary/manifest.json` and
`/beta/manifest.json`; immutable caching is allowed only for versioned installer
paths. Never rewrite or fall back between the two channel prefixes. Apply a
conservative per-IP rate limit. Do not
log query strings, referrers, user agents, manifest contents, or client identifiers.

## Release gates

The service is not sufficient by itself. Publication remains blocked until:

1. Primary `Setup.exe`, the launcher, and application binaries are Authenticode-signed
   by a consistent trusted publisher and RFC 3161 timestamped before Primary
   automatic application is enabled. Unsigned Beta packages always require a clear
   Windows trust warning and deliberate per-package confirmation.
2. Each canonical manifest is signed offline with its channel's independent ECDSA
   P-256 release key ring. Public keys are pinned per channel in Orbit Navigator.
   Private keys must not be on this host, in the container, repository, CI variables,
   or either artifact volume.
3. The client verifies manifest signature, channel, expiry, monotonically increasing
   release sequence, version/no-downgrade rule, exact HTTPS origin, size, SHA-256, and
   Authenticode publisher before allowing apply/restart.
4. Negative tests prove that LAN addresses, unrelated hostnames and paths, uploads,
   listings, traversal, unexpected methods, unsigned/tampered packages, stale
   manifests, and tunnel fallback routes fail closed.

Primary is the default and Beta requires explicit `Allow Beta Updates` opt-in. The
selection is exclusive: a client checks one exact manifest and never falls back to,
promotes from, or compares release sequences with the other channel. Each channel
has its own monotonic sequence and ETag cache.

Rollback is forward-only within the selected channel: publish a newly signed higher
version that restores the last-known-good application. Never lower the version or
release sequence.

## Client rollout and channel behavior

The client creates a cryptographically random 32-byte install seed on first run and
stores it only in local update state. The seed is never sent to the feed. It is used
with the signed channel and release sequence to select a deterministic rollout bucket
and to jitter initial, regular, and exponential retry schedules. Requests contain no
install identifier; an ordinary shared ETag may be sent with `If-None-Match`.

Primary and Beta use independent manifest URLs, signing-key rings, ETag caches, and
accepted-release sequence high-watermarks. Primary is selected by default. Settings
may expose one explicit `Allow Beta Updates` choice; enabling it selects Beta only,
and disabling it returns to Primary only. There is no cross-channel fallback.

Manifest signature, expiry, rollout, exact-origin, size, hash, and sequence checks are
mandatory for both channels. An unsigned Primary installer is blocked. An unsigned
Beta installer ignores any automatic-update preference and requires a deliberate
per-package confirmation after showing this meaning:

> This Beta update is authenticated by Orbit's signed update manifest, but its
> installer is not yet signed by a Windows-trusted publisher. Windows may show an
> unknown-publisher warning. Continue only if you intentionally want this Beta build.

Presentation owns final accessible wording, but it must preserve all of those facts
and cannot relabel the package as Windows-trusted.
