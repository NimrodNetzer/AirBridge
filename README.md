# AirBridge

A single app that connects Windows PCs with Android phones and tablets — no accounts, no cloud, no command lines.

## Features

| Feature | Description | Status |
|---------|-------------|--------|
| File Transfer | Instant wireless transfer over local Wi-Fi | Planned |
| Phone as Floating Window | Mirror and control Android phone on PC | Planned |
| Tablet as Second Monitor | Extend Windows desktop wirelessly to tablet | Planned |

## Requirements

**Windows**
- Windows 10 22H2 or later (Windows 11 recommended)
- .NET 8 Runtime
- Windows App SDK 1.5+

**Android**
- Android 8.0 (API 26) or later
- Wi-Fi connection on the same network as the PC

## Architecture

```
Windows App (WinUI 3 / C#)          Android App (Kotlin)
  ├── AirBridge.App    ◄─── mDNS ──► app/
  ├── AirBridge.Core               ├── core/
  ├── AirBridge.Transport          ├── transport/
  ├── AirBridge.Transfer           ├── transfer/
  └── AirBridge.Mirror             ├── mirror/
                                   └── display/
```

All communication is **local network only** (Wi-Fi). No data leaves your network. No account required.

## Development Setup

### Windows
```bash
cd windows
dotnet restore
dotnet build AirBridge.sln
dotnet test
```

### Android
```bash
cd android
./gradlew assembleDebug
./gradlew test
```

## Branch Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Stable releases only |
| `dev` | Integration — all features merge here first |
| `feature/windows-transport` | Windows network layer |
| `feature/android-transport` | Android network layer |
| `feature/pairing-flow` | Device pairing & security |
| `feature/file-transfer` | File transfer engine |
| `feature/screen-mirror` | Phone mirroring & tablet display |

## Documentation

- [Architecture & decisions](CLAUDE.md)
- [Protocol specification](protocol/v1/spec.md)
- [Changelog](CHANGELOG.md)
