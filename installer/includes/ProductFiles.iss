[Files]
Source: "{#ProjectRoot}\artifacts\publish\launcher\Orbit Navigator.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\artifacts\publish\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\installer\payloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: dontcopy
Source: "{#ProjectRoot}\assets\branding\orbit-navigator.ico"; DestDir: "{app}"; DestName: "OrbitNavigator.ico"; Flags: ignoreversion
