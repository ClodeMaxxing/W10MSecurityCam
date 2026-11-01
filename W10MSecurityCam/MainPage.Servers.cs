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

        /// <summary>The rows for the stats page (network thread).</summary>
        private string StatsRows() => "";
    }
}
