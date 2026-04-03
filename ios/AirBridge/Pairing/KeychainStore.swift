import Foundation
import Security
import CryptoKit

/// Wraps Security.framework Keychain for persistent key storage.
/// Stores:
/// - The local Ed25519 private key (generated once)
/// - Per-device paired Windows public key, indexed by Windows deviceId
final class KeychainStore {

    private let service = "com.airbridge.app.ios"

    // MARK: - Local identity key

    /// Returns the existing local Ed25519 private key, or generates and stores one.
    func getOrCreatePrivateKey() throws -> Curve25519.Signing.PrivateKey {
        let account = "local_private_key"
        if let existing = try? loadRaw(account: account) {
            return try Curve25519.Signing.PrivateKey(rawRepresentation: existing)
        }
        let key = Curve25519.Signing.PrivateKey()
        try saveRaw(key.rawRepresentation, account: account)
        return key
    }

    // MARK: - Peer keys

    /// Stores the Windows device's public key raw bytes, indexed by its deviceId.
    func storePeerKey(_ key: Data, for deviceId: String) throws {
        let account = peerAccount(deviceId)
        // Delete existing entry first (Keychain does not update on duplicate)
        deleteItem(account: account)
        try saveRaw(key, account: account)
    }

    /// Returns the stored public key for a Windows device, or nil if not paired.
    func getPeerKey(for deviceId: String) -> Data? {
        try? loadRaw(account: peerAccount(deviceId))
    }

    /// Returns true if a peer key exists for the given deviceId.
    func isPaired(deviceId: String) -> Bool {
        getPeerKey(for: deviceId) != nil
    }

    // MARK: - Helpers

    private func peerAccount(_ deviceId: String) -> String { "peer_\(deviceId)" }

    private func saveRaw(_ data: Data, account: String) throws {
        let query: [String: Any] = [
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecValueData as String:   data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw AirBridgeError.pairingFailed("Keychain save failed: \(status)")
        }
    }

    private func loadRaw(account: String) throws -> Data {
        let query: [String: Any] = [
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String:  true,
            kSecMatchLimit as String:  kSecMatchLimitOne
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else {
            throw AirBridgeError.pairingFailed("Keychain load failed: \(status)")
        }
        return data
    }

    private func deleteItem(account: String) {
        let query: [String: Any] = [
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)
    }
}
