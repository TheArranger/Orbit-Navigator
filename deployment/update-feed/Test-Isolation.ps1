[CmdletBinding()]
param(
    [int]$Port = 18789
)

$ErrorActionPreference = "Stop"
$deploymentRoot = Split-Path -Parent $PSCommandPath
$imageName = "orbit-navigator-update-feed:isolation-test"
$containerName = "orbit-navigator-update-feed-isolation-test"
$seccompPath = Join-Path $deploymentRoot "seccomp-no-egress.json"
$seccomp = Get-Content -LiteralPath $seccompPath -Raw | ConvertFrom-Json
if ($seccomp.defaultAction -ne "SCMP_ACT_ERRNO" -or $seccomp.defaultErrnoRet -ne 1) {
    throw "The isolation profile must retain the Moby default-deny policy."
}
$allowedCalls = @($seccomp.syscalls | Where-Object { $_.action -eq "SCMP_ACT_ALLOW" } | ForEach-Object { $_.names })
foreach ($call in @("connect", "sendto", "sendmsg", "sendmmsg", "socketcall")) {
    if ($allowedCalls -contains $call) { throw "Outbound syscall remains allowed: $call" }
}

if ($Port -lt 1024 -or $Port -gt 65535) {
    throw "Test port must be between 1024 and 65535."
}

if (docker ps -a --format "{{.Names}}" | Where-Object { $_ -eq $containerName }) {
    throw "The isolation-test container name is already in use."
}

docker build --tag $imageName $deploymentRoot
if ($LASTEXITCODE -ne 0) {
    throw "The update-feed test image did not build."
}

$seedCommand = @"
mkdir -p /srv/orbit-updates/primary
printf primary-manifest > /srv/orbit-updates/primary/manifest.json
printf primary-package > /srv/orbit-updates/primary/OrbitNavigator-1.2.0.exe
exec nginx -c /etc/nginx/nginx.conf -g 'daemon off;'
"@

docker run --detach --rm `
    --name $containerName `
    --read-only `
    --tmpfs "/tmp:size=16m" `
    --tmpfs "/srv/orbit-updates:size=2m,mode=1777" `
    --user "101:101" `
    --cap-drop ALL `
    --security-opt "no-new-privileges:true" `
    --security-opt "seccomp=$seccompPath" `
    --publish "127.0.0.1:${Port}:8080" `
    --entrypoint sh `
    $imageName `
    -c $seedCommand | Out-Null

try {
    Start-Sleep -Milliseconds 700
    $baseUri = "http://127.0.0.1:$Port"
    $hostHeader = "Host: orbit-nav-updater.snap-it.cc"

    function Get-Body([string]$path) {
        $value = curl.exe --silent --header $hostHeader "$baseUri$path"
        if ($LASTEXITCODE -ne 0) { throw "GET failed for $path." }
        return $value
    }

    function Get-Status([string]$method, [string]$path, [switch]$RawPath) {
        $arguments = @("--silent", "--output", "NUL", "--write-out", "%{http_code}",
            "--request", $method, "--header", $hostHeader)
        if ($RawPath) { $arguments += "--path-as-is" }
        $arguments += "$baseUri$path"
        $value = & curl.exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "$method failed for $path." }
        return $value
    }

    if ((Get-Body "/primary/manifest.json") -ne "primary-manifest") {
        throw "Primary did not resolve to its own root."
    }
    if ((Get-Status GET "/primary/OrbitNavigator-1.2.0-beta.1.exe") -ne "404") {
        throw "A Beta package crossed into the Primary route."
    }
    if ((Get-Status GET "/primary/../beta/manifest.json" -RawPath) -ne "400") {
        throw "Plain dot-segment traversal was not rejected."
    }
    if ((Get-Status GET "/primary/%2e%2e/beta/manifest.json" -RawPath) -ne "400") {
        throw "Encoded dot-segment traversal was not rejected."
    }
    if ((Get-Status GET "/beta/manifest.json") -ne "404") {
        throw "The retired Beta route is still exposed."
    }
    if ((Get-Status GET "/primary/") -ne "404") {
        throw "The Primary route exposed a directory listing."
    }
    if ((Get-Status PUT "/primary/manifest.json") -ne "403") {
        throw "The read-only Primary route accepted an unexpected method."
    }

    # HttpClient preserves the quoted ETag exactly on Windows PowerShell 5.1.
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseCookies = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.DefaultRequestHeaders.Host = "orbit-nav-updater.snap-it.cc"
    $packageUri = "$baseUri/primary/OrbitNavigator-1.2.0.exe"
    try {
        $head = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Head, $packageUri)
        $response = $client.SendAsync($head).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200 -or $null -eq $response.Headers.ETag) { throw "Package HEAD/ETag failed." }
        $etag = $response.Headers.ETag.ToString()
        $response.Dispose(); $head.Dispose()
        $conditional = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Head, $packageUri)
        [void]$conditional.Headers.TryAddWithoutValidation("If-None-Match", $etag)
        $response = $client.SendAsync($conditional).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 304) { throw "Conditional ETag request failed." }
        $response.Dispose(); $conditional.Dispose()
        $range = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $packageUri)
        [void]$range.Headers.TryAddWithoutValidation("Range", "bytes=0-6")
        $response = $client.SendAsync($range).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 206 -or $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() -ne "primary") {
            throw "Installer range download failed."
        }
        $response.Dispose(); $range.Dispose()
    }
    finally { $client.Dispose(); $handler.Dispose() }

    # Sentinel destinations never receive packets: connect is rejected by seccomp.
    foreach ($destination in @("127.0.0.1", "10.255.255.1", "1.1.1.1")) {
        $probe = & docker exec $containerName sh -c "wget -T 2 -O /dev/null http://${destination}:8080/ 2>&1"
        if ($LASTEXITCODE -eq 0 -or ($probe -join " ") -notmatch "Operation not permitted") {
            throw "Outbound connect was not denied by seccomp."
        }
    }
    $writeProbe = & docker exec $containerName sh -c "touch /etc/nginx/orbit-write-denial-probe 2>&1"
    if ($LASTEXITCODE -eq 0 -or ($writeProbe -join " ") -notmatch "Read-only file system") {
        throw "The container root filesystem is writable."
    }

    Write-Host "Primary update-feed isolation passed."
}
finally {
    docker stop $containerName | Out-Null
}
