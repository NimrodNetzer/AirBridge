import SwiftUI

/// Shows a list of discovered Windows AirBridge hosts on the local network.
struct DeviceListView: View {
    @EnvironmentObject var viewModel: AppViewModel
    @State private var manualIP: String = ""
    @State private var showManualConnect = false

    var body: some View {
        NavigationView {
            Group {
                if viewModel.discoveredDevices.isEmpty {
                    searchingView
                } else {
                    deviceList
                }
            }
            .navigationTitle("AirBridge")
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    statusBadge
                }
                if viewModel.connectionState == .active ||
                   viewModel.connectionState == .connecting ||
                   viewModel.connectionState == .connected {
                    ToolbarItem(placement: .navigationBarLeading) {
                        disconnectButton
                    }
                }
            }
        }
        .navigationViewStyle(.stack)
    }

    // MARK: - Subviews

    private var searchingView: some View {
        VStack(spacing: 20) {
            ProgressView()
                .scaleEffect(1.5)
            Text("Searching for Windows PCs…")
                .font(.headline)
                .foregroundColor(.secondary)
            Text("Make sure AirBridge is running on your PC\nand both devices are on the same Wi-Fi network.")
                .multilineTextAlignment(.center)
                .foregroundColor(.secondary)
                .padding(.horizontal)
            manualConnectSection
        }
    }

    private var manualConnectSection: some View {
        VStack(spacing: 12) {
            Divider().padding(.horizontal)
            Text("Can't find your PC? Enter its IP address manually.")
                .font(.caption)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal)
            HStack(spacing: 8) {
                TextField("e.g. 192.168.1.100", text: $manualIP)
                    .textFieldStyle(.roundedBorder)
                    .keyboardType(.numbersAndPunctuation)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                Button("Connect") {
                    viewModel.connectManually(host: manualIP, port: 47821)
                }
                .buttonStyle(.borderedProminent)
                .disabled(manualIP.isEmpty || viewModel.connectionState != .idle)
            }
            .padding(.horizontal)
        }
    }

    private var deviceList: some View {
        List(viewModel.discoveredDevices) { device in
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(device.name)
                        .font(.headline)
                    Text("Windows PC")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                }
                Spacer()
                connectButton(for: device)
            }
            .padding(.vertical, 4)
        }
    }

    private func connectButton(for device: DiscoveredDevice) -> some View {
        Button {
            viewModel.connect(to: device)
        } label: {
            switch viewModel.connectionState {
            case .connecting:
                ProgressView()
                    .frame(width: 80)
            case .connected, .pairing, .active:
                Text("Connected")
                    .foregroundColor(.green)
                    .frame(width: 80)
            default:
                Text("Connect")
                    .frame(width: 80)
            }
        }
        .buttonStyle(.borderedProminent)
        .disabled(viewModel.connectionState != .idle &&
                  viewModel.connectionState != .error(""))
    }

    private var statusBadge: some View {
        HStack(spacing: 4) {
            Circle()
                .fill(statusColor)
                .frame(width: 10, height: 10)
            Text(statusLabel)
                .font(.caption)
        }
    }

    private var statusColor: Color {
        switch viewModel.connectionState {
        case .idle:        return .gray
        case .connecting:  return .orange
        case .connected:   return .yellow
        case .pairing:     return .blue
        case .active:      return .green
        case .error:       return .red
        }
    }

    private var statusLabel: String {
        switch viewModel.connectionState {
        case .idle:        return "Idle"
        case .connecting:  return "Connecting…"
        case .connected:   return "Connected"
        case .pairing:     return "Pairing…"
        case .active:      return "Active"
        case .error:       return "Error"
        }
    }

    private var disconnectButton: some View {
        Button("Disconnect") {
            Task { await viewModel.disconnect() }
        }
        .foregroundColor(.red)
    }
}
