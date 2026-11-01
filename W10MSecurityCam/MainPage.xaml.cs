using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using W10MSecurityCam.Rtsp;
using W10MSecurityCam.Streaming;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Devices.Power;
using Windows.Graphics.Display;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace W10MSecurityCam
{
    /// <summary>
    /// The single screen of the app: camera control, settings, blackout mode, crash guard and logging.
    /// The camera/encoder work lives in Streaming\CameraEngine.cs; the RTSP server in Rtsp\.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const int RtspPort = 8554;  // rtsp://PHONE-IP:8554/live over Wi-Fi
        private const int UsbPort = 8020;   // same RTSP server over USB; Microsoft's IP-over-USB service maps PC 127.0.0.1:8020 to it
        private const int LogPort = 5001;   // http://PHONE-IP:5001 shows the log (the RTSP ports also answer an HTTP "GET" with the log)
        private const string StreamPath = "/live";

        private CameraEngine _camera;
        private readonly RtspServer _server = new RtspServer(RtspPort, UsbPort);
        private readonly LogServer _logServer;
        private readonly HttpApi _api = new HttpApi();
        private readonly CameraTuning _tuning = new CameraTuning();
        private readonly DispatcherTimer _tuningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // True until the camera's ranges are on screen: sliders fire ValueChanged while the page is built
        // (the white-balance slider snaps to its minimum), which must not overwrite the saved values.
        private bool _loadingTuningUi = true;
        private bool _loadingSaveUi = true;
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly PowerMonitor _power = new PowerMonitor();
        private DateTime _lastPowerLog = DateTime.MinValue;
        private DateTime _holdNotBefore = DateTime.MaxValue;
        private bool _holdBusy;
        private DateTime _lastTuningLog = DateTime.MinValue;
        private readonly DispatcherTimer _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        private readonly DateTime _appStarted = DateTime.Now;
        private string _lastRestartReason = "";
        private int _restarts;
        private bool _safeMode;
        private DateTime _lastPowerWarning = DateTime.MinValue;
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        private bool _busy;
        private DateTime _busySince;
        private bool _keepAwake;
        private BrightnessOverride _brightness;
        private bool _blackedOut;

        // crash guard + stall watchdog
        private DateTime _startedAt = DateTime.MinValue;
        private int _nativeStalls;
        private DateTime _lastStall = DateTime.MinValue;

        private string _currentFormat, _currentBitrate, _currentFormatKey = "", _currentBitrateKey = "";
        private volatile string _formatList = "";

        private sealed class KeyframeOption
        {
            public double Seconds;
            public override string ToString() => Seconds + " s";
        }

        private sealed class BitrateOption
        {
            public uint Bps; // 0 = auto
            public override string ToString() => Bps == 0 ? "Auto" : (Bps / 1e6).ToString("0.#") + " Mbps";
        }

        /// <summary>
        /// Startup self-check. A launch that never reached 20 s of healthy running (or a clean suspend) counts as a failed launch.
        /// After two failed launches in a row the saved settings are wiped so the app starts with defaults instead of crashing again.
        /// The stream login is kept (it lives in the password vault, not in these settings).
        /// </summary>
        private void LaunchSelfCheck()
        {
            try
            {
                int fails = _settings.Values["LaunchFailures"] is int lf ? lf : 0;
                if (_settings.Values["LaunchInProgress"] is bool lip && lip) fails++; else fails = 0;
                if (fails >= 2)
                {
                    _settings.Values.Clear();
                    fails = 0;
                    _selfCheckReset = true;
                }
                _settings.Values["LaunchFailures"] = fails;
                _settings.Values["LaunchInProgress"] = true;
            }
            catch { }
        }

        private bool _selfCheckReset, _launchCleared;

        private void MarkLaunchHealthy()
        {
            if (_launchCleared) return;
            _launchCleared = true;
            try { _settings.Values["LaunchInProgress"] = false; _settings.Values["LaunchFailures"] = 0; } catch { }
        }

        public MainPage()
        {
            RotateLogFile();
            StartLogWriter();
            LaunchSelfCheck();
            InitializeComponent();

            VersionText.Text = "v" + AppVersion;

            Application.Current.UnhandledException += (s, e) => { Log("CRASH (unhandled): " + e.Exception); FlushLog(); };
            _server.Log = Log;
            _server.Auth.Log = Log;
            _server.Auth.Load();
            _server.Auth.AllowBasic = _settings.Values["AllowBasic"] is bool ab && ab;
            _server.AllowUdp = _settings.Values["AllowUdp"] is bool au && au;
            _api.GetLog = GetFullLog;
            _api.GetStatusJson = GetStatusJson;
            _api.GetConfig = GetConfigText;
            _api.GetCameraJson = GetCameraJson;
            _api.ApplyConfig = ApplyConfigAsync;
            _api.Auth = _server.Auth;
            _api.Log = Log;
            _api.GetSnapshotJpeg = GetSnapshotAsync;
            _snapshotsOn = _settings.Values["Snapshots"] is bool sn && sn;
            _audioOn = _settings.Values["Audio"] is bool ao && ao;
            _tuning.Log = Log;
            bool oldAuto = false;
            if (_settings.Values["CamAuto"] is bool ca) { _tuning.Auto = ca; oldAuto = true; } // 1.6.0 / 1.6.1: one switch for all three
            if (_settings.Values["CamFocusAuto"] is bool cfa) _tuning.FocusAuto = cfa;
            if (_settings.Values["CamExposureAuto"] is bool cea) _tuning.ExposureAuto = cea;
            if (_settings.Values["CamWbAuto"] is bool cwa) _tuning.WbAuto = cwa;
            if (_settings.Values["CamFocus"] is double cf) _tuning.FocusPercent = cf;
            if (_settings.Values["CamExposureMs"] is double ce) _tuning.ExposureMs = ce;
            if (_settings.Values["CamWbK"] is int cw) _tuning.WhiteBalanceKelvin = (uint)cw;
            if (_settings.Values["SaveOis"] is bool so) _tuning.SaveOis = so;
            if (_settings.Values["SaveFocus"] is bool sf) _tuning.SaveFocus = sf;
            if (oldAuto) { SaveTuning(); _settings.Values.Remove("CamAuto"); } // kept as the three switches from now on
            _saveTimer.Tick += async (s, e) => { _saveTimer.Stop(); await ApplyPowerChangeAsync(); };
            _tuningTimer.Tick += async (s, e) => { _tuningTimer.Stop(); await ApplyTuningAsync(); };
            _server.Http = _api;
            _logServer = new LogServer(_api) { Log = Log };
            _healthTimer.Tick += (s, e) => { CheckPower(); CheckMemory(); };
            _statsTimer.Tick += (s, e) => UpdateStats();
            _wakeTimer.Tick += WakeTimer_Tick;
            // Any touch on the page (handled or not) keeps a woken screen awake and a shown picture showing.
            AddHandler(PointerPressedEvent, new Windows.UI.Xaml.Input.PointerEventHandler(AnyTouch), true);
            Window.Current.VisibilityChanged += (s, e) => { if (!e.Visible) Log("App hidden (screen locked or switched away)"); };

            _highBw = _settings.Values["HighBandwidth"] is bool hb0 && hb0;
            HighBwToggle.IsOn = _highBw;
            RebuildBitrateList(); // selects 4 Mbps: plenty for 1080p of a mostly still scene
            foreach (var k in new[] { 0.5, 1.0, 2.0, 3.0, 4.0 }) KeyframeBox.Items.Add(new KeyframeOption { Seconds = k });
            KeyframeBox.SelectedIndex = 2; // 2 s: NVRs cut recordings at keyframes; longer saves bandwidth
            UpdateLoginUi();
            UdpToggle.IsOn = _server.AllowUdp;
            SnapshotToggle.IsOn = _snapshotsOn;
            AudioToggle.IsOn = _audioOn;
            LoadSaveUi();
            LoadTorchUi();

            Application.Current.Suspending += OnSuspending;
            Application.Current.Resuming += OnResuming;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;
            await StartCameraAsync();
        }

        // ------------------------------------------------------------------ startup

        private async Task StartCameraAsync()
        {
            try
            {
                // Servers first, so the log is reachable even if the camera fails.
                try { await _server.StartAsync(); } catch (Exception ex) { Log("Video server: " + ex.Message); }
                try { await _logServer.StartAsync(LogPort); } catch (Exception ex) { Log("Log server: " + ex.Message); }

                if (_selfCheckReset) { _selfCheckReset = false; Log("SELF-CHECK: the app failed to start twice in a row, so its saved settings were reset to defaults (the stream login is kept)."); }
                Log("Starting camera...");
                _camera = new CameraEngine { Log = Log, AudioRequested = _audioOn };
                _camera.AccessUnitReady += _server.SendAccessUnit;
                _camera.ParameterSetsReady += _server.SetParameterSets;
                _camera.AudioFrameReady += _server.SendAudio;
                _camera.AudioFormatReady += _server.SetAudioFormat;
                await _camera.InitializeAsync();

                var formats = _camera.GetRecordFormats();
                ResolutionBox.Items.Clear();
                foreach (var f in formats) ResolutionBox.Items.Add(f);
                Log(formats.Count + " recording formats, up to " + (formats.Count > 0 ? formats[0].ToString() : "none"));

                Log("Formats: " + string.Join(", ", formats.Select(f => f.ToString().Replace(" fps", ""))));
                _formatList = string.Join(", ", formats.Select(FormatKey));

                // Security-camera defaults (v1.2+): 1080p at the lowest offered rate of 15 fps or more, 4 Mbps, keyframe every 2 s.
                // Settings saved by older versions used different lists, so they are reset once.
                if (!(_settings.Values["DefaultsVersion"] is int dv) || dv < 2)
                {
                    _settings.Values.Remove("Format");
                    _settings.Values.Remove("Bitrate");
                    _settings.Values.Remove("Keyframe");
                    _settings.Values["DefaultsVersion"] = 2;
                    Log("Applied security-camera defaults (1080p, 4 Mbps, keyframe every 2 s)");
                }
                string savedFormat = _settings.Values["Format"] as string;
                ResolutionBox.SelectedItem = formats.FirstOrDefault(f => f.ToString() == savedFormat)
                    ?? formats.Where(f => f.Width == 1920 && f.Height == 1080 && f.Fps >= 14.5).OrderBy(f => f.Fps).FirstOrDefault()
                    ?? formats.FirstOrDefault();
                if (_settings.Values["BitrateBps"] is int savedBps && savedBps >= 0)
                {
                    _applyingSettings = true; // restoring, not a user change
                    ApplyVideoSettings(null, savedBps == 0 ? "auto" : (savedBps / 1e6).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _applyingSettings = false;
                }
                else if (_settings.Values["Bitrate"] is int savedBitrate && savedBitrate >= 0 && savedBitrate < BitrateBox.Items.Count)
                {
                    BitrateBox.SelectedIndex = savedBitrate; // a list index, saved by versions before BitrateBps
                    _settings.Values["BitrateBps"] = (int)((BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0);
                }
                _settings.Values.Remove("Bitrate");
                if (_settings.Values["Keyframe"] is int kf && kf >= 0 && kf < KeyframeBox.Items.Count)
                    KeyframeBox.SelectedIndex = kf;
                AutoStartToggle.IsOn = !(_settings.Values["AutoStart"] is bool b0) || b0;
                PowerSaveToggle.IsOn = !(_settings.Values["PowerSave"] is bool b1) || b1;

                bool crashedLastTime = _settings.Values["StartInProgress"] is bool sip && sip;
                bool autoStarting = AutoStartToggle.IsOn && !crashedLastTime;

                // Crash guard for camera settings: if the app died while applying them last time, a saved
                // manual value may be the cause, so go back to automatic instead of crashing again.
                if (_settings.Values["TuningInProgress"] is bool tip && tip)
                {
                    _settings.Values["TuningInProgress"] = false;
                    _tuning.Auto = true;
                    SaveTuning();
                    Log("Camera settings reset to automatic: the app closed while applying them last time.");
                }

                if (_settings.Values["PowerSaveInProgress"] is bool psip && psip)
                {
                    _settings.Values["PowerSaveInProgress"] = false;
                    _tuning.SaveOis = false;
                    SavePowerSettings(); LoadSaveUi();
                    Log("Camera stabilization power saving switched off: the app closed while applying it last time.");
                }

                PreviewElement.Source = _camera.Capture;
                // Don't spin the preview up just to switch it off again: starting 4K recording while the
                // preview is toggled right after camera power-up crashes the camera pipeline.
                if (PreviewWanted && !(autoStarting && PowerSaveToggle.IsOn)) await _camera.SetPreviewAsync(true);

                _tuning.Describe(_camera.Capture);
                LoadSaveUi();
                LoadTorchUi();
                LoadTuningUi();
                PowerSaveToggle.IsEnabled = !_protect;
                await ApplyTuningAsync(true);

                ShowAddresses();
                StartButton.IsEnabled = formats.Count > 0;
                _statsTimer.Start();
                _healthTimer.Start();
                CheckPower();
                Log("Ready.");

                if (crashedLastTime)
                {
                    // Crash guard: the last launch died while starting a stream. Don't auto-start, and use the
                    // proven MP4 engine for this launch only.
                    _settings.Values["StartInProgress"] = false;
                    _camera.UseNativeSink = false;
                    _safeMode = true;
                    _lastRestartReason = "safe mode after a crash while starting";
                    Log("SAFE MODE: the app closed unexpectedly while starting the stream last time.");
                    Log("Auto-start skipped; the standard (MP4) engine will be used until the app is reopened.");
                }
                else if (autoStarting && StartButton.IsEnabled)
                {
                    // 4K needs longer after power-up before recording starts (a cold 4K start has crashed the camera pipeline).
                    bool big = ResolutionBox.SelectedItem is FormatOption af && (long)af.Width * af.Height > 1920L * 1080;
                    Log("Auto-start: letting the camera settle for " + (big ? 6 : 2) + " s");
                    await Task.Delay(big ? 6000 : 2000);
                    await ToggleStreamingAsync();
                }
            }
            catch (Exception ex)
            {
                Log("ERROR starting camera: " + ex.Message);
            }
        }

        private void ShowAddresses()
        {
            var ips = NetworkInformation.GetHostNames()
                .Where(h => h.Type == HostNameType.Ipv4 && h.IPInformation != null)
                .Select(h => h.CanonicalName)
                .Where(ip => !ip.StartsWith("169.254.")) // link-local addresses aren't reachable from the PC
                .ToList();
            var lines = ips.Select(ip => "rtsp://" + ip + ":" + RtspPort + StreamPath).ToList();
            lines.Add("USB: rtsp://127.0.0.1:" + UsbPort + StreamPath);
            lines.Add("USB settings page: http://127.0.0.1:" + UsbPort + "/camera");
            AddressText.Text = string.Join("\n", lines);
            UpdateLoginUi();
            Log("Addresses: " + string.Join("  ", lines) + (ips.Count > 0 ? "  (log: http://" + ips[0] + ":" + LogPort + ")" : ""));
        }

        // ------------------------------------------------------------------ start / stop

        private async void StartButton_Click(object sender, RoutedEventArgs e) => await ToggleStreamingAsync();

        private async void Setting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_applyingSettings) SyncVideoKeys();
            if (_applyingSettings) return; // a USB config change restarts the stream once, itself
            if (_camera == null || !_camera.IsStreaming || _busy) return;
            Log("Setting changed - restarting stream");
            await ToggleStreamingAsync(); // stop
            await ToggleStreamingAsync(); // start with the new setting
        }

        /// <summary>Keeps the format/bitrate the web page shows in step with the dropdowns (not only when a stream starts).</summary>
        private void SyncVideoKeys()
        {
            if (ResolutionBox.SelectedItem is FormatOption f) _currentFormatKey = FormatKey(f);
            if (BitrateBox.SelectedItem is BitrateOption b)
                _currentBitrateKey = b.Bps == 0 ? "auto" : (b.Bps / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ToggleStreamingAsync()
        {
            if (_busy && (DateTime.Now - _busySince).TotalSeconds > 30)
            {
                Log("Previous start/stop never finished - unlocking controls");
                _busy = false;
            }
            if (_busy || _camera == null) return;
            _busy = true;
            _busySince = DateTime.Now;
            StartButton.IsEnabled = false;
            try
            {
                if (!_camera.IsStreaming)
                {
                    var f = ResolutionBox.SelectedItem as FormatOption;
                    if (f == null) { Log("Pick a resolution first"); return; }
                    if (!_camera.UseNativeSink && (long)f.Width * f.Height > 1920L * 1080)
                    {
                        // The MP4 fallback engine ignores the bitrate setting, so keep it at 1080p to stay within Wi-Fi limits.
                        var capped = ResolutionBox.Items.OfType<FormatOption>()
                            .Where(o => o.Width == 1920 && o.Height == 1080).OrderBy(o => Math.Abs(o.Fps - f.Fps)).FirstOrDefault();
                        if (capped != null) { Log("Standard (MP4) engine: using " + capped + " instead of " + f); f = capped; }
                    }
                    var b = BitrateBox.SelectedItem as BitrateOption;
                    uint bps = (b == null || b.Bps == 0) ? CameraEngine.AutoBitrate(f, _highBw) : b.Bps;
                    if (KeyframeBox.SelectedItem is KeyframeOption ko) _camera.KeyframeSeconds = ko.Seconds;
                    if (!_protect) _settings.Values["Format"] = f.ToString(); // heat protection only borrows the video settings
                    _currentFormat = f.ToString();
                    _currentFormatKey = FormatKey(f);
                    _currentBitrateKey = (b == null || b.Bps == 0) ? "auto" : (b.Bps / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    _currentBitrate = (b == null || b.Bps == 0) ? "Auto (" + (bps / 1e6).ToString("0.#") + " Mbps)" : b.ToString();
                    if (!_protect)
                    {
                        _settings.Values["BitrateBps"] = (int)(b?.Bps ?? 0);
                    }
                    _settings.Values["Keyframe"] = KeyframeBox.SelectedIndex;

                    _settings.Values["StartInProgress"] = true; // cleared after 15 s of healthy streaming
                    _startedAt = DateTime.Now;
                    _streamBeganAt = DateTime.Now;
                    // Switch the preview off BEFORE recording starts; changing it mid-recording stalls the 4K pipeline.
                    await _camera.SetPreviewAsync(PreviewWanted);
                    _server.ResetStream(); // connected viewers wait for the new stream's first keyframe
                    ApplyCameraPower();
                    _holdNotBefore = DateTime.Now.AddSeconds(30);
                    await _camera.StartStreamingAsync(f, bps);
                    _tuning.FrameLimitMs = 1000.0 / Math.Max(1, f.Fps);
                    await Task.Delay(2500); // let the first frames flow before touching the camera controls again
                    await ApplyTuningAsync(true);
                    StartButton.Content = "Stop streaming";
                    await ApplyBlackoutAsync();
                    if (!_keepAwake) { _displayRequest.RequestActive(); _keepAwake = true; }
                }
                else
                {
                    await _camera.StopStreamingAsync();
                    ClearStartFlag();
                    StopUi();
                    await ApplyBlackoutAsync();
                }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                try { await _camera.StopStreamingAsync(); } catch { }
                ClearStartFlag();
                StopUi();
            }
            finally
            {
                StartButton.IsEnabled = true;
                _busy = false;
            }
        }

        private void ClearStartFlag()
        {
            _settings.Values["StartInProgress"] = false;
            _startedAt = DateTime.MinValue;
        }

        private void StopUi()
        {
            StartButton.Content = "Start streaming";
            if (_keepAwake) { _displayRequest.RequestRelease(); _keepAwake = false; }
        }

        // ------------------------------------------------------------------ blackout (battery saver)

        /// <summary>While streaming: preview off, screen at minimum brightness, full black overlay (tap to wake for 15 s).</summary>
        /// <summary>
        /// While any camera setting is manual the person is adjusting it and needs to see the picture, so the
        /// blackout is paused and the preview forced on (their saved toggles are left alone and come back
        /// when everything is automatic again).
        /// </summary>
        // The three display rules live here and nowhere else:
        //  - Adjusting: someone is touching the camera controls (or Wake) ON THE PHONE; it lapses after 60 s without a touch.
        //    Settings changed from the web page never count, so the web can adjust while the phone stays black.
        //  - Blackout: streaming, the power-save switch is on, and nobody is adjusting on the phone.
        //  - Preview: the user's switch, or forced on while adjusting.
        private bool Adjusting => _phoneAdjusting;
        private bool BlackoutWanted => _camera != null && _camera.IsStreaming && PowerSaveToggle.IsOn && !Adjusting;
        private static string AppVersion { get { var v = Package.Current.Id.Version; return v.Major + "." + v.Minor + "." + v.Build; } }
        private bool PreviewWanted => !PowerSaveToggle.IsOn || Adjusting; // one switch: stealth (black, no preview) or picture shown

        /// <summary>One-click power save (phone button and web button): brightness 0, black screen, preview off.</summary>
        private async Task SetBlackoutAsync(bool on)
        {
            if (on) EndPhoneAdjust();
            if (PowerSaveToggle.IsOn != on) PowerSaveToggle.IsOn = on; // its handler saves the setting and applies it
            else await ApplyBlackoutAsync();
            await UpdateDisplayForTuningAsync();
            UpdatePowerButton();
        }

        private async void PowerSaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Wake only lifts the black screen for a while (the saved setting stays); the button never touches the preview itself.
            // The button is only reachable while the screen is lit, so it always blacks out: from a 15 s status look
            // (blackout already on, overlay lifted) it puts the overlay straight back; otherwise it switches blackout on.
            if (_blackedOut) WakeTimer_Tick(null, null);
            else await SetBlackoutAsync(true);
            // Blacking out by hand also ends a picture that is showing: stop the preview now rather than waiting for the timer.
            if (_camera != null && _camera.IsStreaming && _camera.Previewing && !PreviewWanted)
                await RestartForPreviewAsync("to stop the preview (screen blacked out)", true);
            if (!(_camera != null && _camera.IsStreaming)) Log("Power save is on; the screen goes black when streaming starts");
        }

        private void UpdatePowerButton()
        {
            PowerSaveButton.Content = "Black out the screen now";
        }

        private DateTime _lastPreviewRestart = DateTime.MinValue;

        // ---- phone-side adjusting: lifts the blackout and shows the picture; lapses after 60 s without a touch
        private bool _phoneAdjusting;
        private readonly DispatcherTimer _adjustTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        private bool _adjustTimerHooked;

        /// <param name="byHand">The Show camera picture button: always (re)starts the picture, even if a restart ran a moment ago.</param>
        private async Task TouchPhoneAdjustAsync(bool byHand = false)
        {
            if (!_adjustTimerHooked) { _adjustTimer.Tick += AdjustTimer_Tick; _adjustTimerHooked = true; }
            _adjustTimer.Stop(); _adjustTimer.Start();
            if (_protect) return;
            if (_phoneAdjusting && !byHand) return;
            if (!_phoneAdjusting) Log(byHand ? "Show camera picture pressed" : "Camera controls touched on the phone: showing the picture");
            _phoneAdjusting = true;
            await UpdateDisplayForTuningAsync(byHand);
        }

        private void TouchPhoneAdjust() { var _ = TouchPhoneAdjustAsync(); }

        private async void ShowPictureButton_Click(object sender, RoutedEventArgs e) => await TouchPhoneAdjustAsync(true);

        /// <summary>Folding section headers: Tag is "PanelName|LABEL".</summary>
        private void Fold_Click(object sender, RoutedEventArgs e)
        {
            var b = (Button)sender;
            var parts = ((string)b.Tag).Split('|');
            if (!(FindName(parts[0]) is UIElement panel)) return;
            bool open = panel.Visibility != Visibility.Visible;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            b.Content = parts[1] + (open ? "  \u25BE" : "  \u25B8");
        }

        private void EndPhoneAdjust() { _phoneAdjusting = false; _adjustTimer.Stop(); }

        private async void AdjustTimer_Tick(object sender, object e)
        {
            _adjustTimer.Stop();
            if (!_phoneAdjusting) return;
            _phoneAdjusting = false;
            Log("No touches for 60 s: back to blackout");
            await UpdateDisplayForTuningAsync();
            // the preview cannot be stopped mid-stream; one restart does it
            if (_camera != null && _camera.Previewing && !PreviewWanted) await RestartForPreviewAsync("to stop the preview");
            UpdatePreviewText();
        }

        /// <summary>
        /// Brings the camera preview in line with what is wanted. While streaming the preview cannot change (that stalls the
        /// 4K pipeline), so a preview that is wanted but not running needs one stream restart; that happens only when
        /// asked for, and at most once a minute.
        /// </summary>
        private async Task EnsurePreviewAsync(bool restartIfNeeded, bool byHand = false)
        {
            if (_camera == null) return;
            bool want = PreviewWanted && !_blackedOut;
            if (!_camera.IsStreaming) { await _camera.SetPreviewAsync(want); return; }
            if (want && !_camera.Previewing && restartIfNeeded) await RestartForPreviewAsync("to show the camera picture on the phone", byHand);
        }

        /// <summary>The preview can only change while the stream is stopped, so this is the one place that restarts the stream for it (at most once a minute).</summary>
        private bool _previewFixPending; // a button asked for a restart while another one was running: do it as soon as possible

        private async Task RestartForPreviewAsync(string why, bool byHand = false)
        {
            if (_camera == null || !_camera.IsStreaming) return;
            if (_busy || _protectBusy) { if (byHand) _previewFixPending = true; return; }
            _previewFixPending = false;
            if (!byHand && (DateTime.Now - _lastPreviewRestart).TotalSeconds < 60) return; // automatic restarts: at most once a minute
            _lastPreviewRestart = DateTime.Now;
            Log("Restarting the stream once " + why);
            await ToggleStreamingAsync();
            await ToggleStreamingAsync();
        }

        private void UpdatePreviewText()
        {
            bool running = _camera != null && _camera.Previewing;
            PreviewOffText.Text = _camera != null && _camera.IsStreaming && PreviewWanted && !running
                ? "Starting the picture (the stream restarts for a moment)..." : "Preview off";
            PreviewOffText.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task UpdateDisplayForTuningAsync(bool byHand = false)
        {
            try
            {
                bool adjusting = Adjusting;
                PowerSaveToggle.IsEnabled = !_protect;
                await ApplyBlackoutAsync();
                if (!_blackedOut && _camera != null)
                {
                    await EnsurePreviewAsync(adjusting, byHand); // someone adjusting the camera needs to see the picture
                    UpdatePreviewText();
                }
            }
            catch (Exception ex) { Log("Display update: " + ex.Message); }
        }

        private async Task ApplyBlackoutAsync()
        {
            bool want = BlackoutWanted;
            if (want == _blackedOut) { UpdatePowerButton(); return; }
            _blackedOut = want;
            try
            {
                if (_brightness == null) _brightness = BrightnessOverride.GetForCurrentView();
                if (want)
                {
                    if (_brightness.IsSupported)
                    {
                        _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None);
                        _brightness.StartOverride();
                    }
                    if (_camera != null && !_camera.IsStreaming) await _camera.SetPreviewAsync(false); // never mid-stream
                    BlackoutOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    _wakeTimer.Stop();
                    BlackoutOverlay.Visibility = Visibility.Collapsed;
                    if (_brightness.IsSupported) _brightness.StopOverride();
                    if (_camera != null && !_camera.IsStreaming && PreviewWanted) await _camera.SetPreviewAsync(true);
                    UpdatePreviewText();
                }
            }
            catch (Exception ex) { Log("Blackout: " + ex.Message); }
            UpdatePowerButton();
        }

        private void BlackoutOverlay_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            BlackoutOverlay.Visibility = Visibility.Collapsed;
            if (_brightness != null && _brightness.IsSupported) _brightness.StopOverride();
            PreviewOffText.Text = "Streaming - preview off (blackout mode)";
            PreviewOffText.Visibility = Visibility.Visible;
            _wakeTimer.Stop();
            _wakeTimer.Start();
        }

        private void AnyTouch(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_blackedOut && BlackoutOverlay.Visibility != Visibility.Visible) { _wakeTimer.Stop(); _wakeTimer.Start(); }
            if (_phoneAdjusting) { _adjustTimer.Stop(); _adjustTimer.Start(); }
        }

        private void WakeTimer_Tick(object sender, object e)
        {
            _wakeTimer.Stop();
            if (!_blackedOut) return;
            BlackoutOverlay.Visibility = Visibility.Visible;
            if (_brightness != null && _brightness.IsSupported)
            {
                _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None);
                _brightness.StartOverride();
            }
        }

        private async void PowerSaveToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_protect) _settings.Values["PowerSave"] = PowerSaveToggle.IsOn; // forced values are temporary
            await ApplyBlackoutAsync();
        }

        private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.Values["AutoStart"] = AutoStartToggle.IsOn;
        }


        // ------------------------------------------------------------------ stats + watchdog

        private int _statsTick; // counts the stats ticks (one a second) for the jobs that run every few seconds
        private BatteryReport _battery; // read once per stats tick; the status card, heat check and status text share it

        private void UpdateStats()
        {
            int tick = _statsTick++;
            _battery = TryBattery();
            UpdatePowerFlags();
            if ((DateTime.Now - _appStarted).TotalSeconds > 20) MarkLaunchHealthy();
            // a preview left running behind the black screen (its stop restart was skipped while busy) is stopped on a later tick
            if (!_blackedOut) UpdatePreviewText();
            // The preview does not match what is wanted (a restart was skipped while another one ran): fix it with one restart.
            // A pending button press skips the once-a-minute limit.
            if (_camera != null && _camera.IsStreaming && !_busy && !_protectBusy)
            {
                if (_camera.Previewing && !PreviewWanted && _blackedOut)
                { var _ = RestartForPreviewAsync("to stop the preview", _previewFixPending); }
                else if (!_camera.Previewing && PreviewWanted && !_blackedOut && _phoneAdjusting)
                { var _ = RestartForPreviewAsync("to show the camera picture on the phone", _previewFixPending); }
            }
            // keep the flashlight switch in step with the stream: it is only usable while streaming
            bool torchUsable = _tuning.TorchSupported && _camera != null && _camera.IsStreaming && !_protect;
            if (TorchToggle.IsEnabled != (torchUsable || _torchWanted)) LoadTorchUi();
            if (_torchWanted)
            {
                if (_camera == null || !_camera.IsStreaming) { _torchWanted = false; LoadTorchUi(); }
                else if (DateTime.Now >= _torchOffAt) { SetTorch(false); Log("Flashlight switched off automatically (time limit)"); }
            }
            if (tick % 10 == 0)
            {
                _power.Sample();
                if ((DateTime.Now - _lastPowerLog).TotalSeconds >= 60)
                {
                    _lastPowerLog = DateTime.Now; // a trail of readings for comparing settings
                    Log(_power.PowerText() + (_power.CpuText().Length > 0 ? "; " + _power.CpuText() : "") +
                        " [" + _power.RemainingMwh + " mWh, temp " + _heat + ", " + _power.Status + (_camera != null && _camera.IsStreaming ? ", " + _currentFormat + " " + _currentBitrate : "") + "]");
                }
            }
            if (!_blackedOut && tick % 2 == 0) UpdateStatusText(); // nobody sees it while the screen is black
            if (_camera == null) return;
            if (!_camera.IsStreaming)
            {
                UpdateStatusCard(false);
                UpdateHeatHint();
                return;
            }
            UpdateHeatHint();
            _camera.UpdateStats();
            CheckStall();
            if (_tuning.SaveFocus && _tuning.FocusAuto && !_tuning.FocusHeld && !_holdBusy && !_busy && !_applyingTuning && DateTime.Now >= _holdNotBefore)
            {
                _holdBusy = true;
                var _h = HoldFocusSafeAsync();
            }
            if (tick % 15 == 0)
            {
                _tuning.Read(_camera.Capture);
                TuningStateText.Text = _tuning.State;
                if ((DateTime.Now - _lastTuningLog).TotalMinutes >= 10)
                {
                    // Lets you see exposure and ISO follow the daylight in the log.
                    _lastTuningLog = DateTime.Now;
                    Log("Camera now: " + _tuning.State);
                }
            }
            if (_startedAt != DateTime.MinValue && (DateTime.Now - _startedAt).TotalSeconds > 15)
                ClearStartFlag(); // ran fine for 15 s

            UpdateStatusCard(true);
        }

        private bool _dotPulsing;
        // One brush per colour, made once: the status updates every second would otherwise make dozens of new ones.
        private static Dictionary<int, Windows.UI.Xaml.Media.SolidColorBrush> _brushes;
        private static Windows.UI.Xaml.Media.SolidColorBrush Brush(byte r, byte g, byte b)
        {
            if (_brushes == null) _brushes = new Dictionary<int, Windows.UI.Xaml.Media.SolidColorBrush>();
            int key = r << 16 | g << 8 | b;
            if (!_brushes.TryGetValue(key, out var brush))
                _brushes[key] = brush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            return brush;
        }

        private string _resLabelKey, _resLabel; // the resolution pill ("4K30") for _currentFormatKey

        /// <summary>The status card: dot and state, resolution and viewer pills, big fps / Mbps, engine line.</summary>
        private void UpdateStatusCard(bool streaming)
        {
            int viewers = _server.ClientCount;
            ViewersText.Text = viewers + (viewers == 1 ? " viewer" : " viewers");
            StateText.Text = _protect ? "HEAT PROTECTION" : streaming ? "STREAMING" : "STOPPED";
            StateText.Foreground = _protect ? Brush(0xFF, 0x6B, 0x6B) : Brush(0x9C, 0xF0, 0xB0);
            StatusDot.Fill = _protect ? Brush(0xFF, 0x55, 0x55) : streaming ? Brush(0x39, 0xFF, 0x14) : Brush(0x55, 0x55, 0x55);
            if (_resLabelKey != _currentFormatKey)
            {
                _resLabelKey = _currentFormatKey; // parsed again only when the format changes
                var m = System.Text.RegularExpressions.Regex.Match(_currentFormatKey ?? "", @"^(\d+)x(\d+)@(\d+)");
                _resLabel = m.Success ? (int.Parse(m.Groups[2].Value) >= 2160 ? "4K" : m.Groups[2].Value + "p") + m.Groups[3].Value : null;
            }
            ResPill.Visibility = streaming && _resLabel != null ? Visibility.Visible : Visibility.Collapsed;
            if (_resLabel != null) ResText.Text = _resLabel;
            StatsText.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
            if (streaming) StatsText.Text = string.Format("{0,2:0} fps {1,4:0.0} Mbps", _camera.Fps, _camera.Mbps);
            if (!_blackedOut || BlackoutOverlay.Visibility != Visibility.Visible) UpdateTiles(streaming); // nobody sees them under the black overlay
            // the pulse only runs while someone can see it
            bool pulse = streaming && !_blackedOut;
            if (pulse != _dotPulsing)
            {
                _dotPulsing = pulse;
                if (pulse) DotPulse.Begin(); else { DotPulse.Stop(); StatusDot.Opacity = 1; }
            }
        }

        private static readonly Windows.UI.Xaml.Media.SolidColorBrush GreenFill = Brush(0x39, 0xFF, 0x14), AmberFill = Brush(0xE8, 0xD0, 0x40),
            RedFill = Brush(0xFF, 0x55, 0x55), OffFill = Brush(0x16, 0x30, 0x1E);

        /// <summary>A status badge: 0 = good (green), 1 = caution (amber), 2 = bad (red).</summary>
        private static void SetBadge(Border b, Windows.UI.Xaml.Shapes.Ellipse dot, TextBlock text, string label, int level)
        {
            text.Text = label;
            var fg = level == 2 ? RedFill : level == 1 ? AmberFill : GreenFill;
            text.Foreground = fg; dot.Fill = fg;
            b.BorderBrush = level == 2 ? Brush(0x7D, 0x2E, 0x2E) : level == 1 ? Brush(0x7D, 0x6E, 0x2E) : Brush(0x2E, 0x7D, 0x46);
            b.Background = level == 2 ? Brush(0x2A, 0x10, 0x10) : level == 1 ? Brush(0x2A, 0x26, 0x10) : Brush(0x0F, 0x2A, 0x17);
        }

        /// <summary>A meter bar in a tile: the two star columns split the width by percentage.</summary>
        private static void SetBar(Grid bar, Border fill, double pct, Windows.UI.Xaml.Media.SolidColorBrush color)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            bar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            fill.Background = color;
        }

        /// <summary>The four graphic tiles of the status card: battery, Wi-Fi, engine and encoder latency.</summary>
        private void UpdateTiles(bool streaming)
        {
            try
            {
                var r = _battery;
                int? pct = Percent(r);
                BattText.Text = pct.HasValue ? pct + "% " + (r.Status == Windows.System.Power.BatteryStatus.Charging ? "chg" : r.Status.ToString().ToLowerInvariant()) : "n/a";
                int p = pct ?? 0;
                SetBar(BattBar, BattBarFill, p, BatteryFalling || p < 20 ? RedFill : p < 40 ? AmberFill : GreenFill);

                int bars = WifiBars();
                WifiText.Text = bars >= 0 ? bars + " of 4" : "n/a";
                var on = bars < 2 ? AmberFill : GreenFill;
                Wifi1.Fill = bars >= 1 ? on : OffFill; Wifi2.Fill = bars >= 2 ? on : OffFill;
                Wifi3.Fill = bars >= 3 ? on : OffFill; Wifi4.Fill = bars >= 4 ? on : OffFill;

                bool safe = _camera != null && !_camera.UseNativeSink;
                SetBadge(EngBadge, EngBadgeDot, EngBadgeText, _camera == null ? "n/a" : safe ? "Safe mode" : "Fast", safe || !streaming ? 1 : 0);
                var heat = HeatBadge();
                SetBadge(TempBadge, TempBadgeDot, TempBadgeText, heat.Item1, heat.Item2);

                double cp = _power.SysCpuPct ?? 0, ca = _power.ProcCpuPct ?? 0;
                CpuPhoneText.Text = _power.SysCpuPct.HasValue ? Math.Round(cp) + "%" : "n/a";
                CpuAppText.Text = _power.ProcCpuPct.HasValue ? Math.Round(ca) + "%" : "n/a";
                SetBar(CpuPhoneBar, CpuPhoneBarFill, cp, cp > 70 ? AmberFill : GreenFill);
                SetBar(CpuAppBar, CpuAppBarFill, ca, ca > 50 ? AmberFill : GreenFill);
                double mb = AppMemory() / 1048576.0, lim = Math.Max(1, AppMemoryLimit() / 1048576.0), mp = 100 * mb / lim;
                MemText.Text = Math.Round(mb) + " MB";
                SetBar(MemBar, MemBarFill, mp, mp > 80 ? RedFill : mp > 60 ? AmberFill : GreenFill);

                bool charging = IsCharging(r);
                double? w = !charging && _power.DrawWatts.HasValue ? _power.DrawWatts : _power.NetWatts.HasValue ? (double?)(charging ? _power.NetWatts.Value : Math.Abs(_power.NetWatts.Value)) : null;
                PowerLabel.Text = charging ? "INTO BATTERY" : "BATTERY DRAW";
                PowerValText.Text = w.HasValue ? (charging && w.Value >= 0 ? "+" : "") + w.Value.ToString("0.0") + " W" : "measuring";
                SetBar(PowerBar, PowerBarFill, w.HasValue ? 100 * Math.Abs(w.Value) / 15 : 0,
                    charging ? (w < 0 ? RedFill : w < 1 ? AmberFill : GreenFill) : (w > 8 ? RedFill : AmberFill));
                LoginNotice.Visibility = _server.Auth.IsDefault ? Visibility.Visible : Visibility.Collapsed;
                ChargerNoticeBorder.Visibility = _chargerNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_chargerNotice.Length > 0) ChargerNoticeText.Text = _chargerNotice;

                long lat = streaming && _camera != null ? _camera.LastLatencyMs : -1;
                LatText.Text = lat >= 0 ? lat + " ms" : "n/a";
                SetBar(LatBar, LatBarFill, lat >= 0 ? lat / 3.0 : 0, lat > 250 ? RedFill : lat > 150 ? AmberFill : GreenFill);
            }
            catch { }
        }

        /// <summary>If the low-latency engine stops delivering frames, restart; after two stalls, use the MP4 engine.</summary>
        private async void CheckStall()
        {
            if (_busy || _camera == null || !_camera.IsStreaming || _camera.PathName != "low-latency") return;
            if (_camera.SecondsSinceLastFrame < 4) return;
            if ((DateTime.Now - _lastStall).TotalSeconds > 120) _nativeStalls = 0; // stalls far apart don't add up
            _lastStall = DateTime.Now;
            _nativeStalls++;
            Log("Low-latency engine stalled (no frames for 4 s) - restart #" + _nativeStalls);
            _restarts++;
            _lastRestartReason = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " encoder stalled";
            if (_nativeStalls >= 2)
            {
                Log("Switching to the standard (MP4) engine until the app is reopened");
                _camera.UseNativeSink = false;
            }
            await ToggleStreamingAsync();
            await ToggleStreamingAsync();
        }

        // ------------------------------------------------------------------ app lifecycle

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                _statsTimer.Stop();
                if (_camera != null) await _camera.ShutdownAsync();
                _camera = null;
                ClearStartFlag(); // a clean suspend is not a crash
                MarkLaunchHealthy();
                _server.Stop();
                _logServer.Stop();
                StopUi();
                _blackedOut = false;
                BlackoutOverlay.Visibility = Visibility.Collapsed;
            }
            finally { FlushLog(); deferral.Complete(); }
        }

        private async void OnResuming(object sender, object e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await StartCameraAsync());
        }

        // ------------------------------------------------------------------ login

        private void UpdateLoginUi()
        {
            if (LoginStatusText == null) return;
            bool on = _server.Auth.Enabled;
            LoginStatusText.Text = _server.Auth.IsDefault
                ? "Default login in use (admin / admin) - please change the password"
                : "Login required (user \"" + _server.Auth.UserName + "\")";
            if (on && string.IsNullOrEmpty(UserBox.Text)) UserBox.Text = _server.Auth.UserName;
        }

        private void SaveLoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(PassBox.Password) && PassBox.Password.Length < DigestAuth.MinPasswordLength) { Log("Password not changed: it needs at least " + DigestAuth.MinPasswordLength + " characters"); return; }
                _server.Auth.Save(UserBox.Text, PassBox.Password);
                PassBox.Password = "";
                Log(_server.Auth.IsDefault ? "Login reset to the default (admin / admin)" : "Login set for user \"" + _server.Auth.UserName + "\" (stream and web page)");
            }
            catch (Exception ex) { Log("Saving login failed: " + ex.Message); }
            UpdateLoginUi();
        }

        // ------------------------------------------------------------------ health: /status, power warnings, /config over USB

        private BatteryReport TryBattery()
        {
            try { return Battery.AggregateBattery.GetReport(); } catch { return null; }
        }

        private static int? Percent(BatteryReport r)
        {
            if (r == null || r.FullChargeCapacityInMilliwattHours == null || r.RemainingCapacityInMilliwattHours == null) return null;
            int full = r.FullChargeCapacityInMilliwattHours.Value;
            return full > 0 ? (int?)(r.RemainingCapacityInMilliwattHours.Value * 100 / full) : null;
        }

        /// <summary>A dead battery looks the same as a dead stream from the NVR's side, so warn early.</summary>
        // ------------------------------------------------------------------ focus / exposure / white balance

        private bool _applyingTuning;

        private bool _tuningPending, _tuningForce;

        /// <summary>
        /// Applies the camera settings. Changes that arrive while one is being applied are queued, not dropped,
        /// and run one after another (never two at once). force = resend everything (camera just (re)started).
        /// </summary>
        private async Task ApplyTuningAsync(bool force = false)
        {
            if (_camera?.Capture == null) return;
            if (force) _tuningForce = true;
            if (_applyingTuning) { _tuningPending = true; return; }
            _applyingTuning = true;
            _settings.Values["TuningInProgress"] = true; // cleared when the camera accepted the settings; see the crash guard at startup
            try
            {
                do
                {
                    _tuningPending = false;
                    bool f = _tuningForce; _tuningForce = false;
                    await _tuning.ApplyAsync(_camera.Capture, f);
                } while (_tuningPending);
                TuningStateText.Text = _tuning.State;
                _settings.Values["TuningInProgress"] = false;
            }
            catch (Exception ex) { Log("Camera tuning: " + ex.Message); }
            finally { _applyingTuning = false; }
        }

        // ------------------------------------------------------------------ camera power saving

        // ---- Flashlight (the camera's torch LED). Off whenever the stream stops or restarts; never remembered as "on".
        private bool _torchWanted;
        private bool _loadingTorchUi = true; // true until the first LoadTorchUi: XAML startup events must not overwrite the saved values
        private DateTime _torchOffAt = DateTime.MaxValue;
        private const int TorchSeconds = 30; // the LED heats the phone fast: a short look in the dark, never a lamp

        private void LoadTorchUi()
        {
            _loadingTorchUi = true;
            try
            {
                bool streaming = _camera != null && _camera.IsStreaming;
                bool ok = _tuning.TorchSupported && streaming && !_protect;
                TorchToggle.IsOn = _torchWanted;
                TorchToggle.IsEnabled = ok || _torchWanted;
                TorchNote.Text = _protect ? "Locked by heat protection." : _camera == null || !_tuning.TorchSupported && streaming ? (_camera == null ? "" : "This camera has no flashlight control.")
                    : !streaming ? "Start the stream to use the flashlight."
                    : "Switches off by itself after 30 seconds.";
            }
            finally { _loadingTorchUi = false; }
        }

        private void ApplyTorch()
        {
            try
            {
                var cap = _camera?.Capture;
                if (cap == null || !_tuning.TorchSupported) return;
                var t = cap.VideoDeviceController.TorchControl;
                bool want = _torchWanted && _camera.IsStreaming;
                if (t.Enabled != want) t.Enabled = want;
            }
            catch (Exception ex)
            {
                Log("Flashlight: " + ex.Message.Trim());
                _torchWanted = false;
                LoadTorchUi();
            }
        }

        private void SetTorch(bool on)
        {
            if (on && _protect) { Log("Flashlight unavailable: heat protection is on"); on = false; }
            else if (on && (!_tuning.TorchSupported || _camera == null || !_camera.IsStreaming))
            {
                Log("Flashlight: start the stream first (or this camera has no flashlight control)");
                on = false;
            }
            bool was = _torchWanted;
            _torchWanted = on;
            _torchOffAt = on ? DateTime.Now.AddSeconds(TorchSeconds) : DateTime.MaxValue;
            ApplyTorch();
            LoadTorchUi();
            if (on != was)
                Log("Flashlight " + (_torchWanted ? "on, off automatically after " + TorchSeconds + " s" : "off"));
        }

        private void TorchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingTorchUi) return;
            SetTorch(TorchToggle.IsOn);
        }

        private void SavePowerSettings()
        {
            if (_protect) return; // forced values are temporary
            _settings.Values["SaveOis"] = _tuning.SaveOis;
            _settings.Values["SaveFocus"] = _tuning.SaveFocus;
        }

        private void LoadSaveUi()
        {
            _loadingSaveUi = true;
            try
            {
                SaveAllCheck.IsChecked = _tuning.SaveAll;
                SavePanel.Visibility = _tuning.SaveAll ? Visibility.Collapsed : Visibility.Visible;
                SaveOisCheck.IsChecked = _tuning.SaveOis;
                SaveFocusCheck.IsChecked = _tuning.SaveFocus;
                // Not every camera offers every mode; say so instead of ticking something that does nothing.
                SaveOisCheck.IsEnabled = _tuning.OisSupported || _camera == null;
            }
            finally { _loadingSaveUi = false; }
        }

        private void SaveAll_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSaveUi) return;
            _tuning.SaveAll = SaveAllCheck.IsChecked == true;
            PowerChangedFromUi();
        }

        private void SaveSub_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSaveUi) return;
            bool on = ((CheckBox)sender).IsChecked == true;
            if (sender == SaveOisCheck) _tuning.SaveOis = on;
            else if (sender == SaveFocusCheck) _tuning.SaveFocus = on;
            PowerChangedFromUi(); // all four ticked ticks the main box and hides the list
        }

        private void PowerChangedFromUi()
        {
            SavePowerSettings();
            LoadSaveUi();
            _saveTimer.Stop(); _saveTimer.Start(); // several clicks in a row cause one stream restart
        }

        /// <summary>Stabilization can only change between recordings, so a running stream restarts once.</summary>
        private async Task ApplyPowerChangeAsync()
        {
            if (_camera == null) return;
            if (!_tuning.SaveFocus && _tuning.FocusHeld) _tuning.ReleaseFocusHold();
            else if (_tuning.SaveFocus) _holdNotBefore = DateTime.Now.AddSeconds(15);
            if (PowerSig() != _appliedPowerSig && _camera.IsStreaming && !_busy)
            {
                Log("Camera power-saving options changed - restarting the stream");
                await ToggleStreamingAsync();
                await ToggleStreamingAsync();
            }
            else if (!_camera.IsStreaming && PowerSig() != _appliedPowerSig) ApplyCameraPower();
            await ApplyTuningAsync();
        }

        /// <summary>
        /// On request (probe=devices): lists the sensors this phone offers to apps and any device that looks like a battery,
        /// temperature or thermal reading. Windows exposes no battery temperature through the normal app APIs.
        /// </summary>
        private async Task<string> ProbeDevicesAsync()
        {
            try
            {
                var have = new List<string>();
                if (Windows.Devices.Sensors.Accelerometer.GetDefault() != null) have.Add("accelerometer");
                if (Windows.Devices.Sensors.Barometer.GetDefault() != null) have.Add("barometer");
                if (Windows.Devices.Sensors.Altimeter.GetDefault() != null) have.Add("altimeter");
                if (Windows.Devices.Sensors.Gyrometer.GetDefault() != null) have.Add("gyrometer");
                if (Windows.Devices.Sensors.Compass.GetDefault() != null) have.Add("compass");
                if (Windows.Devices.Sensors.LightSensor.GetDefault() != null) have.Add("light sensor");
                string sensors = "Sensors available to apps: " + (have.Count > 0 ? string.Join(", ", have) : "none");
                Log(sensors);
                var all = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync();
                var hits = all.Where(d => System.Text.RegularExpressions.Regex.IsMatch((d.Name ?? "") + " " + d.Id,
                    "temp|therm|battery|sensor|barom|accel", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(d => d.Name + " | " + d.Id).Take(40).ToList();
                string head = "Device scan: " + all.Count + " devices, " + hits.Count + " look like sensors/battery/thermal";
                Log(head);
                foreach (var h in hits) Log("  " + h);
                return sensors + "; " + head + " (details in the log)";
            }
            catch (Exception ex) { Log("Device scan: " + ex.Message); return "Device scan failed: " + ex.Message; }
        }

        private string _appliedPowerSig = "";
        private string PowerSig() => (_tuning.SaveOis ? "1" : "0");

        private void ApplyCameraPower()
        {
            if (_camera?.Capture == null) return;
            _settings.Values["PowerSaveInProgress"] = true; // if the app dies here, the next launch switches these options off
            try { _tuning.ApplyPower(_camera.Capture); }
            catch (Exception ex) { Log("Camera power saving: " + ex.Message); }
            _settings.Values["PowerSaveInProgress"] = false;
            _appliedPowerSig = PowerSig();
        }

        private async Task HoldFocusSafeAsync()
        {
            _settings.Values["TuningInProgress"] = true;
            try
            {
                bool ok = _camera?.Capture != null && await _tuning.HoldFocusAsync(_camera.Capture);
                if (!ok) _holdNotBefore = DateTime.Now.AddMinutes(10); // e.g. too dark to focus: try again later
                else { _holdNotBefore = DateTime.MaxValue; _tuning.Read(_camera.Capture); TuningStateText.Text = _tuning.State; }
            }
            finally { _settings.Values["TuningInProgress"] = false; _holdBusy = false; }
        }

        private void SaveTuning()
        {
            _settings.Values["CamFocusAuto"] = _tuning.FocusAuto;
            _settings.Values["CamExposureAuto"] = _tuning.ExposureAuto;
            _settings.Values["CamWbAuto"] = _tuning.WbAuto;
            _settings.Values["CamFocus"] = _tuning.FocusPercent;
            _settings.Values["CamExposureMs"] = _tuning.ExposureMs;
            _settings.Values["CamWbK"] = (int)_tuning.WhiteBalanceKelvin;
        }

        // Exposure slider is logarithmic (0..100 -> shortest..longest shutter time), since useful values span 1000x.
        private double ExposureMaxForSlider => Math.Max(_tuning.ExposureMinUseful + 0.001, Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs));
        private double SliderToExposure(double v) =>
            _tuning.ExposureMinUseful * Math.Pow(ExposureMaxForSlider / _tuning.ExposureMinUseful, v / 100.0);
        private double ExposureToSlider(double ms) =>
            100.0 * Math.Log(Math.Max(ms, _tuning.ExposureMinUseful) / _tuning.ExposureMinUseful) / Math.Log(ExposureMaxForSlider / _tuning.ExposureMinUseful);

        /// <summary>Puts the current settings and the camera's ranges on screen.</summary>
        private void LoadTuningUi()
        {
            _loadingTuningUi = true;
            try
            {
                AutoCameraCheck.IsChecked = _tuning.Auto;
                ManualCameraPanel.Visibility = _tuning.Auto ? Visibility.Collapsed : Visibility.Visible;
                FocusAutoCheck.IsChecked = _tuning.FocusAuto;
                ExposureAutoCheck.IsChecked = _tuning.ExposureAuto;
                WbAutoCheck.IsChecked = _tuning.WbAuto;
                FocusAutoCheck.IsEnabled = _tuning.FocusSupported;
                ExposureAutoCheck.IsEnabled = _tuning.ExposureSupported;
                WbAutoCheck.IsEnabled = _tuning.WhiteBalanceSupported;
                FocusSlider.IsEnabled = _tuning.FocusSupported && !_tuning.FocusAuto;
                ExposureSlider.IsEnabled = _tuning.ExposureSupported && !_tuning.ExposureAuto;
                WbSlider.IsEnabled = _tuning.WhiteBalanceSupported && !_tuning.WbAuto;
                FocusSlider.Value = _tuning.FocusPercent;
                if (_tuning.ExposureSupported) ExposureSlider.Value = Math.Max(0, Math.Min(100, ExposureToSlider(_tuning.ExposureMs)));
                if (_tuning.WhiteBalanceSupported)
                {
                    WbSlider.Minimum = _tuning.WbMin;
                    WbSlider.Maximum = Math.Max(_tuning.WbMin + 1, _tuning.WbMax);
                    WbSlider.Value = Math.Max(_tuning.WbMin, Math.Min(_tuning.WbMax, _tuning.WhiteBalanceKelvin));
                }
                UpdateTuningLabels();
            }
            finally { _loadingTuningUi = false; }
        }

        private void UpdateTuningLabels()
        {
            FocusLabel.Text = !_tuning.FocusSupported ? "Focus: not adjustable"
                : _tuning.FocusAuto ? "Focus: automatic" : "Focus: " + Math.Round(_tuning.FocusPercent) + " (0 near - 100 far)";
            ExposureLabel.Text = !_tuning.ExposureSupported ? "Exposure: not adjustable"
                : _tuning.ExposureAuto ? "Exposure: automatic"
                : string.Format("Exposure: {0:0.##} ms (1/{1:0} s)", _tuning.ExposureMs, 1000.0 / Math.Max(0.001, _tuning.ExposureMs));
            WbLabel.Text = !_tuning.WhiteBalanceSupported ? "White balance: not adjustable"
                : _tuning.WbAuto ? "White balance: automatic" : "White balance: " + _tuning.WhiteBalanceKelvin + " K";
        }

        private void AutoCameraCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingTuningUi) return;
            bool auto = AutoCameraCheck.IsChecked == true;
            // Unticking the main box makes all three manual, starting from what the camera is using right now.
            if (!auto) _tuning.SeedFromCamera(_camera?.Capture, _tuning.FocusAuto, _tuning.ExposureAuto, _tuning.WbAuto);
            _tuning.Auto = auto;
            TuningChangedFromUi();
        }

        private void SubAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingTuningUi) return;
            bool on = ((CheckBox)sender).IsChecked == true;
            if (!on) _tuning.SeedFromCamera(_camera?.Capture, sender == FocusAutoCheck, sender == ExposureAutoCheck, sender == WbAutoCheck);
            if (sender == FocusAutoCheck) _tuning.FocusAuto = on;
            else if (sender == ExposureAutoCheck) _tuning.ExposureAuto = on;
            else if (sender == WbAutoCheck) _tuning.WbAuto = on;
            TuningChangedFromUi(); // all three on Auto ticks the main box and hides the panel
        }

        private async void FocusOnceButton_Click(object sender, RoutedEventArgs e) { TouchPhoneAdjust(); await FocusOnceAsync(); }

        /// <summary>Autofocus once, then hold that focus (manual at the found position).</summary>
        private async Task FocusOnceAsync()
        {
            if (_camera?.Capture == null) return;
            FocusOnceButton.IsEnabled = false;
            _settings.Values["TuningInProgress"] = true;
            try
            {
                await _tuning.FocusOnceAsync(_camera.Capture);
                _settings.Values["TuningInProgress"] = false;
                SaveTuning();
                LoadTuningUi();
                await UpdateDisplayForTuningAsync();
                await ApplyTuningAsync();
            }
            finally { FocusOnceButton.IsEnabled = true; }
        }

        private void TuningChangedFromUi()
        {
            TouchPhoneAdjust();
            LoadTuningUi();
            SaveTuning();
            var _ = UpdateDisplayForTuningAsync();
            _tuningTimer.Stop(); _tuningTimer.Start();
        }

        private void TuningSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingTuningUi || FocusLabel == null) return;
            TouchPhoneAdjust();
            if (sender == FocusSlider) _tuning.FocusPercent = FocusSlider.Value;
            else if (sender == ExposureSlider) _tuning.ExposureMs = SliderToExposure(ExposureSlider.Value);
            else if (sender == WbSlider) _tuning.WhiteBalanceKelvin = (uint)Math.Round(WbSlider.Value / 100.0) * 100;
            UpdateTuningLabels();
            SaveTuning();
            _tuningTimer.Stop(); _tuningTimer.Start(); // apply once the slider stops moving
        }

        // ------------------------------------------------------------------ memory (to spot slow leaks in long runs)

        private ulong _peakMemory;
        private DateTime _lastMemoryLog = DateTime.MinValue;

        private static ulong AppMemory() { try { return Windows.System.MemoryManager.AppMemoryUsage; } catch { return 0; } }
        private static ulong AppMemoryLimit() { try { return Windows.System.MemoryManager.AppMemoryUsageLimit; } catch { return 0; } }

        private static int WifiBars()
        {
            try
            {
                var p = NetworkInformation.GetInternetConnectionProfile();
                if (p == null || !p.IsWlanConnectionProfile) return -1;
                var b = p.GetSignalBars();
                return b.HasValue ? b.Value : -1;
            }
            catch { return -1; }
        }

        /// <summary>Every minute: track peak memory; every 30 minutes (and when it grows past 75% of the limit) log it.</summary>
        private void CheckMemory()
        {
            ulong mem = AppMemory(), limit = AppMemoryLimit();
            if (mem > _peakMemory) _peakMemory = mem;
            bool high = limit > 0 && mem > limit * 3 / 4;
            if ((DateTime.Now - _lastMemoryLog).TotalMinutes >= 30 || (high && (DateTime.Now - _lastMemoryLog).TotalMinutes >= 5))
            {
                _lastMemoryLog = DateTime.Now;
                Log(string.Format("{0}Memory: {1:0.0} MB (peak {2:0.0}, limit {3:0}, managed {4:0.0}), uptime {5:0.0} h",
                    high ? "WARNING " : "", mem / 1048576.0, _peakMemory / 1048576.0, limit / 1048576.0,
                    GC.GetTotalMemory(false) / 1048576.0, (DateTime.Now - _appStarted).TotalHours));
            }
        }

        private void CheckPower()
        {
            var r = TryBattery();
            if (r == null) return;
            int? pct = Percent(r);
            bool discharging = r.Status == Windows.System.Power.BatteryStatus.Discharging;
            // A weak charger (e.g. a PC USB port) can report "Charging" while the battery still runs down.
            // Compare with the level 30 minutes ago.
            if (pct.HasValue)
            {
                _batteryHistory.Enqueue(Tuple.Create(DateTime.Now, pct.Value));
                while (_batteryHistory.Count > 0 && (DateTime.Now - _batteryHistory.Peek().Item1).TotalMinutes > 30) _batteryHistory.Dequeue();
            }
            var oldest = _batteryHistory.Count > 0 ? _batteryHistory.Peek() : null;
            bool fallingWhileCharging = !discharging && oldest != null && pct.HasValue &&
                                        (DateTime.Now - oldest.Item1).TotalMinutes >= 20 && pct.Value <= oldest.Item2 - 2;
            _fallingSlow = fallingWhileCharging; // BatteryFalling itself is judged every second from the power reading (MainPage.Power.cs)
            if ((discharging || fallingWhileCharging || pct < 15) && (DateTime.Now - _lastPowerWarning).TotalMinutes >= 10)
            {
                _lastPowerWarning = DateTime.Now;
                Log("POWER: " + (discharging ? "not charging" : fallingWhileCharging ? ChargeNote() + " - battery still falling" : "battery low") +
                    (pct.HasValue ? " - battery " + pct + "%" : "") + (pct < 20 ? " - LOW BATTERY, use a wall charger" : ""));
            }
        }

        // ------------------------------------------------------------------ status block (phone screen and web page)

        private DateTime _streamBeganAt = DateTime.MinValue;
        private string _heat = "n/a";
        private string _heatDetail = "";
        private bool _heatWarn;

        /// <summary>
        /// Windows 10 Mobile gives apps no temperature reading. The only honest signal is indirect: a phone that is
        /// too hot stops charging (status Idle) although the charger is fine (see UpdateHeatHint).
        /// </summary>
        private int _pausedSecs, _chargingSecs;

        private void UpdateHeatHint()
        {
            // The frame rate does NOT show heat on this phone (it stayed at 25 fps while Windows warned about overheating),
            // so the only sign used is that charging pauses (status Idle) on a charger Windows calls adequate.
            var r = _battery;
            int? pct = Percent(r);
            bool paused = r != null && r.Status == Windows.System.Power.BatteryStatus.Idle && _power.Supply == "Adequate" && (!pct.HasValue || pct.Value < 95);
            bool charging = r != null && r.Status == Windows.System.Power.BatteryStatus.Charging;
            _pausedSecs = paused ? _pausedSecs + 1 : 0;
            _chargingSecs = charging ? _chargingSecs + 1 : 0;
            // 20 s of paused charging = hot (a brief pause is not); back to normal after 30 s of charging again.
            if (!_protect && _pausedSecs >= 20) { var _e = EnterProtectionAsync(); }
            else if (_protect && _chargingSecs >= 30 && (DateTime.Now - _protectSince).TotalMinutes >= 3) { var _x = LeaveProtectionAsync(); }
            else if (_protectRestartOwed && !_protectBusy && !_busy) { var _r = RestartStreamForProtectionAsync(); }
            _heatWarn = paused || _protect;
            // A brief pause is only a caution (amber); 20 s of it is hot (red), as on the Temp badge (HeatBadge).
            _heat = _protect ? "Heat protection on" : _pausedSecs >= 20 ? "Hot (charging paused)" : paused ? "Charging paused" : "Healthy";
            // The usual cause is a power source that can't keep up (a PC USB port, a small adapter): the phone then also
            // draws on its battery, the battery heats, and Windows pauses charging. So the messages point at the charger too.
            _heatDetail = _protect ? "Heat protection: charging paused because the phone is hot - usually the charger can't keep up (a PC USB port or a small adapter; use 2 A or more). Video is 1080p / 6 Mbps and power saving is on until it charges normally again."
                : paused ? "Charging has paused: the phone is probably hot - usually the charger can't keep up (a PC USB port or a small adapter; use 2 A or more)." : "";
        }

        // ---- Heat protection: when charging stalls on a good charger, force every power-saving option on and lock the
        // flashlight; put the person's own settings back once charging resumes. The forced values are never written to the
        // saved settings, so an app restart in between also comes back with the person's own choices.
        private bool _protect, _protectBusy, _protectRestartOwed;
        private bool _pOis, _pFocus, _pBlackoutSetting;
        private string _pFormatKey = "", _pBitrateKey = "";
        private DateTime _protectSince = DateTime.MinValue;
        private const string CoolBitrate = "6"; // Mbps while cooling down

        private async Task EnterProtectionAsync()
        {
            if (_protect || _protectBusy) return;
            _protectBusy = true;
            try
            {
                _pOis = _tuning.SaveOis; _pFocus = _tuning.SaveFocus;
                _pBlackoutSetting = PowerSaveToggle.IsOn;
                _pFormatKey = _currentFormatKey; _pBitrateKey = _currentBitrateKey;
                _protectSince = DateTime.Now;
                _protect = true;
                EndPhoneAdjust();
                Log("HEAT PROTECTION ON: charging has paused on a good charger, so the phone is probably too hot. Forcing the power-saving options on and locking the flashlight.");
                if (_torchWanted) SetTorch(false);
                _tuning.SaveOis = true; _tuning.SaveFocus = true;
                await SetBlackoutAsync(true);
                // 4K was measured to keep the phone hot (GPU near 90 %) while 1080p at 6 Mbps let charging resume within a minute.
                string cooled = ApplyVideoSettings(CoolFormatKey(), CoolBitrateKey());
                LoadSaveUi(); LoadTorchUi(); UpdateProtectUi();
                if (cooled.Length > 0) Log("Heat protection: video lowered (" + cooled + ")");
                await RestartStreamForProtectionAsync();
                _saveTimer.Stop(); _saveTimer.Start(); // stabilization is applied by that restart; this only settles the rest
            }
            catch (Exception ex) { Log("Heat protection: " + ex.Message); }
            finally { _protectBusy = false; }
        }

        private async Task LeaveProtectionAsync()
        {
            if (!_protect || _protectBusy) return;
            _protectBusy = true;
            try
            {
                _protect = false;
                Log("HEAT PROTECTION OFF: charging has resumed. Your own settings are back (the flashlight stays off).");
                _tuning.SaveOis = _pOis; _tuning.SaveFocus = _pFocus;
                SavePowerSettings();
                string back = ApplyVideoSettings(_pFormatKey, _pBitrateKey);
                if (back.Length > 0) Log("Heat protection: video restored (" + back + ")");
                EndPhoneAdjust();
                PowerSaveToggle.IsOn = _pBlackoutSetting; // its handler saves the setting
                await UpdateDisplayForTuningAsync();
                LoadSaveUi(); LoadTorchUi(); UpdateProtectUi();
                await RestartStreamForProtectionAsync();
                _saveTimer.Stop(); _saveTimer.Start();
            }
            catch (Exception ex) { Log("Heat protection: " + ex.Message); }
            finally { _protectBusy = false; }
        }

        /// <summary>1080p at the same frame rate when the stream is bigger than that, otherwise unchanged.</summary>
        private string CoolFormatKey()
        {
            if (!(ResolutionBox.SelectedItem is FormatOption f) || (long)f.Width * f.Height <= 1920L * 1080) return null;
            var c = ResolutionBox.Items.OfType<FormatOption>().Where(o => o.Width == 1920 && o.Height == 1080)
                .OrderBy(o => Math.Abs(o.Fps - f.Fps)).FirstOrDefault();
            return c == null ? null : FormatKey(c);
        }

        /// <summary>6 Mbps (or the current rate when lower).</summary>
        private string CoolBitrateKey()
        {
            uint cur = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0;
            return cur != 0 && cur <= 6000000 ? null : CoolBitrate;
        }

        /// <summary>
        /// One restart picks up the video format and the stabilization setting together. When the stream is busy
        /// starting or stopping, the restart is owed and UpdateHeatHint retries it (it was skipped for good before, so
        /// the stream could stay at 4K while heat protection was on).
        /// </summary>
        private async Task RestartStreamForProtectionAsync()
        {
            if (_camera == null || !_camera.IsStreaming) { _protectRestartOwed = false; return; } // the next start uses the new settings
            if (_busy) { if (!_protectRestartOwed) Log("Heat protection: stream busy, restarting it shortly"); _protectRestartOwed = true; return; }
            _protectRestartOwed = false;
            Log("Heat protection: restarting the stream once");
            await ToggleStreamingAsync();
            await ToggleStreamingAsync();
        }

        /// <summary>Greys out what the person cannot change while heat protection is on.</summary>
        private void UpdateProtectUi()
        {
            bool free = !_protect;
            TorchAlert.Visibility = PowerAlert.Visibility = _protect ? Visibility.Visible : Visibility.Collapsed;
            SaveAllCheck.IsEnabled = free;
            SaveOisCheck.IsEnabled = free && (_tuning.OisSupported || _camera == null);
            SaveFocusCheck.IsEnabled = free;
            ResolutionBox.IsEnabled = BitrateBox.IsEnabled = HighBwToggle.IsEnabled = free;
            VideoAlert.Visibility = _protect ? Visibility.Visible : Visibility.Collapsed;
            PowerSaveButton.IsEnabled = free;
            PowerSaveToggle.IsEnabled = free;
        }

        private static string BatterySaverText()
        {
            try { return Windows.System.Power.PowerManager.EnergySaverStatus.ToString(); } catch { return "unknown"; }
        }

        /// <summary>Current is flowing into the battery. "Idle" (plugged in, full or paused because hot) is not charging.</summary>
        private static bool IsCharging(Windows.Devices.Power.BatteryReport r) =>
            r != null && r.Status == Windows.System.Power.BatteryStatus.Charging;

        /// <summary>A charger is connected (Charging or Idle); only "Discharging" means the phone runs on its battery.</summary>
        private static bool OnExternalPower(Windows.Devices.Power.BatteryReport r) =>
            r != null && r.Status != Windows.System.Power.BatteryStatus.Discharging;

        private void UpdateStatusText()
        {
            try
            {
                var r = _battery;
                int? pct = Percent(r);
                string bat = pct.HasValue ? pct + "% " : "";
                bat += r == null ? "unknown" : r.Status.ToString().ToLowerInvariant();
                int bars = WifiBars();
                bool low = pct.HasValue && pct.Value < 20 && !OnExternalPower(r);
                var sb = new System.Text.StringBuilder();
                sb.Append("Battery: ").Append(bat);
                if (low) sb.Append("  LOW - use a wall charger");
                else if (BatteryFalling) sb.Append("  (" + ChargeNote() + ")");
                sb.Append("\nWi-Fi: ").Append(bars >= 0 ? bars + " of 4 bars" : "n/a");
                sb.Append("   Memory: ").Append(Math.Round(AppMemory() / 1048576.0)).Append(" MB");
                sb.Append("\nUp: ").Append(FormatUptime(DateTime.Now - _appStarted));
                sb.Append("\nTemperature: ").Append(_heat);
                if (_protect) sb.Append("\nHeat protection on");
                sb.Append("\n").Append(_power.PowerText());
                if (_power.CpuText().Length > 0) sb.Append("\n").Append(_power.CpuText());
                sb.Append("\nBattery Saver: ").Append(BatterySaverText());
                StatusText.Text = sb.ToString();
                // the one full heat message (the charger hint included); the sections only say "Heat protection: ..."
                string alert = _heatWarn ? _heatDetail : ""; // battery and charger state show in the battery tile
                AlertText.Text = alert;
                AlertText.Visibility = alert.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    (low || _heatWarn) ? Windows.UI.Colors.Salmon : Windows.UI.Colors.LightGray);
            }
            catch { }
        }

        private static string FormatUptime(TimeSpan t) => t.TotalHours >= 2 ? Math.Round(t.TotalHours, 1) + " h" : Math.Round(t.TotalMinutes) + " min";

        /// <summary>Why the battery is falling on a charger. "Idle" means Windows has stopped charging, which it does when the battery is hot.</summary>
        private string ChargeNote()
        {
            var r = TryBattery();
            if (_chargerNotice.Length > 0 && _power.NetWatts.HasValue)
                return "charger too weak: " + (-_power.NetWatts.Value).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " W leaves the battery";
            if (_power.Supply == "NotPresent") return "No charger connected";
            if (_power.Supply == "Inadequate") return "Windows reports the charger as inadequate";
            return r != null && r.Status == Windows.System.Power.BatteryStatus.Idle
                ? "charging is paused - the battery is probably too hot; cool the phone"
                : "charger too weak";
        }

        private readonly Queue<Tuple<DateTime, int>> _batteryHistory = new Queue<Tuple<DateTime, int>>();
        private bool BatteryFalling;

        /// <summary>Camera settings for the web page. Runs on a network thread: cached values only.</summary>
        private string GetCameraJson()
        {
            var o = new JsonObject();
            o["mode"] = JsonValue.CreateStringValue(_tuning.Mode);
            o["now"] = JsonValue.CreateStringValue(_tuning.State ?? "");
            o["focusAuto"] = JsonValue.CreateBooleanValue(_tuning.FocusAuto);
            o["exposureAuto"] = JsonValue.CreateBooleanValue(_tuning.ExposureAuto);
            o["wbAuto"] = JsonValue.CreateBooleanValue(_tuning.WbAuto);
            o["focus"] = JsonValue.CreateNumberValue(Math.Round(_tuning.FocusPercent));
            o["exposureMs"] = JsonValue.CreateNumberValue(Math.Round(_tuning.ExposureMs, 3));
            double hi = Math.Max(_tuning.ExposureMinUseful + 0.001, Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs));
            o["expMin"] = JsonValue.CreateNumberValue(_tuning.ExposureMinUseful);
            o["expMax"] = JsonValue.CreateNumberValue(hi);
            // video settings and addresses, for the web page
            var fl = new JsonArray();
            foreach (var k in (_formatList ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)) fl.Add(JsonValue.CreateStringValue(k));
            o["formats"] = fl;
            o["format"] = JsonValue.CreateStringValue(_currentFormatKey ?? "");
            var bl = new JsonArray();
            bl.Add(JsonValue.CreateStringValue("auto"));
            foreach (var m in BitrateMenuMbps()) bl.Add(JsonValue.CreateStringValue(m.ToString()));
            o["highBw"] = JsonValue.CreateBooleanValue(_highBw);
            o["version"] = JsonValue.CreateStringValue(AppVersion);
            o["engine"] = JsonValue.CreateStringValue(_camera == null ? "" : _camera.UseNativeSink ? "Fast" : "Safe mode");
            o["latencyMs"] = JsonValue.CreateNumberValue(_camera != null && _camera.IsStreaming ? _camera.LastLatencyMs : -1);
            o["memoryLimitMB"] = JsonValue.CreateNumberValue(Math.Round(AppMemoryLimit() / 1048576.0));
            o["blackout"] = JsonValue.CreateBooleanValue(_blackedOut);
            o["previewing"] = JsonValue.CreateBooleanValue(_camera != null && _camera.Previewing);
            o["safeMode"] = JsonValue.CreateBooleanValue(_camera != null && !_camera.UseNativeSink);
            o["bitrates"] = bl;
            o["bitrate"] = JsonValue.CreateStringValue(_currentBitrateKey ?? "");
            o["rtspPort"] = JsonValue.CreateNumberValue(RtspPort);
            o["usbPort"] = JsonValue.CreateNumberValue(UsbPort);
            o["streamPath"] = JsonValue.CreateStringValue(StreamPath);
            o["streaming"] = JsonValue.CreateBooleanValue(_camera != null && _camera.IsStreaming);
            // status block
            var cm = _camera;
            var br = TryBattery();
            int? bp = Percent(br);
            o["fps"] = JsonValue.CreateNumberValue(Math.Round(cm?.Fps ?? 0, 1));
            o["mbps"] = JsonValue.CreateNumberValue(Math.Round(cm?.Mbps ?? 0, 1));
            o["viewers"] = JsonValue.CreateNumberValue(_server.ClientCount);
            o["battery"] = bp.HasValue ? JsonValue.CreateNumberValue(bp.Value) : JsonValue.CreateNullValue();
            o["batteryStatus"] = JsonValue.CreateStringValue(br != null ? br.Status.ToString() : "unknown");
            o["charging"] = JsonValue.CreateBooleanValue(IsCharging(br));
            o["onExternalPower"] = JsonValue.CreateBooleanValue(OnExternalPower(br));
            o["batteryFalling"] = JsonValue.CreateBooleanValue(BatteryFalling);
            o["chargerNotice"] = JsonValue.CreateStringValue(_chargerNotice);
            o["batterySaver"] = JsonValue.CreateStringValue(BatterySaverText());
            int wb2 = WifiBars();
            o["wifiBars"] = wb2 >= 0 ? JsonValue.CreateNumberValue(wb2) : JsonValue.CreateNullValue();
            o["memoryMB"] = JsonValue.CreateNumberValue(Math.Round(AppMemory() / 1048576.0));
            o["uptime"] = JsonValue.CreateNumberValue(Math.Round((DateTime.Now - _appStarted).TotalSeconds));
            o["heat"] = JsonValue.CreateStringValue(_heat);
            o["heatDetail"] = JsonValue.CreateStringValue(_heatDetail);
            o["heatWarn"] = JsonValue.CreateBooleanValue(_heatWarn);
            var hb = HeatBadge();
            o["heatBadge"] = JsonValue.CreateStringValue(hb.Item1);
            o["heatLevel"] = JsonValue.CreateNumberValue(hb.Item2);
            o["chargeNote"] = JsonValue.CreateStringValue(BatteryFalling ? ChargeNote() : "");
            o["powerText"] = JsonValue.CreateStringValue(_power.PowerText());
            o["cpuText"] = JsonValue.CreateStringValue(_power.CpuText());
            o["netWatts"] = _power.NetWatts.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.NetWatts.Value, 2)) : JsonValue.CreateNullValue();
            o["drawWatts"] = _power.DrawWatts.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.DrawWatts.Value, 2)) : JsonValue.CreateNullValue();
            o["saveAll"] = JsonValue.CreateBooleanValue(_tuning.SaveAll);
            o["saveOis"] = JsonValue.CreateBooleanValue(_tuning.SaveOis);
            o["saveFocus"] = JsonValue.CreateBooleanValue(_tuning.SaveFocus);
            o["oisSupported"] = JsonValue.CreateBooleanValue(_tuning.OisSupported);
            o["protect"] = JsonValue.CreateBooleanValue(_protect);
            o["torchSupported"] = JsonValue.CreateBooleanValue(_tuning.TorchSupported);
            o["torchOn"] = JsonValue.CreateBooleanValue(_torchWanted);
            o["torchLeftSec"] = JsonValue.CreateNumberValue(_torchWanted ? Math.Max(0, Math.Ceiling((_torchOffAt - DateTime.Now).TotalSeconds)) : 0);
            o["focusHeld"] = JsonValue.CreateBooleanValue(_tuning.FocusHeld);
            o["wb"] = JsonValue.CreateNumberValue(_tuning.WhiteBalanceKelvin);
            o["wbMin"] = JsonValue.CreateNumberValue(_tuning.WbMin > 0 ? _tuning.WbMin : 2000);
            o["wbMax"] = JsonValue.CreateNumberValue(_tuning.WbMax > _tuning.WbMin ? _tuning.WbMax : 8000);
            return o.Stringify();
        }

        private string GetStatusJson()
        {
            var o = new JsonObject();
            var v = Package.Current.Id.Version;
            o["app"] = JsonValue.CreateStringValue("W10MSecurityCam " + v.Major + "." + v.Minor + "." + v.Build);
            o["uptimeSeconds"] = JsonValue.CreateNumberValue(Math.Round((DateTime.Now - _appStarted).TotalSeconds));
            var cam = _camera;
            o["streaming"] = JsonValue.CreateBooleanValue(cam != null && cam.IsStreaming);
            o["engine"] = JsonValue.CreateStringValue(cam?.PathName ?? "");
            o["format"] = JsonValue.CreateStringValue(_currentFormat ?? "");
            o["fps"] = JsonValue.CreateNumberValue(Math.Round(cam?.Fps ?? 0, 1));
            o["mbps"] = JsonValue.CreateNumberValue(Math.Round(cam?.Mbps ?? 0, 2));
            o["keyframeSeconds"] = JsonValue.CreateNumberValue(Math.Round(cam?.KeyframeIntervalSec ?? 0, 1));
            o["sensorToEncodedMs"] = JsonValue.CreateNumberValue(cam?.LastLatencyMs ?? -1);
            o["viewers"] = JsonValue.CreateNumberValue(_server.ClientCount);
            o["loginRequired"] = JsonValue.CreateBooleanValue(_server.Auth.Enabled);
            o["audio"] = JsonValue.CreateStringValue(cam == null ? (_audioOn ? "on" : "off") : (cam.AudioStreaming ? "streaming" : cam.AudioStatus));
            o["safeMode"] = JsonValue.CreateBooleanValue(_safeMode);
            o["restarts"] = JsonValue.CreateNumberValue(_restarts);
            o["lastRestartReason"] = JsonValue.CreateStringValue(_lastRestartReason);
            var r = TryBattery();
            int? pct = Percent(r);
            o["batteryPercent"] = pct.HasValue ? JsonValue.CreateNumberValue(pct.Value) : JsonValue.CreateNullValue();
            o["batteryStatus"] = JsonValue.CreateStringValue(r != null ? r.Status.ToString() : "unknown");
            o["batteryFalling"] = JsonValue.CreateBooleanValue(BatteryFalling);
            o["chargerNotice"] = JsonValue.CreateStringValue(_chargerNotice);
            o["netWatts"] = _power.NetWatts.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.NetWatts.Value, 2)) : JsonValue.CreateNullValue();
            o["drawWatts"] = _power.DrawWatts.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.DrawWatts.Value, 2)) : JsonValue.CreateNullValue();
            o["cpuAppPct"] = _power.ProcCpuPct.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.ProcCpuPct.Value)) : JsonValue.CreateNullValue();
            o["cpuPhonePct"] = _power.SysCpuPct.HasValue ? JsonValue.CreateNumberValue(Math.Round(_power.SysCpuPct.Value)) : JsonValue.CreateNullValue();
            o["bitrateSetting"] = JsonValue.CreateStringValue(_currentBitrate ?? "");
            o["cameraMode"] = JsonValue.CreateStringValue(_tuning.Mode);
            o["camera"] = JsonValue.CreateStringValue(_tuning.State ?? "");
            ulong mem = AppMemory();
            if (mem > _peakMemory) _peakMemory = mem;
            o["memoryMB"] = JsonValue.CreateNumberValue(Math.Round(mem / 1048576.0, 1));
            o["peakMemoryMB"] = JsonValue.CreateNumberValue(Math.Round(_peakMemory / 1048576.0, 1));
            o["memoryLimitMB"] = JsonValue.CreateNumberValue(Math.Round(AppMemoryLimit() / 1048576.0, 1));
            o["managedMemoryMB"] = JsonValue.CreateNumberValue(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
            int bars = WifiBars();
            o["wifiSignalBars"] = bars >= 0 ? JsonValue.CreateNumberValue(bars) : JsonValue.CreateNullValue();
            o["time"] = JsonValue.CreateStringValue(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            return o.Stringify();
        }

        private string GetConfigText()
        {
            return "login=" + (_server.Auth.Enabled ? "on (user " + _server.Auth.UserName + ")" : "off") + "\n" +
                   "basic=" + (_server.Auth.AllowBasic ? "on" : "off") + "\n" +
                   ExtraConfigText() +
                   "\nChange with POST /config, form fields: user, password (empty = reset to admin/admin), basic=on|off" + ExtraConfigHelp() + "\n";
        }

        /// <summary>Settings over the USB cable (tools\Set-Camera-Config.cmd). Runs on a network thread.</summary>
        private async Task<string> ApplyConfigAsync(IDictionary<string, string> form)
        {
            var done = new List<string>();
            if (_protect)
            {
                bool had = false;
                foreach (var k in new[] { "powersave", "savepower", "saveois", "savefocus", "torch", "format", "bitrate", "highbw" }) had |= form.Remove(k);
                if (had) done.Add("heat protection is on: power-saving options, video format/bitrate and the flashlight cannot be changed until charging resumes");
            }
            if (form.TryGetValue("password", out var newPassword) && newPassword.Length > 0 && newPassword.Length < DigestAuth.MinPasswordLength)
                done.Add("login not changed: the password needs at least " + DigestAuth.MinPasswordLength + " characters");
            else if (form.ContainsKey("password"))
            {
                string user = form.TryGetValue("user", out var u) ? u : _server.Auth.UserName;
                _server.Auth.Save(user, form["password"]);
                done.Add(_server.Auth.IsDefault ? "login reset to the default (admin / admin)" : "login set for " + _server.Auth.UserName);
            }
            if (form.TryGetValue("basic", out var basic))
            {
                _server.Auth.AllowBasic = IsOn(basic);
                _settings.Values["AllowBasic"] = _server.Auth.AllowBasic;
                done.Add("basic " + (_server.Auth.AllowBasic ? "on" : "off"));
            }
            await ApplyExtraConfigAsync(form, done);
            string summary = done.Count > 0 ? "Changed: " + string.Join(", ", done) : "Nothing changed";
            Log("Config over USB: " + summary);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdateLoginUi);
            return summary;
        }

        private static bool IsOn(string s) => s == "1" || s.Equals("on", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase);

        private string ExtraConfigText() =>
            // Runs on a network thread: only cached strings here, never the on-screen controls.
            "format=" + (_currentFormat ?? (_settings.Values["Format"] as string ?? "")) + "\n" +
            "bitrate=" + (_currentBitrate ?? "") + "\n" +
            "highbw=" + (_highBw ? "on" : "off") + " (bitrates above 24 Mbps need it; use 5 GHz Wi-Fi or USB-C)\n" +
            "  formats: " + _formatList + "\n" +
            "camera=" + _tuning.Mode + " (now: " + _tuning.State + ")\n" +
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "  focus={0} (0 near - 100 far), exposure={1} (ms, max {2:0.#}), wb={3} (K, {4}-{5})\n",
                _tuning.FocusAuto ? "auto" : Math.Round(_tuning.FocusPercent).ToString(),
                _tuning.ExposureAuto ? "auto" : _tuning.ExposureMs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs),
                _tuning.WbAuto ? "auto" : _tuning.WhiteBalanceKelvin.ToString(), _tuning.WbMin, _tuning.WbMax) +
            "audio=" + (_audioOn ? "on" : "off") + (_camera != null ? " (" + (_camera.AudioStreaming ? "streaming" : _camera.AudioStatus) + ")" : "") + "\n" +
            "udp=" + (_server.AllowUdp ? "on" : "off") + "\n" +
            "snapshot=" + (_snapshotsOn ? "on" : "off") +
            (_camera != null ? (_camera.PhotoWhileRecordingSupported ? "" : " (this camera can't take photos while recording)") : "") + "\n";

        private string ExtraConfigHelp() => ", stream=start|stop|restart, engine=normal, probe=devices|power, powersave=on|off, savepower=on|off, saveois|savefocus=on|off, torch=on|off, focus=once, highbw=on|off, camera=auto|manual, focus=auto|manual|0-100, exposure=auto|manual|MS, wb=auto|manual|KELVIN, format=WIDTHxHEIGHT@FPS, bitrate=MBPS|auto, audio=on|off, udp=on|off, snapshot=on|off";

        private static string FormatKey(FormatOption f) => f.Width + "x" + f.Height + "@" + Math.Round(f.Fps);

        /// <summary>
        /// format=3840x2160@30 and/or bitrate=12 (Mbps, or "auto"). Changing them while streaming restarts the stream,
        /// as the on-screen boxes do. Runs on the UI thread. Returns what changed, or an error.
        /// </summary>
        private string ApplyVideoSettings(string format, string bitrate)
        {
            var notes = new List<string>();
            _applyingSettings = true;
            try
            {
                if (format != null)
                {
                    var f = ResolutionBox.Items.OfType<FormatOption>()
                        .FirstOrDefault(o => FormatKey(o).Equals(format.Trim().Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    if (f == null) notes.Add("format " + format + " not offered by this camera");
                    else if (ResolutionBox.SelectedItem != f) { ResolutionBox.SelectedItem = f; notes.Add("format " + FormatKey(f)); }
                }
                if (bitrate != null)
                {
                    uint bps;
                    double mbps;
                    if (bitrate.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) bps = 0;
                    else if (double.TryParse(bitrate.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mbps)
                             && mbps >= 0.5 && mbps <= (_highBw ? 80 : NormalMaxMbps)) bps = (uint)Math.Round(mbps * 1e6);
                    else if (mbps > NormalMaxMbps && mbps <= 80 && !_highBw)
                    { notes.Add("bitrate " + bitrate + " not allowed: above " + NormalMaxMbps + " Mbps needs high bandwidth mode (highbw=on)"); bps = uint.MaxValue; }
                    else { notes.Add("bitrate " + bitrate + " not understood (Mbps 0.5-" + (_highBw ? 80 : NormalMaxMbps) + ", or auto)"); bps = uint.MaxValue; }
                    if (bps != uint.MaxValue)
                    {
                        var opt = BitrateBox.Items.OfType<BitrateOption>().FirstOrDefault(o => o.Bps == bps);
                        if (opt == null)
                        {
                            opt = new BitrateOption { Bps = bps };
                            int at = 1;
                            while (at < BitrateBox.Items.Count && ((BitrateOption)BitrateBox.Items[at]).Bps < bps) at++;
                            BitrateBox.Items.Insert(at, opt);
                        }
                        if (BitrateBox.SelectedItem != opt) { BitrateBox.SelectedItem = opt; notes.Add("bitrate " + opt); }
                    }
                }
            }
            finally { _applyingSettings = false; SyncVideoKeys(); }
            return string.Join(", ", notes);
        }

        private bool _applyingSettings;

        // ------------------------------------------------------------------ high bandwidth mode

        private bool _highBw;
        private const uint NormalMaxMbps = 24;
        private static readonly uint[] NormalBitrates = { 1, 2, 3, 4, 6, 8, 12, 16, 20, 24 };
        private static readonly uint[] HighBitrates = { 30, 40, 50, 60 };

        private IEnumerable<uint> BitrateMenuMbps() => _highBw ? NormalBitrates.Concat(HighBitrates) : NormalBitrates;

        /// <summary>Fills the bitrate box for the current mode, keeping the selection when it is still allowed (otherwise Auto).</summary>
        private void RebuildBitrateList()
        {
            uint cur = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 4000000;
            if (!_highBw && cur > NormalMaxMbps * 1000000u) cur = 0;
            bool was = _applyingSettings;
            _applyingSettings = true; // not a user change
            try
            {
                BitrateBox.Items.Clear();
                BitrateBox.Items.Add(new BitrateOption { Bps = 0 });
                foreach (var m in BitrateMenuMbps()) BitrateBox.Items.Add(new BitrateOption { Bps = m * 1000000 });
                ApplyVideoSettings(null, cur == 0 ? "auto" : (cur / 1e6).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { _applyingSettings = was; }
        }

        private async void HighBwToggle_Toggled(object sender, RoutedEventArgs e) => await SetHighBwAsync(HighBwToggle.IsOn);

        /// <summary>Turns high bandwidth mode on or off. Turning it off while a higher bitrate is in use drops back to Auto and restarts the stream.</summary>
        private async Task<string> SetHighBwAsync(bool on)
        {
            if (_highBw == on) return "";
            _highBw = on;
            _settings.Values["HighBandwidth"] = on;
            HighBwToggle.IsOn = on;
            uint before = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0;
            RebuildBitrateList();
            uint after = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0;
            Log("High bandwidth mode " + (on ? "on (bitrates above 24 Mbps allowed; needs 5 GHz Wi-Fi or USB-C)" : "off"));
            if (before == after) return "";
            Log("Bitrate set back to Auto (above 24 Mbps needs high bandwidth mode)");
            if (_camera != null && _camera.IsStreaming && !_busy)
            {
                await ToggleStreamingAsync();
                await ToggleStreamingAsync();
            }
            return ", bitrate back to Auto";
        }

        // ------------------------------------------------------------------ microphone audio (optional)

        private bool _audioOn;

        /// <summary>Opening or closing the microphone needs the camera to be set up again.</summary>
        private async Task SetAudioAsync(bool on)
        {
            if (_audioOn == on) return;
            _audioOn = on;
            _settings.Values["Audio"] = on;
            AudioToggle.IsOn = on;
            Log("Microphone audio " + (on ? "on" : "off") + " - restarting the camera");
            await RestartCameraAsync();
        }

        private async Task RestartCameraAsync()
        {
            if (_busy) { Log("Camera busy - audio change applies at the next start"); return; }
            _busy = true;
            _busySince = DateTime.Now;
            try
            {
                _statsTimer.Stop();
                if (_camera != null)
                {
                    try { await _camera.ShutdownAsync(); } catch (Exception ex) { Log("Camera shutdown: " + ex.Message); }
                }
                _camera = null;
                ClearStartFlag();
                StopUi();
                _blackedOut = false;
                BlackoutOverlay.Visibility = Visibility.Collapsed;
            }
            finally { _busy = false; }
            await StartCameraAsync(); // starts streaming again if auto-start is on
        }

        private async void AudioToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioOn == AudioToggle.IsOn) return;
            await SetAudioAsync(AudioToggle.IsOn);
        }

        // ------------------------------------------------------------------ snapshots (optional, off by default)

        private volatile bool _snapshotsOn;
        private byte[] _lastSnapshot;
        private DateTime _lastSnapshotAt = DateTime.MinValue;
        private readonly System.Threading.SemaphoreSlim _snapshotLock = new System.Threading.SemaphoreSlim(1);

        /// <summary>/snapshot.jpg: a still taken while recording, at most 1280 px wide; reused for 2 s so polling NVRs can't overload the camera.</summary>
        private async Task<byte[]> GetSnapshotAsync()
        {
            var cam = _camera;
            if (!_snapshotsOn || cam == null || !cam.IsStreaming) return null;
            await _snapshotLock.WaitAsync();
            try
            {
                if (_lastSnapshot != null && (DateTime.Now - _lastSnapshotAt).TotalSeconds < 2) return _lastSnapshot;
                var jpg = await cam.CaptureJpegAsync(1280);
                if (jpg != null) { _lastSnapshot = jpg; _lastSnapshotAt = DateTime.Now; }
                return jpg;
            }
            finally { _snapshotLock.Release(); }
        }

        private void SnapshotToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_snapshotsOn == SnapshotToggle.IsOn) return;
            _snapshotsOn = SnapshotToggle.IsOn;
            _settings.Values["Snapshots"] = _snapshotsOn;
            Log("Snapshots " + (_snapshotsOn ? "on: http://PHONE-IP:" + LogPort + "/snapshot.jpg" : "off"));
        }

        /// <summary>Runs work on the UI thread and completes when it has finished (Dispatcher.RunAsync alone returns at the first await).</summary>
        private Task RunUiAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource<bool>();
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { await work(); tcs.SetResult(true); }
                catch (Exception ex) { Log("UI action failed: " + ex.Message); tcs.SetResult(false); }
            });
            return tcs.Task;
        }

        private async Task ApplyExtraConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool tuningChanged = false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (form.TryGetValue("focus", out var fonce) && fonce.Trim().Equals("once", StringComparison.OrdinalIgnoreCase))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await FocusOnceAsync());
                done.Add("focusing once; the lens then stays where it lands (see /camera/state)");
            }
            if (form.TryGetValue("powersave", out var psv))
            {
                bool on = IsOn(psv);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await SetBlackoutAsync(on);
                });
                done.Add("phone screen blackout " + (on ? "on" : "off") + (on && !(_camera != null && _camera.IsStreaming) ? " (starts with the stream)" : ""));
            }
            if (form.TryGetValue("camera", out var cm))
            {
                bool a = cm.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
                if (!a) _tuning.SeedFromCamera(_camera?.Capture, _tuning.FocusAuto, _tuning.ExposureAuto, _tuning.WbAuto);
                _tuning.Auto = a; tuningChanged = true;
            }
            // Each of these takes "auto" or a value; a value makes just that setting manual.
            if (form.TryGetValue("focus", out var fo))
            {
                if (fo.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.FocusAuto = true; tuningChanged = true; }
                else if (fo.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.FocusAuto) _tuning.SeedFromCamera(_camera?.Capture, true, false, false); _tuning.FocusAuto = false; tuningChanged = true; }
                else if (double.TryParse(fo, System.Globalization.NumberStyles.Float, inv, out var fv))
                { _tuning.FocusPercent = Math.Max(0, Math.Min(100, fv)); _tuning.FocusAuto = false; tuningChanged = true; }
            }
            if (form.TryGetValue("exposure", out var ex))
            {
                if (ex.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.ExposureAuto = true; tuningChanged = true; }
                else if (ex.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.ExposureAuto) _tuning.SeedFromCamera(_camera?.Capture, false, true, false); _tuning.ExposureAuto = false; tuningChanged = true; }
                else if (double.TryParse(ex, System.Globalization.NumberStyles.Float, inv, out var ev))
                { _tuning.ExposureMs = Math.Max(0.01, ev); _tuning.ExposureAuto = false; tuningChanged = true; }
            }
            if (form.TryGetValue("wb", out var wb))
            {
                if (wb.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.WbAuto = true; tuningChanged = true; }
                else if (wb.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.WbAuto) _tuning.SeedFromCamera(_camera?.Capture, false, false, true); _tuning.WbAuto = false; tuningChanged = true; }
                else if (uint.TryParse(wb, out var wv)) { _tuning.WhiteBalanceKelvin = wv; _tuning.WbAuto = false; tuningChanged = true; }
            }
            if (tuningChanged)
            {
                SaveTuning();
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { LoadTuningUi(); await UpdateDisplayForTuningAsync(); await ApplyTuningAsync(); });
                done.Add(string.Format(inv, "camera {0} (focus {1}, exposure {2}, wb {3})", _tuning.Mode,
                    _tuning.FocusAuto ? "auto" : Math.Round(_tuning.FocusPercent).ToString(inv),
                    _tuning.ExposureAuto ? "auto" : _tuning.ExposureMs.ToString("0.## ms", inv),
                    _tuning.WbAuto ? "auto" : _tuning.WhiteBalanceKelvin + " K"));
            }
            if (form.TryGetValue("savepower", out var spv) || form.TryGetValue("saveois", out var _o) || form.TryGetValue("savefocus", out var _fc))
            {
                if (form.TryGetValue("savepower", out spv)) _tuning.SaveAll = IsOn(spv);
                if (form.TryGetValue("saveois", out var v1)) _tuning.SaveOis = IsOn(v1);
                if (form.TryGetValue("savefocus", out var v4)) _tuning.SaveFocus = IsOn(v4);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => PowerChangedFromUi());
                done.Add(string.Format("camera power saving: stabilization {0}, hold focus {1} (a running stream restarts once for stabilization)",
                    _tuning.SaveOis ? "off" : "on", _tuning.SaveFocus ? "on" : "off"));
            }
            if (form.ContainsKey("torch"))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (form.TryGetValue("torch", out var tov)) SetTorch(IsOn(tov));
                    LoadTorchUi();
                });
                done.Add("flashlight " + (_torchWanted ? "on" : "off") + (_tuning.TorchSupported ? "" : " (this camera has no flashlight control)"));
            }
            if (form.TryGetValue("probe", out var pr) && pr.Trim().Equals("devices", StringComparison.OrdinalIgnoreCase))
                done.Add(await ProbeDevicesAsync());
            if (form.TryGetValue("probe", out var pr2) && pr2.Trim().Equals("power", StringComparison.OrdinalIgnoreCase))
            { string d = PowerMonitor.Dump(); Log(d); done.Add(d); }
            if (form.TryGetValue("highbw", out var hbv))
            {
                bool on = IsOn(hbv);
                string note = "";
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { note = await SetHighBwAsync(on); });
                done.Add("high bandwidth mode " + (on ? "on" : "off") + note);
            }
            if (form.TryGetValue("engine", out var eng) && eng.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                await RunUiAsync(async () =>
                {
                    if (_camera == null) return;
                    bool was = _camera.IsStreaming;
                    _camera.UseNativeSink = true;
                    _safeMode = false;
                    _lastRestartReason = "";
                    Log("Normal (low-latency) engine restored from the web page");
                    if (was) { await ToggleStreamingAsync(); await ToggleStreamingAsync(); }
                });
                done.Add("normal engine restored");
            }
            form.TryGetValue("format", out var fmt);
            form.TryGetValue("bitrate", out var br);
            if (fmt != null || br != null)
            {
                string result = "";
                await RunUiAsync(async () =>
                {
                    result = ApplyVideoSettings(fmt, br);
                    bool ok = result.Length > 0 && !result.Contains("not ");
                    // Asking for a format means "show me this": start the stream if it's stopped, restart it if it's running.
                    if (_camera != null && (ok || (fmt != null && !_camera.IsStreaming && !result.Contains("not "))))
                    {
                        if (!_camera.UseNativeSink && ResolutionBox.SelectedItem is FormatOption sel && (long)sel.Width * sel.Height > 1920L * 1080)
                            result += (result.Length > 0 ? ", " : "") + "SAFE MODE engine is limited to 1080p - use 'Return to normal mode' for 4K";
                        bool running = _camera.IsStreaming;
                        Log("Video settings changed from the network - " + (running ? "restarting" : "starting") + " the stream");
                        if (running) await ToggleStreamingAsync();
                        await ToggleStreamingAsync();
                        result += (result.Length > 0 ? ", " : "") + (running ? "stream restarting" : "stream starting");
                    }
                });
                if (result.Length > 0) done.Add(result);
            }
            if (form.TryGetValue("stream", out var sact))
            {
                string a = sact.Trim().ToLowerInvariant();
                await RunUiAsync(async () =>
                {
                    bool on = _camera != null && _camera.IsStreaming;
                    if (a == "start" && !on) await ToggleStreamingAsync();
                    else if (a == "stop" && on) await ToggleStreamingAsync();
                    else if (a == "restart") { if (on) await ToggleStreamingAsync(); await ToggleStreamingAsync(); }
                });
                done.Add("stream " + a);
            }
            if (form.TryGetValue("udp", out var udp))
            {
                _server.AllowUdp = IsOn(udp);
                _settings.Values["AllowUdp"] = _server.AllowUdp;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UdpToggle.IsOn = _server.AllowUdp);
                done.Add("udp " + (_server.AllowUdp ? "on" : "off"));
            }
            if (form.TryGetValue("audio", out var audio))
            {
                bool on = IsOn(audio);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await SetAudioAsync(on));
                done.Add("audio " + (on ? "on (camera restarting; the phone may ask for microphone access)" : "off"));
            }
            if (form.TryGetValue("snapshot", out var snap))
            {
                _snapshotsOn = IsOn(snap);
                _settings.Values["Snapshots"] = _snapshotsOn;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => SnapshotToggle.IsOn = _snapshotsOn);
                done.Add("snapshot " + (_snapshotsOn ? "on" : "off"));
            }
        }

        private void UdpToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_server.AllowUdp == UdpToggle.IsOn) return;
            _server.AllowUdp = UdpToggle.IsOn;
            _settings.Values["AllowUdp"] = UdpToggle.IsOn;
            Log("RTP over UDP " + (UdpToggle.IsOn ? "allowed" : "off (TCP only)"));
        }

        // ------------------------------------------------------------------ logging

        private readonly LinkedList<string> _screenLog = new LinkedList<string>(); // under _fullLog, like the two below
        private readonly List<string> _fullLog = new List<string>();
        private List<string> _logPending = new List<string>(); // lines not in the log file yet
        private bool _screenLogDirty;
        private int _screenLogQueued;
        private readonly object _logFileLock = new object();
        private System.Threading.Timer _logTimer;
        private static readonly string LogFile = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "log.txt");
        private static readonly string PrevLogFile = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "log-previous.txt");
        private string _previousSession = "";
        private int _linesSinceSizeCheck;

        /// <summary>Text served at http://PHONE-IP:5001 and to "GET" requests on the USB port.</summary>
        private string GetFullLog()
        {
            string stats = "";
            var cam = _camera;
            if (cam != null)
                stats = string.Format("Streaming: {0}  fps: {1:0.0}  Mbps: {2:0.00}  keyframe every: {3:0.0}s  viewers: {4}  engine: {5}  login: {6}\n\n",
                    cam.IsStreaming, cam.Fps, cam.Mbps, cam.KeyframeIntervalSec, _server.ClientCount, cam.PathName, _server.Auth.Enabled ? "on" : "off");
            lock (_fullLog) return stats + string.Join("\n", _fullLog) +
                (_previousSession.Length > 0 ? "\n\n--- previous session (last lines) ---\n" + _previousSession : "");
        }

        /// <summary>Keep the last session's log (it survives a crash) and start a fresh one.</summary>
        private void RotateLogFile()
        {
            try
            {
                if (System.IO.File.Exists(LogFile))
                {
                    if (System.IO.File.Exists(PrevLogFile)) System.IO.File.Delete(PrevLogFile);
                    System.IO.File.Move(LogFile, PrevLogFile);
                }
                if (System.IO.File.Exists(PrevLogFile))
                {
                    var lines = System.IO.File.ReadAllLines(PrevLogFile);
                    _previousSession = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 120)));
                }
            }
            catch (Exception ex) { _previousSession = "(could not read previous log: " + ex.Message + ")"; }
        }

        /// <summary>
        /// Called from any thread (the encoder's frame thread and the servers too), so it only queues the line:
        /// the writer timer puts it in the log file and on the screen within a second.
        /// </summary>
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine("[W10MSecurityCam] " + message);
            var now = DateTime.Now;
            string line = now.ToString("HH:mm:ss.fff") + " " + message;
            lock (_fullLog)
            {
                _fullLog.Add(line);
                if (_fullLog.Count > 1000) _fullLog.RemoveRange(0, 200);
                _logPending.Add(line);
                _screenLog.AddFirst(now.ToString("HH:mm:ss") + " " + message);
                if (_screenLog.Count > 25) _screenLog.RemoveLast();
                _screenLogDirty = true;
            }
        }

        /// <summary>Once a second on a pool thread: the queued lines go to the file, then the on-screen log is refreshed.</summary>
        private void StartLogWriter()
        {
            _logTimer = new System.Threading.Timer(_ =>
            {
                FlushLog();
                bool dirty;
                lock (_fullLog) dirty = _screenLogDirty;
                if (dirty && System.Threading.Interlocked.Exchange(ref _screenLogQueued, 1) == 0)
                { var __ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, ShowScreenLog); }
            }, null, 1000, 1000);
        }

        /// <summary>Appends the queued lines to the log file: from the writer timer, on suspend and after a crash.</summary>
        private void FlushLog()
        {
            lock (_logFileLock) // taken first so two flushes keep the lines in order
            {
                List<string> batch;
                lock (_fullLog)
                {
                    if (_logPending.Count == 0) return;
                    batch = _logPending;
                    _logPending = new List<string>();
                }
                try
                {
                    // Keep the on-phone log file small during long sessions: start over above ~2 MB.
                    _linesSinceSizeCheck += batch.Count;
                    if (_linesSinceSizeCheck >= 500)
                    {
                        _linesSinceSizeCheck = 0;
                        if (System.IO.File.Exists(LogFile) && new System.IO.FileInfo(LogFile).Length > 2 * 1024 * 1024)
                            System.IO.File.Delete(LogFile);
                    }
                    System.IO.File.AppendAllText(LogFile, string.Join("\r\n", batch) + "\r\n");
                }
                catch { }
            }
        }

        private void ShowScreenLog()
        {
            System.Threading.Volatile.Write(ref _screenLogQueued, 0);
            if (_blackedOut && BlackoutOverlay.Visibility == Visibility.Visible) return; // nobody sees it: shown when the screen wakes
            string text;
            lock (_fullLog)
            {
                if (!_screenLogDirty) return;
                _screenLogDirty = false;
                text = string.Join("\n", _screenLog);
            }
            LogText.Text = text;
        }
    }
}
