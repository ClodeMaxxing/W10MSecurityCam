using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.Devices;

namespace W10MSecurityCam.Streaming
{
    /// <summary>
    /// Focus, exposure and white balance. Automatic mode lets the camera adjust continuously (exposure follows the
    /// light from day to night); manual mode holds the values from the sliders. Ranges come from the camera itself.
    /// </summary>
    internal sealed class CameraTuning
    {
        public Action<string> Log = _ => { };

        // Settings (manual values are percentages / milliseconds / Kelvin; kept even while in automatic mode)
        public bool FocusAuto = true, ExposureAuto = true, WbAuto = true;
        /// <summary>All three automatic. Setting it switches all three.</summary>
        public bool Auto
        {
            get { return FocusAuto && ExposureAuto && WbAuto; }
            set { FocusAuto = ExposureAuto = WbAuto = value; }
        }
        public string Mode => Auto ? "automatic" : (!FocusAuto && !ExposureAuto && !WbAuto) ? "manual" : "mixed";
        public double FocusPercent = 100;   // 0 = nearest, 100 = farthest
        public double ExposureMs = 20;
        public uint WhiteBalanceKelvin = 5000;
        /// <summary>Longest manual shutter time that keeps the frame rate (1000 / fps).</summary>
        public double FrameLimitMs = 1000.0 / 24;

        // What the camera supports (filled by Describe)
        public bool FocusSupported, ExposureSupported, WhiteBalanceSupported;
        public uint FocusMin, FocusMax, WbMin, WbMax;
        public double ExposureMinMs, ExposureMaxMs;

        // Last values read back (for /status)
        public string State = "";

        // ---- camera power saving (less processing and heat; each part can be switched off)
        public bool SaveOis = true, SaveFocus = false; // the automatic focus hold is off by default since 1.3 (Focus once and lock does it by hand)
        public bool SaveAll
        {
            get { return SaveOis && SaveFocus; }
            set { SaveOis = SaveFocus = value; }
        }
        public bool OisSupported;
        public bool TorchSupported, TorchPowerSupported;
        /// <summary>True while the lens is parked where a single autofocus left it (automatic focus stays selected).</summary>
        public bool FocusHeld;
        private OpticalImageStabilizationMode? _origOis;

        public void Describe(MediaCapture cap)
        {
            var vdc = cap.VideoDeviceController;
            var parts = new System.Collections.Generic.List<string>();
            try
            {
                var f = vdc.FocusControl;
                FocusSupported = f.Supported;
                if (FocusSupported)
                {
                    FocusMin = f.Min; FocusMax = f.Max;
                    parts.Add("focus " + f.Min + "-" + f.Max + " (modes " + string.Join("/", f.SupportedFocusModes) +
                              ", ranges " + string.Join("/", f.SupportedFocusRanges) + ")");
                }
                else parts.Add("focus: not adjustable");
            }
            catch (Exception ex) { parts.Add("focus: " + ex.Message); }
            try
            {
                var e = vdc.ExposureControl;
                ExposureSupported = e.Supported;
                if (ExposureSupported)
                {
                    ExposureMinMs = e.Min.TotalMilliseconds; ExposureMaxMs = e.Max.TotalMilliseconds;
                    ExposureStepMs = e.Step.TotalMilliseconds;
                    parts.Add(string.Format("exposure {0:0.###}-{1:0.#} ms (step {2:0.####})", ExposureMinMs, ExposureMaxMs, ExposureStepMs));
                }
                else parts.Add("exposure: not adjustable");
            }
            catch (Exception ex) { parts.Add("exposure: " + ex.Message); }
            try
            {
                var w = vdc.WhiteBalanceControl;
                WhiteBalanceSupported = w.Supported;
                if (WhiteBalanceSupported)
                {
                    WbMin = w.Min; WbMax = w.Max;
                    parts.Add("white balance " + w.Min + "-" + w.Max + " K");
                }
                else parts.Add("white balance: not adjustable");
            }
            catch (Exception ex) { parts.Add("white balance: " + ex.Message); }
            try
            {
                var iso = vdc.IsoSpeedControl;
                if (iso.Supported) parts.Add("ISO " + iso.Min + "-" + iso.Max);
            }
            catch { }
            try
            {
                var c = vdc.OpticalImageStabilizationControl;
                OisSupported = c.Supported;
                if (OisSupported) { if (_origOis == null) _origOis = c.Mode; parts.Add("stabilization " + c.Mode + " (" + string.Join("/", c.SupportedModes) + ")"); }
            }
            catch (Exception ex) { parts.Add("stabilization: " + ex.Message.Trim()); }
            try
            {
                var tc = vdc.TorchControl;
                TorchSupported = tc.Supported; TorchPowerSupported = tc.Supported && tc.PowerSupported;
                parts.Add(TorchSupported ? "flashlight " + (TorchPowerSupported ? "adjustable" : "on/off only") : "flashlight: not offered");
            }
            catch (Exception ex) { TorchSupported = TorchPowerSupported = false; parts.Add("flashlight: " + ex.Message.Trim()); }
            Log("Camera controls: " + string.Join(", ", parts));
        }

        /// <summary>
        /// Sets the power-saving camera feature: optical stabilization off (or back to what the camera had). The Lumia 950 offers
        /// no HDR video or noise reduction control. Call it while no recording is running; the stream then starts with this mode.
        /// </summary>
        public void ApplyPower(MediaCapture cap)
        {
            if (cap == null) return;
            var vdc = cap.VideoDeviceController;
            var done = new System.Collections.Generic.List<string>();
            if (OisSupported) try
            {
                var c = vdc.OpticalImageStabilizationControl;
                if (c.Supported)
                {
                    var want = SaveOis ? OpticalImageStabilizationMode.Off : (_origOis ?? OpticalImageStabilizationMode.Auto);
                    if (c.SupportedModes.Contains(want) && c.Mode != want) c.Mode = want;
                    done.Add("stabilization " + c.Mode);
                }
            }
            catch (Exception ex) { Log("Stabilization: " + ex.Message.Trim()); }
            Log("Camera power saving: " + (done.Count > 0 ? string.Join(", ", done) : "nothing adjustable") +
                (SaveFocus ? ", focus held once settled" : ", continuous focus"));
        }

        /// <summary>
        /// Power saving: run the autofocus once, then park the lens there. Automatic focus stays selected (the lens is simply
        /// not hunting all day). Press Focus once, or restart the stream, to aim again.
        /// </summary>
        public async Task<bool> HoldFocusAsync(MediaCapture cap)
        {
            if (cap == null || !FocusSupported || !FocusAuto) return false;
            try
            {
                // Watch the continuous autofocus for 8 s. Only park the lens when it has clearly settled away from the end stops:
                // a single autofocus run was seen to report "0" (nearest) and would have left the picture blurry.
                var f = cap.VideoDeviceController.FocusControl;
                var samples = new System.Collections.Generic.List<uint>();
                for (int i = 0; i < 8; i++) { await Task.Delay(1000); samples.Add(f.Value); }
                var tail = samples.Skip(3).OrderBy(x => x).ToList();
                double span = Math.Max(1, FocusMax - FocusMin);
                uint lo = tail.First(), hi = tail.Last(), mid = tail[tail.Count / 2];
                if (hi - lo > span * 0.05 || mid <= FocusMin + span * 0.02)
                {
                    Log("Hold focus: not settled yet (lens readings " + string.Join(", ", samples) + ") - will try again later");
                    return false;
                }
                if (f.SupportedFocusModes.Contains(FocusMode.Manual))
                    f.Configure(new FocusSettings { Mode = FocusMode.Manual, Value = mid, DisableDriverFallback = false });
                await f.SetValueAsync(mid);
                FocusHeld = true;
                _aFocusAuto = true;
                Log("Focus held at " + mid + " (power saving: the lens stops hunting; Focus once or a stream restart re-aims it)");
                return true;
            }
            catch (Exception ex) { Log("Hold focus: " + ex.Message.Trim()); return false; }
        }

        /// <summary>Back to continuous autofocus on the next apply.</summary>
        public void ReleaseFocusHold() { FocusHeld = false; _aFocusAuto = null; }

        // What was last sent to the camera, so a change only touches the control that changed
        // (repeated driver calls while recording were causing errors).
        private bool? _aFocusAuto, _aExposureAuto, _aWbAuto;
        private uint _aFocus; private double _aExposure; private uint _aWb;

        /// <summary>Shortest shutter time offered. Below this the picture is simply black.</summary>
        public double ExposureMinUseful => Math.Max(ExposureMinMs, 0.5);
        public double ExposureStepMs;

        /// <summary>Exposure values must be a multiple of the camera's step, or it answers "parameter is incorrect".</summary>
        public double SnapExposureMs(double ms)
        {
            double lo = ExposureMinUseful, hi = Math.Max(lo, Math.Min(ExposureMaxMs, FrameLimitMs));
            ms = Math.Max(lo, Math.Min(hi, ms));
            if (ExposureStepMs > 0)
            {
                ms = ExposureMinMs + Math.Round((ms - ExposureMinMs) / ExposureStepMs) * ExposureStepMs;
                if (ms > hi) ms -= ExposureStepMs;
                if (ms < ExposureMinMs) ms = ExposureMinMs;
            }
            return ms;
        }

        /// <summary>
        /// Applies the current settings. Each control is independent: one failing doesn't stop the others.
        /// force = send everything (after the camera was (re)started); otherwise only what changed.
        /// </summary>
        public async Task ApplyAsync(MediaCapture cap, bool force = false)
        {
            if (cap == null) return;
            var vdc = cap.VideoDeviceController;
            if (force) { _aFocusAuto = _aExposureAuto = _aWbAuto = null; FocusHeld = false; }
            if (!FocusAuto) FocusHeld = false;

            if (FocusSupported)
            {
                uint v = PercentToFocus(FocusPercent);
                if (_aFocusAuto != FocusAuto || (!FocusAuto && _aFocus != v))
                {
                    try
                    {
                        var f = vdc.FocusControl;
                        if (FocusAuto)
                        {
                            // Continuous autofocus, kept out of macro range so it doesn't lock onto the window glass.
                            var mode = f.SupportedFocusModes.Contains(FocusMode.Continuous) ? FocusMode.Continuous : FocusMode.Auto;
                            var settings = new FocusSettings { Mode = mode, WaitForFocus = false, DisableDriverFallback = false };
                            if (f.SupportedFocusRanges.Contains(AutoFocusRange.Normal)) settings.AutoFocusRange = AutoFocusRange.Normal;
                            else if (f.SupportedFocusRanges.Contains(AutoFocusRange.FullRange)) settings.AutoFocusRange = AutoFocusRange.FullRange;
                            f.Configure(settings);
                            await f.FocusAsync();
                        }
                        else
                        {
                            if (f.SupportedFocusModes.Contains(FocusMode.Manual))
                                f.Configure(new FocusSettings { Mode = FocusMode.Manual, Value = v, DisableDriverFallback = false });
                            await f.SetValueAsync(v);
                        }
                        _aFocusAuto = FocusAuto; _aFocus = v;
                    }
                    catch (Exception ex) { Log("Focus: " + ex.Message.Trim()); }
                }
            }

            if (ExposureSupported)
            {
                double ms = SnapExposureMs(ExposureMs);
                if (_aExposureAuto != ExposureAuto || (!ExposureAuto && Math.Abs(_aExposure - ms) > 1e-6))
                {
                    try
                    {
                        var e = vdc.ExposureControl;
                        if (ExposureAuto) await e.SetAutoAsync(true);
                        else
                        {
                            if (_aExposureAuto != false) await e.SetAutoAsync(false);
                            await e.SetValueAsync(TimeSpan.FromTicks((long)Math.Round(ms * 10000)));
                        }
                        _aExposureAuto = ExposureAuto; _aExposure = ms;
                        try
                        {
                            // ISO always follows automatically; in manual mode the camera adds gain for the chosen shutter time.
                            var iso = vdc.IsoSpeedControl;
                            if (iso.Supported) await iso.SetAutoAsync();
                        }
                        catch (Exception ex) { Log("ISO: " + ex.Message.Trim()); }
                    }
                    catch (Exception ex) { Log("Exposure (" + ms.ToString("0.###") + " ms): " + ex.Message.Trim()); }
                }
            }

            if (WhiteBalanceSupported)
            {
                uint k = Math.Max(WbMin, Math.Min(WbMax, WhiteBalanceKelvin));
                if (_aWbAuto != WbAuto || (!WbAuto && _aWb != k))
                {
                    try
                    {
                        var w = vdc.WhiteBalanceControl;
                        if (WbAuto) await w.SetPresetAsync(ColorTemperaturePreset.Auto);
                        else await w.SetValueAsync(k);
                        _aWbAuto = WbAuto; _aWb = k;
                    }
                    catch (Exception ex) { Log("White balance: " + ex.Message.Trim()); }
                }
            }

            Read(cap);
            Log("Camera tuning " + Mode + ": " + State);
        }

        /// <summary>
        /// Copies what the camera is using right now into the manual values, so switching a control
        /// from automatic to manual holds the current picture instead of jumping to an old setting.
        /// </summary>
        public void SeedFromCamera(MediaCapture cap, bool focus, bool exposure, bool wb)
        {
            if (cap == null) return;
            var vdc = cap.VideoDeviceController;
            try
            {
                // The 950's continuous autofocus sometimes reports 0 (the near end stop) while the picture is sharp, so a
                // reading at the end stop is replaced by the last sensible one (switching to manual showed "focus 0" and a
                // blurred picture; fix from W10MWebCam).
                if (focus && FocusSupported && FocusMax > FocusMin)
                {
                    uint v = vdc.FocusControl.Value;
                    if (IsSensibleFocus(v)) FocusPercent = ToPercent(v);
                    else if (LastGoodFocusPercent.HasValue) FocusPercent = LastGoodFocusPercent.Value;
                }
            }
            catch { }
            try
            {
                if (exposure && ExposureSupported)
                {
                    double ms = vdc.ExposureControl.Value.TotalMilliseconds;
                    if (ms > 0) ExposureMs = SnapExposureMs(ms);
                }
            }
            catch { }
            try
            {
                if (wb && WhiteBalanceSupported)
                {
                    uint k = vdc.WhiteBalanceControl.Value; // 0 when the driver doesn't report the automatic value
                    if (k >= WbMin && k <= WbMax && k > 0) WhiteBalanceKelvin = (uint)(Math.Round(k / 100.0) * 100);
                }
            }
            catch { }
        }

        /// <summary>
        /// Runs the autofocus once, then keeps the lens where it landed (switches focus to manual at that value).
        /// For a fixed scene this avoids continuous autofocus hunting. Returns true when it worked.
        /// </summary>
        public async Task<bool> FocusOnceAsync(MediaCapture cap)
        {
            if (cap == null || !FocusSupported) return false;
            try
            {
                var f = cap.VideoDeviceController.FocusControl;
                var mode = f.SupportedFocusModes.Contains(FocusMode.Single) ? FocusMode.Single : FocusMode.Auto;
                var settings = new FocusSettings { Mode = mode, WaitForFocus = true, DisableDriverFallback = false };
                if (f.SupportedFocusRanges.Contains(AutoFocusRange.Normal)) settings.AutoFocusRange = AutoFocusRange.Normal;
                else if (f.SupportedFocusRanges.Contains(AutoFocusRange.FullRange)) settings.AutoFocusRange = AutoFocusRange.FullRange;
                f.Configure(settings);
                await f.FocusAsync();
                if (FocusMax > FocusMin)
                    FocusPercent = Math.Max(0, Math.Min(100, 100.0 * ((double)f.Value - FocusMin) / (FocusMax - FocusMin)));
                FocusAuto = false;
                _aFocusAuto = null; // send the locked value as a manual setting on the next apply
                Log("Focus locked at " + f.Value + " (" + Math.Round(FocusPercent) + " %)");
                return true;
            }
            catch (Exception ex) { Log("Focus once: " + ex.Message.Trim()); return false; }
        }

        /// <summary>Reads back what the camera is using right now (exposure and ISO change by themselves in automatic mode).</summary>
        public void Read(MediaCapture cap)
        {
            if (cap == null) return;
            var vdc = cap.VideoDeviceController;
            var s = new System.Text.StringBuilder();
            try { if (ExposureSupported) s.AppendFormat("exposure {0:0.##} ms{1}", vdc.ExposureControl.Value.TotalMilliseconds, vdc.ExposureControl.Auto ? " (auto)" : ""); } catch { }
            try { var iso = vdc.IsoSpeedControl; if (iso.Supported) s.Append(", ISO ").Append(iso.Value); } catch { }
            try { if (FocusSupported && IsSensibleFocus(vdc.FocusControl.Value)) LastGoodFocusPercent = ToPercent(vdc.FocusControl.Value); } catch { }
            try { if (FocusSupported) s.Append(", focus ").Append(vdc.FocusControl.Value).Append(" (").Append(FocusHeld ? "held" : vdc.FocusControl.Mode.ToString()).Append(")"); } catch { }
            try { if (WhiteBalanceSupported) s.Append(", white balance ").Append(vdc.WhiteBalanceControl.Value).Append(" K (").Append(vdc.WhiteBalanceControl.Preset).Append(")"); } catch { }
            State = s.ToString().TrimStart(',', ' ');
        }

        /// <summary>The last lens position read back that was not at the near end stop (see SeedFromCamera).</summary>
        public double? LastGoodFocusPercent;

        private bool IsSensibleFocus(uint v) => FocusMax > FocusMin && v > FocusMin + (FocusMax - FocusMin) * 0.02;
        private double ToPercent(uint v) => Math.Max(0, Math.Min(100, 100.0 * ((double)v - FocusMin) / (FocusMax - FocusMin)));

        private uint PercentToFocus(double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            return (uint)Math.Round(FocusMin + (FocusMax - FocusMin) * pct / 100.0);
        }
    }
}
