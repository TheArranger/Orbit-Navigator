#include "includes\Constants.iss"
#define ProjectRoot AddBackslash(SourcePath) + ".."

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-dev"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={localappdata}\Programs\Orbit Navigator
DefaultGroupName=Orbit Navigator
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\branding\orbit-navigator.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\OrbitNavigator.ico
CloseApplications=yes
RestartApplications=yes
MinVersion=10.0.17763

#include "includes\ProductFiles.iss"
#include "includes\Prerequisites.iss"
#include "includes\Shortcuts.iss"
