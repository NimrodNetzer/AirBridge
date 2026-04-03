import SwiftUI

/// Displays the 6-digit pairing PIN during the TOFU pairing flow.
/// The user enters the PIN on the Windows PC to confirm.
struct PairingView: View {
    @EnvironmentObject var viewModel: AppViewModel

    var body: some View {
        VStack(spacing: 32) {
            Text("Pair with Windows PC")
                .font(.title)
                .bold()
                .foregroundColor(.white)

            Text("Show this PIN on your Windows PC and confirm.")
                .font(.subheadline)
                .foregroundColor(.white.opacity(0.8))
                .multilineTextAlignment(.center)
                .padding(.horizontal, 40)

            if let pin = viewModel.pairingPin {
                Text(pin)
                    .font(.system(size: 64, weight: .bold, design: .monospaced))
                    .foregroundColor(.white)
                    .padding(24)
                    .background(Color.white.opacity(0.15))
                    .cornerRadius(16)
                    .accessibilityLabel("Pairing PIN: \(pin.map(String.init).joined(separator: " "))")
            } else {
                ProgressView()
                    .progressViewStyle(CircularProgressViewStyle(tint: .white))
                    .scaleEffect(2)
            }

            Button {
                viewModel.rejectPairing()
            } label: {
                Text("Cancel")
                    .foregroundColor(.white)
                    .frame(width: 160, height: 44)
                    .background(Color.red.opacity(0.8))
                    .cornerRadius(10)
            }
        }
        .padding(40)
        .background(Color.black.opacity(0.5))
        .cornerRadius(20)
        .padding(60)
    }
}
