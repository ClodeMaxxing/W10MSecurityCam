using System;
using System.Threading.Tasks;
using Windows.Graphics.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace W10MSecurityCam
{
    /// <summary>
    /// The phone's display rules, the same in W10MSecurityCam, W10MDashcam and W10MWebCam (kept in step by
    /// tools\Sync-Core.ps1). Three switches and one rule set:
    ///  - Preview on the phone (Camera, off by default): the picture while stopped, and while streaming unless the
    ///    screen is blacked out.
    ///  - Screensaver (under the status, on by default): black overlay, brightness 0, and the preview stopped.
    ///  - Keep the preview running behind the black screen (Power, off by default): the preview stays alive under the
    ///    overlay, so a tap shows the picture at once; costs CPU and heat, and heat protection forces it off.
    ///  - Adjusting (Automatic unticked, a slider, Focus once, Preview (60 s), a tap on the preview area): the
    ///    black screen lifts and the picture shows until 60 s after the last touch.
    /// The preview can only start or stop while no recording runs (changing it mid-stream stalls the 4K pipeline), so
    /// while streaming a preview change costs one stream restart; automatic ones at most once a minute, button presses
    /// always. Nothing else touches the overlay or the preview: every timer, switch and tap goes through ApplyDisplayAsync,
    /// and DisplayTick (once a second) repairs a preview that disagrees with the overlay.
    /// Each app provides: _camera (IsStreaming, Previewing, SetPreviewAsync), _busy, _protect, _protectBusy,
    /// ToggleStreamingAsync, Log, _settings, PanelOnScreen (a banner that must stay visible), and the controls
    /// PreviewToggle, PowerSaveToggle, KeepPreviewToggle, BlackoutOverlay, PreviewOffText, ShowPictureButton.
    /// </summary>
    public sealed partial class MainPage
    {
        private const int AdjustSeconds = 60;   // the picture stays this long after the last touch
        private const int WakeSeconds = 15;     // a tap on the black screen shows the status this long

        private bool _phoneAdjusting, _wakeShowing, _blackedOut, _loadingDisplayUi, _previewFixPending;
        private DateTime _lastPreviewRestart = DateTime.MinValue;
        private BrightnessOverride _brightness;
        private DispatcherTimer _adjustTimer, _wakeTimer;
        private bool _pKeepPreview, _pBlackScreen; // the person's own switches while heat protection forces them

        private bool StreamingNow => _camera != null && _camera.IsStreaming;

        /// <summary>The black overlay is wanted now.</summary>
        private bool OverlayWanted => StreamingNow && PowerSaveToggle.IsOn && !_phoneAdjusting && !_wakeShowing && !PanelOnScreen;

        /// <summary>The camera preview is wanted now (decided before every stream start too).</summary>
        private bool PreviewWanted =>
            _phoneAdjusting
            || (PreviewToggle.IsOn && !(StreamingNow && PowerSaveToggle.IsOn))
            || (StreamingNow && PowerSaveToggle.IsOn && KeepPreviewToggle.IsOn && !_protect);

        /// <summary>From the settings load, before the camera starts (the switches' handlers must not act meanwhile).</summary>
        private void LoadDisplaySettings()
        {
            _loadingDisplayUi = true;
            try
            {
                PreviewToggle.IsOn = _settings.Values["Preview"] is bool p && p;
                PowerSaveToggle.IsOn = !(_settings.Values["PowerSave"] is bool b) || b;
                KeepPreviewToggle.IsOn = _settings.Values["KeepPreview"] is bool k && k;
            }
            finally { _loadingDisplayUi = false; }
            if (_adjustTimer == null)
            {
                _adjustTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AdjustSeconds) };
                _adjustTimer.Tick += AdjustTimer_Tick;
                _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(WakeSeconds) };
                _wakeTimer.Tick += WakeTimer_Tick;
            }
        }

        /// <summary>
        /// Brings the overlay and the preview in line with the switches and the moment. The overlay changes at once;
        /// the preview changes directly while stopped and through one stream restart while streaming.
        /// </summary>
        private async Task ApplyDisplayAsync(bool byHand = false)
        {
            try
            {
                bool overlay = OverlayWanted;
                if (overlay != _blackedOut)
                {
                    _blackedOut = overlay;
                    if (_brightness == null) _brightness = BrightnessOverride.GetForCurrentView();
                    if (overlay)
                    {
                        if (_brightness.IsSupported) { _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None); _brightness.StartOverride(); }
                        BlackoutOverlay.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        BlackoutOverlay.Visibility = Visibility.Collapsed;
                        if (_brightness.IsSupported) _brightness.StopOverride();
                    }
                }
                else if (overlay && BlackoutOverlay.Visibility != Visibility.Visible)
                {
                    // the status look (15 s) or a banner is over: back to black
                    BlackoutOverlay.Visibility = Visibility.Visible;
                    if (_brightness != null && _brightness.IsSupported) { _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None); _brightness.StartOverride(); }
                }
                if (_camera != null)
                {
                    if (!StreamingNow) { if (!_busy) await _camera.SetPreviewAsync(PreviewWanted); }
                    else if (_camera.Previewing != PreviewWanted) await RestartForPreviewAsync(PreviewWanted ? "to show the camera picture" : "to stop the preview", byHand);
                }
            }
            catch (Exception ex) { Log("Display: " + ex.GetType().Name + ": " + ex.Message); }
            UpdatePreviewText();
        }

        /// <summary>Once a second (the stats tick): a preview that disagrees with what is wanted is fixed with one restart.</summary>
        private void DisplayTick()
        {
            if (_camera == null || !StreamingNow || _busy || _protectBusy) { if (!_blackedOut) UpdatePreviewText(); return; }
            if (_camera.Previewing != PreviewWanted) { var _ = RestartForPreviewAsync(PreviewWanted ? "to show the camera picture" : "to stop the preview", _previewFixPending); }
            else if (!_blackedOut) UpdatePreviewText();
        }

        /// <summary>The one place that restarts the stream for the preview: automatic restarts at most once a minute, button presses always.</summary>
        private async Task RestartForPreviewAsync(string why, bool byHand)
        {
            if (_camera == null || !StreamingNow) return;
            if (_busy || _protectBusy) { if (byHand) _previewFixPending = true; return; }
            if (!byHand && !_previewFixPending && (DateTime.Now - _lastPreviewRestart).TotalSeconds < 60) return;
            _previewFixPending = false;
            _lastPreviewRestart = DateTime.Now;
            Log("Restarting the stream once " + why + " (the preview can't change during a recording)");
            await ToggleStreamingAsync();
            await ToggleStreamingAsync();
        }

        private void UpdatePreviewText()
        {
            bool previewing = _camera != null && _camera.Previewing;
            PreviewOffText.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            // the one button under the status: it ends whatever shows the picture, or shows it for 60 s
            ShowPictureButton.Content = previewing || _phoneAdjusting ? "Turn preview off" : "Preview (" + AdjustSeconds + " s)";
            if (previewing) return;
            PreviewOffText.Text = _camera == null ? "Camera starting..."
                : StreamingNow && _phoneAdjusting ? "Starting the camera picture (the stream restarts for a moment)..."
                : StreamingNow ? "Streaming - preview off" + (PowerSaveToggle.IsOn ? " (black screen)" : "") + "\nTap to show the picture for " + AdjustSeconds + " s (the stream restarts for a moment)"
                : "Preview off\nTap to show the picture for " + AdjustSeconds + " s";
        }

        // ---- adjusting: the picture until 60 s after the last touch

        /// <summary>A camera control was touched on the phone (or Preview (60 s) / the preview area was tapped).</summary>
        private void TouchPhoneAdjust() { var _ = TouchPhoneAdjustAsync(false); }

        private async Task TouchPhoneAdjustAsync(bool byHand)
        {
            if (_adjustTimer == null) LoadDisplaySettings();
            _adjustTimer.Stop(); _adjustTimer.Start();
            if (_protect) return; // heat protection keeps the screen black
            bool was = _phoneAdjusting;
            _phoneAdjusting = true;
            _wakeShowing = false; _wakeTimer.Stop();
            if (!was) Log(byHand ? "Preview (60 s): the picture stays until " + AdjustSeconds + " s after the last touch" : "Camera controls touched on the phone: showing the picture until " + AdjustSeconds + " s after the last touch");
            if (!was || byHand) await ApplyDisplayAsync(byHand);
        }

        /// <summary>Keeps the timer going while the person is still adjusting (does not start adjusting by itself).</summary>
        private void KeepAdjusting() { if (_phoneAdjusting) { _adjustTimer.Stop(); _adjustTimer.Start(); } }

        private void EndPhoneAdjust() { _phoneAdjusting = false; _adjustTimer?.Stop(); }

        private async void AdjustTimer_Tick(object sender, object e)
        {
            _adjustTimer.Stop();
            if (!_phoneAdjusting) return;
            if (_busy || _protectBusy) { _adjustTimer.Start(); return; } // a start/stop is running: try again a minute later
            _phoneAdjusting = false;
            Log("No camera adjustments for " + AdjustSeconds + " s: ending the picture");
            await ApplyDisplayAsync();
        }

        /// <summary>Preview (60 s) (60 s), or - while a picture shows for any reason - Turn preview off: ends the 60 s and switches both preview switches off.</summary>
        private async void ShowPictureButton_Click(object sender, RoutedEventArgs e)
        {
            bool showing = (_camera != null && _camera.Previewing) || _phoneAdjusting;
            if (!showing) { await TouchPhoneAdjustAsync(true); return; }
            await TurnPreviewOffAsync();
        }

        /// <summary>No picture at all: the 60 s ends and Preview on the phone and Keep the preview running go off (one restart while streaming).</summary>
        private async Task TurnPreviewOffAsync()
        {
            EndPhoneAdjust();
            _loadingDisplayUi = true;
            try
            {
                if (PreviewToggle.IsOn) { PreviewToggle.IsOn = false; _settings.Values["Preview"] = false; }
                if (KeepPreviewToggle.IsOn) { KeepPreviewToggle.IsOn = false; if (!_protect) _settings.Values["KeepPreview"] = false; }
            }
            finally { _loadingDisplayUi = false; }
            Log("Preview off");
            await ApplyDisplayAsync(byHand: true);
        }

        /// <summary>A tap on the dark preview area ("Preview off"): the same as Preview (60 s).</summary>
        private async void PreviewArea_Tapped(object sender, TappedRoutedEventArgs e) => await TouchPhoneAdjustAsync(true);

        // ---- the black screen: a tap shows the status for 15 s; Black out the screen now ends a picture at once

        private async void BlackoutOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _wakeShowing = true;
            _wakeTimer.Stop(); _wakeTimer.Start();
            await ApplyDisplayAsync();
        }

        /// <summary>Any touch on the lit page keeps a woken screen awake and a shown picture showing.</summary>
        private void TouchKeepsDisplay()
        {
            if (_wakeShowing) { _wakeTimer.Stop(); _wakeTimer.Start(); }
            KeepAdjusting();
        }

        private async void WakeTimer_Tick(object sender, object e)
        {
            _wakeTimer.Stop();
            if (!_wakeShowing) return;
            _wakeShowing = false;
            await ApplyDisplayAsync();
        }

        /// <summary>The black-screen switch from the web page or USB config (powersave=on|off).</summary>
        private async Task SetBlackScreenAsync(bool on)
        {
            if (on) EndPhoneAdjust();
            if (PowerSaveToggle.IsOn != on) PowerSaveToggle.IsOn = on; // its handler saves and applies
            else await ApplyDisplayAsync(byHand: true);
        }

        private async void PowerSaveToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingDisplayUi) return;
            if (!_protect) _settings.Values["PowerSave"] = PowerSaveToggle.IsOn; // forced by heat protection: not saved
            if (PowerSaveToggle.IsOn)
            {
                // the screensaver ends a picture at once, and the two switches are exclusive: screensaver on = preview off
                EndPhoneAdjust(); _wakeShowing = false; _wakeTimer?.Stop();
                if (PreviewToggle.IsOn) { _loadingDisplayUi = true; try { PreviewToggle.IsOn = false; } finally { _loadingDisplayUi = false; } _settings.Values["Preview"] = false; }
            }
            Log("Screensaver " + (PowerSaveToggle.IsOn ? "on" : "off"));
            await ApplyDisplayAsync(byHand: true);
        }

        private async void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingDisplayUi) return;
            _settings.Values["Preview"] = PreviewToggle.IsOn;
            Log("Preview on the phone " + (PreviewToggle.IsOn ? "on" : "off"));
            if (!PreviewToggle.IsOn) { await TurnPreviewOffAsync(); return; } // off means no picture at all, now
            // on: the picture must show, so the screensaver goes off (the two switches are exclusive; the person's choice is saved)
            if (PowerSaveToggle.IsOn && !_protect) { _loadingDisplayUi = true; try { PowerSaveToggle.IsOn = false; } finally { _loadingDisplayUi = false; } _settings.Values["PowerSave"] = false; }
            await ApplyDisplayAsync(byHand: true);
        }

        private async void KeepPreviewToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingDisplayUi) return;
            if (!_protect) _settings.Values["KeepPreview"] = KeepPreviewToggle.IsOn;
            Log("Preview behind the black screen " + (KeepPreviewToggle.IsOn ? "on (costs CPU and heat; a tap on the black screen shows the picture at once)" : "off"));
            await ApplyDisplayAsync(byHand: true);
        }

        // ---- heat protection: the black screen is forced on and the preview behind it off; the switches come back afterwards

        private void ForceDisplayForProtection()
        {
            _pKeepPreview = KeepPreviewToggle.IsOn; _pBlackScreen = PowerSaveToggle.IsOn;
            EndPhoneAdjust();
            _wakeShowing = false; _wakeTimer?.Stop();
            _loadingDisplayUi = true;
            try { KeepPreviewToggle.IsOn = false; PowerSaveToggle.IsOn = true; }
            finally { _loadingDisplayUi = false; }
            KeepPreviewToggle.IsEnabled = PowerSaveToggle.IsEnabled = false;
        }

        private void RestoreDisplayAfterProtection()
        {
            _loadingDisplayUi = true;
            try { KeepPreviewToggle.IsOn = _pKeepPreview; PowerSaveToggle.IsOn = _pBlackScreen; }
            finally { _loadingDisplayUi = false; }
            KeepPreviewToggle.IsEnabled = PowerSaveToggle.IsEnabled = true;
        }
    }
}
