# Changelog

All notable changes to AirBridge will be documented in this file.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
Versioning: [Semantic Versioning](https://semver.org/)

---

## [Unreleased]

### Added
- Unified WinUI 3 application (AirBridge.App) with NavigationView, Mica backdrop, device list, pairing dialog, transfer page, mirror page, settings page
- Jetpack Compose Android UI with Material You theme, bottom navigation, device discovery, pairing screen, transfer screen, mirror screen
- MSIX packaging manifest (Package.appxmanifest) with auto-start capability
- Protocol parser fuzz tests (Windows + Android) — 30+ property-based test cases verifying the parser never propagates unhandled exceptions under malformed input
- Transfer throughput benchmark (100 MB loopback, asserts > 50 MB/s)
- Mirror frame latency benchmark (100 frames × 50 KB, asserts < 5 ms/frame average)
- TLS configuration audit tests (documents scaffold AcceptAllCertificates callback, verifies TLS 1.3 enforcement, port validation)
- Input validation tests for network message parser (size guard, enum cast safety, empty payload handling)

---

## [0.6.0] — 2026-03-26

### Added — Iteration 6 (Mirror Full)
- Input relay: `InputEventMessage` (0x30 wire format), `MirrorWindow` pointer and key capture, `MirrorSession` bounded send channel
- Android `AirBridgeAccessibilityService` + `InputInjector` (GestureDescription / performGlobalAction) — 25 tests
- Drag-and-drop file transfer: `IDroppedFile` / `ITransferEngine` / `MirrorTransferEngine`, `ChannelNetworkStreamAdapter`, `MirrorWindow` DragOver+Drop with overlay, `SendFileAsync`, Android FILE_TRANSFER_* passthrough — 11 tests
- IddCx tablet display: full UMDF2 C++ driver (Driver/Device/SwapChainProcessor/H264Encoder/MonitorDescriptor + vcxproj + INF), named-pipe IPC to `TabletDisplaySession` C#, Android `TabletDisplaySession` + `TabletDisplayActivity` full-screen renderer — protocol spec updated, 31 tests

---

## [0.5.0] — 2026-03-26

### Added — Iteration 5 (Mirror MVP)
- Android screen capture via `MediaProjection` API and `ScreenCaptureSession`
- H.264 hardware encoding via `MediaCodec` with configurable bitrate and frame rate
- `MirrorSession` on Android: captures frames and streams over TLS channel
- `MirrorDecoder` on Windows: Windows Media Foundation H.264 decoder
- `MirrorWindow`: frameless, always-on-top WinUI 3 floating window rendering decoded frames
- `MirrorSession` on Windows: manages decoder lifecycle and input event surface
- `IMirrorService` interface and session lifecycle (start, stop, state events)

---

## [0.4.0] — 2026-03-26

### Added — Iteration 4 (File Transfer)
- Chunked file transfer engine: 64 KB chunks, incremental SHA-256 verification
- `TransferSession` (Windows C# + Android Kotlin): send and receive sides
- `TransferQueue`: concurrent transfer queue with priority and pause/cancel
- `IFileTransferService` interface: `SendFileAsync`, `ReceiveFileAsync`, `GetActiveSessions`
- `TransferMessage` wrappers: `FileTransferStart`, `FileChunk`, `FileTransferAck`, `FileTransferEnd`
- Resume support: receiver reports `nextExpected` chunk index on reconnect
- SHA-256 hash verification on transfer completion
- Progress callbacks via `ProgressChanged` event
- 20+ unit tests for transfer engine and queue

---

## [0.3.0] — 2026-03-26

### Added — Iteration 3 (Pairing)
- Ed25519 key generation via `System.Security.Cryptography.ECDiffieHellman` (Windows) and Bouncy Castle (Android)
- TOFU (Trust On First Use) pairing model: exchange public keys, confirm 6-digit PIN on both devices
- `PairingService` (Windows + Android): PIN generation, key storage, pairing state
- `KeyStore` (Windows): local file-backed Ed25519 key persistence
- `KeyStore` (Android): `EncryptedSharedPreferences` for secure key storage
- `PairingCoordinator` (Windows Transport): wire-protocol handshake over `IMessageChannel`
- `Hilt` dependency injection wiring in `CoreModule.kt`
- 15+ unit tests for pairing logic and key exchange

---

## [0.2.0] — 2026-03-26

### Added — Iteration 2 (Transport)
- mDNS device discovery: `MdnsDiscoveryService` (Windows, `_airbridge._tcp.local`) and `NsdDiscoveryService` (Android, `NsdManager`)
- TLS 1.3 TCP sockets: `TlsConnectionManager` and `TlsMessageChannel` on both platforms
- Binary message framing: 4-byte big-endian length prefix + 1-byte message type + payload
- `ProtocolMessage` parser/serializer (Windows C# + Android Kotlin)
- `IDiscoveryService`, `IConnectionManager`, `IMessageChannel` interfaces
- Transport DI module (`TransportModule.kt`) for Android Hilt
- 10+ integration tests for discovery and message framing

---

## [0.1.0] — 2026-03-26

### Added — Iteration 1 (Scaffold)
- Windows solution: 5 C# projects (`AirBridge.Core`, `.Transport`, `.Transfer`, `.Mirror`, `.Tests`) + IddCx driver project
- Android Gradle project (Kotlin, min API 26, Hilt DI)
- Core interfaces on both platforms: `IDeviceRegistry`, `ITransferSession`, `IMirrorSession`, `IPairingService`
- Transport interfaces: `IDiscoveryService`, `IConnectionManager`, `IMessageChannel`
- Protocol v1 specification (`protocol/v1/spec.md`) and Protobuf definitions (`messages.proto`)
- GitHub Actions CI: Windows (.NET 8 build + test) and Android (Gradle build + test)
- Branch strategy: `main` (stable), `dev` (integration), `feature/*` (per-iteration)
