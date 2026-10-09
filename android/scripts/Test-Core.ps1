[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$androidRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $androidRoot 'core\build\offline-tests'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $androidRoot 'core\src\main\java'), (Join-Path $androidRoot 'core\src\test\java') -Filter *.java -Recurse | ForEach-Object FullName)
& javac --release 17 -encoding UTF-8 -d $outputRoot @sources
if ($LASTEXITCODE -ne 0) { throw 'Java core compilation failed.' }
& java -cp $outputRoot com.orbitnav.navigator.core.CoreContractTests
if ($LASTEXITCODE -ne 0) { throw 'Browser core tests failed.' }
$fixturePath = Join-Path (Split-Path -Parent $androidRoot) 'tests\OrbitNavigator.Sync.Tests\Interop\v1-vectors.json'
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
if (-not $fixture.fixture_only) { throw 'Only public synthetic interop fixtures may run here.' }
foreach ($vector in $fixture.records) {
    $record = $vector.record
    $aad = $record.aad
    $fixtureArgs = @('record', $fixture.root_key_hex, $vector.category_key_hex, $vector.canonical_aad,
        $record.nonce, $vector.plaintext_hex, $record.ciphertext, $record.authentication_tag,
        $aad.keyset_id, [string]$aad.key_epoch, $aad.category, $aad.profile_id, $aad.device_id,
        $aad.record_kind, $aad.envelope_id, $aad.entity_id, [string]$aad.client_generation, [string]$aad.client_sequence)
    & java -cp $outputRoot com.orbitnav.navigator.core.InteropContractTests @fixtureArgs
    if ($LASTEXITCODE -ne 0) { throw 'JVM record interop failed.' }
}
$wrapped = $fixture.wrapped_keyset
$fixtureArgs = @('recovery', $fixture.root_key_hex, $fixture.recovery_code, $fixture.profile_id,
    $wrapped.keyset_id, [string]$wrapped.generation, [string]$wrapped.kdf.iterations, $wrapped.kdf.salt,
    $wrapped.nonce, $wrapped.wrapped_key_ciphertext, $wrapped.authentication_tag)
& java -cp $outputRoot com.orbitnav.navigator.core.InteropContractTests @fixtureArgs
if ($LASTEXITCODE -ne 0) { throw 'JVM recovery interop failed.' }
