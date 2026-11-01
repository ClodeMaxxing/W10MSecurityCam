# Changelog

Release 1.0 is the first version considered complete for this fork. Build numbers 1.0-1.9 before it were the
development steps; they are kept in the history table for reference.

## Release 1.3 (build 1.9.34.0)

**Preview and black screen, one rule set in all three apps** (`MainPage.Display.cs`, kept in step by Sync-Core):
- **Preview on the phone** is a switch under *Camera*, **off by default**: no picture on the phone unless you ask for
  it. On: the picture while stopped, and while streaming unless the screen is blacked out.
- **Black screen while streaming** also stops the preview. New under *Power*: **Keep the preview running behind the
  black screen** (off by default: it costs CPU and heat), so a tap on the black screen shows the picture at once;
  heat protection forces it off and puts it back afterwards.
- Adjusting the camera on the phone shows the picture for 60 s as before; **a tap on the dark preview area** does the
  same as *Show camera picture*. A preview that disagrees with the black screen is fixed with one restart (automatic
  ones at most once a minute), and nothing else touches the screen or the preview any more, so the screen can no
  longer go black with the preview still running.
- The manual focus / exposure / white balance sliders are always on screen, greyed while automatic, and enable at once.
- The web page and `/config` gain `preview=on|off` and `keeppreview=on|off`.

Feature parity with W10MWebCam and W10MDashcam: what one app of the family did better now exists in all three where it
applies. The stream, the login and the RTSP transport are unchanged.

**What you notice**
- **The battery tile turns red when the battery is draining although the phone says "Charging"** (a PC USB port with
  4K), judged every second from the power reading: Charging and the 5-minute average below −0.3 W. Before, the level
  had to drop 2 % over 20 minutes. The amber **charger-insufficient notice** with the watts is new under the status card,
  on the web page and in `/status` (`chargerNotice`); the web page's battery bar turns red too.
- **Temp badge in three levels:** *Paused* (amber) the moment charging pauses, *Hot* (red) after 20 s, *Protect* (red)
  while heat protection is on. Before, a one-second pause flashed the badge red. The heat texts now read as in the
  other apps ("Heat protection: ..." and the usual cause, a charger that can't keep up).
- **A chime and a vibration** when heat protection sets in, and a short one when charging resumes.
- **Service pills under the start button:** RTSP over Wi-Fi (address and viewers), RTSP over USB, and the web page,
  green while streaming and grey when stopped; a tap opens *Network and login*. The button says *Start RTSP stream*.
- **Web page, log page and stats page switches** under *Network and login*: the web page is on by default (it is the way
  to set the camera up without a cable), the log page (`/log`) and the new **stats page** (`/stats`, the last 12 hours
  of frame rate, bitrate, latency, viewers, battery, power, CPU, memory, Wi-Fi and heat as graphs, a sample every 30 s
  while on) are off until switched on, on the phone or on the settings page. The settings page is now served at `/` as
  well; the log moved to `/log`. A page that is off answers 403.
- **View only without the login:** the settings page, `/camera/state`, `/status`, the log and the stats can be read on
  the home network without logging in; changes, the snapshot and `/login` need the login (the browser asks). The page
  shows a *View only* notice with a *Log in* button.
- **Changing the login needs the current password** (new *Current password* box, `current=` over USB), as in W10MDashcam.
- The phone's **addresses follow network changes** (a new DHCP lease, another Wi-Fi network).
- **Return to normal mode** button on the phone while the app is in safe mode (the MP4 engine after a crash or two
  stalls); before, only the web page could do that.
- The camera and power-saving options are toggle switches, as in the other apps.

**Under the hood**
- `/status` also carries W10MWebCam's field names (`uptime`, `battery`, `charging`, `onExternalPower`, `latencyMs`,
  `wifiBars`, `cpuPhone`, `cpuApp`, `heat`, `heatBadge`, `heatLevel`, `protect`, `alert`, `blackout`, `previewing`,
  `torchOn`, `webOn`, `logOn`, `statsOn`, ...); the older names stay. `charging` means current flows in (Charging), the
  new `onExternalPower` means a charger is connected (Charging or Idle).
- Changes (`/camera/set`, `/config`) are read from the request body only, never from the URL.
- The web page reloads itself after an app update.
- Three previous sessions' logs are kept (`log-previous.txt`, `-2`, `-3`), each headed CRASHED, closed normally or
  ended abruptly; a clean close writes "App suspended" so the difference can be told.
- `tools\Soak-Monitor.ps1` is the same script in all three repositories (reads `/status` without a login, collects the
  phone log every 5 minutes while the log page is on); `tools\Make-Release.ps1` packs the release zip and SHA256SUMS.
- New partials `MainPage.Power.cs` and `MainPage.Servers.cs`, as in W10MDashcam; the project references the Windows
  Mobile Extensions for the vibration API.

## Release 1.2 (build 1.9.31.0)

**What you notice**
- **New passwords need at least 8 characters** (8-64), on the phone, on the settings page and through `/config`. A shorter password set before keeps working until you change it.
- **Wrong logins are slowed down:** after 5 wrong logins from one address within a minute, that address is refused for 30 seconds (the web pages answer `429 Too Many Requests`, the stream answers 401). A right login clears the count.
- The login challenge is renewed every 5 minutes. Browsers, VLC and NVRs take the new one by themselves; nobody is asked for the password again.
- An RTSP connection that has not started playing is closed when no complete request arrives for 30 seconds, so a silent or half-open connection no longer holds one of the 8 places. The last of the 8 only takes the PC over USB, so Wi-Fi clients get at most 7 and the USB port stays usable when Wi-Fi is full.
- The web port (5001) answers at most 16 connections at once; more get `503` "Busy, try again". A request has to arrive complete within 10 seconds, and a request with a body over 64 KB is refused.
- The pages can no longer be shown inside a frame on another site. An error answer (500) only says "Internal error, see the app log"; the details go to the app log.

**Fixes**
- **Heat protection:** when the stream was busy starting or stopping, the restart that lowers the video to 1080p at 6 Mbps was skipped, so the stream could stay at 4K while heat protection was on. It is now retried by the once-a-second heat check.
- Switching focus to manual starts from the last sensible lens position: the 950's autofocus sometimes reports focus 0 (the near end stop) while the picture is sharp, which made the manual picture jump out of focus (fix from W10MWebCam).
- When the encoder sink drops a video backlog to wait for the next keyframe, the queued audio frames are kept.
- The jobs that run every 2, 10 or 15 seconds (status text, power sample, camera readout) were skipped whenever a one-second tick came late; they now count the ticks.
- If the Windows password vault fails while storing the default login, the app still starts and the log says so; nobody gets in until a login is saved (before, a missing password let everyone in).
- Failed web requests are logged, at most one line per 30 seconds.

**Under the hood**
- Login: random nonces, kept for 5 minutes; `qop="auth"` is offered and its nonce count must go up, while clients without qop (VLC, many NVRs) keep working. Each login answer must be for the address requested (for RTSP also an address under it, since VLC and many NVRs sign every request with the stream's base address). The login is checked once per request.
- RTSP: no list query or lock for every frame, and one audio payload per frame for all viewers.
- The fps and Mbps counters are read and written safely between the encoder thread and the screen (64-bit values on ARM).
- The battery is read once a second instead of three times; the resolution label is worked out only when the format changes; the status card keeps one brush per colour.
- The encoder sink hands each frame to the app without an extra copy, and reports handler errors again after a stream restart.
- The log: a line is only queued when it is logged; the file and the screen are updated once a second (also on suspend and after a crash). `/log` is unchanged.
- The old `CamAuto` and `Bitrate` settings keys are converted once and removed. Unused code removed.

**Tools**
- `tools\Crash-Dumps.ps1`, with `tools\Enable-Crash-Dumps.cmd` and `tools\Get-Crash-Dump.cmd` (from W10MWebCam): switch Windows crash-dump collection on for the installed W10MSecurityCam through Device Portal (it is per installed version, so switch it on again after an update), list the dumps and download them to `tools\dump\`. A Device Portal pairing made by a sister app's tools is reused.
- `tools\Sync-Core.ps1` compares the source files the three apps share (`-Check`, the default) and copies one app's copy over the others (`-Fix -From APP`). It needs all three repositories on the PC.
- `tools\Build-And-Deploy.cmd` finds Visual Studio 2019's MSBuild with vswhere (the 2019 Community path if vswhere is missing) and logs which one it used. It and `tools\Install-Only.cmd` now report the right exit code after the pairing retry.
- `tools\test-rtsp.ps1` takes the renewed login challenge during long runs.

## Release 1.1.1 (build 1.9.30.0)
- **Security fix:** a single crafted RTSP request with a negative `Content-Length` made the app handle the same request again forever until it ran out of memory. It needed no login (OPTIONS) and reached the always-on RTSP ports (8554, and 8020 over USB). Such a request now closes the connection, and a client that never reads its replies is disconnected after 64.
- **The login is needed over the USB cable too.** Any program or web page on the PC the phone is plugged into can reach the USB port, so "no login over USB" let a web page change settings or reset the login. The settings page, `/status`, the log and `/config` now ask for the login on every port.
- Changes (`/config`, `/camera/set`) also need the `X-Requested-With: W10MSecurityCam` header and the phone's address in the `Host` header, over USB too.
- `tools\set-camera-config.ps1` and `tools\get-phone-log-usb.ps1` take `-User` / `-Password` (or the `W10M_USER` / `W10M_PASSWORD` environment variables) and ask otherwise. The config script no longer says an empty password removes the login: it resets it to the default.

## Release 1.1 (build 1.9.29.0)
- **Crash fix:** the app could close about a second after the first 4K frame on a fresh launch (crash dump: fast-fail abort from an exception escaping the native sink's worker thread). The native sink and the frame handler now catch and log such errors ("handler threw an exception - ignored") instead of ending the app.
- **Camera power saving:** one "Power-saving camera features" box (stabilization off for a fixed mount, hold focus once it has settled); untick it to choose each option. HDR and noise-reduction controls were removed because this camera does not offer them.
- **Power monitor:** the status block shows net watts into or out of the battery (5-minute average, restarted whenever the charger is plugged in or out or charging pauses, so a reading never mixes two states), the phone's draw while it is on battery or charging is paused (from the capacity change; the driver's own rate is stale on this phone and is not used), the charger state Windows reports, and CPU use (phone and app). A "Power:" line is logged every minute for comparing settings. `probe=power` dumps the raw battery and CPU readings.
- **Flashlight:** on/off toggle on the phone and the web page, only while streaming, switches off by itself after 30 seconds (this phone cannot dim it). It heats the phone quickly.
- **Heat protection:** if charging pauses on a good charger for 20 s the phone is probably too hot. The app then forces the power-saving options on (screen blackout, preview off, stabilization off, held focus), locks the flashlight and shows a red warning on the phone and the web page. While it is active the video is also lowered to 1080p at 6 Mbps (4K kept the phone hot; 1080p let charging resume within about 30 s). Your own settings, including the video, come back after 30 s of charging again and at least 3 minutes (the flashlight stays off). The temperature line reads Healthy or Unstable on this basis. The frame rate is no longer used: it stayed at 25 fps while Windows warned of overheating.
- **Blackout by default:** while streaming the phone screen is black and the preview is off, whether the camera is automatic or manual and whether settings were changed on the phone or on the web page. Changing settings on the web page never wakes the screen. Touching a camera control on the phone (or pressing Wake) lifts the blackout and shows the picture for 60 s after the last touch; the stream restarts once to start the preview and once more to stop it afterwards (at most once a minute). The preview is never switched on or off mid-stream: doing so stalled the 4K pipeline and could crash the app (this was the cause of crashes on "Save power now"). "Save power now" / Wake only change the overlay and brightness.
- **Simpler phone screen:** the main screen holds Start/Stop, the status card, warnings, Show camera picture (60 s) and Black out the screen now; Video, Camera (with the flashlight), Power, Network and login, and Details and log fold away. Both buttons restart the stream once to start or end the picture. Measured before choosing: keeping the preview running behind the black screen doubled the app's CPU use (12% to 24%) and raised the battery draw, so the preview stays off while streaming.
- **Green theme:** black background with soft green accents on the phone and the web page (section bars, buttons, switches, sliders); the version sits small and bright green at the top right of both.
- **Status card:** a pulsing green dot (red in heat protection), the state, resolution and viewer pills and large fps / Mbps figures on the phone; the web page adds tiles for battery, temperature, CPU, Wi-Fi, memory and uptime. The pulse stops while the phone screen is black.
- **Graphic status:** battery bar, Wi-Fi signal bars and meters for temperature, CPU, memory, engine and encoder latency on the web page; battery, Wi-Fi, engine and latency tiles on the phone. Web notes moved into hover/tap info icons; High BW is a toggle beside the resolution and bitrate; the stream addresses open in a pop-up.
- Status tiles match on both screens: battery, Wi-Fi, temperature (Healthy / Unstable badge), phone and app CPU, memory, engine (Fast / Safe mode badge), encoder latency and power (watts with a 0-15 W bar). The charger warning line is gone (the battery tile turns red instead); the default-login warning is a themed notice. The flashlight moved into the Camera section.
- The stream start/stop chirp is Windows' own recording sound; it follows the phone's Ringer + notifications volume (noted under Power).
- Cleaner status lines (CPU, Temperature); web page sections fold with an arrow at the right (remembered per browser).
- Longer settle time before auto-starting 4K, a delay before the first camera-control apply, the device scan is on demand (`probe=devices`).
- `tools/Soak-Monitor.ps1` records every status value on the PC for long runs and keeps an HTML report with graphs and a summary (availability, restarts, heat time, fps, memory trend).
- `tools/Monitor-Portal.ps1` logs whole-phone CPU, GPU and battery from Device Portal once a minute.

## Release 1.0 (build 1.9.1.0)
- MIT licence added.
- The login is always on, default admin / admin, for the stream and the web page. It can be changed on the settings page (Login section) or on the phone; an empty password resets it.
- `/status`, the log and snapshots now need the login from the network too (USB is exempt).
- Web changes need an `X-Requested-With` header, so another website cannot make your browser change settings.
- Startup self-check: two failed launches in a row reset saved settings (login kept).
- Status block (battery, charging, Wi-Fi, memory, uptime, Battery Saver state) and an indirect heat hint; low-battery and weak-charger warnings.
- One-click power save on the phone, matching the web button; blackout/preview rules in one place.
- Phone screen and web page split into Status / Video / Camera / Power.
- Web page shows the right bitrate and resolution right after a change.

## Development history
| Build | Content |
|---|---|
| 1.0 | Fork of LumiaCam, new identity |
| 1.1 | RTSP server, RTP over TCP |
| 1.2 | Digest login, RTCP SR, `/status`, `/config` over USB, defaults 1080p / 4 Mbps, power warnings |
| 1.3 | Optional RTP over UDP and snapshots, weak-charger warning |
| 1.4 | AAC microphone audio |
| 1.5 | `format=` / `bitrate=` over USB, memory and Wi-Fi in `/status` |
| 1.6 | Focus / exposure / white balance with per-setting Auto, Focus once and lock |
| 1.7 | Web settings page, high bandwidth mode |
| 1.8 | Start/Stop stream, safe-mode visibility, USB address block |
| 1.9.13-1.9.29 | Heat protection lowers video to 1080p/6 Mbps; one display switch; preview only changes at stream (re)start; "Show camera picture (60 s)"; folded phone layout; empty first sample ignored; correct no-charger note |
| 1.9.12 | Native-sink crash guard, camera power saving, power monitor, flashlight, heat protection, folding web sections |
| 1.9.1 | Always-on login (admin / admin default), password change on the web page, login required everywhere |
| 1.9 | Self-check, status, heat hint, phone power-save button, layout sections |
