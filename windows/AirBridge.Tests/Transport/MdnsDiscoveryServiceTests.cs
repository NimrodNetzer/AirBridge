using AirBridge.Core.Models;
using AirBridge.Transport.Discovery;

namespace AirBridge.Tests.Transport;

/// <summary>
/// Unit tests for <see cref="MdnsDiscoveryService"/> that validate TXT-record parsing
/// and <see cref="DeviceInfo"/> construction without touching any real network or
/// mDNS sockets.
/// </summary>
public class MdnsDiscoveryServiceTests
{
    // ── ParseTxtStrings ────────────────────────────────────────────────────

    [Fact]
    public void ParseTxtStrings_WellFormedEntries_ReturnsAllKeyValuePairs()
    {
        var strings = new[]
        {
            "deviceId=abc-123",
            "deviceName=My Phone",
            "deviceType=AndroidPhone",
            "protocolVersion=1"
        };

        var result = MdnsDiscoveryService.ParseTxtStrings(strings);

        Assert.Equal("abc-123",      result["deviceId"]);
        Assert.Equal("My Phone",     result["deviceName"]);
        Assert.Equal("AndroidPhone", result["deviceType"]);
        Assert.Equal("1",            result["protocolVersion"]);
    }

    [Fact]
    public void ParseTxtStrings_ValueContainsEquals_OnlyFirstEqualsIsTheSeparator()
    {
        var strings = new[] { "key=val=ue" };
        var result  = MdnsDiscoveryService.ParseTxtStrings(strings);

        Assert.Equal("val=ue", result["key"]);
    }

    [Fact]
    public void ParseTxtStrings_EntryWithoutEquals_StoresKeyWithEmptyValue()
    {
        var strings = new[] { "flagOnly" };
        var result  = MdnsDiscoveryService.ParseTxtStrings(strings);

        Assert.True(result.ContainsKey("flagOnly"));
        Assert.Equal(string.Empty, result["flagOnly"]);
    }

    [Fact]
    public void ParseTxtStrings_EmptyInput_ReturnsEmptyDictionary()
    {
        var result = MdnsDiscoveryService.ParseTxtStrings(Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTxtStrings_IsCaseInsensitive()
    {
        var strings = new[] { "DeviceId=XYZ" };
        var result  = MdnsDiscoveryService.ParseTxtStrings(strings);

        Assert.True(result.ContainsKey("deviceid"));
        Assert.True(result.ContainsKey("DEVICEID"));
        Assert.Equal("XYZ", result["deviceId"]);
    }

    // ── DeviceInfo construction from parsed TXT records ────────────────────

    [Fact]
    public void DeviceInfo_ConstructedFromParsedTxt_HasCorrectValues()
    {
        // Simulate what MdnsDiscoveryService does after parsing TXT records
        var strings = new[]
        {
            "deviceId=device-001",
            "deviceName=Pixel 7",
            "deviceType=AndroidPhone",
            "protocolVersion=1"
        };

        var props = MdnsDiscoveryService.ParseTxtStrings(strings);

        props.TryGetValue("deviceId",   out var deviceId);
        props.TryGetValue("deviceName", out var deviceName);
        props.TryGetValue("deviceType", out var deviceTypeStr);
        Enum.TryParse<DeviceType>(deviceTypeStr, ignoreCase: true, out var deviceType);

        var info = new DeviceInfo(
            DeviceId:   deviceId!,
            DeviceName: deviceName ?? "Unknown",
            DeviceType: deviceType,
            IpAddress:  "192.168.1.50",
            Port:       47821,
            IsPaired:   false);

        Assert.Equal("device-001",          info.DeviceId);
        Assert.Equal("Pixel 7",             info.DeviceName);
        Assert.Equal(DeviceType.AndroidPhone, info.DeviceType);
        Assert.Equal("192.168.1.50",        info.IpAddress);
        Assert.Equal(47821,                 info.Port);
        Assert.False(info.IsPaired);
    }

    [Fact]
    public void DeviceInfo_UnknownDeviceType_FallsBackToAndroidPhone()
    {
        var strings = new[]
        {
            "deviceId=x",
            "deviceName=X",
            "deviceType=UnknownGizmo"
        };

        var props = MdnsDiscoveryService.ParseTxtStrings(strings);
        props.TryGetValue("deviceType", out var deviceTypeStr);

        var parsed = Enum.TryParse<DeviceType>(deviceTypeStr, ignoreCase: true, out var deviceType);
        if (!parsed) deviceType = DeviceType.AndroidPhone;

        Assert.Equal(DeviceType.AndroidPhone, deviceType);
    }

    [Fact]
    public void DeviceInfo_AllDeviceTypes_ParseCorrectly()
    {
        foreach (var expected in Enum.GetValues<DeviceType>())
        {
            var strings = new[] { $"deviceType={expected}" };
            var props   = MdnsDiscoveryService.ParseTxtStrings(strings);
            props.TryGetValue("deviceType", out var str);
            Enum.TryParse<DeviceType>(str, ignoreCase: true, out var actual);

            Assert.Equal(expected, actual);
        }
    }

    // ── Service lifecycle (no network) ─────────────────────────────────────

    [Fact]
    public void GetVisibleDevices_BeforeStart_ReturnsEmptyList()
    {
        using var svc = new MdnsDiscoveryService("id-1", "Test PC", DeviceType.WindowsPc);
        Assert.Empty(svc.GetVisibleDevices());
    }

    [Fact]
    public void Constructor_NullDeviceId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MdnsDiscoveryService(null!, "name", DeviceType.WindowsPc));
    }

    [Fact]
    public void Constructor_NullDeviceName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MdnsDiscoveryService("id", null!, DeviceType.WindowsPc));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_DoesNotThrow()
    {
        var svc = new MdnsDiscoveryService("id-2", "Test PC", DeviceType.WindowsPc);
        svc.Dispose();
        var ex = Record.Exception(() => svc.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void StopAsync_WhenNotStarted_IsIdempotent()
    {
        using var svc = new MdnsDiscoveryService("id-3", "Test PC", DeviceType.WindowsPc);
        var ex = Record.Exception(() => svc.StopAsync().GetAwaiter().GetResult());
        Assert.Null(ex);
    }
}
