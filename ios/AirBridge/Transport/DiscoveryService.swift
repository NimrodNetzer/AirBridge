import Foundation
import Network

/// A device discovered via mDNS on the local network.
struct DiscoveredDevice: Identifiable, Equatable {
    let id: String         // Unique identifier derived from the mDNS result name
    let name: String       // Human-readable display name
    let endpoint: NWEndpoint

    static func == (lhs: DiscoveredDevice, rhs: DiscoveredDevice) -> Bool {
        lhs.id == rhs.id
    }
}

/// Browses for `_airbridge._tcp` services on the local network using NWBrowser.
@MainActor
final class DiscoveryService: ObservableObject {

    @Published private(set) var discoveredDevices: [DiscoveredDevice] = []

    private var browser: NWBrowser?

    // MARK: - Start / stop

    func start() {
        let descriptor = NWBrowser.Descriptor.bonjour(type: "_airbridge._tcp", domain: nil)
        let params = NWParameters.tcp
        let browser = NWBrowser(for: descriptor, using: params)
        self.browser = browser

        browser.stateUpdateHandler = { [weak self] state in
            guard let self = self else { return }
            Task { @MainActor in
                switch state {
                case .failed(let error):
                    print("[DiscoveryService] Browser failed: \(error)")
                    self.stop()
                    self.start() // retry
                default:
                    break
                }
            }
        }

        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self = self else { return }
            Task { @MainActor in
                var devices: [DiscoveredDevice] = []
                for result in results {
                    if case let .service(name, _, _, _) = result.endpoint {
                        let device = DiscoveredDevice(
                            id: name,
                            name: name,
                            endpoint: result.endpoint
                        )
                        devices.append(device)
                    }
                }
                self.discoveredDevices = devices
            }
        }

        browser.start(queue: .global(qos: .userInitiated))
    }

    func stop() {
        browser?.cancel()
        browser = nil
        discoveredDevices = []
    }
}
