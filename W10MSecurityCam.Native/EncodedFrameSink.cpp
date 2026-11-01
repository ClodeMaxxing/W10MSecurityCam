#include "pch.h"
#include "EncodedFrameSink.h"

using namespace W10MSecurityCam::Native;
using namespace Microsoft::WRL;
using namespace Platform;

EncodedFrameSink::EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties)
    : _hooked(false)
{
    Init(videoProperties, nullptr);
}

EncodedFrameSink::EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                                   Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties)
    : _hooked(false)
{
    Init(videoProperties, audioProperties);
}

void EncodedFrameSink::Init(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                            Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties)
{
    MFStartup(MF_VERSION, MFSTARTUP_LITE);

    ComPtr<IMFMediaType> type, audioType;
    if (videoProperties != nullptr)
    {
        HRESULT hr = MFCreateMediaTypeFromProperties(reinterpret_cast<IUnknown*>(videoProperties), &type);
        if (FAILED(hr)) type = nullptr;
    }
    if (audioProperties != nullptr)
    {
        HRESULT hr = MFCreateMediaTypeFromProperties(reinterpret_cast<IUnknown*>(audioProperties), &audioType);
        if (FAILED(hr)) audioType = nullptr;
        if (audioType)
        {
            // The capture engine configures the AAC encoder's output with this type, and the Microsoft AAC
            // encoder insists on a complete one: 16-bit samples, one of its bitrates, raw AAC payload.
            // The encoder writes its own AudioSpecificConfig, so don't hand it a user-data blob to argue with.
            UINT32 v = 0;
            if (FAILED(audioType->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &v)) || v == 0)
                audioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            v = 0;
            if (FAILED(audioType->GetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, &v)) ||
                (v != 12000 && v != 16000 && v != 20000 && v != 24000))
                audioType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 12000);
            audioType->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
            audioType->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            audioType->DeleteItem(MF_MT_USER_DATA);
            audioType->DeleteItem(MF_MT_AUDIO_BLOCK_ALIGNMENT);
        }
    }

    HRESULT hr = MakeAndInitialize<EncodedMediaSink>(&_sink, type.Get(), audioType.Get());
    if (FAILED(hr)) throw Exception::CreateException(hr, L"Could not create the native media sink");
}

EncodedFrameSink::~EncodedFrameSink()
{
    if (_sink) _sink->ClearCallbacks();
    _sink = nullptr;
    MFShutdown();
}

Windows::Media::IMediaExtension^ EncodedFrameSink::MediaExtension::get()
{
    _callbackErrors = 0; // handed out for each stream start: report handler errors again in this session
    if (!_hooked)
    {
        _hooked = true;
        WeakReference weak(this);
        _sink->SetCallbacks(
            [weak](const BYTE* data, DWORD length, LONGLONG time, bool keyframe, LONGLONG queueDelay, LONGLONG deviceLatency)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseFrame(data, length, time, keyframe, queueDelay, deviceLatency);
            },
            [weak](const wchar_t* message)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseLog(message);
            },
            [weak](const BYTE* data, DWORD length, LONGLONG time)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseAudio(data, length, time);
            });
    }
    // Hand out our own reference: reinterpret_cast to a ^ does not AddRef, but the ^ releases when done.
    ComPtr<ABI::Windows::Media::IMediaExtension> extension;
    _sink.As(&extension);
    return reinterpret_cast<Windows::Media::IMediaExtension^>(extension.Detach());
}

Array<uint8>^ EncodedFrameSink::GetSequenceHeader()
{
    auto header = _sink->GetSequenceHeader();
    auto result = ref new Array<uint8>((unsigned int)header.size());
    if (!header.empty()) memcpy(result->Data, header.data(), header.size());
    return result;
}

void EncodedFrameSink::RaiseFrame(const BYTE* data, DWORD length, LONGLONG sampleTime, bool keyframe, LONGLONG queueDelay, LONGLONG deviceLatency)
{
    // Runs on the sink's worker thread. An exception escaping from here would reach std::terminate and kill the app
    // (crash dump: 0xC0000409 fast-fail in ucrtbase), so catch everything and report it as a log line instead.
    try
    {
        if (length == 0 || data == nullptr)
        {
            FrameReceived(ref new Array<uint8>(0u), sampleTime, keyframe, queueDelay, deviceLatency);
            return;
        }
        // No copy here: the interop layer copies the bytes into the C# handler's own byte[], and the
        // reference is only used during this call (the worker owns the buffer until the call returns).
        ArrayReference<uint8> frame(const_cast<uint8*>(data), length);
        FrameReceived(frame, sampleTime, keyframe, queueDelay, deviceLatency);
    }
    catch (Platform::Exception^ e) { ReportCallbackError(L"video frame", e->HResult); }
    catch (...) { ReportCallbackError(L"video frame", 0); }
}

Array<uint8>^ EncodedFrameSink::GetAudioConfig()
{
    auto config = _sink->GetAudioConfig();
    auto result = ref new Array<uint8>((unsigned int)config.size());
    if (!config.empty()) memcpy(result->Data, config.data(), config.size());
    return result;
}

void EncodedFrameSink::RaiseAudio(const BYTE* data, DWORD length, LONGLONG sampleTime)
{
    try
    {
        auto copy = ref new Array<uint8>(const_cast<uint8*>(data), length);
        AudioReceived(copy, sampleTime);
    }
    catch (Platform::Exception^ e) { ReportCallbackError(L"audio frame", e->HResult); }
    catch (...) { ReportCallbackError(L"audio frame", 0); }
}

void EncodedFrameSink::RaiseLog(const wchar_t* message)
{
    try { LogMessage(ref new String(message)); }
    catch (...) { }
}

void EncodedFrameSink::ReportCallbackError(const wchar_t* what, int hr)
{
    // Limit to a handful of lines so a handler that always throws cannot flood the log.
    if (_callbackErrors++ >= 5) return;
    wchar_t buffer[160];
    swprintf_s(buffer, L"The app's %s handler threw an exception (0x%08X) - ignored", what, (unsigned)hr);
    RaiseLog(buffer);
}
