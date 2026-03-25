using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Transport.Pairing;

/// <summary>
/// Handles the on-wire pairing handshake over an <see cref="IMessageChannel"/>.
/// Sits in Transport (which references both Core and the channel abstraction)
/// and calls back into <see cref="PairingService"/> for key storage.
/// </summary>
public sealed class PairingCoordinator
{
    private readonly PairingService _pairing;

    public PairingCoordinator(PairingService pairing)
    {
        _pairing = pairing;
    }

    /// <summary>
    /// Initiator side: sends PairingRequest, waits for PairingResponse.
    /// </summary>
    public async Task<PairingResult> RequestAsync(
        DeviceInfo remoteDevice,
        IMessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (_pairing.IsPaired(remoteDevice.DeviceId))
            return PairingResult.AlreadyPaired;

        var pin = PairingService.GeneratePin();
        _pairing.RaisePinGenerated(pin);

        var payload = BuildRequestPayload(_pairing.GetLocalPublicKey(), pin);
        await channel.SendAsync(
            new ProtocolMessage(MessageType.PairingRequest, payload), cancellationToken)
            .ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var response = await channel.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (response is null || response.Type != MessageType.PairingResponse)
                return PairingResult.Error;

            var (accepted, remoteKey) = ParseResponsePayload(response.Payload);
            if (!accepted) return PairingResult.RejectedByUser;

            await _pairing.StorePeerKeyAsync(remoteDevice.DeviceId, remoteKey)
                          .ConfigureAwait(false);
            return PairingResult.Success;
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested ? PairingResult.Error : PairingResult.Timeout;
        }
    }

    // ── Wire format helpers ────────────────────────────────────────────────

    public static byte[] BuildRequestPayload(byte[] localPublicKey, string pin)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((ushort)localPublicKey.Length);
        bw.Write(localPublicKey);
        bw.Write(System.Text.Encoding.ASCII.GetBytes(pin));
        return ms.ToArray();
    }

    public static byte[] BuildResponsePayload(bool accepted, byte[] localPublicKey)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(accepted);
        bw.Write((ushort)localPublicKey.Length);
        bw.Write(localPublicKey);
        return ms.ToArray();
    }

    public static (bool Accepted, byte[] RemoteKey) ParseResponsePayload(byte[] payload)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(payload);
            using var br = new System.IO.BinaryReader(ms);
            var accepted = br.ReadBoolean();
            var keyLen = br.ReadUInt16();
            var key = br.ReadBytes(keyLen);
            return (accepted, key);
        }
        catch { return (false, Array.Empty<byte>()); }
    }

    public static (byte[] PublicKey, string Pin) ParseRequestPayload(byte[] payload)
    {
        using var ms = new System.IO.MemoryStream(payload);
        using var br = new System.IO.BinaryReader(ms);
        var keyLen = br.ReadUInt16();
        var key = br.ReadBytes(keyLen);
        var pin = System.Text.Encoding.ASCII.GetString(br.ReadBytes(6));
        return (key, pin);
    }
}
