# Remote bank - build and test guide

How to build the Android app with the remote bank, and how to check that it does what it should.
For how it works, see [remote-bank.md](./remote-bank.md).

## What was checked, and what only you can check

| | Checked by automated tests | Needs a phone / your build |
| --- | --- | --- |
| Transfer engine (every step, resume after any interruption) | yes, `PKVault.Core.Tests` | - |
| Server API (open, commit, download, ack, abort, token) | yes, unit tests **and** end to end against the real Backend | - |
| HTTP client, proxy, device API | yes | - |
| UI (settings, badge, conflicts) | yes, component tests with a mocked server | how it looks and feels on the phone |
| **Android glue** (`PKVault.Mobile/Remote/*`, `DesktopInvoker`, `MainPage`, `MauiProgram`) | **no: could not be compiled** (no MAUI workload, no Android SDK on the machine where it was written) | **compile it, then run it** |
| Writing a save through the Android file access (SAF) | simulated (truncated and failing writes) | real behavior |
| Plain http to a NAS from Android | - | blocked by default, see below |

So the first thing to do is to compile the Android project: if the glue has a compile error, it will show up right away and is small to fix (4 short files).

## 1. Run the automated tests

PowerShell may refuse to run `.ps1` files on your machine (execution policy). The commands below bypass it for the one run, without changing your settings.

```powershell
# C# tests (the 2 end-to-end tests are skipped here: they need a server)
dotnet test --project PKVault.Core.Tests

# Frontend
cd frontend
npm ci
npm run c:type
npm run c:lint
npx vitest run
cd ..

# End to end: starts a real test server with a token, drives the real client / proxy / engine against it, stops it
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-bank\run-e2e.ps1
```

The end-to-end script: a "device" (a temporary folder) sends the sample save to the server, a Pokémon is moved to the bank, the commit gives the modified save back (twice in a row), and a commit whose answer is lost is still delivered at the next start.

## 2. Build the Android app

### Option A: on your machine

Needs, once:

```powershell
dotnet workload install maui-android          # may need an administrator terminal
```

- **JDK 21** (ex: Microsoft OpenJDK 21).
- **Android SDK.** Either install Android Studio, or let .NET download it:

```powershell
$env:JAVA_HOME = "C:\path\to\jdk-21"
$env:ANDROID_HOME = "C:\path\to\android-sdk"      # any folder, it is filled

dotnet build PKVault.Mobile\PKVault.Mobile.csproj -f net10.0-android -t:InstallAndroidDependencies `
  -p:TargetFrameworks=net10.0-android `
  -p:JavaSdkDirectory=$env:JAVA_HOME -p:AndroidSdkDirectory=$env:ANDROID_HOME -p:AcceptAndroidSDKLicenses=True
```

Then, from the repository root:

```powershell
# 1. the web app embedded in the APK (same as "make apps-prepare")
npm run --prefix frontend build
Remove-Item PKVault.Mobile\Resources\Raw\wwwroot -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item frontend\dist PKVault.Mobile\Resources\Raw\wwwroot -Recurse

# 2. compile (this is the check of the Android glue)
dotnet build PKVault.Mobile\PKVault.Mobile.csproj -f net10.0-android -c Debug `
  -p:TargetFrameworks=net10.0-android `
  -p:JavaSdkDirectory=$env:JAVA_HOME -p:AndroidSdkDirectory=$env:ANDROID_HOME

# 3. install & start on the phone / emulator that "adb devices" lists
dotnet build PKVault.Mobile\PKVault.Mobile.csproj -f net10.0-android -c Debug -t:Run `
  -p:TargetFrameworks=net10.0-android `
  -p:JavaSdkDirectory=$env:JAVA_HOME -p:AndroidSdkDirectory=$env:ANDROID_HOME
```

`-p:TargetFrameworks=net10.0-android` is there so the Windows target (which needs another workload) is not built. If .NET complains with `NETSDK1147` about a missing workload anyway, run `dotnet workload install maui`.

A **Debug** build is needed to read the transfers journal from the phone (see section 5): a release build is not debuggable.

On a phone: enable *Developer options* and *USB debugging*, then accept the prompt on the phone when you plug it. `adb` is in `<ANDROID_HOME>\platform-tools`.

### Option B: let GitHub build it (nothing to install, updates with Obtainium)

See [remote-bank-publishing.md](./remote-bank-publishing.md): a tag push builds the signed APK and the server image and publishes them.

### Option C: with Docker on your machine

The repository already builds the APK this way (`Dockerfile`, target `mobile-android`). It needs a signing key; a throwaway one is enough for tests:

```powershell
keytool -genkeypair -v -keystore test.jks -alias test -keyalg RSA -keysize 2048 -validity 3650 -storepass testpass -keypass testpass -dname "CN=PKVault test"
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("test.jks"))

docker build --target mobile-android -t pkvault-apk `
  --build-arg ANDROID_KEYSTORE_BASE64=$b64 --build-arg ANDROID_KEYSTORE_PASSWORD=testpass --build-arg ANDROID_KEYSTORE_ALIAS=test .

docker create --name pkvault-apk pkvault-apk
docker cp pkvault-apk:/app/pkvault.apk .\pkvault-test.apk
docker rm pkvault-apk

adb install -r pkvault-test.apk
```

This one is a Release build: it works for trying the feature, but `pull-device-journal.ps1` cannot read it. Not run by the author.

### If the compilation fails

The Android specific code is only these files, all small:

- `PKVault.Mobile/Remote/MauiRemoteDeviceStore.cs` (preferences)
- `PKVault.Mobile/Remote/MauiRemoteSecretStore.cs` (secure storage)
- `PKVault.Mobile/MauiProgram.cs` (3 registrations)
- `PKVault.Mobile/DesktopInvoker.cs` (`Fetch`: asks the remote bank first)
- `PKVault.Mobile/MainPage.xaml.cs` (resumes the transfers at start)

Send the compiler errors as they are, it is the fastest way to fix them.

## 3. A server to try with

### On your computer (quickest)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-bank\start-test-server.ps1 -Reset
```

It starts the Backend with a token, prints the addresses (including your IP on the network, for the phone), and the token. `-Stop` stops it. Its data lives in `PKVault.Backend\bin\Debug\net10.0`, not in your real PKVault data.

The phone must reach your computer: same Wi-Fi, and Windows Firewall must allow port 5000 on private networks.

### On the NAS (the real use)

```yaml
services:
  pkvault:
    image: ghcr.io/chnapy/pkvault:latest     # a build which contains this feature
    ports: ["3000:3000"]
    environment:
      - PKVAULT_API_TOKEN=a-long-random-value
    volumes:
      - ./pkvault-data:/pkvault
```

The saves received from the phone are kept in `/pkvault/remote-saves`, inside the volume.

The address to give to the app is `http(s)://<nas>:3000`.

## 4. Plain http and Android

Android blocks plain `http://` addresses by default. For a NAS in your home network you have two ways:

1. **https** (recommended, the token and your saves are then encrypted): a reverse proxy (Caddy, Traefik, Nginx Proxy Manager...) in front of the container, or a VPN such as Tailscale (`tailscale serve`).
2. **Allow http for your test build.** In `PKVault.Mobile/Platforms/Android/AndroidManifest.xml`, add `android:usesCleartextTraffic="true"` to the `<application>` element. This is NOT done in the repository: it would apply to every user of the app, and it is a security decision for you to take. If you do it for tests, do not commit it.

The settings screen warns when the address is plain http and a token is used.

## 5. Try it, and break it

In the app: *Settings* → *Remote bank*: enter the address and the token, **Save**, **Test the connection**, then switch to **Server**. A badge appears in the header.

On the *Saves* page, a section *Saves of this device* lists the phone saves: **Send to the server**, then **Edit on the server**: it opens the usual storage page, on the server bank. Move Pokémon, then save as usual: the modified save is written back to the phone.

The point of this feature is what happens when things go wrong. For each line, the expected result is the same: **no Pokémon lost, no corrupted save, and the transfer ends by itself once the problem is gone** (or asks you what to do).

| What you do | Expected |
| --- | --- |
| Normal: send, move a Pokémon to the bank, save | The Pokémon is in the bank, gone from the phone save. Badge back to *Remote bank*. |
| Airplane mode, then try to save | The save button disappears and the badge says *Server offline*. Nothing changes. Turn it back on: it comes back by itself. |
| Airplane mode **right after pressing save** | The save may or may not have reached the server. When the network is back the badge goes *Syncing…* then back to normal, and the phone save and the bank agree. |
| Force-stop the app right after pressing save, restart it | Same: the transfer resumes at start. |
| Wrong token (change it on the server, or type a wrong one) | Badge *Token refused*. Nothing else breaks. |
| Stop the server while a transfer is in progress | The transfer shows *Paused: ...*. Start the server again: it goes on. |
| Play the game on the phone **after** sending the save, then save in PKVault | *Action needed*. **Nothing is written** to your save. Choose: cancel on the server, or replace the phone save. |
| Same, choose *Cancel the changes made on the server* | Bank back as it was before; phone save untouched. (Refused with an explanation if something else was saved on the server since.) |
| Switch back to *This device* with a transfer in progress | Refused until the delivery is finished. |
| Send a save the server already has from its own saves | Refused with an explanation (same save in two places). |
| Clear the app data while a delivery is waiting on the server, then send the same save | The waiting delivery is delivered first. |

## 6. Looking inside

- **The app**: tap the badge. It shows the connection, each transfer with its state, why it is paused, and warnings.
- **The server**: `GET /api/remote/transfers?deviceId=<id>` (with `Authorization: Bearer <token>`) lists the deliveries waiting for a device. Deliveries are also in the `RemoteTransfers` table of `pkvault.db`. The server log says `Remote delivery recorded`, `acknowledged`, `aborted`.
- **The phone journal** (Debug build only):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-bank\pull-device-journal.ps1
```

  It copies `files/remote-journal` from the app and prints each transfer. Files: `*.json` the transfer, `*.original.bin` what was sent, `*.new.bin` what was delivered, `*.device-before-write.bin` what was on the phone just before replacing it. That last one is where your progress is if you chose to replace a save you had played on. `pull-device-journal.ps1` was not run by the author (no device available): tell me if it needs a fix.

## 7. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| *Test the connection* fails right away, no matter the address | Plain http blocked by Android (section 4), or the server is not reachable (firewall, wrong IP, not the same network) |
| `401` / *Token refused* | The token differs from `PKVAULT_API_TOKEN` of the server |
| A transfer stays in `UPLOADING` with `409 SAVE_ID_CONFLICT` | The server already has this save from another source |
| *Send to the server* does nothing, error about the action list | The server has pending actions (another session); save or discard them there |
| *Action needed* with `DELIVERY_GONE` | The server data was restored from an older backup; the changes it made are not there anymore. *Forget these changes* is the only choice |
| The Remote bank block is not in the settings | It only appears in the Android app |
| `.ps1` "scripts are disabled on this system" | Use `powershell -NoProfile -ExecutionPolicy Bypass -File ...` as in the examples |
