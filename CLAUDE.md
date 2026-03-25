# AirBridge — Project Reference for Claude

## Project Vision

AirBridge is a single unified app that connects Windows PCs with Android phones and tablets seamlessly — no accounts, no cloud, no command lines.

**Three core features:**
1. **File Transfer** — instant wireless transfer between connected devices (AirDrop for Windows + Android)
2. **Tablet as Second Monitor** — extend the Windows desktop wirelessly to an Android tablet
3. **Phone as Floating Window** — mirror and control an Android phone on the PC screen, with drag-and-drop file transfer

**Non-goals (for now):** iOS/macOS support, cloud sync, internet relay.

---

## Target Platform

- **Windows** (desktop host app) — WinUI 3 / C# (.NET 8+)
- **Android** (client app) — Kotlin, min API 26 (Android 8.0)
- Communication: **local Wi-Fi only**, no internet required, no account required

---

## Repository Structure

```
AirBridge/
├── windows/                   # Windows desktop app (WinUI 3, C#)
│   ├── AirBridge.App/         # Main WinUI 3 application
│   ├── AirBridge.Core/        # Platform-agnostic business logic
│   ├── AirBridge.Transport/   # Network layer (discovery, sockets, TLS)
│   ├── AirBridge.Mirror/      # Screen mirroring + virtual display driver
│   ├── AirBridge.Transfer/    # File transfer engine
│   └── AirBridge.Tests/       # Unit + integration tests
├── android/                   # Android app (Kotlin, Gradle)
│   ├── app/
│   │   ├── core/              # Shared business logic
│   │   ├── transport/         # Network layer (mirrors Windows)
│   │   ├── mirror/            # Screen capture (MediaProjection)
│   │   ├── display/           # Tablet display rendering
│   │   └── transfer/          # File transfer engine
│   └── androidTest/
├── protocol/                  # Shared protocol specs (versioned .proto or docs)
├── docs/                      # Architecture docs, diagrams, decisions
└── CLAUDE.md
```

---

## Architecture

### Device Discovery
- **Protocol:** mDNS (Multicast DNS / DNS-SD) — zero-config, works on any LAN
- Windows uses `Zeroconf` / `DnsServiceBrowse` (Win32) or a .NET mDNS library
- Android uses `NsdManager`
- Devices advertise `_airbridge._tcp.local` service with port + device metadata

### Pairing & Security
- **Model:** TOFU (Trust On First Use) — like SSH host keys
- On first connect, devices exchange Ed25519 public keys; user confirms a PIN on both sides
- All subsequent connections are TLS 1.3 (mutual authentication with stored keys)
- Pairing database stored locally (no cloud, no sync)
- **No pairing = no data access**

### Transport Layer
- Custom binary framing protocol over TCP with TLS
- Shared protocol spec in `/protocol/` (documented as spec, possibly Protobuf for messages)
- Message types: `Handshake`, `FileChunk`, `MirrorFrame`, `InputEvent`, `ClipboardSync`, `Ping`
- Versioned protocol with backward-compat rules

### File Transfer Engine
- Chunked transfer with resume support
- Progress reporting via callbacks
- Hash verification (SHA-256) on completion
- Transfer queue with pause/cancel

### Screen Mirroring (Phone as Floating Window)
- Android: `MediaProjection` API for screen capture → H.264/H.265 encode via `MediaCodec`
- Windows: decode stream → render in a frameless floating window (Win2D or Direct3D surface)
- Input relay: mouse/keyboard events sent back to Android via `AccessibilityService` or ADB
- Target latency: <100ms on local Wi-Fi

### Tablet as Second Monitor
- Windows: virtual display driver (IddCx — Indirect Display Driver) creates a new monitor
- Android: receives H.264 frame stream → decodes → renders full screen at native resolution
- Input: touch events on tablet → translated to mouse events on virtual monitor

---

## Tech Stack

### Windows
| Layer | Technology |
|-------|-----------|
| UI Framework | WinUI 3 (Windows App SDK) |
| Language | C# (.NET 8) |
| Display Driver | IddCx (Indirect Display Driver, C++) |
| Networking | System.Net.Sockets + SslStream |
| Discovery | mDNS via managed library |
| Video Decode | Windows Media Foundation / Direct3D |
| Packaging | MSIX |

### Android
| Layer | Technology |
|-------|-----------|
| Language | Kotlin |
| Min SDK | API 26 (Android 8.0) |
| Screen Capture | MediaProjection API |
| Video Encode | MediaCodec (hardware H.264/H.265) |
| Video Render | SurfaceView / TextureView |
| Networking | Java NIO with Kotlin coroutines |
| Discovery | NsdManager |
| Build | Gradle (Kotlin DSL) |

---

## Core Principles

1. **Zero friction** — open app, devices appear, features work
2. **No account required** — fully local, air-gapped capable
3. **Privacy first** — no telemetry, no data leaves the LAN without explicit user action
4. **Security by default** — TLS everywhere, pairing required, no open ports without user awareness
5. **One unified experience** — single app per platform, not 3 separate tools
6. **Modular codebase** — each feature (transfer, mirror, display) is an independent module with a clean interface
7. **Testable** — all business logic is unit-testable without hardware; integration tests use mock transports

---

## Code Standards

### General
- All modules must have clear public interfaces; internals are private
- No business logic in UI layer
- Async everywhere: `async/await` (C#), Kotlin coroutines (Android)
- Errors propagate as typed results, not exceptions at module boundaries
- Every public API must have XML doc comments (C#) or KDoc (Kotlin)

### Testing
- Unit tests for all business logic (transfer engine, protocol parser, pairing logic)
- Integration tests for transport layer using loopback sockets
- UI tests for critical user flows (pairing, file send, mirror start)
- Test coverage target: 80%+ for Core and Transport modules
- CI must pass before merge

### Security
- Never log sensitive data (keys, file contents, pairing PINs)
- Input validation on all received network messages
- Fuzz testing for protocol parser
- Dependency audit in CI (Dependabot or equivalent)

### Versioning
- Protocol version is separate from app version
- Breaking protocol changes require major version bump
- Older clients must receive a clear "update required" message, not a crash

---

## Module Contracts

### Windows: `AirBridge.Core`
- `IDeviceRegistry` — manages known/paired devices
- `ITransferSession` — represents a single file transfer (send or receive)
- `IMirrorSession` — represents an active mirror connection
- `IPairingService` — handles the pairing handshake

### Windows: `AirBridge.Transport`
- `IDiscoveryService` — advertise and browse mDNS
- `IConnectionManager` — accept/initiate TLS connections
- `IMessageChannel` — send/receive framed protocol messages

### Android (mirrors Windows contracts)
- Same logical interfaces, Kotlin equivalents
- Dependency-injected via Hilt

---

## Development Workflow

- **Branching:** `main` = stable, `dev` = integration, `feature/*` = features, `fix/*` = bugfixes
- **PR requirements:** passing CI, one approval, no unresolved review comments
- **Commit style:** conventional commits (`feat:`, `fix:`, `chore:`, `docs:`, `test:`)
- **Breaking changes:** must be flagged in PR title and CHANGELOG

---

## Current Status

- [ ] Project scaffolding (solution structure, Gradle setup)
- [ ] Device discovery prototype (mDNS on both platforms)
- [ ] Secure pairing flow
- [ ] File transfer MVP
- [ ] Phone mirroring MVP (view only)
- [ ] Phone mirroring with input relay
- [ ] Tablet as second monitor (virtual display driver)
- [ ] Unified UI (all features in one app)
- [ ] Packaging and distribution

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03 | Windows: WinUI 3 + C# over Electron | Virtual display driver requires native Windows kernel APIs; lower latency; no ~100MB Electron overhead |
| 2026-03 | Android: Kotlin native over React Native/Flutter | Needs direct access to MediaProjection, MediaCodec, NsdManager — no viable RN/Flutter wrappers |
| 2026-03 | Security: TOFU pairing + TLS 1.3 | Privacy-first, no server dependency, matches user mental model (like Bluetooth pairing) |
| 2026-03 | Discovery: mDNS over custom broadcast | Standards-based, works without router config, supported natively on both platforms |
| 2026-03 | Video: H.264 hardware encode/decode | Hardware support on virtually all Android devices (API 18+) and Windows (DXVA); low latency |
