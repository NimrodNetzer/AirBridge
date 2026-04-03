import SwiftUI

/// Root view. Shows the device list when idle, overlays the full-screen
/// display when a mirror session is active, and overlays the pairing PIN
/// when pairing is in progress.
struct ContentView: View {
    @EnvironmentObject var viewModel: AppViewModel

    var body: some View {
        ZStack {
            DeviceListView()
                .environmentObject(viewModel)

            // Full-screen mirror overlay
            if viewModel.connectionState == .active,
               let session = viewModel.currentSession {
                DisplayView(session: session)
                    .ignoresSafeArea()
                    .transition(.opacity)
            }

            // Pairing PIN overlay
            if viewModel.connectionState == .pairing {
                Color.black.opacity(0.6).ignoresSafeArea()
                PairingView()
                    .environmentObject(viewModel)
            }

            // Error banner
            if case .error(let msg) = viewModel.connectionState {
                VStack {
                    Spacer()
                    Text(msg)
                        .foregroundColor(.white)
                        .padding()
                        .background(Color.red.opacity(0.85))
                        .cornerRadius(10)
                        .padding()
                }
            }
        }
        .animation(.easeInOut, value: viewModel.connectionState)
    }
}
