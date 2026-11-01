using System;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Devices.Power;
using Windows.Foundation.Metadata;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace W10MSecurityCam
{
    /// <summary>
    /// Battery and charger judgements shared by the phone screen, the web page and /status (the same rules as in
    /// W10MWebCam and W10MDashcam):
    /// - "charger insufficient": the phone says Charging but the 5-minute average of net watts shows the battery draining
    ///   (a PC USB port with 4K); the notice names the watts;
    /// - "battery falling": on battery, or that notice is up. Without a power reading yet (the first minutes) the slower
    ///   2 % in 20 minutes rule from CheckPower stands in;
    /// - the heat badge: Protect / Hot / Paused / Healthy;
    /// - a chime and a vibration for the moments worth noticing.
    /// </summary>
    public sealed partial class MainPage
    {
        private string _chargerNotice = "";
        private bool _fallingSlow; // the 2 %/20 min rule, from CheckPower (once a minute)
        private MediaPlayer _chime;

        /// <summary>Text of the notice when the battery drains although the phone says it is charging (empty otherwise).</summary>
        private string ChargerNotice()
        {
            var w = _power.NetWatts; // a 5-minute average, restarted when the charging state changes
            var r = _battery; // "Charging" only: a heat pause (Idle) is reported by the heat warning instead
            if (r == null || r.Status != Windows.System.Power.BatteryStatus.Charging || !w.HasValue || w.Value > -0.3) return "";
            return "Charger insufficient: the battery is draining while charging (" + w.Value.ToString("0.0", CultureInfo.InvariantCulture)
                + " W). A PC USB port often can't keep up with 4K; use a 2 A or better charger or a powered hub, or a lower resolution.";
        }

        /// <summary>Once a second (UpdateStats), after the battery report was read: the charger notice and the battery-falling flag.</summary>
        private void UpdatePowerFlags()
        {
            var r = _battery;
            _chargerNotice = ChargerNotice();
            bool discharging = r != null && r.Status == Windows.System.Power.BatteryStatus.Discharging;
            BatteryFalling = discharging || _chargerNotice.Length > 0 || (!_power.NetWatts.HasValue && _fallingSlow);
        }

        /// <summary>Heat as a short badge: label and level (0 good, 1 caution, 2 bad).</summary>
        private Tuple<string, int> HeatBadge()
        {
            if (_protect) return Tuple.Create("Protect", 2);
            if (HotUnprotected) return Tuple.Create("HOT, no protection", 2);
            if (_pausedSecs >= 20) return Tuple.Create("Hot", 2);
            if (_pausedSecs > 0) return Tuple.Create("Paused", 1);
            return Tuple.Create("Healthy", 0);
        }

        /// <summary>A short sound from the app's own files (media volume): ok = all good, else a warning.</summary>
        private void PlayChime(bool ok)
        {
            try
            {
                if (_chime == null) _chime = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.SoundEffects, Volume = 1.0 };
                _chime.Source = MediaSource.CreateFromUri(new Uri(ok ? "ms-appx:///Assets/ClipSaved.wav" : "ms-appx:///Assets/ClipNone.wav"));
                _chime.Play();
            }
            catch (Exception ex) { Log("Chime: " + ex.Message); }
        }

        private static async Task BuzzAsync(int times)
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Phone.Devices.Notification.VibrationDevice")) return;
                var v = Windows.Phone.Devices.Notification.VibrationDevice.GetDefault();
                if (v == null) return;
                for (int i = 0; i < times; i++)
                {
                    v.Vibrate(TimeSpan.FromMilliseconds(times == 1 ? 250 : 120));
                    await Task.Delay(times == 1 ? 250 : 260);
                }
            }
            catch { }
        }
    }
}
