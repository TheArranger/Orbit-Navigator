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

Primary update checks run automatically after a randomized local delay and
then on a bounded retry schedule. The client requests a signed manifest and
downloads an installer only after an explicit user action. Launching an
unsigned installer always requires a separate confirmation and may show the
Windows unknown-publisher warning. A random rollout seed remains local and is
not sent to the feed. Requests may use a shared HTTP ETag and ordinary
connection metadata required by the network; Orbit adds no install identifier
or browser-usage telemetry.

## Bug reports and contributions

Bug reports are voluntary. Orbit does not automatically attach logs, browsing
history, URLs, page contents, screenshots, or account information. Reporters
choose what to provide and should remove personal or sensitive information.

Settings > Report a problem opens an ordinary Orbit tab only after the user
clicks an action. The private Portfolio form link contains only the fixed
Orbit Navigator project and Windows platform; adding the application version
is optional and off by default. No current address, browsing context, profile
identifier, device identifier, diagnostics, or attachment is put in the link.
The destination receives ordinary network metadata and uses its own website
session, if already signed in; Orbit does not copy account credentials between
profiles or into a reporting API.
The separate Copy private report link action replaces the clipboard only when
clicked, uses the same reviewed context, and makes no network request.

The Portfolio website requires sign-in and lets the reporter review the form
and optionally type environment details or select redacted screenshots. Only
submission confirmed by that website creates a private work order. Opening
the form does not submit a report. The separately labeled GitHub fallback
creates a public issue, not a Portfolio work order; security vulnerabilities
use the private process in `SECURITY.md`.
