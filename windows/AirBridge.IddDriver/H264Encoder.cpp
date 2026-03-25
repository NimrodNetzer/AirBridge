// H264Encoder.cpp — Media Foundation H.264 encoder implementation
//
// Uses the software/hardware H.264 MFT that ships with Windows 8+.
// Input: BGRA (MFVideoFormat_ARGB32 — Windows MF names this format ARGB32 but
//        the byte order on little-endian is actually BGRA in memory).
// Output: H.264 Baseline (no B-frames, constrained baseline for low latency).
//
// Key design choices:
//   - We request LOW_LATENCY mode to minimise encoder-induced buffering.
//   - Each call to EncodeFrame() submits one input sample and then drains all
//     available output samples synchronously.  On a fast encoder this is ~1:1.
//   - The caller is responsible for framing the NAL bytes before writing to the
//     named pipe.

#include "H264Encoder.h"

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mfplat.lib")

// Guids for the H.264 encoder MFT
static const GUID MFT_CATEGORY_VIDEO_ENCODER_GUID = MFT_CATEGORY_VIDEO_ENCODER;

// ── Initialize ─────────────────────────────────────────────────────────────

HRESULT H264Encoder::Initialize(UINT32 width, UINT32 height, UINT32 fps) noexcept
{
    if (m_initialized)
        return S_OK;

    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) return hr;

    // Enumerate H.264 encoders
    MFT_REGISTER_TYPE_INFO outputType = {};
    outputType.guidMajorType = MFMediaType_Video;
    outputType.guidSubtype   = MFVideoFormat_H264;

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;
    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        nullptr,      // any input type
        &outputType,
        &ppActivate,
        &count);
    if (FAILED(hr) || count == 0)
    {
        // Fall back to software encoder
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            nullptr,
            &outputType,
            &ppActivate,
            &count);
        if (FAILED(hr) || count == 0)
            return (FAILED(hr) ? hr : MF_E_TRANSFORM_NOT_POSSIBLE);
    }

    // Activate the first encoder
    hr = ppActivate[0]->ActivateObject(__uuidof(IMFTransform), reinterpret_cast<void**>(&m_pMFT));
    for (UINT32 i = 0; i < count; ++i) ppActivate[i]->Release();
    CoTaskMemFree(ppActivate);
    if (FAILED(hr)) return hr;

    m_width  = width;
    m_height = height;
    m_fps    = fps;

    // Request low-latency mode if the encoder supports ICodecAPI
    ICodecAPI* pCodecAPI = nullptr;
    if (SUCCEEDED(m_pMFT->QueryInterface(__uuidof(ICodecAPI), reinterpret_cast<void**>(&pCodecAPI))))
    {
        VARIANT vLowLatency;
        vLowLatency.vt    = VT_BOOL;
        vLowLatency.boolVal = VARIANT_TRUE;
        pCodecAPI->SetValue(&CODECAPI_AVLowLatencyMode, &vLowLatency);

        // Force Baseline profile (no B-frames)
        VARIANT vProfile;
        vProfile.vt  = VT_UI4;
        vProfile.ulVal = eAVEncH264VProfile_Base;
        pCodecAPI->SetValue(&CODECAPI_AVEncH264CABACEnable, nullptr); // disable CABAC
        pCodecAPI->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, nullptr);
        pCodecAPI->Release();
    }

    hr = SetOutputType();
    if (FAILED(hr)) return hr;

    hr = SetInputType();
    if (FAILED(hr)) return hr;

    hr = m_pMFT->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    if (FAILED(hr)) return hr;

    hr = m_pMFT->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (FAILED(hr)) return hr;

    hr = m_pMFT->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    if (FAILED(hr)) return hr;

    m_initialized = true;
    return S_OK;
}

HRESULT H264Encoder::SetOutputType() noexcept
{
    IMFMediaType* pType = nullptr;
    HRESULT hr = MFCreateMediaType(&pType);
    if (FAILED(hr)) return hr;

    hr = pType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = pType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(pType, MF_MT_FRAME_SIZE, m_width, m_height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(pType, MF_MT_FRAME_RATE, m_fps, 1);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(pType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = pType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = pType->SetUINT32(MF_MT_AVG_BITRATE, 8 * 1024 * 1024); // 8 Mbps
    if (SUCCEEDED(hr)) hr = m_pMFT->SetOutputType(0, pType, 0);

    pType->Release();
    return hr;
}

HRESULT H264Encoder::SetInputType() noexcept
{
    IMFMediaType* pType = nullptr;
    HRESULT hr = MFCreateMediaType(&pType);
    if (FAILED(hr)) return hr;

    // ARGB32 in MF terminology = BGRA byte order in memory on x86/x64
    hr = pType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = pType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_ARGB32);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(pType, MF_MT_FRAME_SIZE, m_width, m_height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(pType, MF_MT_FRAME_RATE, m_fps, 1);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(pType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (SUCCEEDED(hr)) hr = pType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr)) hr = m_pMFT->SetInputType(0, pType, 0);

    pType->Release();
    return hr;
}

// ── EncodeFrame ─────────────────────────────────────────────────────────────

HRESULT H264Encoder::EncodeFrame(
    const uint8_t*        bgra,
    size_t                size,
    LONGLONG              timestampHns,
    bool&                 isKeyFrame,
    std::vector<uint8_t>& nalOut) noexcept
{
    isKeyFrame = false;
    if (!m_initialized) return E_NOT_VALID_STATE;

    const DWORD frameBytes = static_cast<DWORD>(m_width * m_height * 4);
    if (size < frameBytes) return E_INVALIDARG;

    // Create / reuse input media buffer
    IMFMediaBuffer* pBuf = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(frameBytes, &pBuf);
    if (FAILED(hr)) return hr;

    BYTE* pData = nullptr;
    hr = pBuf->Lock(&pData, nullptr, nullptr);
    if (SUCCEEDED(hr))
    {
        memcpy(pData, bgra, frameBytes);
        pBuf->Unlock();
        pBuf->SetCurrentLength(frameBytes);
    }
    if (FAILED(hr)) { pBuf->Release(); return hr; }

    // Wrap buffer in a sample
    IMFSample* pSample = nullptr;
    hr = MFCreateSample(&pSample);
    if (FAILED(hr)) { pBuf->Release(); return hr; }

    pSample->AddBuffer(pBuf);
    pBuf->Release();
    pSample->SetSampleTime(timestampHns);
    pSample->SetSampleDuration(static_cast<LONGLONG>(10'000'000 / m_fps));

    hr = m_pMFT->ProcessInput(0, pSample, 0);
    pSample->Release();
    if (FAILED(hr)) return hr;

    // Drain all available output
    return DrainOutput(isKeyFrame, nalOut);
}

HRESULT H264Encoder::DrainOutput(bool& isKeyFrame, std::vector<uint8_t>& nalOut) noexcept
{
    MFT_OUTPUT_DATA_BUFFER outputDataBuffer = {};
    DWORD                  status           = 0;

    for (;;)
    {
        // Allocate a sample for output
        IMFSample*      pOutSample = nullptr;
        IMFMediaBuffer* pOutBuf    = nullptr;
        HRESULT hr = MFCreateSample(&pOutSample);
        if (FAILED(hr)) return hr;

        const DWORD outSize = m_width * m_height; // conservative upper bound
        hr = MFCreateMemoryBuffer(outSize, &pOutBuf);
        if (SUCCEEDED(hr)) hr = pOutSample->AddBuffer(pOutBuf);
        if (FAILED(hr)) { SafeRelease(pOutBuf); pOutSample->Release(); return hr; }
        pOutBuf->Release();

        outputDataBuffer.pSample = pOutSample;

        hr = m_pMFT->ProcessOutput(0, 1, &outputDataBuffer, &status);

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            pOutSample->Release();
            return S_FALSE; // encoder buffered the frame
        }

        if (FAILED(hr))
        {
            pOutSample->Release();
            return hr;
        }

        // Check for key frame
        UINT32 cleanPoint = 0;
        if (SUCCEEDED(pOutSample->GetUINT32(MFSampleExtension_CleanPoint, &cleanPoint)) && cleanPoint)
            isKeyFrame = true;

        // Copy all NAL bytes to nalOut
        DWORD bufCount = 0;
        pOutSample->GetBufferCount(&bufCount);
        for (DWORD b = 0; b < bufCount; ++b)
        {
            IMFMediaBuffer* pBuf = nullptr;
            if (SUCCEEDED(pOutSample->GetBufferByIndex(b, &pBuf)))
            {
                BYTE*  pData  = nullptr;
                DWORD  cbData = 0;
                if (SUCCEEDED(pBuf->Lock(&pData, nullptr, &cbData)))
                {
                    nalOut.insert(nalOut.end(), pData, pData + cbData);
                    pBuf->Unlock();
                }
                pBuf->Release();
            }
        }
        pOutSample->Release();
    }
}

// ── Shutdown ────────────────────────────────────────────────────────────────

void H264Encoder::Shutdown() noexcept
{
    if (!m_initialized) return;

    if (m_pMFT)
    {
        m_pMFT->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        m_pMFT->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    }
    SafeRelease(m_pInputBuf);
    SafeRelease(m_pMFT);
    MFShutdown();
    m_initialized = false;
}
