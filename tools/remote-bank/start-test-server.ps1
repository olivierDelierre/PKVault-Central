<#
.SYNOPSIS
    Starts a PKVault server (the Backend, Debug build) to try the remote bank, with an access token.

.DESCRIPTION
    The server listens on port 5000, on every network interface, so a phone on the same Wi-Fi can reach it.
    Its data (bank, saves received from devices) lives in the build folder PKVault.Backend\bin\Debug\net10.0,
    not in your real PKVault data: -Reset empties it for a clean start.

    Stop it with: .\start-test-server.ps1 -Stop

.EXAMPLE
    .\start-test-server.ps1 -Reset
    .\start-test-server.ps1 -Reset -Token my-test-token
#>
param(
    # Access token the server requires (PKVAULT_API_TOKEN). Generated if not given.
    [string]$Token = '',
    # Empties the data of the test server first (bank, saves received from devices).
    [switch]$Reset,
    # Only stops the test server.
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$outputDir = Join-Path $root 'PKVault.Backend\bin\Debug\net10.0'
$logFile = Join-Path $PSScriptRoot 'test-server.log'

function Stop-TestServer {
    $stopped = 0
    Get-Process -Name PKVault.Backend -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -Confirm:$false -ErrorAction SilentlyContinue
        $stopped++
    }
    # the "dotnet run" which started it (it usually ends by itself with the server)
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -like '*run --project PKVault.Backend*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Confirm:$false -ErrorAction SilentlyContinue }
    if ($stopped -gt 0) { Start-Sleep -Seconds 2 }
    return $stopped
}

$stopped = Stop-TestServer
if ($Stop) {
    Write-Host "Test server stopped ($stopped process)."
    return
}

if ($Reset) {
    foreach ($name in 'tmp', 'remote-saves', 'db', 'storage', 'backup', 'saves-uploads') {
        $path = Join-Path $outputDir $name
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force -Confirm:$false
            Write-Host "Emptied $path"
        }
    }
}

if (-not $Token) {
    $Token = -join ((48..57) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
}

# inherited by the server
$env:PKVAULT_API_TOKEN = $Token

Write-Host 'Starting the server (first start builds it, it can take a minute)...'
$process = Start-Process -FilePath 'dotnet' `
    -ArgumentList 'run', '--project', 'PKVault.Backend' `
    -WorkingDirectory $root `
    -RedirectStandardOutput $logFile `
    -RedirectStandardError "$logFile.err" `
    -WindowStyle Hidden -PassThru

$ready = $false
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 1
    if ($process.HasExited) { break }
    try {
        $response = Invoke-WebRequest -Uri 'http://localhost:5000/api/remote/ping' -UseBasicParsing `
            -Headers @{ Authorization = "Bearer $Token" } -TimeoutSec 3
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    } catch {
        # not ready yet
    }
}

if (-not $ready) {
    Write-Host "The server did not start. Last lines of $logFile :" -ForegroundColor Red
    if (Test-Path $logFile) { Get-Content $logFile -Tail 15 }
    if (Test-Path "$logFile.err") { Get-Content "$logFile.err" -Tail 15 }
    Stop-TestServer | Out-Null
    exit 1
}

$addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress

Write-Host ''
Write-Host 'Test server is running.' -ForegroundColor Green
Write-Host "  On this computer : http://localhost:5000"
foreach ($address in $addresses) {
    Write-Host "  From a phone     : http://${address}:5000   (same network)"
}
Write-Host "  Access token     : $Token"
Write-Host "  Log              : $logFile"
Write-Host "  Stop it          : .\start-test-server.ps1 -Stop"
Write-Host ''
Write-Host 'If a phone cannot reach it, Windows Firewall probably blocks port 5000 (allow it for private networks).'
Write-Host 'Android blocks plain http by default: see docs/technical/remote-bank-testing.md.'
