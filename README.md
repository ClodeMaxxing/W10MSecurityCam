# W10MSecurityCam

A fork of LumiaCam (now [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam)) that turns a Lumia 950 (Windows 10 Mobile) into an always-on security
camera. It serves an RTSP stream that an NVR (Frigate, Blue Iris, Synology Surveillance
Station) or VLC can pull over the home Wi-Fi.

It has its own package identity (`W10MSecurityCam`, CN=W10MSecurityCam), so it installs beside
W10MWebCam (formerly LumiaCam). Only one of the two can use the camera at a time.

## Addresses

| What | Address |
|---|---|
| Stream (Wi-Fi) | `rtsp://PHONE-IP:8554/live` (the app shows the IP) |
| Stream (USB) | `rtsp://127.0.0.1:8020/live` |
| Settings page | `http://PHONE-IP:5001/camera` (over USB: `http://127.0.0.1:8020/camera`) |
| Health | `http://PHONE-IP:5001/status` (JSON: fps, bitrate, viewers, battery, memory, Wi-Fi signal, restarts) |
| Log | `http://PHONE-IP:5001` |
| Snapshot | `http://PHONE-IP:5001/snapshot.jpg` (only when turned on, see below) |

Video is H.264 (track 1); with microphone audio on, there is also an AAC track (48 kHz mono,
about 96 kbps). Both are sent as RTP over the RTSP TCP connection. Choose TCP in the NVR if it
asks; VLC and ffmpeg switch to it by themselves. Quick check:
`ffplay -rtsp_transport tcp rtsp://PHONE-IP:8554/live`.

Give the phone a fixed address (a DHCP reservation in the router), so the NVR always finds it.

## Defaults

1080p at 24 fps (the lowest rate of 15 fps or more the camera offers), 4 Mbps. These replace
LumiaCam's settings once, the first time version 1.2 or later starts. You can change them in the app.

**4K:** 4K30 works over the home Wi-Fi: 45-second tests at 8, 12, 16 and 24 Mbps all ran at a full 30 fps with no
lost packets (about 7, 11, 15 and 22 Mbps on the wire). How much bitrate 4K needs depends on the scene; 12-16 Mbps
is a sensible start for a view with some motion. The NVR has to decode 4K continuously, and the phone runs warmer.

The Lumia's encoder sends a keyframe every second whatever the "Keyframe every" setting says
above 1 s. This costs a little bandwidth, nothing else.

## Settings page and camera controls

Open `http://PHONE-IP:5001/camera` in any browser. The page and the phone screen share one look (black with green
accents, version number at the top right) and the same sections. Click a header to fold its section; the web page
remembers the choice in that browser. Explanations sit behind small (i) icons: hover or tap them.

- **Status:** a card with the state (Streaming / Stopped / Heat protection, with a pulsing dot), resolution, uptime and
  viewer count, large fps and Mbps figures, and tiles with graphs: battery bar, Wi-Fi bars, temperature badge (Healthy /
  Unstable), phone and app CPU, app memory, engine badge (Fast / Safe mode), encoder latency, and power (watts into or
  out of the battery on a 0-15 W bar). A tile turns amber or red when it needs attention. An amber notice appears while
  the default login is in use, a red one for heat.
- **Video:** resolution/fps, bitrate and a **High BW** toggle on one line, Start/Stop stream, and **Stream addresses**,
  a pop-up with the Wi-Fi and USB stream addresses, this page over USB and the live log.
  Changing resolution or bitrate starts the stream, or restarts it if it is running.
- **Camera:** Automatic focus, exposure and white balance (one master box, plus a box and slider for each), **Focus once
  and lock**, and the **flashlight** (only while streaming; it switches itself off after 30 seconds because the LED heats
  the phone fast, and it cannot be dimmed on this phone). Leave everything on Auto for a camera that follows the daylight.
- **Power:** one button that blacks out the phone screen, and the **Power-saving camera features** box (stabilization
  off for a fixed mount, hold focus once it has settled; untick the box to choose each). Changing stabilization restarts
  the stream once.

**The phone screen while streaming** is black with the preview off, whatever you change on the web page. The main
screen holds Start/Stop, the status card, **Show camera picture (60 s)**, **Black out the screen now** and any warning;
Video, Camera, Power, Network and login, and Details and log fold away. Tapping the black screen shows the status for
15 seconds with no restart. **Show camera picture** (or touching a camera control) shows the picture until 60 seconds
after your last touch; **Black out the screen now** ends it straight away. The preview can only start or stop while the
stream is stopped (changing it mid-stream stalled the 4K pipeline), so each of these restarts the stream once, a gap of
a second or two for the NVR. Keeping the preview running behind the black screen was measured and rejected: it doubled
the app's CPU use (12% to 24%) and raised the battery draw. The start/stop chirp is Windows' own recording sound; it
follows the phone's Ringer + notifications volume.

Everything on the page needs the login (see Login below). Over the USB cable no login is needed.

**High bandwidth mode** unlocks bitrates above 24 Mbps (up to 60). It needs 5 GHz Wi-Fi or a USB-C connection; at
60 Mbps on 2.4 GHz the stream drops frames. Auto bitrate is capped at 16 Mbps (40 Mbps in high bandwidth mode).

**Temperature and heat protection:** Windows 10 Mobile gives apps no temperature reading, and the frame rate does not
show heat (it stayed at 25 fps while Windows warned of overheating). The app uses the charging state instead: if
charging pauses ("Idle") on a charger Windows reports as adequate, with the battery below 95%, for 20 seconds, the
temperature reads **Unstable** and *heat protection* switches on. It forces the power-saving options on (screen blackout,
preview off, stabilization off, held focus), lowers the video to 1080p at 6 Mbps (restored afterwards), locks the flashlight and shows a red warning on the phone and the web page.
After 30 seconds of charging again your own settings come back; the flashlight stays off but can be used again.
Windows can still stop the app first if it overheats quickly; the app then restarts into heat protection.

**Safe mode:** after a crash or two stalls the app uses the slower MP4 engine, limited to 1080p. The page says so and
offers **Return to normal mode**.

**Self-check:** if the app fails to start twice in a row, it clears its saved settings and starts with defaults (the
login is kept). The log says "SELF-CHECK".

## Login

One login protects everything on the phone from the network: the RTSP stream, the settings page, `/status`, the log and
snapshots. A fresh install uses **admin / admin**; the phone screen and the settings page warn until you change it.

- **Settings page:** open `http://PHONE-IP:5001/camera`, sign in, and use the **Login** section at the bottom (user name,
  new password twice, 4-64 characters). Your browser asks you to sign in again afterwards.
- **On the phone:** tap the screen to wake it, scroll to "Login (stream and web page)", enter a user name and password and
  tap **Save login**. An empty password resets the login to admin / admin (it is never turned off).
- **From the PC over USB:** run `tools\Set-Camera-Login.cmd`. The USB cable needs no login.

NVRs then use `rtsp://USER:PASSWORD@PHONE-IP:8554/live`. The phone uses Digest authentication, which doesn't send the
password itself. Older NVRs that only know Basic authentication can be allowed with `basic=on` (see Settings over USB).
The login is kept in the Windows password vault, so it survives reinstalling the app. Traffic is not encrypted
(plain HTTP/RTSP), so use it on a network you trust.

## Microphone audio

Turn on **Microphone audio in the stream (AAC)** in the app, or send `audio=on` (see Settings over USB).
The camera restarts and, the first time, the phone asks whether the app may use the microphone.
Audio only works with the normal low-latency engine; in safe mode (MP4 engine) the stream is video only.
`/status` shows `"audio":"streaming"` when it is working.

## Optional extras (off by default)

| Setting | What it does | Turn on |
|---|---|---|
| Allow RTP over UDP | Lets clients that ask for UDP get it. On Wi-Fi, TCP is more reliable, which is why it's off. | App toggle, or `udp=on` |
| Snapshots | `/snapshot.jpg` returns a 1280-px still taken while the video keeps recording. **Not possible on the Lumia 950** (its camera can't take photos while recording). | App toggle, or `snapshot=on` |

## Settings over USB

Most settings can be changed from the web page after signing in. The fields below can all be sent over the USB cable, which needs no login, since plugging in the cable shows you own the phone. `tools\set-camera-config.ps1` takes form fields:

```
powershell -File tools\set-camera-config.ps1 -Body "user=admin&password=secret"
powershell -File tools\set-camera-config.ps1 -Body "format=3840x2160@30&bitrate=12"
powershell -File tools\set-camera-config.ps1 -Body "udp=on&audio=off"
powershell -File tools\set-camera-config.ps1 -Body "password="        (resets the login to admin / admin)
```

Fields: `user`, `password`, `basic=on|off`, `stream=start|stop|restart`, `engine=normal`, `highbw=on|off`, `powersave=on|off`,
`camera=auto|manual`, `focus=auto|manual|once|0-100`, `exposure=auto|manual|MS`, `wb=auto|manual|KELVIN`, `format=WIDTHxHEIGHT@FPS`, `bitrate=MBPS|auto`, `audio=on|off`,
`udp=on|off`, `snapshot=on|off`. Changing format or bitrate restarts the stream (viewers reconnect within a
second or two). The list of formats the camera offers is shown by `/config`.
`http://127.0.0.1:8020/config` shows the current values.

## Running it as a camera

- Keep the phone on a charger with **W10MSecurityCam open**. Windows 10 Mobile takes the camera
  away when the screen locks or another app opens. Blackout mode keeps the screen on but black.
- The app starts streaming on its own and recovers from encoder stalls (after two stalls it switches
  to the slower MP4 engine until reopened). If it ever crashes while starting, it opens in safe mode.
- The phone cannot start the app after a reboot or power cut; someone has to open it, and it then starts streaming by itself.
- Use a wall charger rather than a PC USB port, and keep the phone out of direct sun.
- Watch `/status` from your NVR or monitoring tool.
- `/status` and the log warn when the phone is not charging or the battery is low.
- The original 15 W (5 V) charger is the limit: 4K streaming uses several watts, so the battery only gains slowly.
  The Power line shows the real balance. To use less power and make less heat, lower the resolution, frame rate or
  bitrate, keep the power-saving options on, and change these phone settings (an app cannot change them):
  turn on Battery Saver, turn off background apps, use airplane mode with Wi-Fi switched back on, turn off Bluetooth,
  NFC and location, turn off "Hey Cortana" and the glance screen, turn off sync and automatic app and map updates,
  use 5 GHz Wi-Fi, close every other app, and switch Device Portal off when you do not need it.

## Soak testing

`tools\Soak-Monitor.ps1` records the camera's status on the PC for as long as it runs:

```
powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1                 (phone address from tools\phone-ip.txt)
powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1 -Every 15 -User admin -Password secret
```

Every sample (default 30 s) goes to `tools\soak\soak-DATE.csv`: streaming, fps, Mbps, viewers, format, engine,
latency, battery %, charging, watts, heat, heat protection, phone and app CPU, memory, Wi-Fi, uptime, screen and
preview state, and the busiest GPU engine if Device Portal is paired (`tools\Monitor-Portal.ps1 -Pair PIN`). A sample
the phone does not answer is recorded as unreachable. Next to it, `soak-DATE.html` shows a graph of each metric over
time (hover for values) and a summary: phone reachable %, streaming %, app restarts, stream stops, fps average and
minimum, time with a heat warning or in heat protection, battery minimum, and the memory trend in MB per hour (a slow
leak shows there). The page reloads itself every minute while the monitor runs. Stop with Ctrl+C; `-Csv FILE`
continues an earlier run and `-ReportOnly -Csv FILE` redraws a report.

## Install a release

Download `W10MSecurityCam_1.0_ARM.zip` from the project's Releases page (it holds the signed package, its three Microsoft
runtime packages, the test certificate and `INSTALL.txt` with the steps). Check it against `SHA256SUMS.txt`.

## Build and install

Same PC setup as W10MWebCam, formerly LumiaCam (VS 2019 with UWP + C++ UWP + ARM tools, SDK 10.0.19041, IP over USB).
Bump `Version` in `W10MSecurityCam/Package.appxmanifest` for every install, or the phone refuses it.

| Script | Use |
|---|---|
| `tools\Build-And-Deploy.cmd` | Build Release ARM and install on the phone (USB first, then Wi-Fi using the address in `tools\phone-ip.txt`) |
| `tools\Install-Only.cmd` | Re-install the last build |
| `tools\Get-Phone-Log.cmd` | Download the phone log to `tools\phone-log.txt` |
| `tools\Set-Camera-Login.cmd` | Set or remove the stream login over USB |
| `tools\Test-RTSP.cmd` | Play the Wi-Fi stream for 15 s, check it, save it to `tools\rtsp-test.h264` |
| `tools\Test-USB-Video.cmd` | Same test over USB |
| `tools\test-rtsp.ps1` | The test itself: `-Seconds`, `-User`/`-Password`, `-Udp`, `-NoSave` (long runs), `-Abrupt`, `-NoAudio`. Saves video to `.h264` and audio to `.aac` |
| `tools\check-http.ps1`, `tools\check-snapshot.ps1` | Check the web endpoints / fetch snapshots |
| `tools\Soak-Monitor.ps1` | Long-run recorder: samples every status value every 30 s to `tools\soak\*.csv` and keeps an HTML report with graphs of each metric over time and a summary (availability, restarts, heat time, fps, memory trend). See *Soak testing* above |
| `tools\Monitor-Portal.ps1` | Logs whole-phone CPU, GPU and battery from Device Portal once a minute (`-Pair PIN` first) |
| `tools\Remove-Dead-IpOverUsb-Keys.cmd` | Optional, run as admin: deletes the unused `LumiaCam*` IP-over-USB registry entries |

Put the phone's IP address in `tools\phone-ip.txt` (one line, not committed) for the Wi-Fi fallback and the test scripts. `tools\autotest\Run-Next.cmd`
is the runner used for unattended testing; it can be ignored.

Developer notes are in [NOTES.md](NOTES.md). Release history is in [CHANGELOG.md](CHANGELOG.md).

## Credits

W10MSecurityCam started as a fork of LumiaCam 1.0 (its camera engine and low-latency encoder sink). LumiaCam has
since been renamed [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam); its version 1.1 took back this fork's crash fix, power monitor, heat
detection and camera controls. The RTSP/RTP server,
login, audio, camera controls, settings page, status and safety nets are new.

## Licence

MIT, see [LICENSE](LICENSE). Use it, change it, share it. It comes with no warranty, and it is a hobby-grade
camera: do not rely on it alone for anything safety-critical.
