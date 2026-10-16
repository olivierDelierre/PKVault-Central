<#
.SYNOPSIS
    Copies the transfers journal of the remote bank from an Android phone or emulator, and prints it.

.DESCRIPTION
    The journal is where the app records, step by step, what happens between the phone and the server
    (see docs/technical/remote-bank.md). It lives in the private storage of the app, which can only be
    read with "adb run-as" and only for a DEBUG build of the app (a release build is not debuggable).

    Needs adb (Android SDK platform-tools) and a phone with USB debugging, or an emulator.

    NOT TESTED by the author: no Android device or adb was available when it was written.

.EXAMPLE
    .\pull-device-journal.ps1
    .\pull-device-journal.ps1 -Adb "C:\Android\sdk\platform-tools\adb.exe"
#>
param(
    [string]$Adb = 'adb',
    [string]$Package = 'io.github.chnapy.pkvault',
    [string]$OutDir = (Join-Path $PSScriptRoot 'device-journal')
)

$ErrorActionPreference = 'Stop'

$devices = & $Adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\sdevice$' }
if (-not $devices) {
    Write-Host 'No device found. Check "adb devices" (USB debugging on, or an emulator running).' -ForegroundColor Red
    exit 1
}

$journal = 'files/remote-journal'
$names = & $Adb shell run-as $Package ls $journal 2>&1
if ($LASTEXITCODE -ne 0 -or "$names" -match 'not debuggable|Unknown package|No such file') {
    Write-Host "Cannot read $journal of $Package :" -ForegroundColor Red
    Write-Host "$names"
    Write-Host 'Either the app is not a Debug build (run-as needs a debuggable app), or no transfer was ever made.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($name in ($names -split "\r?\n" | Where-Object { $_ })) {
    $target = Join-Path $OutDir $name
    # exec-out keeps binary files intact, which a PowerShell redirection would not
    cmd /c "`"$Adb`" exec-out run-as $Package cat $journal/$name > `"$target`""
}

Write-Host "Copied to $OutDir" -ForegroundColor Green
Write-Host ''

foreach ($file in Get-ChildItem $OutDir -Filter *.json) {
    $entry = Get-Content $file.FullName -Raw | ConvertFrom-Json
    Write-Host "$($entry.fileName)  [$($entry.state)]  transfer=$($entry.localId)"
    if ($entry.conflictReason) { Write-Host "    conflict : $($entry.conflictReason)" -ForegroundColor Yellow }
    if ($entry.lastError)      { Write-Host "    paused on: $($entry.lastError)" -ForegroundColor Yellow }
    if ($entry.warning)        { Write-Host "    warning  : $($entry.warning)" -ForegroundColor Yellow }
    Write-Host "    updated  : $($entry.updatedAt)"
}

Write-Host ''
Write-Host '*.original.bin = save sent to the server, *.new.bin = save delivered by the server,'
Write-Host '*.device-before-write.bin = what was on the phone just before it was replaced.'
