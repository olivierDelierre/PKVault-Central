<#
.SYNOPSIS
    Runs the end-to-end tests of the remote bank against a real test server.

.DESCRIPTION
    Starts a clean test server (see start-test-server.ps1), runs RemoteBankEndToEndTests which drive the real
    HTTP client, proxy and transfer engine of the device against it, then stops the server.

    What they check: a save of a "device" (a temporary folder) is sent to the server, a Pokémon is moved to
    the bank, the commit gives the modified save back to the device (twice in a row), and a commit whose
    answer is lost is still delivered at the next start.

.EXAMPLE
    .\run-e2e.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$token = -join ((48..57) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })

& (Join-Path $PSScriptRoot 'start-test-server.ps1') -Reset -Token $token
# $LASTEXITCODE is null when no program ran yet, and $null -ne 0 is true: it has to be checked
if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exitCode = 1
try {
    $env:PKVAULT_E2E_URL = 'http://localhost:5000'
    $env:PKVAULT_E2E_TOKEN = $token

    Push-Location $root
    dotnet test --project PKVault.Core.Tests --filter-class '*RemoteBankEndToEndTests'
    $exitCode = $LASTEXITCODE
    Pop-Location
} finally {
    & (Join-Path $PSScriptRoot 'start-test-server.ps1') -Stop
}

if ($exitCode -eq 0) {
    Write-Host 'End-to-end tests passed.' -ForegroundColor Green
} else {
    Write-Host 'End-to-end tests FAILED.' -ForegroundColor Red
}
exit $exitCode
