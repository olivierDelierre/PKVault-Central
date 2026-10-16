# Remote bank - publishing the builds

How to have GitHub build the Android APK and the server image of this repository, so the APK can be installed and updated with [Obtainium](https://obtainium.imranr.dev/) and the server image pulled by the NAS.

The workflow is `.github/workflows/fork-build.yml`. It is separate from `release.yml`, which is wired to the upstream repository (its image name, its version check, the desktop apps).
It was written without being able to run GitHub Actions: the first run is the real test, see "If the run fails".

## One time: the signing key

An Android app can only be updated by an APK **signed with the same key**. Create it once, and keep it safe: lose it and every phone has to uninstall the app (and lose its local data) before it can install a build signed with a new key.

The key store password and the key password must be the **same** (this is how the project's Dockerfile signs).

```powershell
# no JDK needed: keytool comes from a Docker image
$pass = Read-Host "Password of the key (keep it)"
docker run --rm -v "${PWD}:/work" -w /work eclipse-temurin:21-jdk keytool -genkeypair `
  -keystore pkvault-release.jks -alias pkvault -keyalg RSA -keysize 2048 -validity 10000 `
  -storepass $pass -keypass $pass -dname "CN=PKVault"

# the key as text, to paste in a secret
[Convert]::ToBase64String([IO.File]::ReadAllBytes("pkvault-release.jks")) | Set-Clipboard
```

Do not commit `pkvault-release.jks`. Keep a copy and the password in your password manager.

On GitHub: *Settings* → *Secrets and variables* → *Actions* → *New repository secret*, three times:

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | the text copied above |
| `ANDROID_KEYSTORE_PASSWORD` | the password |
| `ANDROID_KEYSTORE_ALIAS` | `pkvault` |

## Each build

From the branch you want to publish:

```powershell
git tag remote-bank-1
git push origin remote-bank-1
```

Use a new name each time (`remote-bank-2`...). The workflow then:

1. builds the signed APK (about 15 minutes, in Docker, like the official release);
2. builds the server image and pushes it to `ghcr.io/<your github name in lower case>/pkvault`, tagged with the name of the release and `remote-bank` (always the latest);
3. creates a GitHub release with that name, with the APK attached.

The *Run workflow* button of the *Actions* tab does the same, but only shows up once the file is on the default branch of the repository.

## The server image

The image is created private if the repository is private. For the NAS to pull it without credentials, make it public: on GitHub, your profile → *Packages* → `pkvault` → *Package settings* → *Change visibility*. Otherwise the NAS must be logged in to `ghcr.io` with a token that can read packages.

In the compose file of the NAS use `ghcr.io/<name in lower case>/pkvault:remote-bank`.

## Obtainium

1. Install Obtainium on the phone (from its GitHub releases or F-Droid).
2. *Add app* → paste `https://github.com/<you>/PKVault-Central` → *Add*. The source is recognized as GitHub.
3. If the repository is private: Obtainium *Settings* → GitHub source → a *personal access token* that can read the repository contents and releases.
4. Obtainium downloads the APK of the latest release; Android asks once to allow Obtainium to install apps.

Updates: push a new tag; Obtainium sees a new release and offers it.

### Before the first install

The package name is the same as the official app (`io.github.chnapy.pkvault`) but the signature is yours. Android refuses to install over an app signed by someone else: **uninstall the official one first**, which erases its local data. Export or copy your PKVault folder and saves before.

### If Obtainium offers an update that Android refuses

The version code of the APK comes from `ApplicationVersion` in `Directory.Build.props`, which is not increased by this workflow. The update path (same package, same signature, same version code) was not tested. If it fails, uninstall then install, or bump `ApplicationVersion`.

## If the run fails

| Message | Cause |
| --- | --- |
| `Secret ANDROID_KEYSTORE_BASE64 is not set` | the secrets above are missing in the repository settings |
| Signing error in the *Android APK* job | the password or alias secret does not match the key store, or the key password differs from the store password |
| `denied` when pushing the image | *Settings* → *Actions* → *General* → *Workflow permissions*: allow read and write |
| Release created without APK | the *Android APK* job failed: the release job needs it |
