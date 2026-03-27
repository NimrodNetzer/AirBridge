using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Transport.Connection;

/// <summary>
/// <see cref="IConnectionManager"/> implementation that listens for incoming TLS 1.3
/// TCP connections and opens outbound TLS connections to peer devices.
/// </summary>
/// <remarks>
/// <para>
/// <b>Certificate policy (Iteration 2 / scaffold):</b> A self-signed RSA certificate
/// is generated at runtime and used for both server and client authentication.
/// Certificate validation on the remote side is intentionally disabled here — this is a
/// temporary scaffold that is replaced in Iteration 3 by TOFU (Trust On First Use) key
/// pinning (Ed25519 keys, mutual TLS with stored peer certificates).
/// </para>
/// <para>
/// Immediately after every TLS handshake (both inbound and outbound), a HANDSHAKE
/// message (type 0x01) is exchanged.  Each side sends its own device identity as
/// UTF-8 JSON and reads the peer's identity to populate
/// <see cref="TlsMessageChannel.RemoteDeviceId"/>.
/// </para>
/// <para>
/// A PING/PONG keepalive loop is started on each channel after the HANDSHAKE exchange.
/// </para>
/// </remarks>
public sealed class TlsConnectionManager : IConnectionManager
{
    // ── Configuration ──────────────────────────────────────────────────────
    private readonly int     _port;
    private readonly KeyStore? _keyStore;
    private readonly string  _localDeviceId;
    private readonly string  _localDeviceName;

    // ── Runtime state ──────────────────────────────────────────────────────
    private TcpListener?         _listener;
    private CancellationTokenSource? _listenerCts;
    private X509Certificate2?    _certificate;
    private readonly SemaphoreSlim _startStopLock = new(1, 1);
    private bool _listening;
    private bool _disposed;

    // Tracks active channels so we can close them on StopAsync
    private readonly List<TlsMessageChannel> _activeChannels = new();
    private readonly object _channelsLock = new();

    /// <inheritdoc/>
    public event EventHandler<IMessageChannel>? ConnectionReceived;

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the connection manager.
    /// </summary>
    /// <param name="localDeviceId">
    /// Stable UUID that identifies this Windows device across connections.
    /// </param>
    /// <param name="localDeviceName">
    /// Human-readable name of this device (e.g. <see cref="Environment.MachineName"/>).
    /// </param>
    /// <param name="keyStore">
    /// Optional <see cref="KeyStore"/> used to persist the TLS certificate across restarts.
    /// When provided, the same certificate is reused on every start so peers can pin its
    /// fingerprint for TOFU.  When null, a fresh certificate is generated each run (scaffold mode).
    /// </param>
    /// <param name="port">
    /// TCP port to listen on.  Defaults to <see cref="ProtocolMessage.DefaultPort"/> (47821).
    /// </param>
    public TlsConnectionManager(
        string    localDeviceId,
        string    localDeviceName,
        KeyStore? keyStore = null,
        int       port     = ProtocolMessage.DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(localDeviceId))
            throw new ArgumentException("Device ID must not be empty.", nameof(localDeviceId));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _localDeviceId   = localDeviceId;
        _localDeviceName = localDeviceName ?? Environment.MachineName;
        _port            = port;
        _keyStore        = keyStore;
    }

    // ── IConnectionManager ─────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent: calling while already listening is a no-op.
    /// The accept loop runs on a background task and does not block the caller.
    /// </remarks>
    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listening) return;
            ThrowIfDisposed();

            _certificate = await LoadOrCreateCertificateAsync().ConfigureAwait(false);
            _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _listening = true;

            // Run accept loop in background
            _ = AcceptLoopAsync(_listenerCts.Token);
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
            if (!_listening) return;
            TearDown();
            _listening = false;
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Opens an outbound TLS 1.3 connection to <paramref name="remoteDevice"/>.
    /// The remote certificate is accepted without validation in this iteration;
    /// Iteration 3 replaces this with TOFU key pinning.
    /// After TLS authentication, a HANDSHAKE message is exchanged with the peer
    /// and the channel's <see cref="TlsMessageChannel.RemoteDeviceId"/> is updated.
    /// </remarks>
    public async Task<IMessageChannel> ConnectAsync(
        DeviceInfo remoteDevice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteDevice);
        ThrowIfDisposed();

        var cert = _certificate ?? CreateSelfSignedCertificate(); // lazily ensure cert exists for outbound-only use

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(remoteDevice.IpAddress, remoteDevice.Port, cancellationToken)
                           .ConfigureAwait(false);

            var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: AcceptAllCertificates);

            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost             = remoteDevice.IpAddress,
                ClientCertificates     = new X509CertificateCollection { cert },
                EnabledSslProtocols    = System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = AcceptAllCertificates
            };

            await sslStream.AuthenticateAsClientAsync(clientOptions, cancellationToken)
                           .ConfigureAwait(false);

            var channel = new TlsMessageChannel(sslStream, remoteDevice.DeviceId);
            await PerformHandshakeAsync(channel, cancellationToken).ConfigureAwait(false);
            TrackChannel(channel);
            channel.StartKeepaliveAsync(cancellationToken);
            return channel;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
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

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken)
                                            .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException)            { break; }
            catch (ObjectDisposedException)    { break; }

            // Handle each connection on its own task to not block the accept loop
            _ = Task.Run(() => HandleIncomingAsync(tcpClient, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleIncomingAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        try
        {
            var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: AcceptAllCertificates);

            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate               = _certificate,
                EnabledSslProtocols             = System.Security.Authentication.SslProtocols.Tls13,
                ClientCertificateRequired       = false,
                RemoteCertificateValidationCallback = AcceptAllCertificates
            };

            await sslStream.AuthenticateAsServerAsync(serverOptions, cancellationToken)
                           .ConfigureAwait(false);

            // Use IP as placeholder until HANDSHAKE reveals the stable device ID
            var remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var channel = new TlsMessageChannel(sslStream, remoteEndpoint);
            await PerformHandshakeAsync(channel, cancellationToken).ConfigureAwait(false);
            TrackChannel(channel);
            channel.StartKeepaliveAsync(cancellationToken);
            ConnectionReceived?.Invoke(this, channel);
        }
        catch
        {
            tcpClient.Dispose();
        }
    }

    /// <summary>
    /// Exchanges HANDSHAKE messages with the peer.  Each side sends its own identity
    /// and receives the peer's identity.  Updates <paramref name="channel"/>'s
    /// <see cref="TlsMessageChannel.RemoteDeviceId"/> with the received device ID.
    /// </summary>
    private async Task PerformHandshakeAsync(TlsMessageChannel channel, CancellationToken cancellationToken)
    {
        // Build local identity payload
        var identityJson = JsonSerializer.Serialize(new
        {
            deviceId   = _localDeviceId,
            deviceName = _localDeviceName,
            deviceType = "windows_pc"
        });
        var payload = Encoding.UTF8.GetBytes(identityJson);
        var handshakeMsg = new ProtocolMessage(MessageType.Handshake, payload);

        // Send our identity and receive the peer's identity concurrently to avoid deadlock
        // when both sides send before reading.
        var sendTask    = channel.SendAsync(handshakeMsg, cancellationToken);
        var receiveTask = channel.ReceiveAsync(cancellationToken);

        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);

        var received = receiveTask.Result;
        if (received is null) return; // peer closed before handshake

        if (received.Type == MessageType.Handshake && received.Payload.Length > 0)
        {
            try
            {
                var json = Encoding.UTF8.GetString(received.Payload);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("deviceId", out var idProp))
                {
                    var peerId = idProp.GetString();
                    if (!string.IsNullOrEmpty(peerId))
                        channel.RemoteDeviceId = peerId;
                }
            }
            catch (JsonException)
            {
                // Malformed JSON — keep the IP-address placeholder; do not abort connection
            }
        }
    }

    private void TrackChannel(TlsMessageChannel channel)
    {
        lock (_channelsLock)
            _activeChannels.Add(channel);
    }

    private void TearDown()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;

        _listener?.Stop();
        _listener = null;

        List<TlsMessageChannel> channels;
        lock (_channelsLock)
        {
            channels = new List<TlsMessageChannel>(_activeChannels);
            _activeChannels.Clear();
        }

        foreach (var ch in channels)
        {
            try { ch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
        }

        _certificate?.Dispose();
        _certificate = null;
    }

    /// <summary>
    /// Returns the persisted TLS certificate from the <see cref="KeyStore"/>, or generates
    /// a new self-signed certificate and persists it so the same identity is reused on the
    /// next start.  When no <see cref="KeyStore"/> is injected the certificate is ephemeral
    /// (scaffold / test mode).
    /// </summary>
    private async Task<X509Certificate2> LoadOrCreateCertificateAsync()
    {
        if (_keyStore is not null)
        {
            var stored = _keyStore.GetTlsCertificatePfx();
            if (stored is not null)
            {
                return new X509Certificate2(
                    stored,
                    (string?)null,
                    X509KeyStorageFlags.Exportable);
            }
        }

        // Generate a new certificate and (if KeyStore is available) persist it.
        var cert = CreateSelfSignedCertificate();
        if (_keyStore is not null)
        {
            var pfxBytes = cert.Export(X509ContentType.Pfx);
            await _keyStore.StoreTlsCertificatePfxAsync(pfxBytes).ConfigureAwait(false);
        }
        return cert;
    }

    /// <summary>
    /// Creates a self-signed RSA certificate for use as the local TLS identity.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=AirBridge",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        // Export/re-import to get a certificate with private key accessible via SslStream
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Accepts all remote certificates.  Replaced by TOFU pinning in Iteration 3.
    /// </summary>
    private static bool AcceptAllCertificates(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors) => true;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TlsConnectionManager));
    }
}
