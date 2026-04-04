using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace AirBridge.Mirror;

/// <summary>
/// Encodes raw BGRA frames to H.264 NAL units using the Windows Media Foundation
/// H.264 encoder MFT (hardware if available, software fallback).
///
/// Usage:
///   1. Construct with target width, height, fps, and bitrate.
///   2. Call <see cref="Start"/> once.
///   3. For each BGRA frame call <see cref="EncodeFrame"/> — returns zero or more NAL byte arrays.
///   4. Call <see cref="Dispose"/> when done.
/// </summary>
public sealed class MfH264Encoder : IDisposable
{
    // Output: H.264 Baseline
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    // Input: BGRA (stored as RGB32 in MF)
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _bitrateBps;

    private IMFTransform? _encoder;
    private long          _sampleDuration; // 100-ns ticks per frame
    private long          _sampleTime;
    private bool          _started;
    private bool          _disposed;

    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="fps">Target frames per second.</param>
    /// <param name="bitrateBps">Target bitrate in bits/second (default 8 Mbps).</param>
    public MfH264Encoder(int width, int height, int fps = 30, int bitrateBps = 8_000_000)
    {
        _width      = width;
        _height     = height;
        _fps        = fps;
        _bitrateBps = bitrateBps;
    }

    /// <summary>Initialises the MF encoder MFT and configures input/output media types.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        MediaFactory.MFStartup();

        // Find H.264 encoder (prefer hardware via MFT_EnumFlag sorting)
        var outputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype   = MFVideoFormat_H264
        };

        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            0x00000008 | 0x00000020, // MFT_ENUM_FLAG_ALL | MFT_ENUM_FLAG_SORTANDFILTER
            null,
            outputInfo,
            out var pActivateArray,
            out uint count);

        if (count == 0)
            throw new InvalidOperationException("No H.264 encoder MFT found on this system.");

        // Activate first (hardware preferred when sort flag is set)
        var activates = new IMFActivate[count];
        for (uint i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(pActivateArray + (int)(i * IntPtr.Size));
            activates[i] = (IMFActivate)Marshal.GetObjectForIUnknown(ptr);
        }

        _encoder = activates[0].ActivateObject<IMFTransform>();
        foreach (var a in activates) a?.Dispose();

        // ── Output type: H.264 ──────────────────────────────────────────────
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType,     MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype,       MFVideoFormat_H264);
        outType.Set(MediaTypeAttributeKeys.FrameSize,     PackSize(_width, _height));
        outType.Set(MediaTypeAttributeKeys.FrameRate,     PackSize(_fps, 1));
        outType.Set(MediaTypeAttributeKeys.AvgBitrate,    _bitrateBps);
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        _encoder.SetOutputType(0, outType, 0);

        // ── Input type: BGRA / RGB32 ────────────────────────────────────────
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType,      MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype,        MFVideoFormat_RGB32);
        inType.Set(MediaTypeAttributeKeys.FrameSize,      PackSize(_width, _height));
        inType.Set(MediaTypeAttributeKeys.FrameRate,      PackSize(_fps, 1));
        inType.Set(MediaTypeAttributeKeys.InterlaceMode,  (int)VideoInterlaceMode.Progressive);
        inType.Set(MediaTypeAttributeKeys.DefaultStride,  _width * 4);
        _encoder.SetInputType(0, inType, 0);

        _encoder.ProcessMessage(TMessageType.MessageCommandFlush,           UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming,   UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream,    UIntPtr.Zero);

        _sampleDuration = 10_000_000L / _fps;
        _sampleTime     = 0;
        _started        = true;
    }

    /// <summary>
    /// Encodes one BGRA frame. Returns all output NAL unit byte arrays produced
    /// (may be zero for the first few frames while the encoder fills its pipeline).
    /// </summary>
    public List<byte[]> EncodeFrame(byte[] bgraFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) throw new InvalidOperationException("Call Start() first.");

        // Wrap BGRA bytes in MF media buffer
        var buffer = MediaFactory.MFCreateMemoryBuffer(bgraFrame.Length);
        buffer.Lock(out var ptr, out _, out _);
        try { Marshal.Copy(bgraFrame, 0, ptr, bgraFrame.Length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = bgraFrame.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();

        sample.SampleTime     = _sampleTime;
        sample.SampleDuration = _sampleDuration;
        _sampleTime += _sampleDuration;

        _encoder!.ProcessInput(0, sample, 0);
        sample.Dispose();

        return DrainOutput();
    }

    private List<byte[]> DrainOutput()
    {
        const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

        var results = new List<byte[]>();

        while (true)
        {
            var outSample = MediaFactory.MFCreateSample();
            var outBuf    = MediaFactory.MFCreateMemoryBuffer(_width * _height * 4);
            outSample.AddBuffer(outBuf);
            outBuf.Dispose();

            var outputBuffer = new OutputDataBuffer { Sample = outSample };
            var hr = _encoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);
            outSample.Dispose();

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            hr.CheckError();

            if (outputBuffer.Sample is null) break;

            var contiguous = outputBuffer.Sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var dataPtr, out _, out int dataLen);
            var nalBytes = new byte[dataLen];
            Marshal.Copy(dataPtr, nalBytes, 0, dataLen);
            contiguous.Unlock();
            contiguous.Dispose();
            outputBuffer.Sample.Dispose();

            results.Add(nalBytes);
        }

        return results;
    }

    // Packs two ints into a ulong for MF frame-size / frame-rate attributes
    private static ulong PackSize(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started && _encoder is not null)
        {
            try { _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
            try { _encoder.ProcessMessage(TMessageType.MessageCommandDrain,      UIntPtr.Zero); } catch { }
        }
        _encoder?.Dispose();
        if (_started) try { MediaFactory.MFShutdown(); } catch { }
    }
}
