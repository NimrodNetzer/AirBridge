using System.Buffers.Binary;
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
    //
    // All multi-byte integers are written in big-endian (network byte order) to match
    // Java's DataOutputStream / DataInputStream used on the Android side.
    // BinaryWriter/BinaryReader are little-endian by default, so we use BinaryPrimitives
    // for the 16-bit key-length field instead.

    /// <summary>
    /// Builds the PAIRING_REQUEST payload.
    /// Wire format: [ushort BE key length][key bytes][6 ASCII PIN bytes]
    /// Matches Android PairingService.buildRequestPayload():
    ///   dos.writeShort(key.size) → dos.write(key) → dos.write(pin ASCII)
    /// </summary>
    public static byte[] BuildRequestPayload(byte[] localPublicKey, string pin)
    {
        var ms = new System.IO.MemoryStream();
        // Write key length as 2 bytes big-endian
        var lenBuf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)localPublicKey.Length);
        ms.Write(lenBuf, 0, 2);
        ms.Write(localPublicKey, 0, localPublicKey.Length);
        var pinBytes = System.Text.Encoding.ASCII.GetBytes(pin);
        ms.Write(pinBytes, 0, pinBytes.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the PAIRING_RESPONSE payload.
    /// Wire format: [1 byte accepted][ushort BE key length][key bytes]
    /// Matches Android PairingService.buildResponsePayload():
    ///   dos.writeBoolean(accepted) → dos.writeShort(key.size) → dos.write(key)
    /// </summary>
    public static byte[] BuildResponsePayload(bool accepted, byte[] localPublicKey)
    {
        var ms = new System.IO.MemoryStream();
        ms.WriteByte(accepted ? (byte)1 : (byte)0);
        var lenBuf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBuf, (ushort)localPublicKey.Length);
        ms.Write(lenBuf, 0, 2);
        ms.Write(localPublicKey, 0, localPublicKey.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Parses a PAIRING_RESPONSE payload from Android.
    /// Android writes: [1 byte boolean][ushort BE key length][key bytes]
    /// </summary>
    public static (bool Accepted, byte[] RemoteKey) ParseResponsePayload(byte[] payload)
    {
        try
        {
            if (payload.Length < 3) return (false, Array.Empty<byte>());
            var accepted = payload[0] != 0;
            var keyLen = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(1, 2));
            if (payload.Length < 3 + keyLen) return (false, Array.Empty<byte>());
            var key = payload[3..(3 + keyLen)];
            return (accepted, key);
        }
        catch { return (false, Array.Empty<byte>()); }
    }

    /// <summary>
    /// Parses a PAIRING_REQUEST payload from Android.
    /// Android writes: [ushort BE key length][key bytes][6 ASCII PIN bytes]
    /// </summary>
    public static (byte[] PublicKey, string Pin) ParseRequestPayload(byte[] payload)
    {
        if (payload.Length < 2) throw new ArgumentException("Payload too short.", nameof(payload));
        var keyLen = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
        if (payload.Length < 2 + keyLen + 6) throw new ArgumentException("Payload too short for key + PIN.", nameof(payload));
        var key = payload[2..(2 + keyLen)];
        var pin = System.Text.Encoding.ASCII.GetString(payload, 2 + keyLen, 6);
        return (key, pin);
    }
}
