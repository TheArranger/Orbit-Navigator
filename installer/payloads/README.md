# Offline prerequisite payloads

Release builds stage `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` here with
`scripts/Acquire-WebView2.ps1`. Use `winget show --id
Microsoft.EdgeWebView2Runtime --exact --source winget` to obtain Microsoft's
current standalone x64 installer URL and SHA-256, then pass both explicitly.
The script requires a standalone-sized payload, the pinned checksum, and a
valid Microsoft Authenticode signature. It records the approved hash in
`MicrosoftEdgeWebView2RuntimeInstallerX64.sha256`; `Build-Setup.ps1` checks all
three conditions before compiling an installer. Payload binaries and their
local approval file are not committed.
