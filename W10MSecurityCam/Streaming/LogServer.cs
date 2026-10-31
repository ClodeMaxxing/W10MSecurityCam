using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace W10MSecurityCam.Streaming
{
    /// <summary>Tiny HTTP server on http://PHONE-IP:5001 - the log, /status, /snapshot.jpg (see HttpApi).</summary>
    internal sealed class LogServer
    {
        private const int MaxConnections = 16;      // more at once get a quick 503
        private const int MaxBodyBytes = 64 * 1024;
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);  // the whole request
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30); // the whole reply

        private readonly HttpApi _api;
        private StreamSocketListener _listener;
        private int _active;
        private DateTime _lastErrorLog = DateTime.MinValue;
        private int _quietErrors;
        private readonly object _logLock = new object();

        public Action<string> Log = _ => { };

        public LogServer(HttpApi api) { _api = api; }

        public async Task StartAsync(int port)
        {
            if (_listener != null) return;
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnection;
            await _listener.BindServiceNameAsync(port.ToString());
        }

        public void Stop()
        {
            _listener?.Dispose();
            _listener = null;
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            int active = Interlocked.Increment(ref _active);
            try
            {
                using (var socket = args.Socket)
                {
                    if (active > MaxConnections)
                    {
                        try
                        {
                            using (var busy = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                                await socket.OutputStream.WriteAsync(HttpApi.Reply(503, "Service Unavailable", "text/plain", "Busy, try again\n").AsBuffer()).AsTask(busy.Token);
                        }
                        catch { }
                        return;
                    }
                    var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                    var data = new List<byte>();
                    int headerEnd = -1, bodyLen = 0;
                    using (var cts = new CancellationTokenSource(ReadTimeout))
                    {
                        while (true)
                        {
                            uint got;
                            try { got = await reader.LoadAsync(4096).AsTask(cts.Token); } // a slow client is cut off after 10 s
                            catch (OperationCanceledException)
                            {
                                if (data.Count == 0) return; // an unused connection (browsers open spare ones): not worth a log line
                                throw new TimeoutException("request not complete within " + (int)ReadTimeout.TotalSeconds + " s");
                            }
                            if (got == 0) break;
                            var chunk = new byte[got];
                            reader.ReadBytes(chunk);
                            data.AddRange(chunk);
                            if (headerEnd < 0)
                            {
                                string s = Encoding.ASCII.GetString(data.ToArray());
                                headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (headerEnd >= 0)
                                {
                                    var m = System.Text.RegularExpressions.Regex.Match(s.Substring(0, headerEnd), @"(?im)^Content-Length:\s*(\d+)");
                                    if (m.Success && (!int.TryParse(m.Groups[1].Value, out bodyLen) || bodyLen > MaxBodyBytes)) return;
                                }
                            }
                            if (headerEnd >= 0 && data.Count >= headerEnd + 4 + bodyLen) break;
                            if (data.Count > 256 * 1024) break;
                        }
                    }
                    reader.DetachStream();
                    if (headerEnd < 0) return;

                    var bytes = data.ToArray();
                    string head = Encoding.UTF8.GetString(bytes, 0, headerEnd);
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    string body = Encoding.UTF8.GetString(bytes, headerEnd + 4, Math.Min(bodyLen, bytes.Length - headerEnd - 4));
                    bool fromUsb = socket.Information.RemoteAddress?.DisplayName == "127.0.0.1";
                    byte[] reply = await _api.HandleAsync(HttpApi.Parse(lines[0], headers, body, fromUsb, socket.Information.RemoteAddress?.DisplayName));
                    using (var wcts = new CancellationTokenSource(WriteTimeout))
                    {
                        await socket.OutputStream.WriteAsync(reply.AsBuffer()).AsTask(wcts.Token);
                        await socket.OutputStream.FlushAsync().AsTask(wcts.Token);
                    }
                }
            }
            catch (Exception ex) { LogError(ex); }
            finally { Interlocked.Decrement(ref _active); }
        }

        /// <summary>At most one line per 30 s, so a client that keeps failing cannot flood the log.</summary>
        private void LogError(Exception ex)
        {
            string line = null;
            lock (_logLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastErrorLog).TotalSeconds < 30) { _quietErrors++; return; }
                line = "Web port: request failed (" + (ex is OperationCanceledException ? "timed out" : ex.Message) + ")" +
                       (_quietErrors > 0 ? " - " + _quietErrors + " more since the last message" : "");
                _lastErrorLog = now;
                _quietErrors = 0;
            }
            Log(line);
        }
    }
}
