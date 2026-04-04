using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirBridge.Mirror;

/// <summary>
/// Captures frames from a specific monitor using DXGI Desktop Duplication.
/// Outputs raw BGRA byte arrays at the monitor's native resolution.
///
/// Usage:
///   1. Call <see cref="Start"/> with a monitor index (0 = primary, 1 = first secondary, …).
///   2. Call <see cref="AcquireFrame"/> in a loop to get BGRA frames.
///   3. Call <see cref="Dispose"/> to release all resources.
///
/// Pass monitorIndex = -1 to auto-select the first non-primary monitor
/// (typically the Parsec Virtual Display). Falls back to primary if none found.
/// </summary>
public sealed class DxgiScreenCapture : IDisposable
{
    private ID3D11Device?           _device;
    private ID3D11DeviceContext?    _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D?        _stagingTexture;
    private bool                    _started;
    private bool                    _disposed;

    /// <summary>Width of the captured monitor in pixels.</summary>
    public int Width  { get; private set; }
    /// <summary>Height of the captured monitor in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Initialises D3D11 device and begins output duplication on the requested monitor.</summary>
    /// <param name="monitorIndex">Zero-based output index. -1 = auto (first non-primary).</param>
    public void Start(int monitorIndex = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.None,
            featureLevels,
            out _device,
            out _context).CheckError();

        using var dxgiDevice  = _device!.QueryInterface<IDXGIDevice>();
        using var dxgiAdapter = dxgiDevice.GetAdapter();

        // Pick target output index
        uint targetIndex = 0;
        if (monitorIndex == -1)
        {
            uint idx = 0;
            while (dxgiAdapter.EnumOutputs(idx, out var o).Success)
            {
                using var tmp = o!;
                var desc = tmp.Description;
                if (desc.AttachedToDesktop && idx > 0) { targetIndex = idx; break; }
                idx++;
            }
        }
        else
        {
            targetIndex = (uint)monitorIndex;
        }

        dxgiAdapter.EnumOutputs(targetIndex, out var selectedOutput).CheckError();
        using var output1 = selectedOutput!.QueryInterface<IDXGIOutput1>();

        var outDesc = output1.Description;
        Width  = outDesc.DesktopCoordinates.Right  - outDesc.DesktopCoordinates.Left;
        Height = outDesc.DesktopCoordinates.Bottom - outDesc.DesktopCoordinates.Top;

        _duplication = output1.DuplicateOutput(_device!);

        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width             = (uint)Width,
            Height            = (uint)Height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            BindFlags         = BindFlags.None,
            CPUAccessFlags    = CpuAccessFlags.Read,
            MiscFlags         = ResourceOptionFlags.None
        });

        selectedOutput!.Dispose();
        _started = true;
    }

    /// <summary>
    /// Acquires the next desktop frame and returns it as a BGRA byte array.
    /// Returns null if no new frame arrived within <paramref name="timeoutMs"/> ms.
    /// </summary>
    public byte[]? AcquireFrame(int timeoutMs = 50)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) throw new InvalidOperationException("Call Start() first.");

        const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

        var result = _duplication!.AcquireNextFrame(
            (uint)timeoutMs, out _, out var desktopResource);

        if (result.Code == DxgiErrorWaitTimeout) return null;
        result.CheckError();

        try
        {
            using var desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            _context!.CopyResource(_stagingTexture!, desktopTexture);

            var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes  = Width * 4;
                var frameData = new byte[rowBytes * Height];
                for (int row = 0; row < Height; row++)
                {
                    Marshal.Copy(
                        mapped.DataPointer + row * (int)mapped.RowPitch,
                        frameData,
                        row * rowBytes,
                        rowBytes);
                }
                return frameData;
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }
        }
        finally
        {
            _duplication!.ReleaseFrame();
            desktopResource?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _duplication?.Dispose();
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
