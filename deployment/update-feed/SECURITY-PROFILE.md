# Static-feed seccomp profile

`seccomp-no-egress.json` derives from the official Moby default profile at
[`seccomp/v0.2.3`, commit `836ae4d37ef2ec995c77c99fc55f5b5f3af3a897`](https://github.com/moby/profiles/blob/836ae4d37ef2ec995c77c99fc55f5b5f3af3a897/seccomp/default.json).
The upstream signed tag was verified through GitHub's tag metadata. Moby's
Apache-2.0 license is retained in `MOBY-LICENSE`.

Only five names were removed from the upstream allow rules: `connect`, `sendto`,
`sendmsg`, `sendmmsg`, and `socketcall`. The last prevents a 32-bit multiplexed
network-call bypass. The default remains `SCMP_ACT_ERRNO`/EPERM; every other rule,
architecture map, capability condition, and default restriction is preserved.
No capabilities are added. `io_uring` creation/entry remain absent from the
allowlist. Do not replace this with a default-allow profile.

The static nginx worker needs inbound `accept` and response `write`/`writev`/
`sendfile`, but never outbound connection setup, DNS, proxying, or unconnected
datagram sends. Blocking those calls also applies to child processes and `docker
exec`, and no-new-privileges prevents relaxing the filter. It does not replace
read-only mounts, non-root execution, the exact route allowlist, or signed update
verification. A kernel or Docker-host compromise is outside this boundary.

The healthcheck verifies the nginx master is alive and its configuration is valid.
HTTP readiness must be checked from the host at `127.0.0.1:8789/healthz` with the
exact public Host header. An in-container wget healthcheck cannot work because its
loopback connect is intentionally denied. `Test-Isolation.ps1` tests static HTTP,
range/ETag behavior, denied writes and outbound connections without touching the
live feed or other containers. Re-run it after an nginx, Docker, or profile update.

Keep the upstream commit pinned, compare future changes against the upstream
default, and fail closed if any removed call reappears in an allow rule.
