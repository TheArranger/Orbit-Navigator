[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft WebView2 Runtime..."; Flags: waituntilterminated; Check: WebView2RuntimeMissing

[Code]
function WebView2RuntimeMissing: Boolean;
var
  Version: String;
begin
  Result := not (
    RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
  );
  if Result then
    ExtractTemporaryFile('MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
end;

