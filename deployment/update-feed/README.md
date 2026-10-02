# Orbit Navigator Primary update feed

This is a static, download-only Docker service. It must never share a container,
volume, listener, or tunnel route with My Orbit or another application.

## Fixed boundary

- Compose service/container: `orbit-navigator-updates`
- Container listener: `8080`
- Host publication: `127.0.0.1:8789:8080` only
- Public hostname: `orbit-nav-updater.snap-it.cc`
- Cloudflare Tunnel origin: `http://127.0.0.1:8789`
- Host artifact root: `C:\ProgramData\OrbitNavigator\UpdateFeed\Primary`

The artifact root contains only:

```text
manifest.json
OrbitNavigator-X.Y.Z.exe
```

Set `ORBIT_PRIMARY_UPDATE_ROOT` to that absolute directory before running Compose.
The directory is mounted read-only. Do not place it in a user profile, repository,
network share, Docker socket directory, or any location containing credentials.

The nginx base image is pinned by digest. The container runs as UID/GID 101 with a
read-only root filesystem, all capabilities dropped, no-new-privileges, bounded
CPU/memory/PIDs, and no Docker socket or secrets. It exposes no upload, directory
listing, admin, proxy, CGI, wildcard host, or dynamic route. Only the exact Primary
manifest and versioned installer filename pattern are downloadable.

Keep the Windows artifact directory ACL restricted to the publishing account,
SYSTEM, and Administrators. Do not inherit the broad ProgramData Users write
permission into this directory. Do not claim host/LAN egress isolation solely from
the read-only container configuration. The pinned default-deny seccomp profile
additionally blocks outbound connection/datagram system calls, including the
32-bit socketcall alternative. See [SECURITY-PROFILE.md](SECURITY-PROFILE.md).
The bridge network retains localhost ingress; no other container or host firewall
is changed. An internal-only Compose network makes the published localhost listener
unreachable on this Windows Docker Desktop host and must not be enabled blindly.

## Cloudflare Tunnel ingress

The existing named tunnel must contain only this exact route ahead of its final
catch-all:

```yaml
ingress:
  - hostname: orbit-nav-updater.snap-it.cc
    service: http://127.0.0.1:8789
  - service: http_status:404
```

Never add a wildcard hostname, LAN CIDR route, dashboard/admin route, or fallback to
another local service. The connector remains outbound-only. Public clients use only
`https://orbit-nav-updater.snap-it.cc`; the edge requires TLS 1.2 or newer. Bypass
caching for `/primary/manifest.json`; immutable caching is allowed only for versioned
installer paths. Logs must omit query strings, referrers, user agents, manifest
contents, and client identifiers.

## Client and release policy

Orbit uses one channel: Primary. There is no Beta selection or cross-channel fallback.
The client automatically checks on a randomized, privacy-preserving local schedule and
uses conditional ETag requests. Its random install seed never leaves the device.
Offline checks fail quietly and local browsing remains available.

Every manifest is signed with the pinned Orbit ECDSA P-256 release key. The client
verifies the manifest signature, channel, expiry, monotonically increasing release
sequence, version/no-downgrade rule, exact HTTPS origin, rollout eligibility, package
size, and SHA-256 before staging an installer.

Until Orbit has a Windows-trusted Authenticode certificate, an unsigned installer may
be offered only after all manifest and hash checks pass. It must never install silently:
the user receives an explicit unknown-publisher disclosure and must deliberately
confirm each package before Windows opens the visible installer. Once Authenticode is
available, automatic application may be considered only for a correctly signed and
timestamped package from the expected publisher.

Rollback is forward-only: publish a newly signed higher version and release sequence
that restores the last-known-good application. Never lower either value.

Run `Test-Isolation.ps1` before deployment. Negative checks cover the retired Beta
path, listings, traversal, unexpected methods, and wrong package shapes.
