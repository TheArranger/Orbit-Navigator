[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [string]$ProfileRoot,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OrbitCanonicalAcceptanceNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
"@

function Wait-Until {
    param([scriptblock]$Condition, [string]$Failure, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Root-For([int]$ProcessId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition) |
        Where-Object { $_.Current.Name -eq 'Orbit Navigator' -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

function Find-Named($Root, [string]$Name) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, $Name))
}

function Find-ButtonLike($Root, [string]$Pattern) {
    $Root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition) |
        Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like $Pattern
        } | Select-Object -First 1
}

function Navigate($Root, [string]$TargetText) {
    $box = Find-Named $Root 'Address and search'
    if (-not $box) { throw 'Installed omnibox is unavailable.' }
    [void][OrbitCanonicalAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$Root.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 200
    $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($TargetText)
    $box.SetFocus()
    [OrbitCanonicalAcceptanceNative]::keybd_event(0x0D,0,0,[UIntPtr]::Zero)
    [OrbitCanonicalAcceptanceNative]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
}

function Omnibox-Value($Root) {
    (Find-Named $Root 'Address and search').GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Capture($Root, [string]$Path) {
    $handle = [IntPtr]$Root.Current.NativeWindowHandle
    [void][OrbitCanonicalAcceptanceNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    $rect = [OrbitCanonicalAcceptanceNative+RECT]::new()
    if (-not [OrbitCanonicalAcceptanceNative]::GetWindowRect($handle,[ref]$rect)) { throw 'Cannot read window bounds.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
        $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    $Path
}

function Tab-HasImage($Tab) {
    @($Tab.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Image))).Count -ge 1
}

$install = [IO.Path]::GetFullPath($InstallRoot)
$profile = [IO.Path]::GetFullPath($ProfileRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$acceptance = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'OrbitNavigatorAcceptance')).TrimEnd('\')
if (-not $profile.StartsWith($acceptance+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe profile path.' }
[IO.Directory]::CreateDirectory($evidence)|Out-Null
$launcher = Join-Path $install 'Orbit Navigator.exe'
$application = [IO.Path]::GetFullPath((Join-Path $install 'app\OrbitNavigator.App.exe'))
$alpha = [Convert]::ToBase64String([IO.File]::ReadAllBytes(
    (Join-Path $PSScriptRoot '..\assets\branding\windows\orbit-navigator-32.png')))
$beta = [Convert]::ToBase64String([IO.File]::ReadAllBytes(
    (Join-Path $PSScriptRoot '..\assets\branding\windows\orbit-navigator-40.png')))
$serverSource = @'
import base64, http.server, socketserver
alpha=base64.b64decode('__ALPHA__'); beta=base64.b64decode('__BETA__')
seen={'alpha':False,'beta':False,'fallback':False}
page=b'''<!doctype html><html><head><meta charset="utf-8"><title>Orbit Favicon Alpha</title><link id="icon" rel="icon" type="image/png" href="/alpha.png"></head><body style="font:24px Segoe UI;background:#0b1117;color:white">Favicon lifecycle fixture<script>setTimeout(()=>{document.title='Orbit Favicon Beta';document.getElementById('icon').href='/beta.png?changed=1'},4000)</script></body></html>'''
fallback=b'<!doctype html><html><head><meta charset="utf-8"><title>Orbit Favicon Fallback</title></head><body style="font:24px Segoe UI;background:#0b1117;color:white">No favicon fixture</body></html>'
class Handler(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  print(self.path,flush=True)
  if self.path.startswith('/status'):
   body=('alpha=%d;beta=%d;fallback=%d'%(seen['alpha'],seen['beta'],seen['fallback'])).encode(); typ='text/plain'; code=200
  elif self.path.startswith('/alpha.png'):
   seen['alpha']=True; body=alpha;typ='image/png';code=200
  elif self.path.startswith('/beta.png'):
   seen['beta']=True; body=beta;typ='image/png';code=200
  elif self.path.startswith('/fallback'):
   seen['fallback']=True;body=fallback;typ='text/html; charset=utf-8';code=200
  elif self.path.startswith('/favicon.ico'):
   body=b'';typ='text/plain';code=404
  else:
   body=page;typ='text/html; charset=utf-8';code=200
  self.send_response(code);self.send_header('Content-Type',typ);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
 def log_message(self,*args): pass
socketserver.TCPServer.allow_reuse_address=True
with socketserver.TCPServer(('127.0.0.1',48672),Handler) as server: server.serve_forever()
'@
$serverSource=$serverSource.Replace('__ALPHA__',$alpha).Replace('__BETA__',$beta)
$serverInfo=[Diagnostics.ProcessStartInfo]::new();$serverInfo.FileName=(Get-Command python).Source;$serverInfo.UseShellExecute=$false;$serverInfo.CreateNoWindow=$true;$serverInfo.RedirectStandardOutput=$true;$serverInfo.RedirectStandardError=$true;$serverInfo.ArgumentList.Add('-c');$serverInfo.ArgumentList.Add($serverSource)
$server=[Diagnostics.Process]::Start($serverInfo)
$process=$null
try {
    Start-Sleep -Milliseconds 500
    if($server.HasExited){throw "Loopback fixture failed: $($server.StandardError.ReadToEnd())"}
    Invoke-WebRequest 'http://127.0.0.1:48672/status' -UseBasicParsing | Out-Null
    $runId=[Guid]::NewGuid()
    $launch=Start-Process -FilePath $launcher -ArgumentList @('--acceptance-profile-root',$profile,'--acceptance-run-id',$runId.ToString('D')) -PassThru
    if(-not $launch.WaitForExit(20000) -or $launch.ExitCode -ne 0){throw 'Disposable launcher failed.'}
    $script:orbitProcessId=0
    Wait-Until {
        $candidate=Get-CimInstance Win32_Process|Where-Object{$_.Name -eq 'OrbitNavigator.App.exe' -and $_.ExecutablePath -eq $application}|Select-Object -First 1
        if($candidate){$script:orbitProcessId=$candidate.ProcessId;return $true};$false
    } 'Disposable app did not start.'
    $process=Get-Process -Id $script:orbitProcessId
    $root=$null
    Wait-Until {$script:root=Root-For $process.Id;$null -ne $script:root} 'Browser window did not appear.'
    [void][OrbitCanonicalAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$script:root.Current.NativeWindowHandle)
    Wait-Until {
        $startup=Find-Named $script:root 'Orbit Navigator startup'
        $null -eq $startup -or $startup.Current.IsOffscreen
    } 'Browser startup overlay did not clear.'
    Wait-Until {
        $script:root=Root-For $process.Id
        $ready=$null
        if($script:root){$ready=Find-Named $script:root 'New private window'}
        $null -ne $script:root -and $null -ne (Find-Named $script:root 'Address and search') -and
            $null -ne $ready -and $ready.Current.IsEnabled
    } 'Browser chrome host did not become ready.'
    $root=$script:root

    Navigate $root 'http://127.0.0.1:48672/lifecycle'
    Wait-Until {$script:alphaTab=Find-ButtonLike $root 'Orbit Favicon Alpha*tab*';$null -ne $script:alphaTab} 'Initial favicon page title was not projected.'
    Wait-Until {(Invoke-WebRequest 'http://127.0.0.1:48672/status' -UseBasicParsing).Content -match 'alpha=1'} 'Initial favicon was not requested.'
    Wait-Until {Tab-HasImage $script:alphaTab} 'Initial favicon bytes were not rendered in the installed tab.' 15
    $alphaCapture=Capture $root (Join-Path $evidence 'row10-favicon-alpha.png')
    Wait-Until {$script:betaTab=Find-ButtonLike $root 'Orbit Favicon Beta*tab*';$null -ne $script:betaTab} 'Document-title lifecycle update was not projected.' 15
    Wait-Until {(Invoke-WebRequest 'http://127.0.0.1:48672/status' -UseBasicParsing).Content -match 'beta=1'} 'Changed favicon was not requested.' 15
    Wait-Until {Tab-HasImage $script:betaTab} 'Changed favicon bytes were not rendered in the installed tab.' 15
    $betaCapture=Capture $root (Join-Path $evidence 'row10-favicon-beta.png')

    Navigate $root 'http://127.0.0.1:48672/fallback'
    Wait-Until {$script:fallbackTab=Find-ButtonLike $root 'Orbit Favicon Fallback*tab*';$null -ne $script:fallbackTab} 'Fallback page title was not projected.'
    Start-Sleep -Seconds 2
    if(Tab-HasImage $script:fallbackTab){throw 'No-favicon page retained a stale favicon image.'}
    $fallbackCapture=Capture $root (Join-Path $evidence 'row10-favicon-fallback.png')

    $nested='https://https://my-orbit.snap-it.cc/'
    Navigate $root $nested
    Wait-Until {
        $script:myOrbitTab=$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|Where-Object{
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like '*tab*' -and $_.Current.HelpText -like '*my-orbit.snap-it.cc*'
        }|Select-Object -First 1
        $null -ne $script:myOrbitTab
    } 'My Orbit site label did not reach the tab projection.' 35
    Wait-Until {
        $script:myOrbitTab=$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|Where-Object{
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $_.Current.Name -like '*tab*' -and $_.Current.HelpText -like '*my-orbit.snap-it.cc*'
        }|Select-Object -First 1
        $null -ne $script:myOrbitTab -and (Tab-HasImage $script:myOrbitTab)
    } 'My Orbit live favicon did not reach the installed tab.' 35
    $script:myOrbitTab.SetFocus()
    $reload=Find-Named $root 'Reload page'
    if(-not $reload){$reload=Find-Named $root 'Stop loading'}
    if(-not $reload){throw 'Reload control unavailable for canonical URL projection.'}
    $reload.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until {
        $script:canonicalValue=Omnibox-Value $root
        $script:canonicalValue -like 'https://my-orbit.snap-it.cc*'
    } 'Nested My Orbit URL was not normalized to the canonical HTTPS authority.' 35
    if($script:canonicalValue -match 'https://https://'){throw 'Installed omnibox retained a doubled HTTPS scheme.'}
    $myOrbitCapture=Capture $root (Join-Path $evidence 'row10-my-orbit-canonical-favicon.png')
    $status=(Invoke-WebRequest 'http://127.0.0.1:48672/status' -UseBasicParsing).Content.Trim()
    [pscustomobject]@{
        Row=10;Result='PASS';NestedInput=$nested;CanonicalOmnibox=$script:canonicalValue
        AlphaFaviconRendered=$true;ChangedFaviconRendered=$true;FallbackClearedStaleFavicon=$true;MyOrbitFaviconRendered=$true
        LoopbackStatus=$status;AlphaCapture=$alphaCapture;BetaCapture=$betaCapture;FallbackCapture=$fallbackCapture;MyOrbitCapture=$myOrbitCapture
    }|ConvertTo-Json -Depth 4|Set-Content (Join-Path $evidence 'row10-result.json') -Encoding utf8
    Get-Content (Join-Path $evidence 'row10-result.json')
}
finally {
    if($process -and -not $process.HasExited){$process.CloseMainWindow()|Out-Null;if(-not $process.WaitForExit(10000)){Stop-Process -Id $process.Id -Force}}
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force;$server.WaitForExit(5000)|Out-Null}
    if($server){$server.StandardOutput.ReadToEnd()|Set-Content (Join-Path $evidence 'row10-loopback-requests.log') -Encoding utf8}
}
