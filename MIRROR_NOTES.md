# AirBridge — Iteration 5 Mirror MVP: Verification Notes

## What was built

**Phone-as-floating-window screen mirror (view-only).**

### Protocol additions
`protocol/v1/spec.md` updated with binary layout documentation for three new message types:
- `MirrorStart` (0x20) — Android → Windows: announces width, height, fps, codec
- `MirrorFrame` (0x21) — Android → Windows: H.264 NAL data + timestamp + keyframe flag
- `MirrorStop`  (0x22) — either direction: graceful teardown

### Android (`android/app/src/main/java/com/airbridge/app/mirror/`)
| File | What it does |
|------|-------------|
| `MirrorMessage.kt` | Serialization/deserialization for MirrorStart, MirrorFrame, MirrorStop |
| `ScreenCaptureSession.kt` | MediaProjection → MediaCodec H.264 encoder → SharedFlow of MirrorFrameMessage |
| `MirrorSession.kt` | Wires ScreenCaptureSession to IMessageChannel; sends MirrorStart, streams MirrorFrame, sends MirrorStop |
| `di/MirrorModule.kt` | Hilt binding: IMirrorService → MirrorService |

### Windows (`windows/AirBridge.Mirror/`)
| File | What it does |
|------|-------------|
| `MirrorMessage.cs` | Serialization/deserialization for MirrorStart, MirrorFrame, MirrorStop |
| `IMirrorDecoder.cs` | Abstraction over the H.264 decode pipeline (enables unit testing) |
| `MirrorDecoder.cs` | Windows Media Foundation MediaStreamSource H.264 decoder; implements IMirrorDecoder |
| `IMirrorWindowHost.cs` | Abstraction over the floating window (enables headless unit testing) |
| `MirrorWindow.cs` | Frameless always-on-top WinUI 3 Window; MediaPlayerElement renders the stream |
| `MirrorSession.cs` | State machine: receives MirrorStart → open window; MirrorFrame → decode; MirrorStop → close |

---

## How to verify

### Run unit tests (no hardware required)

**Windows:**
```
cd windows
dotnet test AirBridge.sln
```
Expected: all existing tests pass + new mirror tests in `AirBridge.Tests/Mirror/` pass.

**Android:**
```
cd android
./gradlew testDebugUnitTest
```
Expected: all existing tests pass + new `MirrorMessageTest` in `app/src/test/.../mirror/` passes.

---

## End-to-end smoke test (requires hardware)

### Prerequisites
- Windows PC and Android phone on the **same Wi-Fi network**
- Both devices are already paired (Iteration 3)
- Android Studio with an API 26+ device or emulator (note: `MediaProjection` is not available in the emulator — a physical device is required for actual capture)
- Windows App SDK 1.5 installed on the PC

### Steps

1. **Android side — start a mirror session:**
   In a Kotlin scratch or integration test, or by adding a temporary UI button:
   ```kotlin
   // 1. Get MediaProjection (requires user to accept the system prompt)
   val projection: MediaProjection = // ... from MediaProjectionManager
   val capture = ScreenCaptureSession(projection, screenDensityDpi = resources.displayMetrics.densityDpi)
   capture.start(sessionId = "test", width = 1080, height = 1920, fps = 30)

   // 2. Get the active IMessageChannel to the Windows peer
   val channel: IMessageChannel = // ... from TlsConnectionManager

   // 3. Create and start the mirror session
   val session = MirrorSession("test", channel, capture, 1080, 1920, 30)
   lifecycleScope.launch { session.start() }
   ```

2. **Windows side — receive and display:**
   The `MirrorSession` receive loop is driven by calling `StartAsync()` once the channel is open.
   Add a temporary entry point in the app or run a console test:
   ```csharp
   var session = new MirrorSession("test", channel,
       decoderFactory: () => new MirrorDecoder(),
       windowFactory:  d  => new MirrorWindow(d));
   await session.StartAsync();
   ```

3. **Expected observable behaviour:**
   - A frameless black window appears on the Windows PC within ~1 second of the Android session starting
   - The window is always-on-top and has no title bar or border
   - The phone screen is rendered inside the window at the stream resolution
   - Closing the window on Windows (or calling `stop()` on Android) tears down both sides cleanly

---

## Known limitations (Iteration 5)

- **No input relay**: clicking or typing in the mirror window has no effect on the Android device. This is Iteration 6 scope.
- **No IddCx tablet display**: the second-monitor feature is Iteration 6.
- **No resume after disconnect**: if the TLS channel drops, a new pairing + connection is required.
- **Physical device required for capture**: `MediaProjection` is not emulated; `ScreenCaptureSession` tests are manual/integration only.
- **WinAppSDK required**: `MirrorWindow` and `MirrorDecoder` compile against `Microsoft.WindowsAppSDK 1.5`. Ensure the package is restored before building (`dotnet restore`).
