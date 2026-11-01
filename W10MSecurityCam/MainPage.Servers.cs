using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace W10MSecurityCam
{
    /// <summary>
    /// The web switches (as in W10MWebCam and W10MDashcam): the web page (settings and /status; on by default, since it is
    /// the way to set the camera up without a cable), the log page (/log) and the stats page (/stats, the last 12 hours
    /// of status samples as graphs); the last two are off by default and need the web page on. The RTSP stream is never
    /// affected. The web page itself can only be switched off on the phone (the page would cut itself off).
    /// </summary>
    public sealed partial class MainPage
    {
        private bool _webOn = true, _logOn, _statsOn, _loadingServerUi = true;
        private List<string> _ips = new List<string>();

        /// <summary>From the constructor, before the servers start.</summary>
        private void LoadServerSettings()
        {
            _webOn = !(_settings.Values["WebServerOn"] is bool w) || w;
            _logOn = _settings.Values["LogServerOn"] is bool l && l;
            _statsOn = _settings.Values["StatsServerOn"] is bool s && s;
            _api.WebEnabled = _webOn;
            _api.LogEnabled = _logOn;
            _api.StatsEnabled = _statsOn;
            _api.GetStatsRows = StatsRows;
            WebServerToggle.IsOn = _webOn;
            LogServerToggle.IsOn = _logOn;
            StatsServerToggle.IsOn = _statsOn;
            LogServerToggle.IsEnabled = StatsServerToggle.IsEnabled = _webOn;
            _loadingServerUi = false;
        }

        private async void WebServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            await SetWebAsync(WebServerToggle.IsOn);
        }

        private async Task SetWebAsync(bool on)
        {
            _webOn = on;
            _settings.Values["WebServerOn"] = on;
            _api.WebEnabled = on;
            if (on) { try { await _logServer.StartAsync(LogPort); } catch (Exception ex) { Log("Web server: " + ex.Message); } }
            else _logServer.Stop();
            Log("Web page " + (on ? "on" : "off (only the stream is served)"));
            UpdateServerUi();
        }

        private void LogServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            SetLog(LogServerToggle.IsOn);
        }

        private void SetLog(bool on)
        {
            _logOn = on;
            _settings.Values["LogServerOn"] = on;
            _api.LogEnabled = on;
            Log("Log page " + (on ? "on" : "off"));
            UpdateServerUi();
        }

        private void StatsServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            SetStats(StatsServerToggle.IsOn);
        }

        private void SetStats(bool on)
        {
            _statsOn = on;
            _settings.Values["StatsServerOn"] = on;
            _api.StatsEnabled = on;
            Log("Stats page " + (on ? "on: a sample every 30 s from now" : "off"));
            UpdateServerUi();
        }

        /// <summary>The web page addresses under Network and login (or that it is off).</summary>
        private void UpdateServerUi()
        {
            LogServerToggle.IsEnabled = StatsServerToggle.IsEnabled = _webOn;
            if (!_webOn) { WebAddressText.Text = "Off - switch it on below"; return; }
            var lines = _ips.Select(ip => "http://" + ip + ":" + LogPort + "/camera").ToList();
            if (lines.Count == 0) lines.Add("(not on Wi-Fi)");
            lines.Add("USB: http://127.0.0.1:" + UsbPort + "/camera");
            if (_ips.Count > 0 && (_logOn || _statsOn))
                lines.Add((_logOn ? "Log: http://" + _ips[0] + ":" + LogPort + "/log" : "") + (_logOn && _statsOn ? "  ·  " : "") + (_statsOn ? "Stats: http://" + _ips[0] + ":" + LogPort + "/stats" : ""));
            WebAddressText.Text = string.Join("\n", lines);
        }

        // ---------------------------------------------------------------- service pills (under the start button)

        private static string ViewerCountText(int n) => n > 0 ? " · " + n + (n == 1 ? " viewer" : " viewers") : "";

        /// <summary>Once a second (UpdateStatusCard): what streams where. Green while streaming, grey when stopped.</summary>
        private void UpdateServicePills(bool streaming)
        {
            var on = streaming ? GreenFill : Brush(0x55, 0x55, 0x55);
            var text = streaming ? Brush(0x9C, 0xF0, 0xB0) : Brush(0x7F, 0x9A, 0x86);
            string ip = _ips.Count > 0 ? _ips[0] : null;
            SvcWifiDot.Fill = SvcUsbDot.Fill = on;
            SvcWifiText.Foreground = SvcUsbText.Foreground = text;
            SvcWifiText.Text = (ip != null ? "RTSP Wi-Fi " + ip + ":" + RtspPort : "RTSP Wi-Fi (not connected)") + ViewerCountText(_server.ViewerCount(false));
            SvcUsbText.Text = "RTSP USB 127.0.0.1:" + UsbPort + ViewerCountText(_server.ViewerCount(true));
            SvcWebDot.Fill = _webOn ? GreenFill : Brush(0x55, 0x55, 0x55);
            SvcWebText.Foreground = _webOn ? Brush(0x9C, 0xF0, 0xB0) : Brush(0x7F, 0x9A, 0x86);
            SvcWebText.Text = "Web page " + (_webOn ? "ON" : "OFF");
        }

        /// <summary>A tap on a pill opens the Network and login section, where the addresses and switches are.</summary>
        private void ServicePill_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            NetworkPanel.Visibility = Visibility.Visible;
            NetworkFold.Content = "NETWORK AND LOGIN  ▾";
        }

        /// <summary>log=on|off, stats=on|off (the web page can switch these; the web page itself only on the phone). Network thread.</summary>
        private async Task ApplyServerConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool hasLog = form.TryGetValue("log", out var lg), hasStats = form.TryGetValue("stats", out var sg);
            if (!hasLog && !hasStats) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _loadingServerUi = true;
                if (hasLog) LogServerToggle.IsOn = IsOn(lg);
                if (hasStats) StatsServerToggle.IsOn = IsOn(sg);
                _loadingServerUi = false;
                if (hasLog) SetLog(IsOn(lg));
                if (hasStats) SetStats(IsOn(sg));
            });
            if (hasLog) done.Add("log page " + (IsOn(lg) ? "on" : "off"));
            if (hasStats) done.Add("stats page " + (IsOn(sg) ? "on" : "off"));
        }

        private string ServerConfigText() =>
            "log=" + (_logOn ? "on" : "off") + ", stats=" + (_statsOn ? "on" : "off") + " (the log and stats pages; the web page itself is switched on the phone)\n";

        private void AddServerJson(JsonObject o)
        {
            o["webOn"] = JsonValue.CreateBooleanValue(_webOn);
            o["logOn"] = JsonValue.CreateBooleanValue(_logOn);
            o["statsOn"] = JsonValue.CreateBooleanValue(_statsOn);
        }

        // ---------------------------------------------------------------- stats samples (for /stats)

        private const int StatsKeep = 1440; // 12 hours at one sample every 30 s
        private readonly Queue<string> _statRows = new Queue<string>();
        private readonly object _statLock = new object();
        private DateTime _lastStatSample = DateTime.MinValue;

        private static string F(double? v, string fmt = "0.##") => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "";

        /// <summary>Once a second (UpdateStats): one row every 30 s, only while the stats page is on. The columns are W10MWebCam's.</summary>
        private void SampleStats()
        {
            if (!_statsOn || (DateTime.Now - _lastStatSample).TotalSeconds < 30) return;
            _lastStatSample = DateTime.Now;
            try
            {
                var r = _battery;
                bool charging = IsCharging(r);
                bool streaming = _camera != null && _camera.IsStreaming;
                // time,ok,version,streaming,fps,mbps,viewers,format,bitrate,latency_ms,battery_pct,charging,net_w,draw_w,
                // heat_warn,protect,cpu_phone,cpu_app,memory_mb,wifi_bars,uptime_s,safe
                string row = string.Join(",", new[]
                {
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture), "1", AppVersion,
                    streaming ? "1" : "0",
                    streaming ? F(_camera.Fps, "0.0") : "", streaming ? F(_camera.Mbps, "0.0") : "",
                    _server.ClientCount.ToString(CultureInfo.InvariantCulture),
                    _currentFormatKey ?? "", _currentBitrateKey ?? "",
                    streaming && _camera.LastLatencyMs >= 0 ? _camera.LastLatencyMs.ToString(CultureInfo.InvariantCulture) : "",
                    F(Percent(r)), charging ? "1" : "0",
                    F(_power.NetWatts), charging ? "" : F(_power.DrawWatts),
                    _pausedSecs >= 20 ? "1" : "0", _protect ? "1" : "0",
                    F(_power.SysCpuPct, "0"), F(_power.ProcCpuPct, "0"),
                    F(AppMemory() / 1048576.0, "0"), WifiBars() >= 0 ? WifiBars().ToString(CultureInfo.InvariantCulture) : "",
                    F((DateTime.Now - _appStarted).TotalSeconds, "0"),
                    _camera != null && !_camera.UseNativeSink ? "1" : "0"
                }.Select(x => (x ?? "").Replace(",", ";").Replace("\"", ""))); // a comma inside a field would shift the columns
                lock (_statLock)
                {
                    _statRows.Enqueue(row);
                    while (_statRows.Count > StatsKeep) _statRows.Dequeue();
                }
            }
            catch (Exception ex) { Log("Stats sample: " + ex.Message); }
        }

        /// <summary>The rows for the stats page (network thread).</summary>
        private string StatsRows()
        {
            lock (_statLock) return string.Join(",\n", _statRows.Select(x => "'" + x.Replace("'", "") + "'"));
        }
    }
}
