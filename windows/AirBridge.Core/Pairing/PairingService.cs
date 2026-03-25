using System.Security.Cryptography;
using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;

namespace AirBridge.Core.Pairing;

/// <summary>
/// Core pairing service — manages key storage, PIN generation, and pairing state.
/// Protocol-level message exchange (sending PairingRequest over a channel) is handled
/// by PairingCoordinator in AirBridge.Transport, which calls back into this service.
/// </summary>
public sealed class PairingService : IPairingService
{
    private readonly KeyStore _keyStore;

    /// <summary>Raised when a PIN is generated, so the UI layer can display it.</summary>
    public event EventHandler<string>? PinGenerated;

    public PairingService(KeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The actual channel handshake is performed by PairingCoordinator in Transport.
    /// This method is a fallback — returns AlreadyPaired if already paired, Error otherwise.
    /// </remarks>
    public Task<PairingResult> RequestPairingAsync(
        DeviceInfo remoteDevice,
        CancellationToken cancellationToken = default)
    {
        var result = _keyStore.HasRemoteKey(remoteDevice.DeviceId)
            ? PairingResult.AlreadyPaired
            : PairingResult.Error; // coordinator handles actual flow
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<PairingResult> AcceptPairingAsync(
        string remoteDeviceId,
        byte[] remotePublicKey,
        string pin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 6 || !pin.All(char.IsDigit))
            return PairingResult.Error;

        PinGenerated?.Invoke(this, pin);
        await _keyStore.StoreRemoteKeyAsync(remoteDeviceId, remotePublicKey)
                       .ConfigureAwait(false);
        return PairingResult.Success;
    }

    /// <inheritdoc/>
    public async Task RevokePairingAsync(string deviceId, CancellationToken cancellationToken = default)
        => await _keyStore.RemoveRemoteKeyAsync(deviceId).ConfigureAwait(false);

    /// <inheritdoc/>
    public byte[] GetLocalPublicKey() => _keyStore.GetLocalPublicKey();

    // ── Helpers used by PairingCoordinator (Transport layer) ───────────────

    /// <summary>Generates a 6-digit cryptographically-random PIN.</summary>
    public static string GeneratePin()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public void RaisePinGenerated(string pin) => PinGenerated?.Invoke(this, pin);

    public bool IsPaired(string deviceId) => _keyStore.HasRemoteKey(deviceId);

    public async Task StorePeerKeyAsync(string deviceId, byte[] keyBytes)
        => await _keyStore.StoreRemoteKeyAsync(deviceId, keyBytes).ConfigureAwait(false);
}
