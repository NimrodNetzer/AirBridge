using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Mirror.Interfaces;
using AirBridge.Transfer.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Tests;

/// <summary>
/// Smoke tests — verify that all interfaces and models compile and are accessible.
/// These tests do not test behavior; they ensure the project references and
/// public API surface are correct. Real tests are added per-module in later iterations.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Core_DeviceInfo_CanBeConstructed()
    {
        var device = new DeviceInfo(
            DeviceId: "test-id",
            DeviceName: "Test Phone",
            DeviceType: DeviceType.AndroidPhone,
            IpAddress: "192.168.1.100",
            Port: ProtocolMessage.DefaultPort,
            IsPaired: false
        );

        Assert.Equal("test-id", device.DeviceId);
        Assert.Equal(DeviceType.AndroidPhone, device.DeviceType);
        Assert.False(device.IsPaired);
    }

    [Fact]
    public void Transport_ProtocolVersion_IsOne()
    {
        Assert.Equal(1, ProtocolMessage.ProtocolVersion);
    }

    [Fact]
    public void Transport_DefaultPort_IsCorrect()
    {
        Assert.Equal(47821, ProtocolMessage.DefaultPort);
    }

    [Fact]
    public void Transport_MessageType_AllValuesDefinedInSpec()
    {
        // Verify all spec-defined message types are present in the enum
        Assert.True(Enum.IsDefined(typeof(MessageType), MessageType.Handshake));
        Assert.True(Enum.IsDefined(typeof(MessageType), MessageType.FileChunk));
        Assert.True(Enum.IsDefined(typeof(MessageType), MessageType.MirrorFrame));
        Assert.True(Enum.IsDefined(typeof(MessageType), MessageType.InputEvent));
        Assert.True(Enum.IsDefined(typeof(MessageType), MessageType.Error));
    }

    [Fact]
    public void Core_InterfacesExist()
    {
        // Verify all core interfaces are accessible (will fail to compile if missing)
        Assert.NotNull(typeof(IDeviceRegistry));
        Assert.NotNull(typeof(IPairingService));
        Assert.NotNull(typeof(ITransferSession));
        Assert.NotNull(typeof(IMirrorSession));
    }

    [Fact]
    public void Transport_InterfacesExist()
    {
        Assert.NotNull(typeof(IDiscoveryService));
        Assert.NotNull(typeof(IConnectionManager));
        Assert.NotNull(typeof(IMessageChannel));
    }

    [Fact]
    public void Transfer_InterfaceExists()
    {
        Assert.NotNull(typeof(IFileTransferService));
    }

    [Fact]
    public void Mirror_InterfaceExists()
    {
        Assert.NotNull(typeof(IMirrorService));
    }
}
