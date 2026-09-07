# Orbit Navigator privacy notice

Orbit Navigator is designed for local-first browsing without browser-owned
telemetry. This notice describes the current source tree; websites and the
Microsoft WebView2 runtime have their own privacy practices.

## Data kept locally

A normal profile may store browser settings, bookmarks and their user-entered
notes, history, open-tab/session state, workspaces and approved local artwork,
site-protection and permission rules, offline-reading viewport PNG captures,
update state, and bounded diagnostic events. Browser and WebView profile data
remain on the device unless the user performs an explicit operation that
requires a network service.

Diagnostic events are intended to contain state and failure categories rather
than visited URLs, page contents, passwords, cookies, tokens, or form data.
Users should still review any file before attaching it to a bug report.

## Private windows

Private windows use a separate private WebView profile and memory-only private
tab-group state. Private calls are rejected before persistent preference,
permission, clipboard-shelf, account-link, sync, and offline-reading storage
paths. Private profile resources are disposed and cleanup is attempted when
the private window closes. Operating-system or third-party runtime artifacts
outside Orbit's control are not represented as impossible.

## Network activity

Browsing necessarily connects to sites selected by the user. Search text is
sent to the selected search provider when the user submits it. Affiliated-site
cards navigate only after user activation and contain no Orbit tracking
parameters.

My Orbit account linking, when its provider is available, uses the external
system browser and a narrowly scoped authorization flow. Orbit does not ask
for or receive the user's My Orbit password or browser cookies. Full browsing
data sync is disabled in the current application composition.

Beta update checks occur only after explicit Beta opt-in. The client requests
a signed manifest and, after another user action, a selected installer from
the pinned Orbit update host. A random rollout seed remains local and is not
sent to the feed. Requests may use a shared HTTP ETag and ordinary connection
metadata required by the network; Orbit adds no install identifier or
browser-usage telemetry.

## Bug reports and contributions

Bug reports are voluntary. Orbit does not automatically attach logs, browsing
history, URLs, page contents, screenshots, or account information. Reporters
choose what to provide and should remove personal or sensitive information.
