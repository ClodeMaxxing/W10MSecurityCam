#include "pch.h"
#include "EncodedMediaSink.h"
#include <algorithm>

using namespace Microsoft::WRL;
using namespace W10MSecurityCam::Native;

typedef std::lock_guard<std::recursive_mutex> Guard;

// MFSampleExtension_DeviceReferenceSystemTime (defined here so older SDK headers are not a problem)
static const GUID DeviceReferenceSystemTime = { 0x6523775a, 0xba2d, 0x405f, { 0xb2, 0xc5, 0x01, 0xff, 0x88, 0xe2, 0xe8, 0xf6 } };

static bool IsH264(IMFMediaType* type)
{
    if (type == nullptr) return false;
    GUID major = GUID_NULL, sub = GUID_NULL;
    if (FAILED(type->GetMajorType(&major)) || major != MFMediaType_Video) return false;
    if (FAILED(type->GetGUID(MF_MT_SUBTYPE, &sub))) return false;
    return sub == MFVideoFormat_H264 || sub == MFVideoFormat_H264_ES;
}

static bool IsAac(IMFMediaType* type)
{
    if (type == nullptr) return false;
    GUID major = GUID_NULL, sub = GUID_NULL;
    if (FAILED(type->GetMajorType(&major)) || major != MFMediaType_Audio) return false;
    if (FAILED(type->GetGUID(MF_MT_SUBTYPE, &sub))) return false;
    return sub == MFAudioFormat_AAC;
}

// ======================= EncodedMediaSink =======================

EncodedMediaSink::EncodedMediaSink() : _shutdown(false), _stopWorker(false), _dropped(0), _clockStartSystemTime(-1), _waitForKeyframe(false), _videoQueued(0) {}

EncodedMediaSink::~EncodedMediaSink()
{
    StopWorker();
}

void EncodedMediaSink::StopWorker()
{
    {
        std::lock_guard<std::mutex> g(_queueLock);
        _stopWorker = true;
        _queue.clear();
        _videoQueued = 0;
    }
    _queueSignal.notify_all();
    if (_worker.joinable())
    {
        if (_worker.get_id() == std::this_thread::get_id()) _worker.detach();
        else _worker.join();
    }
}

void EncodedMediaSink::WorkerLoop()
{
    for (;;)
    {
        PendingFrame frame;
        {
            std::unique_lock<std::mutex> g(_queueLock);
            _queueSignal.wait(g, [this] { return _stopWorker || !_queue.empty(); });
            if (_stopWorker) return;
            frame = std::move(_queue.front());
            _queue.pop_front();
            if (!frame.audio && _videoQueued > 0) _videoQueued--;
        }
        if (frame.audio)
        {
            AudioCallback acb;
            {
                std::lock_guard<std::mutex> g(_callbackLock);
                acb = _onAudio;
            }
            try { if (acb) acb(frame.data.data(), (DWORD)frame.data.size(), frame.time); } catch (...) { }
            continue;
        }
        FrameCallback cb;
        {
            std::lock_guard<std::mutex> g(_callbackLock);
            cb = _onFrame;
        }
        LONGLONG queueDelay = MFGetSystemTime() - frame.enqueuedAt;
        try { if (cb) cb(frame.data.data(), (DWORD)frame.data.size(), frame.time, frame.keyframe, queueDelay, frame.deviceLatency); } catch (...) { }
    }
}

HRESULT EncodedMediaSink::RuntimeClassInitialize(IMFMediaType* videoType, IMFMediaType* audioType)
{
    if (videoType != nullptr)
    {
        // Pre-create stream 0 for video. If the capture engine calls AddStreamSink(0, ...)
        // later, we hand back this same stream instead of failing.
        ComPtr<EncodedStreamSink> stream;
        HRESULT hr = MakeAndInitialize<EncodedStreamSink>(&stream, this, 0, videoType);
        if (FAILED(hr)) return hr;
        _streams.push_back(stream);
    }
    if (audioType != nullptr)
    {
        // Stream 1 for AAC audio (optional).
        ComPtr<EncodedStreamSink> stream;
        HRESULT hr = MakeAndInitialize<EncodedStreamSink>(&stream, this, 1, audioType);
        if (FAILED(hr)) return hr;
        _streams.push_back(stream);
    }
    return S_OK;
}

void EncodedMediaSink::SetCallbacks(FrameCallback onFrame, LogCallback onLog, AudioCallback onAudio)
{
    {
        std::lock_guard<std::mutex> g(_callbackLock);
        _onFrame = onFrame;
        _onLog = onLog;
        _onAudio = onAudio;
    }
    if (!_worker.joinable()) _worker = std::thread([this] { WorkerLoop(); });
}

void EncodedMediaSink::ClearCallbacks()
{
    std::lock_guard<std::mutex> g(_callbackLock);
    _onFrame = nullptr;
    _onLog = nullptr;
    _onAudio = nullptr;
}

void EncodedMediaSink::Log(const wchar_t* format, ...)
{
    wchar_t buffer[512];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(buffer, _countof(buffer), _TRUNCATE, format, args);
    va_end(args);
    LogCallback cb;
    {
        std::lock_guard<std::mutex> g(_callbackLock);
        cb = _onLog;
    }
    try { if (cb) cb(buffer); } catch (...) { }
}

void EncodedMediaSink::DeliverFrame(const BYTE* data, DWORD length, LONGLONG sampleTime, bool keyframe, LONGLONG /*unused*/, LONGLONG deviceLatency)
{
    bool dropped = false;
    {
        std::lock_guard<std::mutex> g(_queueLock);
        if (_stopWorker) return;
        // Keep at most a few frames between the camera and the app. If the app falls behind,
        // throw the backlog away and resume at the next keyframe instead of adding lag.
        if (_videoQueued >= 3)
        {
            _dropped += _videoQueued;
            // only the video backlog: queued audio frames are small and stay in order
            _queue.erase(std::remove_if(_queue.begin(), _queue.end(), [](const PendingFrame& p) { return !p.audio; }), _queue.end());
            _videoQueued = 0;
            _waitForKeyframe = true;
            dropped = true;
        }
        if (_waitForKeyframe)
        {
            if (!keyframe) { _dropped++; return; }
            _waitForKeyframe = false;
        }
        PendingFrame f;
        f.data.assign(data, data + length);
        f.time = sampleTime;
        f.keyframe = keyframe;
        f.enqueuedAt = MFGetSystemTime();
        f.deviceLatency = deviceLatency;
        f.audio = false;
        _queue.push_back(std::move(f));
        _videoQueued++;
    }
    _queueSignal.notify_one();
    if (dropped) Log(L"App fell behind - cleared backlog, resuming at next keyframe (%u frames dropped so far)", _dropped);
}

void EncodedMediaSink::DeliverAudio(const BYTE* data, DWORD length, LONGLONG sampleTime)
{
    // Strip an ADTS header if the encoder added one; RTP (RFC 3640) carries raw AAC frames.
    if (length > 7 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0)
    {
        DWORD header = (data[1] & 0x01) ? 7 : 9;
        if (length <= header) return;
        data += header;
        length -= header;
    }
    {
        std::lock_guard<std::mutex> g(_queueLock);
        if (_stopWorker) return;
        if (_queue.size() > 200) return; // app not reading at all; don't grow without limit
        PendingFrame f;
        f.data.assign(data, data + length);
        f.time = sampleTime;
        f.keyframe = true;
        f.enqueuedAt = MFGetSystemTime();
        f.deviceLatency = -1;
        f.audio = true;
        _queue.push_back(std::move(f));
    }
    _queueSignal.notify_one();
}

std::vector<BYTE> EncodedMediaSink::GetAudioConfig()
{
    Guard g(_lock);
    for (auto& s : _streams)
    {
        if (!s->IsAudio()) continue;
        auto userData = s->AudioUserData();
        // MF_MT_USER_DATA for AAC = the HEAACWAVEINFO fields after WAVEFORMATEX (12 bytes), then AudioSpecificConfig.
        if (userData.size() > 12) return std::vector<BYTE>(userData.begin() + 12, userData.end());
    }
    return std::vector<BYTE>();
}

std::vector<BYTE> EncodedMediaSink::GetSequenceHeader()
{
    Guard g(_lock);
    for (auto& s : _streams)
    {
        if (s->IsAudio()) continue;
        auto h = s->SequenceHeader();
        if (!h.empty()) return h;
    }
    return std::vector<BYTE>();
}

ComPtr<EncodedStreamSink> EncodedMediaSink::FindStream(DWORD id)
{
    for (auto& s : _streams)
        if (s->Id() == id) return s;
    return nullptr;
}

IFACEMETHODIMP EncodedMediaSink::SetProperties(ABI::Windows::Foundation::Collections::IPropertySet*)
{
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::GetCharacteristics(DWORD* characteristics)
{
    if (characteristics == nullptr) return E_POINTER;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    *characteristics = MEDIASINK_RATELESS;
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::AddStreamSink(DWORD id, IMFMediaType* mediaType, IMFStreamSink** streamSink)
{
    if (streamSink == nullptr) return E_POINTER;
    *streamSink = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;

    // Reuse a pre-created stream of the same kind (audio or video), whatever id the engine asks for.
    bool wantAudio = IsAac(mediaType);
    for (auto& s : _streams)
    {
        if (s->IsAudio() != wantAudio) continue;
        if (s->Id() != id)
        {
            auto clash = FindStream(id);
            if (clash) clash->SetId(s->Id()); // swap ids so they stay unique
            s->SetId(id);
        }
        if (mediaType != nullptr) s->SetCurrentMediaType(mediaType);
        Log(L"AddStreamSink(%u): reusing pre-created %s stream", id, wantAudio ? L"audio" : L"video");
        return s.CopyTo(streamSink);
    }

    ComPtr<EncodedStreamSink> stream;
    HRESULT hr = MakeAndInitialize<EncodedStreamSink>(&stream, this, id, mediaType);
    if (FAILED(hr))
    {
        Log(L"AddStreamSink(%u) failed 0x%08X", id, hr);
        return hr;
    }
    _streams.push_back(stream);
    Log(L"AddStreamSink(%u): new stream", id);
    return stream.CopyTo(streamSink);
}

IFACEMETHODIMP EncodedMediaSink::RemoveStreamSink(DWORD id)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    for (auto it = _streams.begin(); it != _streams.end(); ++it)
    {
        if ((*it)->Id() == id)
        {
            (*it)->Shutdown();
            _streams.erase(it);
            return S_OK;
        }
    }
    return MF_E_INVALIDSTREAMNUMBER;
}

IFACEMETHODIMP EncodedMediaSink::GetStreamSinkCount(DWORD* count)
{
    if (count == nullptr) return E_POINTER;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    *count = (DWORD)_streams.size();
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::GetStreamSinkByIndex(DWORD index, IMFStreamSink** streamSink)
{
    if (streamSink == nullptr) return E_POINTER;
    *streamSink = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (index >= _streams.size()) return MF_E_INVALIDINDEX;
    return _streams[index].CopyTo(streamSink);
}

IFACEMETHODIMP EncodedMediaSink::GetStreamSinkById(DWORD id, IMFStreamSink** streamSink)
{
    if (streamSink == nullptr) return E_POINTER;
    *streamSink = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    auto s = FindStream(id);
    if (!s) return MF_E_INVALIDSTREAMNUMBER;
    return s.CopyTo(streamSink);
}

IFACEMETHODIMP EncodedMediaSink::SetPresentationClock(IMFPresentationClock* presentationClock)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (_clock) _clock->RemoveClockStateSink(static_cast<IMFClockStateSink*>(this));
    _clock = presentationClock;
    if (_clock) _clock->AddClockStateSink(static_cast<IMFClockStateSink*>(this));
    Log(L"SetPresentationClock(%s)", presentationClock ? L"set" : L"null");
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::GetPresentationClock(IMFPresentationClock** presentationClock)
{
    if (presentationClock == nullptr) return E_POINTER;
    *presentationClock = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (!_clock) return MF_E_NO_CLOCK;
    return _clock.CopyTo(presentationClock);
}

IFACEMETHODIMP EncodedMediaSink::Shutdown()
{
    StopWorker(); // outside the lock: the worker may be inside app code that calls back into the sink
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    _shutdown = true;
    for (auto& s : _streams) s->Shutdown();
    _streams.clear();
    if (_clock)
    {
        _clock->RemoveClockStateSink(static_cast<IMFClockStateSink*>(this));
        _clock.Reset();
    }
    Log(L"Sink shut down");
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::OnClockStart(MFTIME systemTime, LONGLONG clockStartOffset)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    // Presentation time 0 corresponds to this system time; used to measure how old each frame is when it leaves the encoder.
    _clockStartSystemTime = systemTime - (clockStartOffset == PRESENTATION_CURRENT_POSITION ? 0 : clockStartOffset);
    Log(L"Clock start (offset %lld), %u stream(s)", clockStartOffset, (unsigned)_streams.size());
    for (auto& s : _streams) s->Start();
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::OnClockStop(MFTIME)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    Log(L"Clock stop");
    for (auto& s : _streams) s->Stop();
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::OnClockPause(MFTIME)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    for (auto& s : _streams) s->Pause();
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::OnClockRestart(MFTIME)
{
    Guard g(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    for (auto& s : _streams) s->Restart();
    return S_OK;
}

IFACEMETHODIMP EncodedMediaSink::OnClockSetRate(MFTIME, float)
{
    return S_OK;
}

// ======================= EncodedStreamSink =======================

EncodedStreamSink::EncodedStreamSink() : _shutdown(false), _id(0), _sink(nullptr), _samples(0), _isAudio(false) {}

EncodedStreamSink::~EncodedStreamSink() {}

HRESULT EncodedStreamSink::RuntimeClassInitialize(EncodedMediaSink* sink, DWORD id, IMFMediaType* mediaType)
{
    _sink = sink;
    _id = id;
    _isAudio = IsAac(mediaType);
    HRESULT hr = MFCreateEventQueue(&_queue);
    if (FAILED(hr)) return hr;
    if (mediaType != nullptr) SetCurrentMediaType(mediaType);
    return S_OK;
}

std::vector<BYTE> EncodedStreamSink::AudioUserData()
{
    Guard g(_lock);
    std::vector<BYTE> result;
    if (!_type) return result;
    UINT32 size = 0;
    if (SUCCEEDED(_type->GetBlobSize(MF_MT_USER_DATA, &size)) && size > 0)
    {
        result.resize(size);
        if (FAILED(_type->GetBlob(MF_MT_USER_DATA, result.data(), size, &size))) result.clear();
    }
    return result;
}

ComPtr<IMFMediaEventQueue> EncodedStreamSink::GetQueue()
{
    Guard g(_lock);
    if (_shutdown) return nullptr;
    return _queue;
}

HRESULT EncodedStreamSink::Start()
{
    QueueEvent(MEStreamSinkStarted, GUID_NULL, S_OK, nullptr);
    return QueueEvent(MEStreamSinkRequestSample, GUID_NULL, S_OK, nullptr);
}

HRESULT EncodedStreamSink::Stop()
{
    return QueueEvent(MEStreamSinkStopped, GUID_NULL, S_OK, nullptr);
}

HRESULT EncodedStreamSink::Pause()
{
    return QueueEvent(MEStreamSinkPaused, GUID_NULL, S_OK, nullptr);
}

HRESULT EncodedStreamSink::Restart()
{
    QueueEvent(MEStreamSinkStarted, GUID_NULL, S_OK, nullptr);
    return QueueEvent(MEStreamSinkRequestSample, GUID_NULL, S_OK, nullptr);
}

void EncodedStreamSink::Shutdown()
{
    Guard g(_lock);
    if (_shutdown) return;
    _shutdown = true;
    if (_queue) _queue->Shutdown();
    _queue.Reset();
    _sink = nullptr;
}

std::vector<BYTE> EncodedStreamSink::SequenceHeader()
{
    Guard g(_lock);
    std::vector<BYTE> result;
    if (!_type) return result;
    UINT32 size = 0;
    if (SUCCEEDED(_type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &size)) && size > 0)
    {
        result.resize(size);
        if (FAILED(_type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, result.data(), size, &size))) result.clear();
    }
    return result;
}

IFACEMETHODIMP EncodedStreamSink::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    auto q = GetQueue(); // don't hold the lock while blocking
    if (!q) return MF_E_SHUTDOWN;
    return q->GetEvent(flags, event);
}

IFACEMETHODIMP EncodedStreamSink::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    auto q = GetQueue();
    if (!q) return MF_E_SHUTDOWN;
    return q->BeginGetEvent(callback, state);
}

IFACEMETHODIMP EncodedStreamSink::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    auto q = GetQueue();
    if (!q) return MF_E_SHUTDOWN;
    return q->EndGetEvent(result, event);
}

IFACEMETHODIMP EncodedStreamSink::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value)
{
    auto q = GetQueue();
    if (!q) return MF_E_SHUTDOWN;
    return q->QueueEventParamVar(type, extendedType, status, value);
}

IFACEMETHODIMP EncodedStreamSink::GetMediaSink(IMFMediaSink** mediaSink)
{
    if (mediaSink == nullptr) return E_POINTER;
    *mediaSink = nullptr;
    Guard g(_lock);
    if (_shutdown || _sink == nullptr) return MF_E_STREAMSINK_REMOVED;
    return _sink->QueryInterface(IID_PPV_ARGS(mediaSink));
}

IFACEMETHODIMP EncodedStreamSink::GetIdentifier(DWORD* identifier)
{
    if (identifier == nullptr) return E_POINTER;
    *identifier = _id;
    return S_OK;
}

IFACEMETHODIMP EncodedStreamSink::GetMediaTypeHandler(IMFMediaTypeHandler** handler)
{
    if (handler == nullptr) return E_POINTER;
    Guard g(_lock);
    if (_shutdown) return MF_E_STREAMSINK_REMOVED;
    return QueryInterface(IID_PPV_ARGS(handler));
}

IFACEMETHODIMP EncodedStreamSink::ProcessSample(IMFSample* sample)
{
    if (sample == nullptr) return E_POINTER;
    EncodedMediaSink* sink;
    {
        Guard g(_lock);
        if (_shutdown) return MF_E_STREAMSINK_REMOVED;
        sink = _sink;
    }

    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = sample->ConvertToContiguousBuffer(&buffer);
    if (SUCCEEDED(hr))
    {
        BYTE* data = nullptr;
        DWORD length = 0;
        hr = buffer->Lock(&data, nullptr, &length);
        if (SUCCEEDED(hr))
        {
            LONGLONG time = 0;
            sample->GetSampleTime(&time);
            UINT32 clean = 0;
            sample->GetUINT32(MFSampleExtension_CleanPoint, &clean);
            _samples++;
            if (_isAudio)
            {
                if (_samples == 1 && sink != nullptr)
                    sink->Log(L"Audio sample 1: %u bytes, time %lld, starts %02X %02X", length, time,
                        length > 0 ? data[0] : 0, length > 1 ? data[1] : 0);
                if (sink != nullptr) sink->DeliverAudio(data, length, time);
                buffer->Unlock();
                return QueueEvent(MEStreamSinkRequestSample, GUID_NULL, S_OK, nullptr);
            }
            if (_samples == 1 && sink != nullptr)
                sink->Log(L"Sample %lld: %u bytes, time %lld, keyframe %u, starts %02X %02X %02X %02X %02X",
                    _samples, length, time, clean,
                    length > 0 ? data[0] : 0, length > 1 ? data[1] : 0, length > 2 ? data[2] : 0,
                    length > 3 ? data[3] : 0, length > 4 ? data[4] : 0);
            LONGLONG now = MFGetSystemTime();
            LONGLONG captureLatency = -1, deviceLatency = -1;
            if (sink != nullptr && sink->ClockStartSystemTime() >= 0)
                captureLatency = now - sink->ClockStartSystemTime() - time;
            UINT64 deviceTime = 0;
            if (SUCCEEDED(sample->GetUINT64(DeviceReferenceSystemTime, &deviceTime)) && deviceTime > 0)
                deviceLatency = now - (LONGLONG)deviceTime;
            if (sink != nullptr) sink->DeliverFrame(data, length, time, clean != 0, captureLatency, deviceLatency);
            buffer->Unlock();
        }
    }
    if (FAILED(hr) && sink != nullptr) sink->Log(L"ProcessSample buffer error 0x%08X", hr);

    // Ask the pipeline for the next sample.
    return QueueEvent(MEStreamSinkRequestSample, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP EncodedStreamSink::PlaceMarker(MFSTREAMSINK_MARKER_TYPE markerType, const PROPVARIANT*, const PROPVARIANT* contextValue)
{
    // Samples are handled synchronously, so every marker is "reached" immediately.
    EncodedMediaSink* sink;
    {
        Guard g(_lock);
        if (_shutdown) return MF_E_STREAMSINK_REMOVED;
        sink = _sink;
    }
    (void)markerType; (void)sink;
    return QueueEvent(MEStreamSinkMarker, GUID_NULL, S_OK, contextValue);
}

IFACEMETHODIMP EncodedStreamSink::Flush()
{
    Guard g(_lock);
    if (_shutdown) return MF_E_STREAMSINK_REMOVED;
    return S_OK;
}

IFACEMETHODIMP EncodedStreamSink::IsMediaTypeSupported(IMFMediaType* mediaType, IMFMediaType** closestMediaType)
{
    if (closestMediaType) *closestMediaType = nullptr;
    if (mediaType == nullptr) return E_POINTER;
    if (_isAudio ? IsAac(mediaType) : IsH264(mediaType)) return S_OK;
    GUID sub = GUID_NULL;
    mediaType->GetGUID(MF_MT_SUBTYPE, &sub);
    EncodedMediaSink* sink = _sink;
    if (sink) sink->Log(L"Stream %u rejected media type (subtype %08X)", _id, sub.Data1);
    return MF_E_INVALIDMEDIATYPE;
}

IFACEMETHODIMP EncodedStreamSink::GetMediaTypeCount(DWORD* typeCount)
{
    if (typeCount == nullptr) return E_POINTER;
    *typeCount = 1;
    return S_OK;
}

IFACEMETHODIMP EncodedStreamSink::GetMediaTypeByIndex(DWORD index, IMFMediaType** mediaType)
{
    if (mediaType == nullptr) return E_POINTER;
    *mediaType = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_STREAMSINK_REMOVED;
    if (index > 0 || !_type) return MF_E_NO_MORE_TYPES;
    return _type.CopyTo(mediaType);
}

IFACEMETHODIMP EncodedStreamSink::SetCurrentMediaType(IMFMediaType* mediaType)
{
    if (mediaType == nullptr) return E_POINTER;
    bool audio = IsAac(mediaType);
    if (!audio && !IsH264(mediaType)) return MF_E_INVALIDMEDIATYPE;
    EncodedMediaSink* sink;
    {
        Guard g(_lock);
        if (_shutdown) return MF_E_STREAMSINK_REMOVED;
        _type = mediaType;
        _isAudio = audio;
        sink = _sink;
    }
    if (audio)
    {
        UINT32 rate = 0, channels = 0, userData = 0, bits = 0, avg = 0, payload = 99, level = 0;
        mediaType->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &rate);
        mediaType->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels);
        mediaType->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits);
        mediaType->GetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, &avg);
        mediaType->GetUINT32(MF_MT_AAC_PAYLOAD_TYPE, &payload);
        mediaType->GetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, &level);
        mediaType->GetBlobSize(MF_MT_USER_DATA, &userData);
        if (sink) sink->Log(L"Stream %u media type: AAC %u Hz, %u ch, %u bit, %u bytes/s, payload %u, level 0x%02X, user data %u bytes",
            _id, rate, channels, bits, avg, payload, level, userData);
        return S_OK;
    }
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(mediaType, MF_MT_FRAME_SIZE, &w, &h);
    UINT32 headerSize = 0;
    mediaType->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerSize);
    if (sink) sink->Log(L"Stream %u media type: H.264 %ux%u, sequence header %u bytes", _id, w, h, headerSize);
    return S_OK;
}

IFACEMETHODIMP EncodedStreamSink::GetCurrentMediaType(IMFMediaType** mediaType)
{
    if (mediaType == nullptr) return E_POINTER;
    *mediaType = nullptr;
    Guard g(_lock);
    if (_shutdown) return MF_E_STREAMSINK_REMOVED;
    if (!_type) return MF_E_NOT_INITIALIZED;
    return _type.CopyTo(mediaType);
}

IFACEMETHODIMP EncodedStreamSink::GetMajorType(GUID* majorType)
{
    if (majorType == nullptr) return E_POINTER;
    *majorType = _isAudio ? MFMediaType_Audio : MFMediaType_Video;
    return S_OK;
}
