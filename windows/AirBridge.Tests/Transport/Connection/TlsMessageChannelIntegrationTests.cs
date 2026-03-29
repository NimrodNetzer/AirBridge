using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirBridge.Transport.Connection;
using AirBridge.Transport.Protocol;

namespace AirBridge.Tests.Transport.Connection;

/// <summary>
/// Integration tests for <see cref="TlsMessageChannel"/> using loopback TCP + TLS sockets.
/// No real network hardware is required — all connections go through 127.0.0.1.
/// </summary>
/// <remarks>
/// Marked Sequential to prevent parallel TLS loopback handshakes from deadlocking testhost.
/// </remarks>
[Collection("Sequential")]
public class TlsMessageChannelIntegrationTests : IAsyncLifetime
{
    // ── Shared self-signed certificate used by both ends ──────────────────────
    private X509Certificate2 _cert = null!;

    public Task InitializeAsync()
    {
        _cert = CreateSelfSignedCert();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cert.Dispose();
        await Task.CompletedTask;
    }

    // ── Helper: create a loopback (client, server) TlsMessageChannel pair ────

    /// <summary>
    /// Opens a loopback TLS pair and returns <c>(client channel, server channel)</c>.
    /// </summary>
    private async Task<(TlsMessageChannel client, TlsMessageChannel server)> CreateLoopbackPairAsync(
        CancellationToken ct = default,
        TimeSpan? keepaliveInterval = null,
        TimeSpan? pongTimeout = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connectTask = Task.Run(async () =>
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            var ssl = new SslStream(tcp.GetStream(), false, AcceptAll);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost          = "localhost",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = AcceptAll
            }, ct);
            return new TlsMessageChannel(ssl, "client", keepaliveInterval, pongTimeout);
        }, ct);

        var acceptTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync(ct);
            listener.Stop();
            var ssl = new SslStream(tcp.GetStream(), false, AcceptAll);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate               = _cert,
                EnabledSslProtocols             = System.Security.Authentication.SslProtocols.Tls13,
                ClientCertificateRequired       = false,
                RemoteCertificateValidationCallback = AcceptAll
            }, ct);
            return new TlsMessageChannel(ssl, "server", keepaliveInterval, pongTimeout);
        }, ct);

        await Task.WhenAll(connectTask, acceptTask);
        return (connectTask.Result, acceptTask.Result);
    }

    // ── Test 1: Clean close ───────────────────────────────────────────────────

    /// <summary>
    /// When the sender closes its side the receiver's ReceiveAsync returns null (clean TLS close_notify)
    /// or throws a network exception (OS-level RST if close_notify was not sent).
    /// In both cases the channel must transition to disconnected and must NOT return a message.
    /// </summary>
    [Fact]
    public async Task CleanClose_ReceiverSignalsDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, server) = await CreateLoopbackPairAsync(cts.Token);

        // Dispose the client side (closes the underlying socket).
        await client.DisposeAsync();

        // Server must either return null or throw a network exception — never a message.
        ProtocolMessage? received = null;
        Exception? caught = null;
        try
        {
            received = await server.ReceiveAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            caught = ex;
        }

        Assert.True(
            received is null || caught is not null,
            "Expected null or network exception when peer closes; got a parsed message.");

        await server.DisposeAsync();
    }

    // ── Test 1b: Clean close — ReceiveAsync returns null ─────────────────────

    /// <summary>
    /// When the remote side closes its socket without a TLS close_notify
    /// (i.e. calls <c>socket.Close()</c> / <c>socket.Dispose()</c>),
    /// <see cref="TlsMessageChannel.ReceiveAsync"/> must return <c>null</c>
    /// rather than throwing a <see cref="SocketException"/> or
    /// <see cref="System.IO.IOException"/>.
    /// </summary>
    [Fact]
    public async Task CleanClose_ReceiverGetsNull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (client, server) = await CreateLoopbackPairAsync(cts.Token);

        // Dispose the client side — no TLS close_notify, raw socket close.
        await client.DisposeAsync();

        // Server's ReceiveAsync must return null (not throw).
        ProtocolMessage? received = await server.ReceiveAsync(cts.Token);

        Assert.Null(received);
        Assert.False(server.IsConnected);

        await server.DisposeAsync();
    }

    // ── Test 2: Mid-frame drop ────────────────────────────────────────────────

    /// <summary>
    /// When the connection drops after writing only 2 bytes of the 4-byte header,
    /// the receiver must NOT return garbage — it should signal a clean disconnect
    /// (null return or a network exception, but never parse garbage).
    /// </summary>
    [Fact]
    public async Task MidFrameDrop_DoesNotProduceGarbageMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Build a raw loopback TCP pair so we can write a partial frame manually.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverConnTask = listener.AcceptTcpClientAsync(cts.Token);
        var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var serverTcp = await serverConnTask;
        listener.Stop();

        // Set up TLS
        var clientSsl = new SslStream(clientTcp.GetStream(), false, AcceptAll);
        var serverSsl = new SslStream(serverTcp.GetStream(), false, AcceptAll);
        var tlsClient = clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = AcceptAll
        }, cts.Token);
        var tlsServer = serverSsl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _cert,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            ClientCertificateRequired = false,
            RemoteCertificateValidationCallback = AcceptAll
        }, cts.Token);
        await Task.WhenAll(tlsClient, tlsServer);

        var channel = new TlsMessageChannel(serverSsl, "mid-drop-server");

        // Write only 2 bytes (partial 4-byte header) then close the client socket.
        await clientSsl.WriteAsync(new byte[] { 0x00, 0x02 }, cts.Token);
        await clientSsl.FlushAsync(cts.Token);
        await clientSsl.DisposeAsync();
        clientTcp.Dispose();

        // The channel should NOT return a garbage message.
        // It must return null OR throw a network exception — never a parsed ProtocolMessage
        // whose contents came from the partial 2-byte header.
        ProtocolMessage? received = null;
        Exception? caught = null;
        try
        {
            received = await channel.ReceiveAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException or ObjectDisposedException)
        {
            caught = ex;
        }

        // Either null (disconnect signalled) or an exception — never a garbage message.
        Assert.True(
            received is null || caught is not null,
            "Expected null or exception from mid-frame disconnect, not a parsed message.");

        await channel.DisposeAsync();
    }

    // ── Test 4: Reconnect ─────────────────────────────────────────────────────

    /// <summary>
    /// Simulates a drop followed by a reconnect: after the first channel closes, a new
    /// <see cref="TlsMessageChannel"/> over a fresh socket should be able to send/receive
    /// correctly, verifying that the session layer can be re-established.
    /// </summary>
    [Fact]
    public async Task Reconnect_NewChannelWorksAfterDrop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // ── First session ────────────────────────────────────────────────────
        var (client1, server1) = await CreateLoopbackPairAsync(cts.Token);

        var testPayload = System.Text.Encoding.UTF8.GetBytes("hello");
        await client1.SendAsync(new ProtocolMessage(MessageType.Handshake, testPayload), cts.Token);
        var received1 = await server1.ReceiveAsync(cts.Token);
        Assert.NotNull(received1);
        Assert.Equal(testPayload, received1!.Payload);

        // Drop the first session.
        await client1.DisposeAsync();
        await server1.DisposeAsync();

        // ── Second session (reconnect) ───────────────────────────────────────
        var (client2, server2) = await CreateLoopbackPairAsync(cts.Token);

        var testPayload2 = System.Text.Encoding.UTF8.GetBytes("reconnected");
        await client2.SendAsync(new ProtocolMessage(MessageType.Handshake, testPayload2), cts.Token);
        var received2 = await server2.ReceiveAsync(cts.Token);
        Assert.NotNull(received2);
        Assert.Equal(testPayload2, received2!.Payload);

        await client2.DisposeAsync();
        await server2.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req  = new CertificateRequest("CN=AirBridgeTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static bool AcceptAll(object _, X509Certificate? __, X509Chain? ___, SslPolicyErrors ____) => true;
}
