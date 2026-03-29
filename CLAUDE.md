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
- **Merge flow:** `feature/*` → `dev` (PR + CI green) → `main` (milestone releases only)
- **Commits & pushes:** Claude commits and pushes freely. No AI attribution — the project owner is the only author in git history.

---

## Branch Strategy

| Branch | Purpose | Status |
|--------|---------|--------|
| `main` | Stable, production-ready releases | Permanent |
| `dev` | Integration — all feature branches merge here | Permanent |
| `feature/windows-transport` | Windows mDNS discovery + TLS socket layer | Ready for Iteration 2 |
| `feature/android-transport` | Android mDNS discovery + TLS socket layer | Ready for Iteration 2 |
| `feature/pairing-flow` | TOFU pairing + Ed25519 key exchange | Blocked: needs transport branches merged first |
| `feature/file-transfer` | Chunked file transfer engine (both platforms) | Blocked: needs pairing merged first |
| `feature/screen-mirror` | Phone mirroring + tablet second monitor | Blocked: needs pairing merged first |

**Rule:** All scaffold/foundation work lands directly on `dev`. Feature agents always branch from `dev`.

---

## Agent Workflow

Each major feature module is implemented by a dedicated agent on its own branch. Agents work in isolation and submit PRs to `dev`.

| Agent | Branch | Scope | Depends On |
|-------|--------|-------|------------|
| windows-transport-agent | `feature/windows-transport` | mDNS discovery (Windows), TLS server/client socket, message framing | Iteration 1 scaffold on `dev` |
| android-transport-agent | `feature/android-transport` | NsdManager discovery, TLS client socket, message framing (Kotlin) | Iteration 1 scaffold on `dev` |
| pairing-agent | `feature/pairing-flow` | Ed25519 keygen, TOFU handshake, PIN verification (both platforms) | Both transport branches merged to `dev` |
| file-transfer-agent | `feature/file-transfer` | Chunked transfer, resume, SHA-256 verify, transfer queue | `feature/pairing-flow` merged to `dev` |
| mirror-agent | `feature/screen-mirror` | MediaProjection capture, H.264 encode/decode, floating window, IddCx driver | `feature/pairing-flow` merged to `dev` |

**How to launch an agent:** Tell Claude — "work on branch `feature/X`, base off `dev`, scope is Y". The agent works in a worktree, submits changes, and you review the PR before merging.

---

## Iteration Roadmap

| Iteration | Focus | Branches |
|-----------|-------|---------|
| **1 — Scaffold** ✅ | Solution structure, interfaces, CI, protocol spec | `dev` |
| **2 — Transport** ✅ | mDNS discovery + TLS sockets on both platforms | merged to `dev` |
| **3 — Pairing** ✅ | TOFU key exchange + PIN confirmation flow | merged to `dev` |
| **4 — File Transfer** ✅ | Chunked transfer engine, SHA-256, resume, transfer queue | merged to `dev` |
| **5 — Mirror MVP** ✅ | Phone screen as floating window (view only) | merged to `dev` |
| **6 — Mirror Full** ✅ | Input relay, drag-and-drop, IddCx tablet display | merged to `dev` |
| **7 — Polish** | Unified UI, installer, performance, security audit | `dev` → `main` |
| **8 — iPad Support** | iPad as second monitor + file transfer (Swift client, VideoToolbox decoder, NWBrowser discovery) | post-`main` |

---

## Current Status

- [x] CLAUDE.md — project reference document
- [x] Branch strategy — documented and maintained
- [x] Iteration 1 — Project scaffold (solution structure, interfaces, CI, protocol spec)
- [x] Iteration 2 — Transport layer (mDNS + TLS) — merged to `dev`
- [x] Iteration 3 — Pairing flow (Ed25519 TOFU, PIN, KeyStore) — merged to `dev`
- [x] Iteration 4 — File transfer (chunked, SHA-256, resume, transfer queue) — merged to `dev`
- [x] Iteration 5 — Screen Mirror MVP (view-only phone floating window) — merged to `dev`
- [x] Iteration 6 — Mirror Full (input relay, drag-and-drop, IddCx tablet display) — merged to `dev`
- [ ] Iteration 7 — Polish + release

**Git state:** active work branch is `feature/android-transport-tests`.
`dev` and `main` exist. `main` will be updated at the Iteration 7 milestone release.

### What is confirmed working (live-tested 2026-03-29)
- File transfer both directions (Windows↔Android), hash verified, ~5MB in <1s ✓
- TLS connection stable, PING/PONG keepalive at 200-300ms RTT ✓
- Infinite reconnect loop with exponential backoff (2s→60s) ✓
- WakeLock + WifiLock + Foreground Service on Android ✓
- Black-box file logging both sides (`%TEMP%\AirBridge\airbridge.log` / Android `filesDir/airbridge.log`) ✓

### What is not yet working
- **Phone as Floating Window (mirror)**: session negotiates (Connecting→Active) but mirror frames never render — next test needed after latest mirror fix
- **Tablet as Second Monitor**: not yet tested end-to-end
- **Input relay** (mouse/keyboard → Android): not yet tested

### Recent bug fixes (branch `feature/android-transport-tests`, not yet merged to `dev`)
All fixes are committed and verified to compile. Require rebuild + retest:

1. `fix(stability)` — ObjectDisposedException on SendAsync race; double session registration; HANDSHAKE diagnostic logging; duplicate Android log entries
2. `fix(mirror)` — concurrent SslStream read crash (NotSupportedException): MirrorSession now receives via internal Channel<ProtocolMessage> fed by DeviceConnectionService dispatch, not by reading the channel directly
3. `fix(mirror)` — ghost keepalive loops: TlsMessageChannel.DisposeAsync now cancels the keepalive CancellationTokenSource immediately

### Known remaining issues
- **COMException on progress bar** (intermittent): ProgressChanged callback triggers WinUI binding on wrong thread in some sessions. Needs one more investigation session.
- **Tablet display**: not tested; shares transport with mirror so mirror fix may unblock it.

---

## Live Debugging Workflow

This is the standard approach for diagnosing bugs using real-device sessions. Use the `/monitor-logs` skill (saved in project settings) to automate steps 1–4.

### Log locations
| Side | Location | Notes |
|------|----------|-------|
| Windows | `%TEMP%\AirBridge\airbridge.log` | Rotates at 500KB. File is held open by the app — use `Get-Content -Wait` to stream. |
| Android | `adb logcat` filtered to AirBridge tags | Tags: `AirBridge/ConnSvc:D AirBridge/Transfer:D AirBridge/Channel:D AndroidRuntime:E *:S` |

### adb path
`C:\Users\nimro\AppData\Local\Android\Sdk\platform-tools\adb.exe` — invoke via `powershell.exe -Command "& 'path' ..."` (bash cannot use backslash paths directly).

### Session procedure
1. Clear Android logcat: `adb logcat -c`
2. Windows log: the app holds it open — do NOT try to delete/truncate; just note the timestamp and read new lines after the test.
3. Start Android stream in background: `adb logcat AirBridge/ConnSvc:D AirBridge/Transfer:D AirBridge/Channel:D AndroidRuntime:E *:S`
4. Start Windows stream in background: `Get-Content 'C:\Users\nimro\AppData\Local\Temp\AirBridge\airbridge.log' -Wait`
5. Tell user: "Monitoring active — Android stream ID: X, Windows stream ID: Y. Do your test."
6. After test: read both outputs with `Select-Object -First N` / `-Last N` (files can exceed 10k token limit).
7. Analyse: session registration on both sides, chunk flow, any exception, last good line, root cause.

### What to look for
- `NotSupportedException: another read operation is pending` → concurrent SslStream reads (architectural)
- `ObjectDisposedException: SemaphoreSlim` → SendAsync race with session disposal
- `PONG timeout` without a preceding `PING sent` → ghost keepalive on a dead channel
- `Non-pairing first message` from a known device → IsPaired returning false (check HANDSHAKE payload log)
- `COMException` in WinUI → UI element updated from a non-UI thread (needs `DispatcherQueue.TryEnqueue`)
- Double session registration (same device ID registered twice < 30s apart) → race between inbound + outbound connect

---

## Agent Briefing (read this before starting any iteration)

This section exists so every agent understands the full picture before writing code — not just the ticket, but how it fits the product.

### What AirBridge is (one paragraph)
AirBridge is a peer-to-peer local-network app: a Windows desktop host and an Android client communicate directly over Wi-Fi — no accounts, no servers, no internet. The three end-user features are: (1) drag-and-drop file transfer between PC and phone/tablet, (2) the Android tablet acting as a wired-free second monitor for Windows, and (3) the Android phone appearing as a floating window on the PC you can interact with. Every layer you build is infrastructure for one of these three visible features.

### How the layers connect to the product
```
User Feature          Depends On
─────────────────────────────────────────────────────
File Transfer         Pairing → Transport → Discovery
Tablet Second Monitor Pairing → Transport → Discovery
Phone Floating Window Pairing → Transport → Discovery
```
- **Discovery (Iteration 2):** Windows and Android find each other on the LAN using mDNS (`_airbridge._tcp.local`). Without this, devices are invisible to each other.
- **Transport (Iteration 2):** Raw TLS 1.3 TCP sockets + a binary framing layer. All subsequent features send their data through this channel.
- **Pairing (Iteration 3):** Ed25519 TOFU handshake. Devices exchange public keys and confirm a 6-digit PIN. After pairing, every connection is mutually authenticated — no anonymous access ever.
- **File Transfer (Iteration 4):** Chunked binary transfer over the paired transport channel. SHA-256 verify, resume support, progress callbacks. This is the first feature the end user can see working end-to-end.
- **Screen Mirror (Iteration 5/6):** MediaProjection → H.264 MediaCodec → TLS channel → Windows decoder → floating window or IddCx virtual display. Latency target <100ms.

### Technologies per layer (quick reference)
| Layer | Windows tech | Android tech |
|-------|-------------|--------------|
| Discovery | Win32 DnsServiceBrowse / managed mDNS lib | `NsdManager` |
| Transport | `System.Net.Sockets` + `SslStream` | Java NIO + Kotlin coroutines |
| Pairing | `System.Security.Cryptography` ECDsa (Ed25519) | `java.security` / Bouncy Castle |
| File Transfer | `System.IO`, async streams | Kotlin coroutines + `java.io` |
| Screen capture | — | `MediaProjection` + `MediaCodec` |
| Screen render | Win2D / Direct3D surface | `SurfaceView` |
| Virtual display | IddCx driver (C++) | — |
| UI | WinUI 3 (C#) | Jetpack Compose / XML layouts |
| DI | Manual / MS DI extensions | Hilt |

### What was built in previous iterations
- **Iteration 1 (Scaffold):** Solution/project structure, all public interfaces (`IPairingService`, `IDiscoveryService`, `IConnectionManager`, `IMessageChannel`, `ITransferSession`, `IMirrorSession`), CI pipeline, protocol spec in `/protocol/`.
- **Iteration 2 (Transport):** mDNS advertisement + browsing on both platforms; TLS server and client sockets; binary message framing (length-prefix + message type byte). Both platforms can now discover each other and open an authenticated socket. Code lives in `AirBridge.Transport` (C#) and `android/app/core/` transport package (Kotlin).
- **Iteration 3 (Pairing):** Ed25519 key generation, persistent `KeyStore` (local file, no cloud), TOFU handshake logic, 6-digit PIN generation and verification. Code lives in `AirBridge.Core/Pairing/` and `android/.../core/pairing/`. Hilt DI wiring in `CoreModule.kt`.

### How to know when your work is testable by the owner
State clearly in your PR description or commit message:
- **What the owner can run** — e.g. "run `dotnet test` to verify unit tests pass", or "install the APK and tap Pair — you should see a 6-digit PIN on both devices".
- **Prerequisites** — e.g. "both devices on the same Wi-Fi", "Android Studio emulator with API 30+".
- **Expected observable behavior** — describe exactly what the owner will see/hear/read in the UI or logs, not just "it works".
When a feature is not yet wired to a UI, provide a minimal test harness (console runner, unit test, or adb command) so the owner can verify the logic is live.

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03 | Windows: WinUI 3 + C# over Electron | Virtual display driver requires native Windows kernel APIs; lower latency; no ~100MB Electron overhead |
| 2026-03 | Android: Kotlin native over React Native/Flutter | Needs direct access to MediaProjection, MediaCodec, NsdManager — no viable RN/Flutter wrappers |
| 2026-03 | Security: TOFU pairing + TLS 1.3 | Privacy-first, no server dependency, matches user mental model (like Bluetooth pairing) |
| 2026-03 | Discovery: mDNS over custom broadcast | Standards-based, works without router config, supported natively on both platforms |
| 2026-03 | Video: H.264 hardware encode/decode | Hardware support on virtually all Android devices (API 18+) and Windows (DXVA); low latency |
| 2026-03 | Future: iPad client as next major milestone | iPad is the dominant tablet globally. Existing solutions (Duet, Luna, Spacedesk) are laggy, require accounts/dongles, or are Mac-only. A zero-config Windows→iPad second monitor is an unsolved market gap and the highest-leverage expansion after Iteration 7. Windows IddCx + H.264 stream is already built — only an iPadOS Swift client is needed. App Store precedent exists (Duet, Spacedesk are approved). |
