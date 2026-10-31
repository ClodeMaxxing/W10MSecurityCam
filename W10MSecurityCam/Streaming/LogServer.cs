using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace W10MSecurityCam.Streaming
{
    /// <summary>Tiny HTTP server on http://PHONE-IP:5001 - the log, /status, /snapshot.jpg (see HttpApi).</summary>
    internal sealed class LogServer
    {
        private readonly HttpApi _api;
        private StreamSocketListener _listener;

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
            try
            {
                using (var socket = args.Socket)
                {
                    var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                    var data = new List<byte>();
                    int headerEnd = -1, bodyLen = 0;
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline)
                    {
                        uint got = await reader.LoadAsync(4096);
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
                                if (m.Success) bodyLen = int.Parse(m.Groups[1].Value);
                            }
                        }
                        if (headerEnd >= 0 && data.Count >= headerEnd + 4 + bodyLen) break;
                        if (data.Count > 256 * 1024) break;
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
                    await socket.OutputStream.WriteAsync(reply.AsBuffer());
                    await socket.OutputStream.FlushAsync();
                }
            }
            catch { }
        }
    }
}
