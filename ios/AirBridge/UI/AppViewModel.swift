import Foundation
import Network
import CryptoKit
import UIKit

// MARK: - Connection state

enum ConnectionState: Equatable {
    case idle
    case connecting
    case connected
    case pairing
    case active
    case error(String)

    static func == (lhs: ConnectionState, rhs: ConnectionState) -> Bool {
        switch (lhs, rhs) {
        case (.idle, .idle), (.connecting, .connecting),
             (.connected, .connected), (.pairing, .pairing),
             (.active, .active):
            return true
        case (.error(let a), .error(let b)):
            return a == b
        default:
            return false
        }
    }
}

// MARK: - AppViewModel

@MainActor
final class AppViewModel: ObservableObject {

    // MARK: - Published

    @Published var discoveredDevices: [DiscoveredDevice] = []
    @Published var connectionState: ConnectionState = .idle
    @Published var pairingPin: String?
    @Published var currentSession: TabletDisplaySession?
    @Published var errorMessage: String?

    // MARK: - Services

    private let discoveryService = DiscoveryService()
    private let keychainStore = KeychainStore()
    private lazy var pairingService = PairingService(keychainStore: keychainStore)

    private var activeChannel: AirBridgeChannel?
    private var connectTask: Task<Void, Never>?

    // MARK: - Init

    init() {
        discoveryService.$discoveredDevices
            .receive(on: RunLoop.main)
            .assign(to: &$discoveredDevices)
        discoveryService.start()
    }

    // MARK: - Connect

    /// Initiates a connection to the selected device, running the full
    /// handshake + pairing flow, then handing off to a TabletDisplaySession.
    func connect(to device: DiscoveredDevice) {
        connectTask?.cancel()
        connectTask = Task {
            do {
                connectionState = .connecting

                let channel = AirBridgeChannel(endpoint: device.endpoint)
                activeChannel = channel
                try await channel.connect()

                connectionState = .connected

                // ------ HANDSHAKE ------
                // Send and receive HANDSHAKE concurrently.
                // We use receiveOne() here (before startReceiving()) so that
                // the background receive loop has not yet started.
                let localHandshake = HandshakeMessage(
                    deviceId: getOrCreateDeviceId(),
                    deviceName: UIDevice.current.name,
                    deviceType: "ipad"
                )
                let handshakePayload = try localHandshake.toPayload()
                async let sendTask: Void = channel.send(
                    ProtocolMessage(type: .handshake, payload: handshakePayload)
                )

                // Read messages until we see the HANDSHAKE (ignore unexpected types)
                var remoteHandshake: HandshakeMessage?
                while remoteHandshake == nil {
                    let msg = try await channel.receiveOne()
                    if msg.type == .handshake {
                        remoteHandshake = try HandshakeMessage.fromPayload(msg.payload)
                    }
                }
                try await sendTask

                guard let remote = remoteHandshake else {
                    throw AirBridgeError.connectionFailed("No HANDSHAKE received")
                }

                // ------ PAIRING ------
                if !keychainStore.isPaired(deviceId: remote.deviceId) {
                    connectionState = .pairing
                    let pin = pairingService.generatePin()
                    pairingPin = pin
                    let accepted = try await pairingService.requestPairing(
                        channel: channel,
                        deviceId: remote.deviceId,
                        pin: pin
                    )
                    pairingPin = nil
                    if !accepted {
                        connectionState = .error("Pairing rejected by Windows host")
                        return
                    }
                }

                // ------ SESSION ------
                // All setup done — hand the channel to the session and start the loop.
                await channel.startReceiving()
                let session = TabletDisplaySession(channel: channel)
                currentSession = session
                session.start()
                connectionState = .active

            } catch is CancellationError {
                connectionState = .idle
            } catch {
                connectionState = .error(error.localizedDescription)
                activeChannel = nil
            }
        }
    }

    // MARK: - Pairing actions (called by UI)

    /// The PIN is informational only — confirm is a no-op (Windows user confirms on their side).
    func confirmPairing() {}

    func rejectPairing() {
        connectTask?.cancel()
        Task { await activeChannel?.close() }
        activeChannel = nil
        connectionState = .idle
        pairingPin = nil
    }

    // MARK: - Disconnect

    func disconnect() async {
        connectTask?.cancel()
        await currentSession?.stop()
        currentSession = nil
        activeChannel = nil
        connectionState = .idle
        pairingPin = nil
    }

    // MARK: - Helpers

    private let deviceIdKey = "com.airbridge.deviceId"

    /// Returns a stable device UUID, creating one on first launch.
    private func getOrCreateDeviceId() -> String {
        if let existing = UserDefaults.standard.string(forKey: deviceIdKey) {
            return existing
        }
        let newId = UUID().uuidString
        UserDefaults.standard.set(newId, forKey: deviceIdKey)
        return newId
    }
}
