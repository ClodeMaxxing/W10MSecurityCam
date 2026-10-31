using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using W10MSecurityCam.Streaming;
using Windows.Networking.Sockets;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>
    /// RTSP 1.0 server (RFC 2326): one live H.264 track (RTP per RFC 6184) and, when the microphone is on,
    /// one AAC track (RFC 3640). URL: rtsp://PHONE-IP:8554/live (any path is accepted).
    /// The listeners stay up while the camera restarts; viewers simply wait for the next keyframe.
    /// </summary>
    internal sealed class RtspServer
    {
        private readonly int[] _ports;
        private readonly List<StreamSocketListener> _listeners = new List<StreamSocketListener>();
        private readonly List<RtspConnection> _connections = new List<RtspConnection>();
        private readonly object _lock = new object();
        private volatile byte[] _sps, _pps;
        private volatile AudioFormat _audio;
        private long _wallBase = long.MinValue; // UTC ticks minus media time, set by the first frame after a (re)start

        public Action<string> Log = _ => { };
        /// <summary>Plain HTTP requests on an RTSP port go here (log, /status, /config). Lets the PC use them over USB.</summary>
        public HttpApi Http;
        /// <summary>Login for RTSP (and the web pages). Always on: with no password stored nobody gets in.</summary>
        public readonly DigestAuth Auth = new DigestAuth();
        /// <summary>Most simultaneous connections accepted (NVR record + detect streams + a few viewers).</summary>
        public int MaxConnections = 8;
        /// <summary>Accept RTP over UDP when a client asks for it. Off by default: on Wi-Fi, TCP is more reliable.</summary>
        public volatile bool AllowUdp;
        /// <summary>If a viewer's oldest unsent frame is older than this, it skips to the next keyframe.</summary>
        public double MaxBacklogSeconds = 1.5;

        public RtspServer(params int[] ports) { _ports = ports; }

        public byte[] Sps => _sps;
        public byte[] Pps => _pps;
        /// <summary>The audio track's format, or null when the stream has no audio.</summary>
        public AudioFormat Audio => _audio;

        public void SetAudioFormat(AudioFormat format)
        {
            var old = _audio;
            _audio = format;
            if (format != null && (old == null || old.SampleRate != format.SampleRate || old.Channels != format.Channels))
                Log("Audio track: AAC " + format.SampleRate + " Hz, " + format.Channels + " channel(s)");
        }

        /// <summary>
        /// Wall-clock time of a frame, from its media time. Video and audio share the same media clock, so both
        /// tracks' RTCP reports use the same mapping and viewers can line them up (lip sync).
        /// </summary>
        public DateTime MediaTimeToUtc(long time100ns)
        {
            long b = System.Threading.Interlocked.Read(ref _wallBase);
            if (b == long.MinValue) return DateTime.UtcNow;
            return new DateTime(b + time100ns, DateTimeKind.Utc);
        }

        private void NoteMediaTime(long time100ns)
        {
            if (System.Threading.Interlocked.Read(ref _wallBase) == long.MinValue)
                System.Threading.Interlocked.CompareExchange(ref _wallBase, DateTime.UtcNow.Ticks - time100ns, long.MinValue);
        }

        /// <summary>Number of viewers currently receiving video.</summary>
        public int ClientCount { get { lock (_lock) return _connections.Count(c => c.IsPlaying); } }

        public async Task StartAsync()
        {
            if (_listeners.Count > 0) return;
            foreach (int port in _ports)
            {
                var l = new StreamSocketListener();
                l.Control.NoDelay = true;
                l.ConnectionReceived += OnConnection;
                await l.BindServiceNameAsync(port.ToString());
                _listeners.Add(l);
                Log("RTSP listening on TCP port " + port);
            }
        }

        public void Stop()
        {
            List<RtspConnection> all;
            lock (_lock) { all = _connections.ToList(); _connections.Clear(); }
            foreach (var c in all) c.Close();
            foreach (var l in _listeners) l.Dispose();
            _listeners.Clear();
        }

        private void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            lock (_lock)
            {
                if (_connections.Count >= MaxConnections)
                {
                    Log("RTSP: too many connections (" + MaxConnections + ") - refusing " + (args.Socket.Information.RemoteAddress?.DisplayName ?? "?"));
                    try { args.Socket.Dispose(); } catch { }
                    return;
                }
            }
            var c = new RtspConnection(this, args.Socket);
            lock (_lock) _connections.Add(c);
            var _ = c.RunAsync(() => { lock (_lock) _connections.Remove(c); });
        }

        public void SetParameterSets(byte[] sps, byte[] pps)
        {
            if (sps == null || pps == null) return;
            // The encoder's own SPS/PPS can differ slightly from the remembered probe copy; the newest always wins.
            _sps = sps;
            _pps = pps;
        }

        /// <summary>Call before the camera (re)starts: every viewer waits for a keyframe and timestamps are re-based.</summary>
        public void ResetStream()
        {
            System.Threading.Interlocked.Exchange(ref _wallBase, long.MinValue);
            lock (_lock) foreach (var c in _connections) c.OnStreamRestart();
        }

        /// <summary>Waits briefly for the decoder setup (needed for DESCRIBE) if the camera hasn't produced it yet.</summary>
        internal async Task<bool> WaitForParameterSetsAsync(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_sps == null || _pps == null)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(100).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>Called by the camera for every encoded frame.</summary>
        public void SendAccessUnit(IList<byte[]> nals, long time100ns, bool keyframe)
        {
            NoteMediaTime(time100ns);
            RtspConnection[] playing;
            lock (_lock)
            {
                if (_connections.Count == 0) return;
                playing = _connections.Where(c => c.IsPlaying).ToArray();
            }
            if (playing.Length == 0) return;

            var payloads = RtpPacketizer.Packetize(nals, _sps, _pps, keyframe);
            foreach (var c in playing) c.SendFrame(payloads, time100ns, keyframe);
        }

        /// <summary>Called by the camera for every AAC frame.</summary>
        public void SendAudio(byte[] frame, long time100ns)
        {
            NoteMediaTime(time100ns);
            RtspConnection[] playing;
            lock (_lock)
            {
                if (_connections.Count == 0) return;
                playing = _connections.Where(c => c.IsPlaying).ToArray();
            }
            if (playing.Length == 0) return;
            var payload = RtpPacketizer.AacPayload(frame);
            foreach (var c in playing) c.SendAudio(payload, time100ns);
        }
    }
}
