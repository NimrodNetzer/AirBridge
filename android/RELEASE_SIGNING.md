# Android Release Signing

This document explains how to sign AirBridge release builds so they can be distributed or submitted to the Google Play Store.

---

## 1. Generate a Keystore

Run the following command once to create a keystore file. Keep the file and passwords safe — you will need them for every future release.

```bash
keytool -genkey -v \
  -keystore airbridge-release.jks \
  -alias airbridge \
  -keyalg RSA \
  -keysize 2048 \
  -validity 10000
```

You will be prompted to enter a store password, key password, and identity information (name, organisation, etc.). Use strong, unique passwords and store them in a password manager.

---

## 2. Place the Keystore File

The keystore can be placed anywhere on your build machine. The recommended location is the repository root (it is already listed in `.gitignore` and will never be committed).

Alternatively, store it outside the repository and point to it via the environment variable described below.

---

## 3. Set Environment Variables

The build reads signing credentials from four environment variables. Set these in your shell, CI secrets store, or `.env` file — never hard-code them in source.

| Variable | Description | Default (if unset) |
|---|---|---|
| `AIRBRIDGE_KEYSTORE_PATH` | Absolute or relative path to the `.jks` file | `airbridge-release.jks` (repo root) |
| `AIRBRIDGE_STORE_PASSWORD` | Password used when creating the keystore | *(empty — build will fail)* |
| `AIRBRIDGE_KEY_ALIAS` | Alias chosen during key generation | `airbridge` |
| `AIRBRIDGE_KEY_PASSWORD` | Password for the individual key entry | *(empty — build will fail)* |

Example (bash):

```bash
export AIRBRIDGE_KEYSTORE_PATH="/path/to/airbridge-release.jks"
export AIRBRIDGE_STORE_PASSWORD="your-store-password"
export AIRBRIDGE_KEY_ALIAS="airbridge"
export AIRBRIDGE_KEY_PASSWORD="your-key-password"
```

---

## 4. Build a Release APK

```bash
cd android
./gradlew assembleRelease
```

The signed APK will be written to `app/build/outputs/apk/release/app-release.apk`.

---

## 5. Build a Release AAB (Google Play)

```bash
cd android
./gradlew bundleRelease
```

The signed AAB will be written to `app/build/outputs/bundle/release/app-release.aab`. Upload this file to the Google Play Console.

---

## Notes

- **Package name:** for Play Store submission, the `applicationId` (`com.airbridge.app`) must be registered in the Google Play Console before uploading the first AAB.
- **Keep the keystore secure:** losing the keystore file or forgetting its passwords means you cannot sign future updates with the same key. Users will need to uninstall and reinstall the app if you ever have to re-key — the Play Store will reject updates signed with a different key.
- **Do not commit the keystore:** the file is listed in `.gitignore`. Verify with `git status` that it is not staged before committing.
- **CI:** inject the four environment variables as encrypted secrets in your CI provider (GitHub Actions secrets, etc.) and pass them to the Gradle build step.
