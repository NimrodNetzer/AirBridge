namespace AirBridge.Core.Models;

/// <summary>Device type classification.</summary>
public enum DeviceType
{
    Unknown,
    WindowsPc,
    AndroidPhone,
    AndroidTablet
}

/// <summary>Represents a discovered or paired remote device.</summary>
public sealed record DeviceInfo(
    string DeviceId,
    string DeviceName,
    DeviceType DeviceType,
    string IpAddress,
    int Port,
    bool IsPaired
);
