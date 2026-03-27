using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Mirror.Interfaces;
using AirBridge.Transport.Interfaces;
using System.Collections.Concurrent;

namespace AirBridge.Mirror;

/// <summary>
/// Default implementation of <see cref="IMirrorService"/>.
/// Creates and tracks <see cref="MirrorSession"/> instances for phone-window
/// and tablet-display modes.
/// </summary>
public sealed class MirrorServiceImpl : IMirrorService
{
    private readonly ConcurrentDictionary<string, IMirrorSession> _sessions = new();

    /// <inheritdoc/>
    public Task<IMirrorSession> StartMirrorAsync(
        DeviceInfo remoteDevice,
        MirrorMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteDevice);

        // MirrorSession constructor requires an IMessageChannel.
        // The caller must have established a channel before invoking this service.
        // For the UI layer we expect the channel to be provided via the DeviceConnectionService.
        // This overload uses a channel-less stub; the full flow goes through StartMirrorWithChannelAsync.
        return Task.FromException<IMirrorSession>(new InvalidOperationException(
            "Use StartMirrorWithChannelAsync(channel, mode, ct) from the UI layer. " +
            "Call DeviceConnectionService.ConnectToDeviceAsync first."));
    }

    /// <summary>
    /// Starts a mirror session on an already-established message channel.
    /// </summary>
    public Task<IMirrorSession> StartMirrorWithChannelAsync(
        IMessageChannel channel,
        MirrorMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var sessionId = Guid.NewGuid().ToString("N");
        var session   = new MirrorSession(sessionId, channel);
        _sessions[sessionId] = session;

        // Start is fire-and-forget — caller holds the session reference
        _ = session.StartAsync(cancellationToken).ContinueWith(t =>
        {
            _sessions.TryRemove(sessionId, out _);
            session.Dispose();
        }, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);

        return Task.FromResult<IMirrorSession>(session);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMirrorSession> GetActiveSessions()
        => _sessions.Values.ToList();
}
