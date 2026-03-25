using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Pairing;
using AirBridge.Transport.Protocol;
using NSubstitute;

namespace AirBridge.Tests.Pairing;

public class PairingServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KeyStore _keyStore;

    public PairingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _keyStore = new KeyStore(Path.Combine(_tempDir, "keys.json"));
    }

    public void Dispose()
    {
        _keyStore.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── PIN generation ─────────────────────────────────────────────────────

    [Fact]
    public void GeneratePin_IsExactlySixDigits()
    {
        var pin = PairingService.GeneratePin();
        Assert.Equal(6, pin.Length);
        Assert.True(pin.All(char.IsDigit));
    }

    [Fact]
    public void GeneratePin_ProducesVariedValues()
    {
        var pins = Enumerable.Range(0, 20).Select(_ => PairingService.GeneratePin()).ToHashSet();
        Assert.True(pins.Count > 1);
    }

    // ── KeyStore ───────────────────────────────────────────────────────────

    [Fact]
    public void GetLocalPublicKey_ReturnsNonEmptyBytes()
        => Assert.NotEmpty(_keyStore.GetLocalPublicKey());

    [Fact]
    public void GetLocalPublicKey_IsStableAcrossInstances()
    {
        var key1 = _keyStore.GetLocalPublicKey();
        using var ks2 = new KeyStore(Path.Combine(_tempDir, "keys.json"));
        Assert.Equal(key1, ks2.GetLocalPublicKey());
    }

    [Fact]
    public async Task StoreAndRetrieveRemoteKey_RoundTrip()
    {
        var fakeKey = new byte[] { 1, 2, 3, 4, 5 };
        await _keyStore.StoreRemoteKeyAsync("device-1", fakeKey);
        Assert.Equal(fakeKey, _keyStore.GetRemoteKey("device-1"));
    }

    [Fact]
    public async Task RemoveRemoteKey_KeyNoLongerRetrievable()
    {
        await _keyStore.StoreRemoteKeyAsync("device-1", new byte[] { 9, 8, 7 });
        await _keyStore.RemoveRemoteKeyAsync("device-1");
        Assert.Null(_keyStore.GetRemoteKey("device-1"));
        Assert.False(_keyStore.HasRemoteKey("device-1"));
    }

    // ── PairingService ─────────────────────────────────────────────────────

    [Fact]
    public async Task RequestPairing_ReturnsAlreadyPaired_WhenKeyExists()
    {
        await _keyStore.StoreRemoteKeyAsync("device-1", new byte[] { 1, 2 });
        var service = new PairingService(_keyStore);
        var device = new DeviceInfo("device-1", "Phone", DeviceType.AndroidPhone, "1.2.3.4", 47821, true);
        Assert.Equal(PairingResult.AlreadyPaired, await service.RequestPairingAsync(device));
    }

    [Fact]
    public async Task AcceptPairing_InvalidPin_ReturnsError()
    {
        var service = new PairingService(_keyStore);
        Assert.Equal(PairingResult.Error, await service.AcceptPairingAsync("d", new byte[] { 1 }, "123"));
    }

    [Fact]
    public async Task AcceptPairing_ValidPin_StoresKey()
    {
        var service = new PairingService(_keyStore);
        var result = await service.AcceptPairingAsync("device-2", new byte[] { 0xAB, 0xCD }, "123456");
        Assert.Equal(PairingResult.Success, result);
        Assert.True(_keyStore.HasRemoteKey("device-2"));
    }

    [Fact]
    public async Task RevokePairing_RemovesStoredKey()
    {
        await _keyStore.StoreRemoteKeyAsync("device-3", new byte[] { 1, 2, 3 });
        var service = new PairingService(_keyStore);
        await service.RevokePairingAsync("device-3");
        Assert.False(_keyStore.HasRemoteKey("device-3"));
    }

    // ── PairingCoordinator ─────────────────────────────────────────────────

    [Fact]
    public async Task Coordinator_HappyPath_StoresRemoteKey()
    {
        var service = new PairingService(_keyStore);
        var coordinator = new PairingCoordinator(service);
        var channel = Substitute.For<IMessageChannel>();

        var remoteKey = new byte[] { 0xAB, 0xCD, 0xEF };
        var responsePayload = PairingCoordinator.BuildResponsePayload(true, remoteKey);
        channel.ReceiveAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ProtocolMessage?>(
                   new ProtocolMessage(MessageType.PairingResponse, responsePayload)));

        var device = new DeviceInfo("device-4", "Tablet", DeviceType.AndroidTablet, "1.2.3.4", 47821, false);
        var result = await coordinator.RequestAsync(device, channel);

        Assert.Equal(PairingResult.Success, result);
        Assert.True(_keyStore.HasRemoteKey("device-4"));
    }

    [Fact]
    public async Task Coordinator_Rejection_ReturnsRejectedByUser()
    {
        var service = new PairingService(_keyStore);
        var coordinator = new PairingCoordinator(service);
        var channel = Substitute.For<IMessageChannel>();

        var responsePayload = PairingCoordinator.BuildResponsePayload(false, Array.Empty<byte>());
        channel.ReceiveAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ProtocolMessage?>(
                   new ProtocolMessage(MessageType.PairingResponse, responsePayload)));

        var device = new DeviceInfo("device-5", "Phone", DeviceType.AndroidPhone, "1.2.3.4", 47821, false);
        Assert.Equal(PairingResult.RejectedByUser, await coordinator.RequestAsync(device, channel));
    }

    [Fact]
    public void Coordinator_ParseRequestPayload_RoundTrip()
    {
        var key = _keyStore.GetLocalPublicKey();
        var pin = "042789";
        var payload = PairingCoordinator.BuildRequestPayload(key, pin);
        var (parsedKey, parsedPin) = PairingCoordinator.ParseRequestPayload(payload);
        Assert.Equal(key, parsedKey);
        Assert.Equal(pin, parsedPin);
    }
}
