# Transfer Engine — Implementation Notes (Iteration 4)

## What was built

### Windows (`windows/AirBridge.Transfer/`)
| File | Purpose |
|------|---------|
| `TransferMessage.cs` | Wire-format records: `FileStartMessage`, `FileChunkMessage`, `TransferAckMessage`, `FileEndMessage`, `TransferErrorMessage`. Manual big-endian binary read/write via `BinaryPrimitives` — no external serialization library. |
| `TransferSession.cs` | Implements `ITransferSession`. 64 KB chunked sender and receiver loops over any `Stream` pair. Incremental SHA-256 via `IncrementalHash`. Progress reported via `ProgressChanged` event and optional `IProgress<long>`. Pause/cancel via `CancellationToken` cooperation. |
| `TransferQueue.cs` | Configurable-concurrency session queue backed by `SemaphoreSlim`. Exposes `EnqueueAsync`, `PauseAllAsync`, `CancelAllAsync`. |

### Android (`android/app/src/main/java/com/airbridge/app/transfer/`)
| File | Purpose |
|------|---------|
| `TransferMessage.kt` | Kotlin mirror of the C# message types. `ByteBuffer` big-endian read/write. `FileEndMessage` and `FileChunkMessage` include manual `equals`/`hashCode` for `ByteArray` fields. |
| `TransferSession.kt` | Implements `ITransferSession`. Same protocol as the C# side. `Flow<TransferState>` and `Flow<Long>` for UI binding. `DataInputStream.readFully` for exact reads. |
| `TransferQueue.kt` | Coroutines-based queue. `Channel<ITransferSession>` as the work queue; `[concurrency]` worker coroutines drain it. `cancelAll` / `pauseAll` iterate over the tracked session list. |

---

## Running the tests

### Windows
```
cd windows
dotnet test AirBridge.Tests/AirBridge.Tests.csproj --filter "FullyQualifiedName~Transfer"
```
All tests should pass. The loopback tests use `System.IO.Pipelines.Pipe` to wire sender and receiver without network sockets.

### Android
```
cd android
./gradlew :app:testDebugUnitTest --tests "com.airbridge.app.transfer.*"
```
All tests should pass. Loopback tests use `PipedInputStream` / `PipedOutputStream`.

---

## Observable behaviour (no UI yet)

- **Sender** reads a source `Stream` in 64 KB chunks, writes length-prefixed binary frames to the network stream, and finalises with a SHA-256 hash frame.
- **Receiver** reads frames, writes data to the destination stream, and throws `InvalidDataException` / `SecurityException` if the final hash does not match.
- **Progress** is updated after every chunk (~64 KB resolution).
- **Cancel** is cooperative: calling `CancelAsync()` / `cancel()` cancels the running coroutine/token and transitions state to `Cancelled`.
- **Pause** stores `Paused` state and cancels the running token; the caller must call `ResumeAsync()` / `resume()` and re-invoke `StartAsync()` / `start()` to continue.

## What is NOT wired to UI

The transfer engine is pure business logic. Nothing calls it yet. A minimal manual test would be:

1. Create two `TransferSession` objects backed by `PipeStream` / `PipedStream` on either side.
2. Call `sender.StartAsync()` and `receiver.StartAsync()` concurrently.
3. Verify the receiver's destination stream contains the same bytes as the source.

The `IFileTransferService` interface in `AirBridge.Transfer/Interfaces/` provides the higher-level API that a future service layer (wired to the transport `IMessageChannel`) will implement to connect sessions to real TLS sockets.
