using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;

namespace AirBridge.Mirror.Interfaces;

/// <summary>
/// High-level mirror service. Creates and manages mirror sessions.
/// Implemented in Iteration 5/6.
/// </summary>
public interface IMirrorService
{
    /// <summary>
    /// Starts a mirror session with a paired Android device.
    /// <paramref name="mode"/> determines phone-window vs tablet-display.
    /// </summary>
    Task<IMirrorSession> StartMirrorAsync(DeviceInfo remoteDevice, MirrorMode mode, CancellationToken cancellationToken = default);

    /// <summary>Returns all currently active mirror sessions.</summary>
    IReadOnlyList<IMirrorSession> GetActiveSessions();
}
