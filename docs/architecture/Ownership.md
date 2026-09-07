# Phase 1 ownership

Foundation exclusively owns the root build metadata, shared Contracts project,
Launcher, App composition root, Foundation, WebViewHost, Updates, UpdateRunner,
installer authoring, build scripts, host harness, and their matching tests.
Foundation also owns the WPF App composition seams, normal-profile
persistent-only storage capability adapters, browser-data facade contracts,
and the durable/atomic tab-group metadata store.

Privacy exclusively owns its implementation project and tests. UX exclusively
owns its Presentation and ClipboardShelf presentation projects and tests. Sync
exclusively owns its opaque transport and integration project and tests. Final
branding, icons, installer artwork, and animation specifications belong to the
coordinator.

After Contracts v1.1 is frozen, privacy, UX, and sync must request contract
changes through the coordinator and must not edit shared contract source.

Privacy owns rule serialization, hydration, and write-through for persistent
site-protection and permission rules. App composition supplies separate
`IProfileStorage` adapters restricted to the selected normal profile and
persistent durability; session, temporary, private, or cross-profile calls are
rejected before reaching disk. UX consumes browser facades and owns
presentation only; it does not own browser-data persistence.
