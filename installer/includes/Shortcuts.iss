[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\Orbit Navigator"; Filename: "{app}\Orbit Navigator.exe"; IconFilename: "{app}\OrbitNavigator.ico"; AppUserModelID: "{#MyAppUserModelId}"
Name: "{autodesktop}\Orbit Navigator"; Filename: "{app}\Orbit Navigator.exe"; IconFilename: "{app}\OrbitNavigator.ico"; Tasks: desktopicon; AppUserModelID: "{#MyAppUserModelId}"

[Run]
Filename: "{app}\Orbit Navigator.exe"; Description: "Launch Orbit Navigator"; Flags: nowait postinstall skipifsilent
