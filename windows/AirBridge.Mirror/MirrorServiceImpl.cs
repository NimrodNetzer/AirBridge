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
///
/// <para>
/// For the phone-window mode two factories are accepted via the constructor:
/// <list type="bullet">
///   <item><paramref name="decoderFactory"/> — creates an <see cref="IMirrorDecoder"/>
///         (production: <see cref="MirrorDecoder"/>; tests: a no-op stub).</item>
///   <item><paramref name="windowFactory"/> — receives the decoder and creates the
///         <see cref="IMirrorWindowHost"/> shown on screen
///         (production: <see cref="MirrorWindow"/>; tests: null for headless).</item>
/// </list>
/// Both factories default to <see langword="null"/>, which runs headless (no UI, no decode).
/// The WinUI 3 App layer passes real factories via <see cref="App.cs"/>'s DI registration.
/// </para>
/// </summary>
public sealed class MirrorServiceImpl : IMirrorService
{
    // ── Optional factories ─────────────────────────────────────────────────

    private readonly Func<IMirrorDecoder>?                    _decoderFactory;
    private readonly Func<IMirrorDecoder, IMirrorWindowHost>? _windowFactory;
    private readonly ITransferEngine?                         _transferEngine;

    // ── Session registry ───────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, IMirrorSession> _sessions = new();

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises <see cref="MirrorServiceImpl"/>.
    /// </summary>
    /// <param name="decoderFactory">
    ///   Factory that creates a fresh <see cref="IMirrorDecoder"/> for each phone-window
    ///   session. Pass <see langword="null"/> for headless / unit-test mode.
    /// </param>
    /// <param name="windowFactory">
    ///   Factory that, given a decoder, creates the <see cref="IMirrorWindowHost"/> floating
    ///   window shown on screen. Pass <see langword="null"/> for headless / unit-test mode.
    /// </param>
    /// <param name="transferEngine">
    ///   Optional drag-and-drop file-transfer engine. Pass <see langword="null"/> to disable.
    /// </param>
    public MirrorServiceImpl(
        Func<IMirrorDecoder>?                    decoderFactory = null,
        Func<IMirrorDecoder, IMirrorWindowHost>? windowFactory  = null,
        ITransferEngine?                         transferEngine = null)
    {
        _decoderFactory = decoderFactory;
        _windowFactory  = windowFactory;
        _transferEngine = transferEngine;
    }

    // ── IMirrorService ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IMirrorSession> StartMirrorAsync(
        DeviceInfo remoteDevice,
        MirrorMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteDevice);

        // An active IMessageChannel is required.  The UI layer must call
        // StartMirrorWithChannelAsync after obtaining a channel from DeviceConnectionService.
        return Task.FromException<IMirrorSession>(new InvalidOperationException(
            "Use StartMirrorWithChannelAsync(channel, mode, ct) from the UI layer. " +
            "Call DeviceConnectionService.ConnectToDeviceAsync first."));
    }

    /// <summary>
    /// Starts a mirror session on an already-established message channel.
    /// </summary>
    /// <param name="channel">The authenticated TLS channel to the Android device.</param>
    /// <param name="mode">
    ///   <see cref="MirrorMode.PhoneWindow"/> — Android is the capture source; Windows renders
    ///   in a floating window using the injected decoder/window factories.<br/>
    ///   <see cref="MirrorMode.TabletDisplay"/> — Windows is the source; a
    ///   <see cref="TabletDisplaySession"/> is created instead of a <see cref="MirrorSession"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the start operation.</param>
    public Task<IMirrorSession> StartMirrorWithChannelAsync(
        IMessageChannel channel,
        MirrorMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var sessionId = Guid.NewGuid().ToString("N");

        IMirrorSession session;

        if (mode == MirrorMode.TabletDisplay)
        {
            // Windows is the source: reads H.264 NAL units from the IddCx driver pipe
            // and streams them to the Android tablet.
            session = new TabletDisplaySession(sessionId, channel);
        }
        else
        {
            // Phone-window mode: Android is the source.
            // Windows receives H.264 frames, decodes them, and renders in a floating window.
            session = new MirrorSession(
                sessionId:      sessionId,
                channel:        channel,
                decoderFactory: _decoderFactory,
                windowFactory:  _windowFactory,
                transferEngine: _transferEngine);
        }

        _sessions[sessionId] = session;

        // Fire-and-forget: start the session and clean up when it finishes.
        _ = session.StartAsync(cancellationToken).ContinueWith(t =>
        {
            _sessions.TryRemove(sessionId, out _);
            session.Dispose();
        }, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);

        return Task.FromResult(session);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMirrorSession> GetActiveSessions()
        => _sessions.Values.ToList();
}
