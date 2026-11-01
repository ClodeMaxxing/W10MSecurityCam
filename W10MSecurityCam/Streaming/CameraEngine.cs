using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Core;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace W10MSecurityCam.Streaming
{
    /// <summary>The AAC track's format: what the SDP needs.</summary>
    internal sealed class AudioFormat
    {
        public int SampleRate;
        public int Channels;
        public byte[] Config; // AudioSpecificConfig
    }

    internal sealed class FormatOption
    {
        public VideoEncodingProperties Props;
        public uint Width, Height;
        public double Fps;
        public override string ToString() => Width + " x " + Height + " @ " + Math.Round(Fps) + " fps";
    }

    /// <summary>
    /// Owns the camera. Uses the phone's hardware H.264 encoder (via the normal MP4
    /// recording path) and taps the encoded frames out live through Mp4TapStream.
    /// </summary>
    internal sealed class CameraEngine
    {
        // MF_MT_MAX_KEYFRAME_SPACING: ask the encoder for a keyframe every N frames.
        private static readonly Guid MaxKeyframeSpacing = new Guid("c16eb52b-73a1-476f-8d62-839d6a020652");
        // Encoder (ICodecAPI) settings, passed through to the hardware encoder.
        private static readonly Guid RateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423"); // CODECAPI_AVEncCommonRateControlMode
        private static readonly Guid MeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");     // CODECAPI_AVEncCommonMeanBitRate
        private static readonly Guid MaxBitRate = new Guid("9651eae4-39b9-4ebf-85ef-d7f444ec7465");      // CODECAPI_AVEncCommonMaxBitRate
        private static readonly Guid GopSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");         // CODECAPI_AVEncMPVGOPSize
        private static readonly Guid LowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");      // CODECAPI_AVLowLatencyMode

        public MediaCapture Capture { get; private set; }
        public Action<string> Log = _ => { };
        /// <summary>
        /// One encoded frame (access unit): its NAL units without start codes (AUDs removed),
        /// its presentation time in 100 ns units, and whether it is a keyframe (contains an IDR slice).
        /// </summary>
        public event Action<IList<byte[]>, long, bool> AccessUnitReady;
        /// <summary>The decoder setup (SPS, PPS) for the current stream. Raised at start and whenever it changes.</summary>
        public event Action<byte[], byte[]> ParameterSetsReady;
        /// <summary>One AAC frame (raw) and its time in 100 ns units, on the same clock as the video.</summary>
        public event Action<byte[], long> AudioFrameReady;
        /// <summary>The audio format for the current stream, or null when the stream has no audio.</summary>
        public event Action<AudioFormat> AudioFormatReady;

        /// <summary>Ask for microphone audio (set before InitializeAsync). Only the low-latency engine carries audio.</summary>
        public bool AudioRequested;
        /// <summary>The microphone was opened with the camera.</summary>
        public bool AudioAvailable { get; private set; }
        /// <summary>The current stream includes audio.</summary>
        public bool AudioStreaming { get; private set; }
        public string AudioStatus { get; private set; } = "off";
        private const int AudioSampleRate = 48000, AudioChannels = 1;
        private const uint AudioBitrate = 96000;
        private long _audioFrames;
        private bool _audioConfigChecked;
        public bool IsStreaming { get; private set; }
        private bool _previewing;
        /// <summary>True while the camera preview is running.</summary>
        public bool Previewing => _previewing;

        private Mp4H264Extractor _extractor;
        private W10MSecurityCam.Native.EncodedFrameSink _nativeSink;
        private bool _nativeHeaderChecked;
        private long _nativeFrames, _nativeMaxGapMs, _nativeLastMs = -1, _nativeStatMs;

        /// <summary>Use the low-latency native sink (falls back to the MP4 path automatically if it fails).</summary>
        public bool UseNativeSink = true;
        public string PathName { get; private set; } = "";
        private Mp4TapStream _tap;
        private H264Config _config;
        private string _configKey;
        private int _profileChoice = -1;
        private uint _requestBitrate;

        // stats
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _frames, _bytes; // written on the encoder thread, read on the UI thread: Interlocked only (64-bit on ARM)
        private long _lastStatTicks, _lastFrames, _lastBytes;
        private double _lastIdrSec = -1;
        public double Fps { get; private set; }
        public double Mbps { get; private set; }
        public double KeyframeIntervalSec { get; private set; }

        /// <summary>Seconds between keyframes.</summary>
        public double KeyframeSeconds = 1.0;

        public async Task InitializeAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0) throw new Exception("No camera found on this device.");
            var dev = devices.FirstOrDefault(d => d.EnclosureLocation != null && d.EnclosureLocation.Panel == Panel.Back) ?? devices[0];
            Log("Camera: " + dev.Name);

            // Note: MediaCategory.Communications + DesiredOptimization=LatencyThenQuality were A/B tested on the
            // Lumia 950 and made no measurable difference, so they are not used (see NOTES.md).
            AudioAvailable = false;
            AudioStatus = "off";
            if (AudioRequested)
            {
                try
                {
                    // The first time, Windows asks the user whether the app may use the microphone.
                    await InitCaptureAsync(dev.Id, StreamingCaptureMode.AudioAndVideo);
                    AudioAvailable = true;
                    AudioStatus = "on";
                    Log("Microphone on");
                }
                catch (UnauthorizedAccessException)
                {
                    AudioStatus = "microphone access denied";
                    Log("Microphone access denied - video only. Allow it in Settings > Privacy > Microphone.");
                }
                catch (Exception ex)
                {
                    AudioStatus = "microphone failed: " + ex.Message;
                    Log("Microphone could not be opened (" + ex.Message + ") - video only");
                }
            }
            if (!AudioAvailable) await InitCaptureAsync(dev.Id, StreamingCaptureMode.Video);
            try { Capture.CameraStreamStateChanged += (s, e) => Log("Camera stream state: " + s.CameraStreamState); }
            catch (Exception ex) { Log("(camera state events unavailable: " + ex.Message + ")"); }
        }

        private async Task InitCaptureAsync(string deviceId, StreamingCaptureMode mode)
        {
            try { Capture?.Dispose(); } catch { }
            Capture = new MediaCapture();
            Capture.Failed += (s, e) => Log("Camera error: " + e.Message);
            var init = new MediaCaptureInitializationSettings { VideoDeviceId = deviceId, StreamingCaptureMode = mode };
            try
            {
                await Capture.InitializeAsync(init);
            }
            catch
            {
                try { Capture.Dispose(); } catch { }
                Capture = null;
                throw;
            }
        }

        /// <summary>AAC-LC AudioSpecificConfig (ISO 14496-3): object type 2, sampling index, channels.</summary>
        private static byte[] BuildAacConfig(int rate, int channels)
        {
            int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000 };
            int idx = Array.IndexOf(rates, rate);
            if (idx < 0) idx = 3;
            return new[] { (byte)((2 << 3) | (idx >> 1)), (byte)(((idx & 1) << 7) | (channels << 3)) };
        }

        public List<FormatOption> GetRecordFormats()
        {
            var all = Capture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
                .OfType<VideoEncodingProperties>()
                .Where(p => p.FrameRate.Denominator != 0)
                .Select(p => new FormatOption
                {
                    Props = p,
                    Width = p.Width,
                    Height = p.Height,
                    Fps = (double)p.FrameRate.Numerator / p.FrameRate.Denominator
                })
                .Where(f => f.Fps >= 14)
                .ToList();

            // one entry per size+rate, preferring NV12
            return all
                .GroupBy(f => f.Width + "x" + f.Height + "@" + Math.Round(f.Fps))
                .Select(g => g.OrderByDescending(f => string.Equals(f.Props.Subtype, "NV12", StringComparison.OrdinalIgnoreCase)).First())
                .OrderByDescending(f => (long)f.Width * f.Height)
                .ThenByDescending(f => f.Fps)
                .ToList();
        }

        public async Task SetPreviewAsync(bool on)
        {
            if (Capture == null || on == _previewing) return; // avoid redundant start/stop calls on the camera
            // Starting or stopping the preview while a recording runs stalls the 4K pipeline (the engine then restarts) and
            // can end the app, so it only ever changes while the stream is stopped.
            if (IsStreaming) { Log("Preview change skipped while streaming (it applies when the stream restarts)"); return; }
            try
            {
                if (on) await Capture.StartPreviewAsync();
                else await Capture.StopPreviewAsync();
                _previewing = on;
            }
            catch (Exception ex) { Log("Preview: " + ex.Message); }
        }

        /// <summary>
        /// Automatic bitrate. Capped at 16 Mbps normally (4K30 tested clean at that rate over Wi-Fi), 40 Mbps in
        /// high bandwidth mode (5 GHz Wi-Fi or USB-C): what the link can carry is the limit, not the encoder.
        /// </summary>
        public static uint AutoBitrate(FormatOption f, bool highBandwidth = false)
        {
            double bps = f.Width * (double)f.Height * f.Fps * 0.15;
            return (uint)Math.Max(2000000, Math.Min(highBandwidth ? 40000000 : 16000000, bps));
        }

        private MediaEncodingProfile CreateProfile(FormatOption f, uint bitrate, int choice)
        {
            var p = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            p.Audio = null;
            p.Video.Width = f.Width;
            p.Video.Height = f.Height;
            p.Video.FrameRate.Numerator = f.Props.FrameRate.Numerator;
            p.Video.FrameRate.Denominator = f.Props.FrameRate.Denominator;
            p.Video.Bitrate = bitrate;
            uint gop = (uint)Math.Max(1, Math.Round(f.Fps * KeyframeSeconds));
            if (choice <= 1) p.Video.ProfileId = H264ProfileIds.Baseline; // no B-frames = lowest delay
            if (choice == 2) p.Video.ProfileId = H264ProfileIds.Main;
            if (choice <= 2) p.Video.Properties[MaxKeyframeSpacing] = gop;
            if (choice == 0)
            {
                p.Video.Properties[RateControlMode] = (uint)1; // peak-constrained VBR
                p.Video.Properties[MeanBitRate] = bitrate;
                p.Video.Properties[MaxBitRate] = (uint)Math.Min(uint.MaxValue, bitrate * 1.25);
                p.Video.Properties[GopSize] = gop;
                p.Video.Properties[LowLatency] = true;
            }
            return p;
        }

        private static readonly string[] ChoiceNames = { "Baseline + bitrate control", "Baseline", "Main", "encoder default" };

        /// <summary>
        /// Records ~1.5 s to a temp file once per setting. This (1) finds an encoder
        /// configuration the phone accepts, (2) reads the SPS/PPS decoder setup data,
        /// and (3) measures the real bitrate so we can correct for the encoder's overshoot.
        /// </summary>
        private async Task ProbeAsync(FormatOption f, uint targetBitrate)
        {
            string key = f + "/" + targetBitrate;
            if (_configKey == key && _config != null) return;

            // The phone's encoder ignores the requested bitrate, so the decoder setup (SPS/PPS)
            // for a resolution never changes. Remember it so later starts need no probe recording.
            var settings = ApplicationData.Current.LocalSettings.Values;
            string cacheKey = "Enc|" + f.Width + "x" + f.Height + "@" + Math.Round(f.Fps);
            if (settings[cacheKey] is string cached && TryLoadCached(cached))
            {
                _configKey = key;
                _requestBitrate = targetBitrate;
                Log("Using remembered encoder setup for " + f + " (no probe needed)");
                return;
            }

            Exception last = null;
            for (int choice = 0; choice < ChoiceNames.Length; choice++)
            {
                try
                {
                    Log("Probing encoder once for " + f + " (" + ChoiceNames[choice] + ")...");
                    var probe = await RecordProbeAsync(CreateProfile(f, targetBitrate, choice));
                    _config = probe.Item1;
                    _profileChoice = choice;
                    _requestBitrate = targetBitrate;
                    _configKey = key;
                    Log("Encoder OK: H.264 profile " + _config.Profile + " level " + _config.Level / 10.0 + ", " + (probe.Item2 / 1e6).ToString("0.0") + " Mbps");
                    settings[cacheKey] = SaveCached();
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log("  failed: " + ex.Message);
                    try { await Capture.StopRecordAsync(); } catch { }
                }
            }
            throw new Exception("Encoder would not start at this setting: " + last?.Message);
        }

        private string SaveCached()
        {
            return _profileChoice + "|" + _config.NalLengthSize + "|" + _config.Profile + "|" + _config.Level + "|" +
                   string.Join(",", _config.Sps.Select(Convert.ToBase64String)) + "|" +
                   string.Join(",", _config.Pps.Select(Convert.ToBase64String));
        }

        private bool TryLoadCached(string s)
        {
            try
            {
                var p = s.Split('|');
                var cfg = new H264Config
                {
                    NalLengthSize = int.Parse(p[1]),
                    Profile = int.Parse(p[2]),
                    Level = int.Parse(p[3])
                };
                cfg.Sps.AddRange(p[4].Split(',').Select(Convert.FromBase64String));
                cfg.Pps.AddRange(p[5].Split(',').Select(Convert.FromBase64String));
                if (cfg.Sps.Count == 0 || cfg.Pps.Count == 0) return false;
                _config = cfg;
                _profileChoice = int.Parse(p[0]);
                return true;
            }
            catch { return false; }
        }

        private async Task<Tuple<H264Config, double>> RecordProbeAsync(MediaEncodingProfile profile)
        {
            var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("probe.mp4", CreationCollisionOption.ReplaceExisting);
            await Capture.StartRecordToStorageFileAsync(profile, file);
            var sw = Stopwatch.StartNew();
            await Task.Delay(2000);
            await Capture.StopRecordAsync();
            double seconds = Math.Max(0.5, sw.Elapsed.TotalSeconds);
            var buffer = await FileIO.ReadBufferAsync(file);
            byte[] bytes = buffer.ToArray();
            var cfg = AvcConfigReader.Find(bytes);
            if (cfg == null) throw new Exception("no avcC found in probe file (" + bytes.Length + " bytes)");
            return Tuple.Create(cfg, bytes.Length * 8 / seconds);
        }

        public async Task StartStreamingAsync(FormatOption f, uint bitrate)
        {
            if (IsStreaming) return;
            Log("Setting camera to " + f + ", " + (bitrate / 1e6).ToString("0.#") + " Mbps");
            await Capture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, f.Props);
            await ProbeAsync(f, bitrate);

            // Hand the decoder setup to the server first.
            _lastSps = _config.Sps[0];
            _lastPps = _config.Pps[0];
            ParameterSetsReady?.Invoke(_lastSps, _lastPps);

            System.Threading.Interlocked.Exchange(ref _frames, 0);
            System.Threading.Interlocked.Exchange(ref _bytes, 0);
            _lastFrames = _lastBytes = 0;
            _lastStatTicks = _clock.ElapsedTicks;
            _lastIdrSec = -1;
            _mp4Pending.Clear();

            AudioStreaming = false;
            if (UseNativeSink)
            {
                // With the microphone open, try video + audio first; if the pipeline refuses, retry video only.
                for (int attempt = AudioAvailable ? 0 : 1; attempt < 2; attempt++)
                {
                    bool withAudio = attempt == 0;
                    try
                    {
                        var profile = CreateProfile(f, _requestBitrate, _profileChoice);
                        if (withAudio)
                        {
                            profile.Audio = AudioEncodingProperties.CreateAac(AudioSampleRate, AudioChannels, AudioBitrate);
                            AudioFormatReady?.Invoke(new AudioFormat
                            {
                                SampleRate = AudioSampleRate, Channels = AudioChannels, Config = BuildAacConfig(AudioSampleRate, AudioChannels)
                            });
                        }
                        else
                        {
                            AudioFormatReady?.Invoke(null);
                        }
                        _nativeSink = withAudio
                            ? new W10MSecurityCam.Native.EncodedFrameSink(profile.Video, profile.Audio)
                            : new W10MSecurityCam.Native.EncodedFrameSink(profile.Video);
                        _nativeSink.LogMessage += m => Log("[native] " + m);
                        _nativeSink.FrameReceived += OnEncodedFrame;
                        _nativeSink.AudioReceived += OnEncodedAudio;
                        _nativeHeaderChecked = false;
                        _audioConfigChecked = false;
                        _audioFrames = 0;
                        _nativeFrames = 0; _nativeMaxGapMs = 0; _nativeLastMs = -1; _nativeStatMs = _clock.ElapsedMilliseconds; _nativeStartMs = _clock.ElapsedMilliseconds;
                        await Capture.StartRecordToCustomSinkAsync(profile, _nativeSink.MediaExtension);
                        IsStreaming = true;
                        AudioStreaming = withAudio;
                        PathName = "low-latency";
                        Log("Streaming started (low-latency native path" + (withAudio ? ", with AAC audio" : "") + ")");
                        return;
                    }
                    catch (Exception ex)
                    {
                        try { await Capture.StopRecordAsync(); } catch { }
                        DisposeNativeSink();
                        if (withAudio)
                        {
                            Log("Starting with audio failed: " + ex.Message + " - retrying without audio");
                            AudioStatus = "audio failed: " + ex.Message;
                            continue;
                        }
                        Log("Native path failed: " + ex.Message + " - using MP4 path instead");
                    }
                }
            }
            AudioFormatReady?.Invoke(null); // the MP4 engine carries video only

            _extractor = new Mp4H264Extractor(Log) { NalLengthSize = _config.NalLengthSize };
            _extractor.NalReady += OnMp4Nal;
            _tap = new Mp4TapStream(_extractor, Log);
            await Capture.StartRecordToStreamAsync(CreateProfile(f, _requestBitrate, _profileChoice), _tap);
            IsStreaming = true;
            PathName = "MP4";
            Log("Streaming started (MP4 path)");
        }

        private void DisposeNativeSink()
        {
            if (_nativeSink == null) return;
            _nativeSink.FrameReceived -= OnEncodedFrame;
            _nativeSink.AudioReceived -= OnEncodedAudio;
            _nativeSink.Dispose();
            _nativeSink = null;
        }

        /// <summary>One AAC frame from the audio encoder (native path only).</summary>
        private void OnEncodedAudio(byte[] data, long sampleTime)
        {
            if (!_audioConfigChecked)
            {
                _audioConfigChecked = true;
                try
                {
                    // Prefer the encoder's own AudioSpecificConfig if it reports one.
                    var cfg = _nativeSink?.GetAudioConfig();
                    if (cfg != null && cfg.Length >= 2)
                    {
                        int idx = ((cfg[0] & 7) << 1) | (cfg[1] >> 7);
                        int ch = (cfg[1] >> 3) & 0xF;
                        int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000 };
                        if (idx < rates.Length && ch > 0)
                        {
                            Log("Encoder audio config: " + BitConverter.ToString(cfg) + " (" + rates[idx] + " Hz, " + ch + " ch)");
                            AudioFormatReady?.Invoke(new AudioFormat { SampleRate = rates[idx], Channels = ch, Config = cfg });
                        }
                    }
                }
                catch (Exception ex) { Log("Audio config: " + ex.Message); }
            }
            _audioFrames++;
            AudioFrameReady?.Invoke(data, sampleTime);
        }

        /// <summary>Encoded sample straight from the hardware encoder (native path).</summary>
        private long _latSum, _latCount, _latMax, _devSum, _devCount, _devMax, _maxFrameBytes;

        private int _frameErrors;
        private volatile int _frameStep; // where the frame handler was when it threw (the stack trace is stripped in release builds)

        private void OnEncodedFrame(byte[] data, long sampleTime, bool keyframe, long queueDelay, long deviceLatency)
        {
            // Runs on the native sink's worker thread: an exception escaping here used to end the app (fast-fail abort).
            try { OnEncodedFrameCore(data, sampleTime, keyframe, queueDelay, deviceLatency); }
            catch (Exception ex)
            {
                if (_frameErrors++ < 5) Log("Frame handler error at step " + _frameStep + " (frame skipped): " + ex.GetType().Name + ": " + ex.Message + " " + (ex.StackTrace ?? "").Split('\n').FirstOrDefault());
            }
        }

        private void OnEncodedFrameCore(byte[] data, long sampleTime, bool keyframe, long queueDelay, long deviceLatency)
        {
            if (data == null || data.Length == 0) return; // an empty sample at start-up: nothing to send
            _frameStep = 1;
            if (queueDelay >= 0 && queueDelay < 100000000) { _latSum += queueDelay; _latCount++; _latMax = Math.Max(_latMax, queueDelay); }
            if (deviceLatency >= 0 && deviceLatency < 100000000) { _devSum += deviceLatency; _devCount++; _devMax = Math.Max(_devMax, deviceLatency); }
            _maxFrameBytes = Math.Max(_maxFrameBytes, data.Length);
            if (!_nativeHeaderChecked)
            {
                _nativeHeaderChecked = true;
                try
                {
                    var header = _nativeSink?.GetSequenceHeader();
                    if (header != null && header.Length > 0)
                    {
                        Log("Encoder sequence header: " + header.Length + " bytes");
                        foreach (var nal in AnnexB.Split(header)) NoteParameterSet(nal);
                    }
                }
                catch (Exception ex) { Log("Sequence header: " + ex.Message); }
            }

            _frameStep = 2;
            long now = _clock.ElapsedMilliseconds;
            if (_nativeLastMs >= 0) _nativeMaxGapMs = Math.Max(_nativeMaxGapMs, now - _nativeLastMs);
            _nativeLastMs = now;
            _nativeFrames++;
            if (now - _nativeStatMs >= 10000) // periodic performance line in the log
            {
                string dev = _devCount > 0
                    ? string.Format("sensor->encoded avg {0} ms / max {1} ms", _devSum / _devCount / 10000, _devMax / 10000)
                    : "sensor->encoded n/a";
                string q = _latCount > 0
                    ? string.Format(", hand-off queue avg {0:0.0} ms / max {1:0.0} ms", _latSum / (double)_latCount / 10000, _latMax / 10000.0)
                    : "";
                Log(string.Format("Encoder delivery (last {0:0.0}s): {1} samples, longest pause {2} ms, biggest frame {3} KB, {4}{5}{6}",
                    (now - _nativeStatMs) / 1000.0, _nativeFrames, _nativeMaxGapMs, _maxFrameBytes / 1024, dev, q,
                    AudioStreaming ? ", audio frames " + _audioFrames : ""));
                _audioFrames = 0;
                LastLatencyMs = _devCount > 0 ? _devSum / _devCount / 10000 : -1;
                _nativeStatMs = now; _nativeFrames = 0; _nativeMaxGapMs = 0;
                _latSum = _latCount = _latMax = _devSum = _devCount = _devMax = _maxFrameBytes = 0;
            }

            _frameStep = 3;
            LastSampleTime = sampleTime;
            // One sample from the encoder = one frame. Pass it on whole, with the encoder's own timestamp.
            var au = new List<byte[]>(4);
            bool key = false;
            foreach (var nal in AnnexB.Split(data))
            {
                if (nal.Length == 0) continue;
                int type = nal[0] & 0x1F;
                _frameStep = 4;
                CountNal(nal, type);
                _frameStep = 5;
                if (type == 9) continue; // access unit delimiter: not needed over RTP
                if (type == 7 || type == 8) NoteParameterSet(nal);
                if (type == 5) key = true;
                au.Add(nal);
            }
            _frameStep = 6;
            if (au.Count > 0) AccessUnitReady?.Invoke(au, sampleTime, key);
        }

        // ---- MP4 (fallback) path: NAL units arrive one by one with no timestamps, so group them into frames here.
        private readonly List<byte[]> _mp4Pending = new List<byte[]>();
        private long _mp4PendingTime;
        private bool _mp4PendingHasSlice, _mp4PendingKey;

        private void OnMp4Nal(byte[] nal, int type)
        {
            CountNal(nal, type);
            bool firstSlice = (type == 1 || type == 5) && nal.Length > 1 && (nal[1] & 0x80) != 0;
            bool startsNewFrame = firstSlice || type == 6 || type == 7 || type == 8 || type == 9;
            if (startsNewFrame && _mp4PendingHasSlice) FlushMp4Frame();
            if (type == 9) return;
            if (type == 7 || type == 8) NoteParameterSet(nal);
            if (_mp4Pending.Count == 0) _mp4PendingTime = _clock.ElapsedTicks * 10000000 / Stopwatch.Frequency;
            if (type == 1 || type == 5) _mp4PendingHasSlice = true;
            if (type == 5) _mp4PendingKey = true;
            _mp4Pending.Add(nal);
        }

        private void FlushMp4Frame()
        {
            var au = _mp4Pending.ToArray();
            bool key = _mp4PendingKey;
            long t = _mp4PendingTime;
            _mp4Pending.Clear();
            _mp4PendingHasSlice = _mp4PendingKey = false;
            AccessUnitReady?.Invoke(au, t, key);
        }

        private byte[] _lastSps, _lastPps;

        /// <summary>Remembers SPS/PPS seen in the stream and tells listeners when they change.</summary>
        private void NoteParameterSet(byte[] nal)
        {
            int type = nal[0] & 0x1F;
            bool changed = false;
            if (type == 7 && (_lastSps == null || !_lastSps.SequenceEqual(nal))) { _lastSps = nal; changed = true; }
            if (type == 8 && (_lastPps == null || !_lastPps.SequenceEqual(nal))) { _lastPps = nal; changed = true; }
            if (changed && _lastSps != null && _lastPps != null) ParameterSetsReady?.Invoke(_lastSps, _lastPps);
        }

        /// <summary>Seconds since the low-latency engine last delivered a frame (counts from stream start).</summary>
        public double SecondsSinceLastFrame =>
            (_clock.ElapsedMilliseconds - (_nativeLastMs >= 0 ? _nativeLastMs : _nativeStartMs)) / 1000.0;
        private long _nativeStartMs;

        /// <summary>Average capture-to-encoded latency over the last 5 s window (ms), or -1.</summary>
        public long LastLatencyMs { get; private set; } = -1;

        /// <summary>Encoder timestamp of the latest sample (100 ns units); used later for RTSP.</summary>
        public long LastSampleTime { get; private set; }

        public async Task StopStreamingAsync()
        {
            if (!IsStreaming) return;
            IsStreaming = false;
            if (_extractor != null) _extractor.Stopped = true; // ignore the index the recorder writes at the end
            try { await Capture.StopRecordAsync(); } catch (Exception ex) { Log("Stop: " + ex.Message); }
            _tap?.Dispose();
            _tap = null;
            _extractor = null;
            DisposeNativeSink();
            Fps = Mbps = 0;
            AudioStreaming = false;
            Log("Streaming stopped");
        }

        /// <summary>Statistics only (fps, Mbps, keyframe interval).</summary>
        private void CountNal(byte[] nal, int type)
        {
            System.Threading.Interlocked.Add(ref _bytes, nal.Length + 4);
            bool firstSlice = (type == 1 || type == 5) && nal.Length > 1 && (nal[1] & 0x80) != 0;
            if (firstSlice)
            {
                System.Threading.Interlocked.Increment(ref _frames);
                if (type == 5)
                {
                    double now = _clock.Elapsed.TotalSeconds;
                    if (_lastIdrSec >= 0) KeyframeIntervalSec = now - _lastIdrSec;
                    _lastIdrSec = now;
                }
            }
        }

        /// <summary>Call about once a second to refresh Fps / Mbps.</summary>
        public void UpdateStats()
        {
            long now = _clock.ElapsedTicks;
            double sec = (now - _lastStatTicks) / (double)Stopwatch.Frequency;
            if (sec <= 0) return;
            long f = System.Threading.Interlocked.Read(ref _frames), b = System.Threading.Interlocked.Read(ref _bytes);
            Fps = (f - _lastFrames) / sec;
            Mbps = (b - _lastBytes) * 8 / sec / 1e6;
            _lastFrames = f; _lastBytes = b; _lastStatTicks = now;
        }

        // ------------------------------------------------------------------ snapshots (optional)

        private readonly System.Threading.SemaphoreSlim _photoLock = new System.Threading.SemaphoreSlim(1);
        private bool _photoFormatChosen;

        /// <summary>Whether this camera can take a photo while it is recording video.</summary>
        public bool PhotoWhileRecordingSupported
        {
            get { try { return Capture != null && Capture.MediaCaptureSettings.ConcurrentRecordAndPhotoSupported; } catch { return false; } }
        }

        /// <summary>
        /// Takes a JPEG still while the video keeps recording, at most maxWidth pixels wide.
        /// Returns null if the camera can't do it right now.
        /// </summary>
        public async Task<byte[]> CaptureJpegAsync(uint maxWidth)
        {
            var cap = Capture;
            if (cap == null || !PhotoWhileRecordingSupported) return null;
            await _photoLock.WaitAsync();
            try
            {
                if (!_photoFormatChosen)
                {
                    // Full-resolution photos (up to 20 MP) are slow; pick the smallest photo size at least maxWidth wide.
                    _photoFormatChosen = true;
                    try
                    {
                        var small = cap.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.Photo)
                            .OfType<ImageEncodingProperties>()
                            .Where(p => p.Width >= maxWidth)
                            .OrderBy(p => (long)p.Width * p.Height)
                            .FirstOrDefault();
                        if (small != null)
                        {
                            await cap.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.Photo, small);
                            Log("Snapshot photo size: " + small.Width + " x " + small.Height);
                        }
                    }
                    catch (Exception ex) { Log("Snapshot photo size: " + ex.Message); }
                }

                using (var photo = new InMemoryRandomAccessStream())
                {
                    var sw = Stopwatch.StartNew();
                    await cap.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), photo);
                    long took = sw.ElapsedMilliseconds;
                    photo.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(photo);
                    if (decoder.PixelWidth <= maxWidth)
                    {
                        photo.Seek(0);
                        return await ReadAllAsync(photo);
                    }
                    // Too big: scale down and re-encode.
                    using (var scaled = new InMemoryRandomAccessStream())
                    {
                        var encoder = await BitmapEncoder.CreateForTranscodingAsync(scaled, decoder);
                        encoder.BitmapTransform.ScaledWidth = maxWidth;
                        encoder.BitmapTransform.ScaledHeight = (uint)(decoder.PixelHeight * (double)maxWidth / decoder.PixelWidth);
                        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                        await encoder.FlushAsync();
                        scaled.Seek(0);
                        if (took > 1000) Log("Snapshot took " + took + " ms");
                        return await ReadAllAsync(scaled);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Snapshot failed: " + ex.Message);
                return null;
            }
            finally
            {
                _photoLock.Release();
            }
        }

        private static async Task<byte[]> ReadAllAsync(IRandomAccessStream s)
        {
            var bytes = new byte[s.Size - s.Position];
            using (var reader = new DataReader(s.GetInputStreamAt(s.Position)))
            {
                await reader.LoadAsync((uint)bytes.Length);
                reader.ReadBytes(bytes);
            }
            return bytes;
        }

        public async Task ShutdownAsync()
        {
            await StopStreamingAsync();
            if (Capture != null)
            {
                try { await Capture.StopPreviewAsync(); } catch { }
                Capture.Dispose();
                Capture = null;
            }
        }
    }
}
