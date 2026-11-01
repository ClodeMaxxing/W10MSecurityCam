using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Buffer = System.Buffer;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>
    /// One RTSP client on one TCP connection. Reads requests (and skips the RTCP the client sends back),
    /// answers them, and once PLAYing sends video (track 0) and AAC audio (track 1, when the stream has it)
    /// as RTP, interleaved on the same connection or over UDP.
    /// All writes go through one queue so responses and video never interleave mid-packet.
    /// </summary>
    internal sealed class RtspConnection
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private const int SessionTimeoutSeconds = 60;
        private const int RequestTimeoutSeconds = 30; // before PLAY: a complete request at least this often

        private readonly RtspServer _server;
        private readonly StreamSocket _socket;
        private readonly string _who;
        private readonly string _ip;
        private readonly Random _rand = new Random(Guid.NewGuid().GetHashCode());

        // RTSP session state
        private string _sessionId;
        private volatile bool _playing;
        private bool _everPlayed;

        /// <summary>Per-track RTP state. Fields are touched by the camera thread while playing; guarded by _rtpLock.</summary>
        private sealed class Track
        {
            public int Index;             // 0 = video, 1 = audio
            public byte PayloadType;      // 96 video, 97 audio
            public uint ClockRate;        // 90000 video, sample rate for audio
            public uint FrameStep;        // timestamp step used to continue after a clock jump
            public volatile bool SetUp;
            public bool Udp;
            public byte Channel;          // interleaved RTP channel; RTCP is Channel + 1
            public DatagramSocket UdpRtp, UdpRtcp;
            public IOutputStream UdpRtpOut, UdpRtcpOut;
            public int UdpErrors;
            public uint Ssrc;
            public ushort Seq;
            public uint RtpStart;
            public bool HasAnchor, ForceRebase;
            public long AnchorTime, LastTime;
            public uint AnchorRtp, LastRtp;
            public uint PacketCount, OctetCount;
            public long LastReportTicks;
        }

        private readonly Track[] _tracks = new Track[2];
        private readonly object _rtpLock = new object();
        private volatile bool _waitingForKeyframe = true;

        // outgoing queue
        private readonly LinkedList<Item> _queue = new LinkedList<Item>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private long _queuedMediaBytes;
        private int _queuedReplies;          // RTSP/HTTP replies waiting (not media)
        private const int MaxQueuedReplies = 64; // a client that sends requests but never reads the replies is closed
        private const int MaxBodyBytes = 64 * 1024;
        private long _lastSendTicks = Clock.ElapsedTicks;
        private long _lastActivityTicks = Clock.ElapsedTicks;
        private volatile bool _closed, _closeWhenDrained;

        private sealed class Item
        {
            public byte[] Data;               // TCP bytes, or one RTCP datagram when ToRtcp
            public List<byte[]> Datagrams;    // RTP over UDP: one datagram per packet
            public Track UdpTrack;            // which track's UDP sockets to use
            public bool ToRtcp;
            public bool IsMedia;
            public long Size;
            public long QueuedAt;
        }

        public RtspConnection(RtspServer server, StreamSocket socket)
        {
            _server = server;
            _socket = socket;
            try { _socket.Control.NoDelay = true; } catch { }
            string ip = socket.Information.RemoteAddress?.DisplayName ?? "?";
            _ip = ip;
            _who = ip == "127.0.0.1" ? "PC over USB" : ip;
            for (int i = 0; i < 2; i++)
            {
                _tracks[i] = new Track
                {
                    Index = i,
                    PayloadType = (byte)(i == 0 ? 96 : 97),
                    ClockRate = 90000,
                    FrameStep = 3000,
                    Ssrc = (uint)_rand.Next() ^ ((uint)_rand.Next() << 1),
                    Seq = (ushort)_rand.Next(0, 65536),
                    RtpStart = (uint)_rand.Next() ^ ((uint)_rand.Next() << 1)
                };
            }
        }

        public bool IsPlaying => _playing && !_closed;

        private void Log(string m) => _server.Log("RTSP " + _who + ": " + m);

        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        // ------------------------------------------------------------------ lifetime

        public async Task RunAsync(Action onClosed)
        {
            var writer = WriteLoopAsync();
            try
            {
                await ReadLoopAsync();
            }
            catch (Exception ex)
            {
                if (!_closed) Log("connection error: " + ex.Message);
            }
            if (!_closeWhenDrained) Close(); // TEARDOWN / HTTP: let the writer finish sending first
            try { await writer; } catch { }
            onClosed();
            if (_announced) Log(_everPlayed ? "left" : "disconnected");
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _playing = false;
            try { _socket.Dispose(); } catch { } // also aborts a write that is stuck on a dead connection
            foreach (var t in _tracks)
            {
                try { t.UdpRtp?.Dispose(); } catch { }
                try { t.UdpRtcp?.Dispose(); } catch { }
            }
            _signal.Release();
        }

        public void OnStreamRestart()
        {
            lock (_rtpLock) foreach (var t in _tracks) t.ForceRebase = true;
            _waitingForKeyframe = true;
            ClearMedia();
        }

        // ------------------------------------------------------------------ reading

        private byte[] _in = new byte[16384];
        private int _inLen;
        private long _lastRequestTicks = Clock.ElapsedTicks; // when the last complete request arrived

        private async Task ReadLoopAsync()
        {
            var reader = new DataReader(_socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            try
            {
                while (!_closed)
                {
                    uint got;
                    if (_everPlayed)
                        got = await reader.LoadAsync(8192); // playing: the keep-alive check (CheckHealth) takes over
                    else
                    {
                        // Not playing yet: a client that sends nothing (or half a request) must not hold a slot for long.
                        double left = RequestTimeoutSeconds - Seconds(Clock.ElapsedTicks - _lastRequestTicks);
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.1, left))))
                        {
                            try { got = await reader.LoadAsync(8192).AsTask(cts.Token); }
                            catch (OperationCanceledException)
                            {
                                Log("no complete request for " + RequestTimeoutSeconds + " s - closing");
                                return;
                            }
                        }
                    }
                    if (got == 0) return; // client closed the connection
                    if (_inLen + (int)got > _in.Length)
                    {
                        if (_inLen + got > 1024 * 1024) { Log("request too large - closing"); return; }
                        Array.Resize(ref _in, Math.Max(_in.Length * 2, _inLen + (int)got));
                    }
                    var chunk = new byte[got];
                    reader.ReadBytes(chunk);
                    Buffer.BlockCopy(chunk, 0, _in, _inLen, (int)got);
                    _inLen += (int)got;
                    Interlocked.Exchange(ref _lastActivityTicks, Clock.ElapsedTicks);

                    if (!await ProcessInputAsync()) return;
                }
            }
            finally
            {
                try { reader.DetachStream(); } catch { } // don't let the reader close the socket under the writer
            }
        }

        /// <summary>Handles every complete message in the input buffer. Returns false to end the connection.</summary>
        private async Task<bool> ProcessInputAsync()
        {
            while (_inLen > 0 && !_closed)
            {
                if (_in[0] == (byte)'\r' || _in[0] == (byte)'\n') { Consume(1); continue; }

                if (_in[0] == (byte)'$')
                {
                    // Interleaved packet from the client (RTCP receiver report): it only proves the client is alive.
                    if (_inLen < 4) return true;
                    int len = _in[2] << 8 | _in[3];
                    if (_inLen < 4 + len) return true;
                    Consume(4 + len);
                    continue;
                }

                int headerEnd = IndexOfBlankLine();
                if (headerEnd < 0) return _inLen < 64 * 1024; // wait for the rest of the request
                string head = Encoding.UTF8.GetString(_in, 0, headerEnd);
                var lines = head.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                int bodyLen = 0;
                if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out bodyLen) || bodyLen < 0 || bodyLen > MaxBodyBytes))
                {
                    // A negative length made Consume() keep the same request and handle it again forever.
                    Log("bad Content-Length - closing");
                    return false;
                }
                int blank = _in[headerEnd] == (byte)'\r' ? 4 : 2;
                if (_inLen < headerEnd + blank + bodyLen) return true; // wait for the body
                string body = bodyLen > 0 ? Encoding.UTF8.GetString(_in, headerEnd + blank, bodyLen) : "";
                Consume(headerEnd + blank + bodyLen);
                _lastRequestTicks = Clock.ElapsedTicks;

                var parts = lines[0].Split(' ');
                string method = parts[0].ToUpperInvariant();
                string url = parts.Length > 1 ? parts[1] : "*";

                if (parts.Length > 2 && parts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    await SendHttpAsync(lines[0], headers, body);
                    return false;
                }
                if (!await HandleRequestAsync(method, url, headers)) return false;
            }
            return true;
        }

        private int IndexOfBlankLine()
        {
            for (int i = 0; i + 1 < _inLen; i++)
            {
                if (_in[i] == (byte)'\n' && _in[i + 1] == (byte)'\n') return i;
                if (i + 3 < _inLen && _in[i] == (byte)'\r' && _in[i + 1] == (byte)'\n' && _in[i + 2] == (byte)'\r' && _in[i + 3] == (byte)'\n') return i;
            }
            return -1;
        }

        private void Consume(int n)
        {
            Buffer.BlockCopy(_in, n, _in, 0, _inLen - n);
            _inLen -= n;
        }

        // ------------------------------------------------------------------ requests

        private bool _announced;

        private async Task<bool> HandleRequestAsync(string method, string url, Dictionary<string, string> h)
        {
            if (!_announced) { _announced = true; Log("connected"); }
            string cseq = h.TryGetValue("CSeq", out var c) ? c : "0";

            if (method != "OPTIONS")
            {
                h.TryGetValue("Authorization", out var authorization);
                bool locked = _server.Auth.IsLockedOut(_ip); // too many wrong logins: 401 without looking
                bool stale = false;
                if (locked || !_server.Auth.Check(method, url, authorization, _ip, out stale, rtsp: true))
                {
                    if (!string.IsNullOrEmpty(authorization) && !locked && !stale) Log("wrong user name or password");
                    Respond(cseq, 401, "Unauthorized", _server.Auth.Challenge(stale), "", quiet: true);
                    return true;
                }
            }

            switch (method)
            {
                case "OPTIONS":
                    Respond(cseq, 200, "OK", "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, GET_PARAMETER, SET_PARAMETER, TEARDOWN\r\n");
                    return true;

                case "DESCRIBE":
                {
                    if (!await _server.WaitForParameterSetsAsync(5000))
                    {
                        Log("DESCRIBE before the camera produced video - asked to retry");
                        Respond(cseq, 503, "Service Unavailable", "Retry-After: 5\r\n");
                        return true;
                    }
                    string ip = _socket.Information.LocalAddress?.CanonicalName ?? "0.0.0.0";
                    string sdp = Sdp.Build(ip, (ulong)Clock.ElapsedTicks, _server.Sps, _server.Pps, _server.Audio);
                    string baseUrl = url.EndsWith("/") ? url : url + "/";
                    Respond(cseq, 200, "OK", "Content-Base: " + baseUrl + "\r\nContent-Type: application/sdp\r\n", sdp);
                    return true;
                }

                case "SETUP":
                {
                    bool wantAudio = url.IndexOf(Sdp.AudioTrackControl, StringComparison.OrdinalIgnoreCase) >= 0;
                    var audioFormat = _server.Audio;
                    if (wantAudio && audioFormat == null) { Respond(cseq, 404, "Stream Not Found"); return true; }
                    var track = _tracks[wantAudio ? 1 : 0];
                    if (wantAudio)
                    {
                        track.ClockRate = (uint)audioFormat.SampleRate;
                        track.FrameStep = 1024; // samples per AAC frame
                    }
                    h.TryGetValue("Transport", out var transport);
                    var pick = PickTransport(transport ?? "", _server.AllowUdp);
                    if (pick == null)
                    {
                        // UDP is off: VLC and ffmpeg retry over TCP after this answer.
                        Respond(cseq, 461, "Unsupported Transport");
                        return true;
                    }
                    if (_sessionId == null) _sessionId = _rand.Next(0x10000000, int.MaxValue).ToString("X8") + _rand.Next(0x10000000, int.MaxValue).ToString("X8");
                    string transportReply;
                    if (pick.Udp)
                    {
                        int serverPort = await OpenUdpAsync(track, pick.ClientPort);
                        if (serverPort < 0) { Respond(cseq, 461, "Unsupported Transport"); return true; }
                        track.Udp = true;
                        transportReply = "RTP/AVP;unicast;client_port=" + pick.ClientPort + "-" + (pick.ClientPort + 1) +
                                         ";server_port=" + serverPort + "-" + (serverPort + 1);
                        Log((wantAudio ? "audio" : "video") + " over UDP to port " + pick.ClientPort);
                    }
                    else
                    {
                        track.Udp = false;
                        track.Channel = pick.Channel;
                        transportReply = "RTP/AVP/TCP;unicast;interleaved=" + pick.Channel + "-" + (pick.Channel + 1);
                    }
                    track.SetUp = true;
                    Respond(cseq, 200, "OK",
                        "Transport: " + transportReply + ";ssrc=" + track.Ssrc.ToString("X8") + ";mode=play\r\n" +
                        "Session: " + _sessionId + ";timeout=" + SessionTimeoutSeconds + "\r\n");
                    return true;
                }

                case "PLAY":
                {
                    if (_sessionId == null) { Respond(cseq, 455, "Method Not Valid in This State"); return true; }
                    if (!_tracks[0].SetUp && !_tracks[1].SetUp) _tracks[0].SetUp = true; // PLAY without SETUP: assume video over TCP
                    var infos = new List<string>();
                    string baseUrl = url.TrimEnd('/');
                    int tid = baseUrl.IndexOf("/trackID=", StringComparison.OrdinalIgnoreCase);
                    if (tid >= 0) baseUrl = baseUrl.Substring(0, tid);
                    lock (_rtpLock)
                    {
                        foreach (var t in _tracks)
                        {
                            if (!t.SetUp) continue;
                            uint nextTs = t.HasAnchor ? unchecked(t.LastRtp + t.FrameStep) : t.RtpStart;
                            string control = t.Index == 0 ? Sdp.TrackControl : Sdp.AudioTrackControl;
                            infos.Add("url=" + baseUrl + "/" + control + ";seq=" + t.Seq + ";rtptime=" + nextTs);
                        }
                    }
                    Respond(cseq, 200, "OK", "Range: npt=0.000-\r\nSession: " + _sessionId + "\r\nRTP-Info: " + string.Join(",", infos) + "\r\n");
                    _waitingForKeyframe = true;
                    _playing = true; // after the response is queued, so media never arrives before it
                    if (!_everPlayed) Log("playing " + (_tracks[1].SetUp ? (_tracks[0].SetUp ? "video + audio" : "audio") : "video") + " (video starts at the next keyframe)");
                    _everPlayed = true;
                    return true;
                }

                case "PAUSE":
                    _playing = false;
                    ClearMedia();
                    Respond(cseq, 200, "OK", _sessionId != null ? "Session: " + _sessionId + "\r\n" : "");
                    return true;

                case "GET_PARAMETER": // keep-alive used by VLC and ffmpeg
                case "SET_PARAMETER":
                    Respond(cseq, 200, "OK", _sessionId != null ? "Session: " + _sessionId + "\r\n" : "");
                    return true;

                case "TEARDOWN":
                    _playing = false;
                    ClearMedia();
                    Respond(cseq, 200, "OK", _sessionId != null ? "Session: " + _sessionId + "\r\n" : "");
                    _closeWhenDrained = true;
                    _signal.Release();
                    return false;

                default:
                    Respond(cseq, 501, "Not Implemented");
                    return true;
            }
        }

        private sealed class TransportChoice
        {
            public bool Udp;
            public byte Channel;
            public int ClientPort;
        }

        /// <summary>
        /// Picks the first usable option in a Transport header (clients may list several, comma-separated):
        /// RTP/AVP/TCP (interleaved), or RTP/AVP[/UDP] unicast with client_port when UDP is allowed.
        /// </summary>
        private static TransportChoice PickTransport(string header, bool allowUdp)
        {
            foreach (var option in header.Split(','))
            {
                var fields = option.Split(';');
                string proto = fields[0].Trim().ToUpperInvariant();
                if (proto.StartsWith("RTP/AVP/TCP"))
                {
                    var c = new TransportChoice();
                    foreach (var f in fields)
                    {
                        var kv = f.Trim();
                        if (!kv.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase)) continue;
                        var first = kv.Substring(12).Split('-')[0];
                        if (byte.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) && ch < 255) c.Channel = ch;
                    }
                    return c;
                }
                if (allowUdp && (proto == "RTP/AVP" || proto == "RTP/AVP/UDP"))
                {
                    if (option.IndexOf("multicast", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    foreach (var f in fields)
                    {
                        var kv = f.Trim();
                        if (!kv.StartsWith("client_port=", StringComparison.OrdinalIgnoreCase)) continue;
                        if (int.TryParse(kv.Substring(12).Split('-')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port < 65535)
                            return new TransportChoice { Udp = true, ClientPort = port };
                    }
                }
            }
            return null;
        }

        /// <summary>Binds an even/odd pair of local UDP ports and connects them to the client. Returns the even port, or -1.</summary>
        private async Task<int> OpenUdpAsync(Track track, int clientPort)
        {
            try { track.UdpRtp?.Dispose(); track.UdpRtcp?.Dispose(); } catch { }
            track.UdpRtp = track.UdpRtcp = null;
            var remote = _socket.Information.RemoteAddress;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int port = 40000 + 2 * _rand.Next(0, 5000);
                var rtp = new DatagramSocket();
                var rtcp = new DatagramSocket();
                // Handlers must be attached before binding. RTCP receiver reports count as keep-alive;
                // clients may also send a dummy packet to the RTP port first to open their firewall.
                rtcp.MessageReceived += (s, e) => Interlocked.Exchange(ref _lastActivityTicks, Clock.ElapsedTicks);
                rtp.MessageReceived += (s, e) => { };
                try
                {
                    await rtp.BindServiceNameAsync(port.ToString());
                    await rtcp.BindServiceNameAsync((port + 1).ToString());
                    track.UdpRtpOut = await rtp.GetOutputStreamAsync(remote, clientPort.ToString());
                    track.UdpRtcpOut = await rtcp.GetOutputStreamAsync(remote, (clientPort + 1).ToString());
                    track.UdpRtp = rtp;
                    track.UdpRtcp = rtcp;
                    return port;
                }
                catch
                {
                    rtp.Dispose();
                    rtcp.Dispose();
                }
            }
            Log("could not open UDP ports");
            return -1;
        }

        private void Respond(string cseq, int code, string reason, string extraHeaders = "", string body = "", bool quiet = false)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string head = "RTSP/1.0 " + code + " " + reason + "\r\n" +
                          "CSeq: " + cseq + "\r\n" +
                          "Server: W10MSecurityCam\r\n" +
                          "Date: " + DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture) + "\r\n" +
                          extraHeaders +
                          "Content-Length: " + bodyBytes.Length + "\r\n\r\n";
            byte[] headBytes = Encoding.UTF8.GetBytes(head);
            var all = new byte[headBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, all, headBytes.Length, bodyBytes.Length);
            if (code >= 400 && !quiet) Log(code + " " + reason);
            Enqueue(all, false);
        }

        /// <summary>A plain HTTP request on the RTSP port (e.g. the log over USB): answer it and close.</summary>
        private async Task SendHttpAsync(string requestLine, Dictionary<string, string> headers, string body)
        {
            bool fromUsb = _socket.Information.RemoteAddress?.DisplayName == "127.0.0.1";
            var request = W10MSecurityCam.Streaming.HttpApi.Parse(requestLine, headers, body, fromUsb, _ip);
            byte[] reply = _server.Http != null
                ? await _server.Http.HandleAsync(request)
                : W10MSecurityCam.Streaming.HttpApi.Reply(404, "Not Found", "text/plain", "");
            Enqueue(reply, false);
            _closeWhenDrained = true;
            _signal.Release();
        }

        // ------------------------------------------------------------------ media

        /// <summary>Disconnects viewers that went silent. Returns false if this connection was closed.</summary>
        private bool CheckHealth(long now)
        {
            if (Seconds(now - Interlocked.Read(ref _lastActivityTicks)) > SessionTimeoutSeconds * 1.5)
            {
                Log("no keep-alive from the viewer for " + (int)(SessionTimeoutSeconds * 1.5) + " s - closing");
                Close();
                return false;
            }
            if (Seconds(now - Interlocked.Read(ref _lastSendTicks)) > 6 && Interlocked.Read(ref _queuedMediaBytes) > 0)
            {
                Log("not receiving for 6 s - disconnecting it");
                Close();
                return false;
            }
            return true;
        }

        /// <summary>Called on the camera thread for each video frame while this client is playing.</summary>
        public void SendFrame(List<byte[]> payloads, long time100ns, bool keyframe)
        {
            var track = _tracks[0];
            if (_closed || !_playing || !track.SetUp) return;
            long now = Clock.ElapsedTicks;
            if (!CheckHealth(now)) return;
            if (!_waitingForKeyframe && OldestMediaAgeSeconds(now) > _server.MaxBacklogSeconds)
            {
                ClearMedia();
                _waitingForKeyframe = true;
                Log("fell behind - skipping to the next keyframe");
            }
            if (_waitingForKeyframe)
            {
                if (!keyframe) return;
                _waitingForKeyframe = false;
            }
            SendPackets(track, payloads, time100ns, now);
        }

        /// <summary>Called for each AAC frame while this client is playing (if it set up the audio track).</summary>
        public void SendAudio(List<byte[]> payloads, long time100ns)
        {
            var track = _tracks[1];
            if (_closed || !_playing || !track.SetUp) return;
            long now = Clock.ElapsedTicks;
            if (_tracks[0].SetUp)
            {
                if (_waitingForKeyframe) return; // start audio together with the picture
            }
            else if (!CheckHealth(now)) return;
            SendPackets(track, payloads, time100ns, now); // one AAC frame, the list shared by every viewer (read only)
        }

        private void SendPackets(Track track, List<byte[]> payloads, long time100ns, long now)
        {
            byte[] data = null, report = null;
            List<byte[]> datagrams = null;
            lock (_rtpLock)
            {
                uint ts = Timestamp(track, time100ns);
                if (track.Udp) datagrams = RtpPacketizer.BuildPackets(payloads, track.Seq, ts, track.Ssrc, track.PayloadType);
                else data = RtpPacketizer.BuildInterleaved(payloads, track.Channel, track.Seq, ts, track.Ssrc, track.PayloadType);
                track.Seq = unchecked((ushort)(track.Seq + payloads.Count));
                track.PacketCount += (uint)payloads.Count;
                foreach (var p in payloads) track.OctetCount += (uint)p.Length;
                // RTCP sender report every 5 s: ties RTP time to wall-clock time, so NVRs can timestamp recordings
                // and line audio up with video.
                if (track.LastReportTicks == 0 || Seconds(now - track.LastReportTicks) >= 5)
                {
                    track.LastReportTicks = now;
                    report = SenderReport(track, ts, _server.MediaTimeToUtc(time100ns));
                }
            }
            if (track.Udp)
            {
                if (report != null) EnqueueItem(new Item { Data = report, ToRtcp = true, UdpTrack = track });
                long size = 0;
                foreach (var d in datagrams) size += d.Length;
                EnqueueItem(new Item { Datagrams = datagrams, UdpTrack = track, IsMedia = true, Size = size });
            }
            else
            {
                if (report != null) Enqueue(Interleave(report, (byte)(track.Channel + 1)), false);
                Enqueue(data, true);
            }
        }

        private static byte[] Interleave(byte[] packet, byte channel)
        {
            var b = new byte[4 + packet.Length];
            b[0] = (byte)'$'; b[1] = channel; b[2] = (byte)(packet.Length >> 8); b[3] = (byte)packet.Length;
            Buffer.BlockCopy(packet, 0, b, 4, packet.Length);
            return b;
        }

        private static readonly DateTime NtpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>RTCP SR (RFC 3550 6.4.1), 28 bytes. 'when' is the wall-clock time of the frame with 'rtpTimestamp'.</summary>
        private static byte[] SenderReport(Track track, uint rtpTimestamp, DateTime when)
        {
            var since = when - NtpEpoch;
            ulong seconds = (ulong)since.Ticks / 10000000UL;
            ulong fraction = ((ulong)since.Ticks % 10000000UL) * 4294967296UL / 10000000UL;
            var b = new byte[28];
            b[0] = 0x80;      // V=2, no reception report blocks
            b[1] = 200;       // PT = SR
            b[2] = 0; b[3] = 6; // length in 32-bit words minus one
            Put32(b, 4, track.Ssrc);
            Put32(b, 8, (uint)seconds);
            Put32(b, 12, (uint)fraction);
            Put32(b, 16, rtpTimestamp);
            Put32(b, 20, track.PacketCount);
            Put32(b, 24, track.OctetCount);
            return b;
        }

        private static void Put32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        /// <summary>
        /// RTP timestamp (track clock rate) from the encoder time. If the encoder clock restarts or jumps (camera restart),
        /// continue one frame after the last timestamp so the viewer sees a continuous stream.
        /// </summary>
        private static uint Timestamp(Track t, long time)
        {
            if (!t.HasAnchor || t.ForceRebase || time <= t.LastTime || time - t.LastTime > 100000000)
            {
                t.AnchorRtp = t.HasAnchor ? unchecked(t.LastRtp + t.FrameStep) : t.RtpStart;
                t.AnchorTime = time;
                t.HasAnchor = true;
                t.ForceRebase = false;
            }
            t.LastTime = time;
            // Round to the nearest tick: frame times arrive rounded to 100 ns.
            uint fromClock = unchecked((uint)(t.AnchorRtp + (ulong)(((time - t.AnchorTime) * t.ClockRate + 5000000L) / 10000000L)));
            if (t.Index == 1 && time != t.AnchorTime) // not the first frame after (re)anchoring
            {
                // Audio: every AAC frame is exactly 1024 samples, but the microphone's timestamps jitter a little.
                // Count samples, and only follow the clock if the two drift apart by more than one frame.
                uint counted = unchecked(t.LastRtp + t.FrameStep);
                int drift = unchecked((int)(fromClock - counted));
                t.LastRtp = Math.Abs(drift) <= (int)t.FrameStep ? counted : fromClock;
            }
            else
            {
                t.LastRtp = fromClock;
            }
            return t.LastRtp;
        }

        // ------------------------------------------------------------------ writing

        private void Enqueue(byte[] data, bool isMedia) => EnqueueItem(new Item { Data = data, IsMedia = isMedia, Size = data.Length });

        private void EnqueueItem(Item item)
        {
            if (_closed) return;
            item.QueuedAt = Clock.ElapsedTicks;
            bool tooMany = false;
            lock (_queue)
            {
                if (!item.IsMedia && _queuedReplies >= MaxQueuedReplies) tooMany = true;
                else
                {
                    _queue.AddLast(item);
                    if (item.IsMedia) _queuedMediaBytes += item.Size;
                    else _queuedReplies++;
                }
            }
            if (tooMany) { Log("too many unread replies - closing"); Close(); return; }
            _signal.Release();
        }

        private void ClearMedia()
        {
            lock (_queue)
            {
                var node = _queue.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.IsMedia) _queue.Remove(node);
                    node = next;
                }
                _queuedMediaBytes = 0;
            }
        }

        private double OldestMediaAgeSeconds(long now)
        {
            lock (_queue)
            {
                foreach (var i in _queue)
                    if (i.IsMedia) return Seconds(now - i.QueuedAt);
            }
            return 0;
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                var output = _socket.OutputStream;
                while (!_closed)
                {
                    await _signal.WaitAsync().ConfigureAwait(false);
                    Item item = null;
                    lock (_queue)
                    {
                        if (_queue.Count > 0)
                        {
                            item = _queue.First.Value;
                            _queue.RemoveFirst();
                            if (item.IsMedia) _queuedMediaBytes -= item.Size;
                            else _queuedReplies--;
                        }
                    }
                    if (item != null)
                    {
                        if (item.Datagrams != null)
                        {
                            try
                            {
                                foreach (var d in item.Datagrams) await item.UdpTrack.UdpRtpOut.WriteAsync(d.AsBuffer());
                                item.UdpTrack.UdpErrors = 0;
                            }
                            catch (Exception ex)
                            {
                                // UDP has no connection: a lost viewer shows up as send errors. Give up after many in a row.
                                if (item.UdpTrack.UdpErrors++ == 0) Log("UDP send failed: " + ex.Message);
                                if (item.UdpTrack.UdpErrors > 300) { Log("UDP viewer unreachable - closing"); break; }
                            }
                        }
                        else if (item.ToRtcp)
                        {
                            try { await item.UdpTrack.UdpRtcpOut.WriteAsync(item.Data.AsBuffer()); } catch { } // RTCP is best effort
                        }
                        else
                        {
                            await output.WriteAsync(item.Data.AsBuffer());
                        }
                        Interlocked.Exchange(ref _lastSendTicks, Clock.ElapsedTicks);
                    }
                    bool empty;
                    lock (_queue) empty = _queue.Count == 0;
                    if (_closeWhenDrained && empty)
                    {
                        await output.FlushAsync();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_closed) Log("send failed: " + ex.Message);
            }
            finally
            {
                Close();
            }
        }
    }
}
