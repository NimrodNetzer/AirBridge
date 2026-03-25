using AirBridge.Core.Models;

namespace AirBridge.Core.Interfaces;

/// <summary>Outcome of a pairing attempt.</summary>
public enum PairingResult { Success, RejectedByUser, Timeout, AlreadyPaired, Error }

/// <summary>
/// Handles the device pairing handshake (TOFU model).
/// Generates and stores Ed25519 key pairs; verifies remote keys via PIN.
/// </summary>
public interface IPairingService
{
    /// <summary>
    /// Initiates a pairing request to a remote device.
    /// Returns once the user on both sides confirms the PIN, or the attempt fails.
    /// </summary>
    Task<PairingResult> RequestPairingAsync(DeviceInfo remoteDevice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an incoming pairing request from a remote device.
    /// Implementor should surface the PIN to the user for confirmation.
    /// </summary>
    Task<PairingResult> AcceptPairingAsync(string remoteDeviceId, byte[] remotePublicKey, string pin, CancellationToken cancellationToken = default);

    /// <summary>Revokes pairing with a device, deleting its stored key.</summary>
    Task RevokePairingAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Returns the local device's Ed25519 public key bytes.</summary>
    byte[] GetLocalPublicKey();
}
