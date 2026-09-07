[CmdletBinding()]
param(
    [int]$Port = 18789
)

$ErrorActionPreference = "Stop"
$deploymentRoot = Split-Path -Parent $PSCommandPath
$imageName = "orbit-navigator-update-feed:isolation-test"
$containerName = "orbit-navigator-update-feed-isolation-test"

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
mkdir -p /srv/orbit-updates/primary /srv/orbit-updates/beta
printf primary-manifest > /srv/orbit-updates/primary/manifest.json
printf beta-manifest > /srv/orbit-updates/beta/manifest.json
printf primary-package > /srv/orbit-updates/primary/OrbitNavigator-1.2.0.exe
printf beta-package > /srv/orbit-updates/beta/OrbitNavigator-1.2.0-beta.1.exe
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
    if ((Get-Body "/beta/manifest.json") -ne "beta-manifest") {
        throw "Beta did not resolve to its own root."
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
    if ((Get-Status PUT "/beta/manifest.json") -ne "403") {
        throw "The read-only Beta route accepted an unexpected method."
    }

    Write-Host "Update-feed channel isolation passed."
}
finally {
    docker stop $containerName | Out-Null
}
