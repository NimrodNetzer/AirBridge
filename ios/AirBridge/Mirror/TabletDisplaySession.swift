import Foundation
import CoreMedia
import Combine

// MARK: - Session state

enum SessionState: Equatable {
    case connecting
    case waitingForStream
    case active
    case stopped
    case error(String)

    static func == (lhs: SessionState, rhs: SessionState) -> Bool {
        switch (lhs, rhs) {
        case (.connecting, .connecting),
             (.waitingForStream, .waitingForStream),
             (.active, .active),
             (.stopped, .stopped):
            return true
        case (.error(let a), .error(let b)):
            return a == b
        default:
            return false
        }
    }
}

// MARK: - TabletDisplaySession

/// Drives a single tablet-display mirror session.
/// Wraps an `AirBridgeChannel` (already connected + handshaked + paired)
/// and processes MIRROR_START / MIRROR_FRAME / MIRROR_STOP messages.
@MainActor
final class TabletDisplaySession: ObservableObject {

    // MARK: - Published state

    @Published private(set) var state: SessionState = .connecting
    @Published private(set) var latestSampleBuffer: CMSampleBuffer?

    // MARK: - Private

    private let channel: AirBridgeChannel
    private let decoder = VideoDecoder()
    private var sessionId: String = ""
    private var receiveTask: Task<Void, Never>?

    // MARK: - Init

    init(channel: AirBridgeChannel) {
        self.channel = channel
    }

    // MARK: - Lifecycle

    /// Starts the message receive loop. Call after handshake + pairing are complete.
    func start() {
        state = .waitingForStream
        receiveTask = Task { [weak self] in
            guard let self = self else { return }
            for await message in await channel.messageStream {
                await self.handle(message)
                if await self.state == .stopped { break }
            }
            await MainActor.run { self.state = .stopped }
        }
    }

    /// Sends MIRROR_STOP and cancels the receive loop.
    func stop() async {
        let stopMsg = MirrorStopMessage(reasonCode: 0)
        let payload = stopMsg.toPayload()
        // payload already has type byte at [0]
        let message = ProtocolMessage(type: .mirrorStop, payload: payload)
        try? await channel.send(message)
        receiveTask?.cancel()
        state = .stopped
        await channel.close()
    }

    /// Sends a touch/input event to the Windows host.
    func sendTouchEvent(normalizedX: Float,
                        normalizedY: Float,
                        kind: InputEventKind = .touch) async {
        let event = InputEventMessage(
            sessionId: sessionId,
            kind: kind,
            normalizedX: normalizedX,
            normalizedY: normalizedY,
            keycode: nil,
            metaState: 0
        )
        let payload = event.toPayload()
        let message = ProtocolMessage(type: .inputEvent, payload: payload)
        try? await channel.send(message)
    }

    // MARK: - Message dispatch

    private func handle(_ message: ProtocolMessage) async {
        switch message.type {
        case .mirrorStart:
            await handleMirrorStart(message.payload)
        case .mirrorFrame:
            await handleMirrorFrame(message.payload)
        case .mirrorStop:
            state = .stopped
        case .ping:
            // PING is handled inside AirBridgeChannel; should not reach here
            break
        default:
            break
        }
    }

    private func handleMirrorStart(_ payload: Data) async {
        do {
            let msg = try MirrorStartMessage.fromPayload(payload)
            sessionId = msg.sessionId
            state = .active
        } catch {
            state = .error("MIRROR_START parse error: \(error.localizedDescription)")
        }
    }

    private func handleMirrorFrame(_ payload: Data) async {
        do {
            let msg = try MirrorFrameMessage.fromPayload(payload)
            let pts = CMTimeMake(value: msg.pts, timescale: 1_000_000)
            if let sampleBuffer = decoder.decode(
                nalData: msg.nalData,
                pts: pts,
                isKeyFrame: msg.isKeyFrame
            ) {
                latestSampleBuffer = sampleBuffer
            }
        } catch {
            // Frame decode errors are non-fatal
        }
    }
}
