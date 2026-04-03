import Foundation
import Network

/// Thread-safe, actor-isolated wrapper around an NWConnection.
/// Handles TLS (accepting self-signed certificates), wire framing, and
/// automatic PING → PONG keepalive. Callers consume messages via
/// `messageStream` and never see PING/PONG.
///
/// Usage sequence:
///   1. `connect()` — establishes TLS connection
///   2. `receiveOne()` — used during handshake/pairing phase to read specific messages
///   3. `startReceiving()` — kicks off the background receive loop; after this point
///      all messages arrive via `messageStream`
actor AirBridgeChannel {

    // MARK: - Types

    enum ChannelError: Error {
        case notConnected
        case connectionClosed
        case invalidMessageType(UInt8)
    }

    // MARK: - Internal state

    private let connection: NWConnection
    private var receiveLoopStarted = false

    // MARK: - Public stream (available after startReceiving())

    /// Continuous stream of decoded messages, excluding PING/PONG (handled internally).
    nonisolated let messageStream: AsyncStream<ProtocolMessage>
    private nonisolated let streamContinuation: AsyncStream<ProtocolMessage>.Continuation

    // MARK: - Init

    init(endpoint: NWEndpoint) {
        let params = AirBridgeChannel.makeTLSParameters()
        connection = NWConnection(to: endpoint, using: params)

        var cont: AsyncStream<ProtocolMessage>.Continuation!
        let stream = AsyncStream<ProtocolMessage> { cont = $0 }
        messageStream = stream
        streamContinuation = cont
    }

    // MARK: - TLS parameters

    private static func makeTLSParameters() -> NWParameters {
        let params = NWParameters.tls
        if let tlsOptions = params.defaultProtocolStack.applicationProtocols
                .first as? NWProtocolTLS.Options {
            // Accept all certificates (TOFU model — key pinning done at application layer)
            sec_protocol_options_set_verify_block(
                tlsOptions.securityProtocolOptions,
                { _, _, complete in complete(true) },
                .main
            )
        }
        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.enableKeepalive = true
        tcpOptions.keepaliveIdle = 30
        params.defaultProtocolStack.transportProtocol = tcpOptions
        return params
    }

    // MARK: - Connect

    /// Connects to the remote endpoint. Throws on failure or timeout (10 s).
    func connect() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            var resumed = false
            let timeoutItem = DispatchWorkItem {
                guard !resumed else { return }
                resumed = true
                self.connection.cancel()
                cont.resume(throwing: AirBridgeError.timeout)
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + 10, execute: timeoutItem)

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    timeoutItem.cancel()
                    guard !resumed else { return }
                    resumed = true
                    cont.resume()
                case .failed(let error):
                    timeoutItem.cancel()
                    guard !resumed else { return }
                    resumed = true
                    cont.resume(throwing: AirBridgeError.connectionFailed(error.localizedDescription))
                case .cancelled:
                    timeoutItem.cancel()
                    guard !resumed else { return }
                    resumed = true
                    cont.resume(throwing: AirBridgeError.connectionFailed("Connection cancelled"))
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .userInitiated))
        }
    }

    // MARK: - Send

    /// Sends a single framed message over the TLS connection.
    func send(_ message: ProtocolMessage) async throws {
        let frame = message.frame()
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            connection.send(content: frame, completion: .contentProcessed { error in
                if let error = error {
                    cont.resume(throwing: AirBridgeError.connectionFailed(error.localizedDescription))
                } else {
                    cont.resume()
                }
            })
        }
    }

    // MARK: - Direct receive (handshake / pairing phase)

    /// Reads exactly one framed message directly from the wire.
    /// Call this only before `startReceiving()` — during the handshake/pairing phase.
    func receiveOne() async throws -> ProtocolMessage {
        // Read 4-byte header (payload length)
        let header = try await readExactly(4)
        var lengthRaw: UInt32 = 0
        withUnsafeMutableBytes(of: &lengthRaw) { $0.copyBytes(from: header) }
        let payloadLength = Int(UInt32(bigEndian: lengthRaw))

        // Read 1-byte message type
        let typeByte = try await readExactly(1)
        guard let messageType = MessageType(rawValue: typeByte[0]) else {
            throw ChannelError.invalidMessageType(typeByte[0])
        }

        // Read payload
        let payload: Data
        if payloadLength > 0 {
            payload = try await readExactly(payloadLength)
        } else {
            payload = Data()
        }

        return ProtocolMessage(type: messageType, payload: payload)
    }

    // MARK: - Background receive loop

    /// Starts the background receive loop, feeding messages into `messageStream`.
    /// Call this once handshake and pairing are complete.
    func startReceiving() {
        guard !receiveLoopStarted else { return }
        receiveLoopStarted = true
        Task {
            while true {
                do {
                    let msg = try await receiveOne()
                    // Handle PING keepalive internally — never surface to callers
                    if msg.type == .ping {
                        let pong = ProtocolMessage(type: .pong, payload: Data())
                        try? await send(pong)
                        continue
                    }
                    streamContinuation.yield(msg)
                } catch {
                    break
                }
            }
            streamContinuation.finish()
        }
    }

    // MARK: - Close

    /// Cancels the underlying NWConnection.
    func close() {
        connection.cancel()
        streamContinuation.finish()
    }

    // MARK: - Private wire helpers

    /// Reads exactly `count` bytes, looping as needed.
    private func readExactly(_ count: Int) async throws -> Data {
        var collected = Data()
        while collected.count < count {
            let remaining = count - collected.count
            let chunk: Data = try await withCheckedThrowingContinuation { cont in
                connection.receive(minimumIncompleteLength: 1,
                                   maximumLength: remaining) { data, _, isComplete, error in
                    if let error = error {
                        cont.resume(throwing: AirBridgeError.connectionFailed(error.localizedDescription))
                        return
                    }
                    if isComplete && (data == nil || data!.isEmpty) {
                        cont.resume(throwing: ChannelError.connectionClosed)
                        return
                    }
                    cont.resume(returning: data ?? Data())
                }
            }
            collected.append(chunk)
        }
        return collected
    }
}
