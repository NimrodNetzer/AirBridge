// H264Encoder.h — Media Foundation H.264 encoder wrapper
//
// Wraps the Windows built-in H.264 MFT (Media Foundation Transform) to encode
// one raw BGRA/ARGB frame at a time and return NAL unit bytes.
// The encoder is configured once via Initialize() and then EncodeFrame() is
// called for every display frame from the swap-chain thread.

#pragma once
#include "Driver.h"

/// <summary>
/// Synchronous, single-threaded H.264 encoder backed by the Windows MFT.
/// Not thread-safe — must be called from the swap-chain thread only.
/// </summary>
class H264Encoder
{
public:
    H264Encoder() noexcept = default;
    ~H264Encoder() noexcept { Shutdown(); }

    // Non-copyable, non-movable
    H264Encoder(const H264Encoder&) = delete;
    H264Encoder& operator=(const H264Encoder&) = delete;

    /// <summary>
    /// Initialises the MF pipeline for the given frame dimensions and rate.
    /// Must be called before any EncodeFrame() calls.
    /// </summary>
    /// <param name="width">Frame width in pixels (must be even).</param>
    /// <param name="height">Frame height in pixels (must be even).</param>
    /// <param name="fps">Target frames per second (e.g. 60).</param>
    /// <returns>S_OK on success; an HRESULT error code on failure.</returns>
    HRESULT Initialize(UINT32 width, UINT32 height, UINT32 fps) noexcept;

    /// <summary>
    /// Encodes one BGRA (32 bpp) frame and appends the H.264 NAL bytes to
    /// <paramref name="nalOut"/>.
    /// </summary>
    /// <param name="bgra">Pointer to the raw BGRA pixel data (width*height*4 bytes).</param>
    /// <param name="size">Size of the <paramref name="bgra"/> buffer in bytes.</param>
    /// <param name="timestampHns">Presentation timestamp in 100-nanosecond units.</param>
    /// <param name="isKeyFrame">Set to true if the output contains an IDR frame.</param>
    /// <param name="nalOut">Output vector — NAL bytes are appended here.</param>
    /// <returns>S_OK if at least one NAL unit was produced; S_FALSE if the encoder
    ///          buffered the frame and will output on the next call; failure HRESULT
    ///          on error.</returns>
    HRESULT EncodeFrame(
        const uint8_t*        bgra,
        size_t                size,
        LONGLONG              timestampHns,
        bool&                 isKeyFrame,
        std::vector<uint8_t>& nalOut) noexcept;

    /// <summary>Flushes the encoder and releases all MF resources.</summary>
    void Shutdown() noexcept;

    bool IsInitialized() const noexcept { return m_initialized; }

private:
    bool            m_initialized = false;
    UINT32          m_width       = 0;
    UINT32          m_height      = 0;
    UINT32          m_fps         = 0;

    // MF objects — all AddRef/Release handled via ComPtr-like raw pointers
    IMFTransform*   m_pMFT        = nullptr;  ///< H.264 encoder MFT
    IMFMediaBuffer* m_pInputBuf   = nullptr;  ///< Reusable input media buffer

    // Internal helpers
    HRESULT SetInputType()  noexcept;
    HRESULT SetOutputType() noexcept;
    HRESULT DrainOutput(bool& isKeyFrame, std::vector<uint8_t>& nalOut) noexcept;

    // Safely release a COM pointer
    template<typename T>
    static void SafeRelease(T*& p) noexcept
    {
        if (p) { p->Release(); p = nullptr; }
    }
};
