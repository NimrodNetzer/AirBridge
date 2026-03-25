using System.Collections.Concurrent;
using AirBridge.Core.Models;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using Makaretu.Dns;

namespace AirBridge.Transport.Discovery;

/// <summary>
/// Implements mDNS-based device discovery and advertisement using the
/// Makaretu.Dns.Multicast library. Advertises the service type
/// <c>_airbridge._tcp.local</c> on port <see cref="ProtocolMessage.DefaultPort"/>
/// and parses incoming announcements into <see cref="DeviceInfo"/> objects.
/// </summary>
/// <remarks>
/// <para>Thread-safe; <see cref="StartAsync"/> and <see cref="StopAsync"/> are idempotent.</para>
/// <para>TXT records carried in each announcement:</para>
/// <list type="bullet">
///   <item><c>deviceId</c> — stable unique identifier for the device</item>
///   <item><c>deviceName</c> — human-readable display name</item>
///   <item><c>deviceType</c> — one of the <see cref="DeviceType"/> enum names</item>
///   <item><c>protocolVersion=1</c> — AirBridge wire-protocol version</item>
/// </list>
/// </remarks>
public sealed class MdnsDiscoveryService : IDiscoveryService
{
    // ── Constants ──────────────────────────────────────────────────────────
    private const string ServiceType     = "_airbridge._tcp.local";
    private const string TxtDeviceId     = "deviceId";
    private const string TxtDeviceName   = "deviceName";
    private const string TxtDeviceType   = "deviceType";
    private const string TxtProtoVersion = "protocolVersion";

    // ── Configuration ──────────────────────────────────────────────────────
    private readonly string _localDeviceId;
    private readonly string _localDeviceName;
    private readonly DeviceType _localDeviceType;

    // ── State ──────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, DeviceInfo> _visibleDevices = new();
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    private MulticastService?  _mdns;
    private ServiceDiscovery?  _sd;
    private ServiceProfile?    _profile;
    private bool _running;
    private bool _disposed;

    // ── Events ─────────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public event EventHandler<DeviceInfo>? DeviceFound;

    /// <inheritdoc/>
    public event EventHandler<string>? DeviceLost;

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the discovery service with the identity of the local device.
    /// </summary>
    /// <param name="localDeviceId">Stable unique device identifier (e.g. a GUID).</param>
    /// <param name="localDeviceName">Human-readable name shown to peers.</param>
    /// <param name="localDeviceType">The platform type of this device.</param>
    public MdnsDiscoveryService(
        string localDeviceId,
        string localDeviceName,
        DeviceType localDeviceType)
    {
        _localDeviceId   = localDeviceId   ?? throw new ArgumentNullException(nameof(localDeviceId));
        _localDeviceName = localDeviceName ?? throw new ArgumentNullException(nameof(localDeviceName));
        _localDeviceType = localDeviceType;
    }

    // ── IDiscoveryService ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<DeviceInfo> GetVisibleDevices() =>
        _visibleDevices.Values.ToList().AsReadOnly();

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running) return;
            ThrowIfDisposed();

            // ── Set up multicast service ──────────────────────────────────
            _mdns = new MulticastService();
            _sd   = new ServiceDiscovery(_mdns);

            _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown   += OnServiceInstanceShutdown;

            // ── Build our advertisement profile ───────────────────────────
            _profile = new ServiceProfile(
                instanceName: _localDeviceName,
                serviceName:  ServiceType,
                port:         (ushort)ProtocolMessage.DefaultPort);

            _profile.AddProperty(TxtDeviceId,     _localDeviceId);
            _profile.AddProperty(TxtDeviceName,   _localDeviceName);
            _profile.AddProperty(TxtDeviceType,   _localDeviceType.ToString());
            _profile.AddProperty(TxtProtoVersion, ProtocolMessage.ProtocolVersion.ToString());

            _mdns.Start();
            _sd.Advertise(_profile);
            _sd.QueryAllServices();

            _running = true;
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        await _startStopLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running) return;
            TearDown();
            _running = false;
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    // ── mDNS callbacks ─────────────────────────────────────────────────────

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        // Only handle our own service type
        if (!e.ServiceInstanceName.ToString().Contains("_airbridge._tcp", StringComparison.OrdinalIgnoreCase))
            return;

        // Extract TXT records
        var txt = e.Message.AdditionalRecords
            .OfType<TXTRecord>()
            .FirstOrDefault();

        if (txt is null) return;

        var props = ParseTxtStrings(txt.Strings);

        if (!props.TryGetValue(TxtDeviceId, out var deviceId) || string.IsNullOrEmpty(deviceId))
            return;

        // Ignore our own advertisement
        if (deviceId == _localDeviceId) return;

        if (!props.TryGetValue(TxtDeviceName, out var deviceName))
            deviceName = "Unknown";

        if (!props.TryGetValue(TxtDeviceType, out var deviceTypeStr) ||
            !Enum.TryParse<DeviceType>(deviceTypeStr, ignoreCase: true, out var deviceType))
        {
            deviceType = DeviceType.AndroidPhone;
        }

        // Try to extract IP from the A/AAAA records in the additional section
        var ipAddress = e.Message.AdditionalRecords
            .OfType<ARecord>()
            .Select(a => a.Address.ToString())
            .FirstOrDefault() ?? string.Empty;

        var info = new DeviceInfo(
            DeviceId:   deviceId,
            DeviceName: deviceName,
            DeviceType: deviceType,
            IpAddress:  ipAddress,
            Port:       ProtocolMessage.DefaultPort,
            IsPaired:   false);

        _visibleDevices[deviceId] = info;
        DeviceFound?.Invoke(this, info);
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        if (!e.ServiceInstanceName.ToString().Contains("_airbridge._tcp", StringComparison.OrdinalIgnoreCase))
            return;

        // We don't have a deviceId directly from the shutdown event — iterate and remove by
        // matching on instance name (best effort; real apps would maintain a name→id map).
        var instanceLabel = e.ServiceInstanceName.Labels.FirstOrDefault() ?? string.Empty;

        var removed = _visibleDevices
            .Where(kv => kv.Value.DeviceName.Equals(instanceLabel, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in removed)
        {
            if (_visibleDevices.TryRemove(id, out _))
                DeviceLost?.Invoke(this, id);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses DNS TXT string entries of the form <c>key=value</c> into a dictionary.
    /// Entries without an '=' sign are stored with an empty-string value.
    /// </summary>
    public static Dictionary<string, string> ParseTxtStrings(IEnumerable<string> strings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in strings)
        {
            var idx = s.IndexOf('=');
            if (idx > 0)
                result[s[..idx]] = s[(idx + 1)..];
            else if (!string.IsNullOrEmpty(s))
                result[s] = string.Empty;
        }
        return result;
    }

    private void TearDown()
    {
        if (_sd is not null)
        {
            _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown   -= OnServiceInstanceShutdown;
            _sd.Dispose();
            _sd = null;
        }

        _mdns?.Stop();
        _mdns?.Dispose();
        _mdns = null;

        _visibleDevices.Clear();
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TearDown();
        _startStopLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MdnsDiscoveryService));
    }
}
