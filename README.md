# W10MSecurityCam

A fork of [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam) (formerly LumiaCam) that turns a Lumia 950
(Windows 10 Mobile) into an always-on security camera. It serves an RTSP stream that an NVR (Frigate, Blue Iris,
Synology Surveillance Station) or VLC can pull over the home Wi-Fi.

> **Compatibility:** tested only on the **Lumia 950** with Windows 10 Mobile 1709, OS build **10.0.15254.603** (ARM).
> Other Windows 10 Mobile phones are untested.

Documentation, step by step: the [wiki](https://github.com/ClodeMaxxing/W10MSecurityCam/wiki). Ready-built packages:
[latest release](https://github.com/ClodeMaxxing/W10MSecurityCam/releases/latest) (see *Install a release* below).

It has its own package identity (`W10MSecurityCam`, CN=W10MSecurityCam), so it installs beside
W10MWebCam and its sister app [W10MDashcam](https://github.com/ClodeMaxxing/W10MDashcam). Only one of them can use the
camera at a time.

## Addresses

| What | Address |
|---|---|
| Stream (Wi-Fi) | `rtsp://PHONE-IP:8554/live` (the app shows the IP) |
| Stream (USB) | `rtsp://127.0.0.1:8020/live` |
| Settings page | `http://PHONE-IP:5001/camera` or just `http://PHONE-IP:5001` (over USB: `http://127.0.0.1:8020/camera`) |
| Health | `http://PHONE-IP:5001/status` (JSON: fps, bitrate, viewers, battery and charger, heat, memory, Wi-Fi signal, restarts) |
| Log | `http://PHONE-IP:5001/log` (with the *Log page* switch on) |
| Stats | `http://PHONE-IP:5001/stats`: graphs of the last 12 hours, sampled every 30 s (with the *Stats page* switch on) |
| Snapshot | `http://PHONE-IP:5001/snapshot.jpg` (only when turned on, see below; needs the login) |

The pages, `/status` and the log can be read on the home network without a login (view only); changes, the snapshot
and the stream need it (see Login). Under *Network and login* on the phone, the **web page** is on by default (it is
the way to set the camera up without a cable), the **log page** and the **stats page** are off until switched on there
or on the settings page; a page that is off answers 403. The stream is never affected by these switches.

Video is H.264 (track 1); with microphone audio on, there is also an AAC track (48 kHz mono,
about 96 kbps). Both are sent as RTP over the RTSP TCP connection. Choose TCP in the NVR if it
asks; VLC and ffmpeg switch to it by themselves. Quick check:
`ffplay -rtsp_transport tcp rtsp://PHONE-IP:8554/live`.

Give the phone a fixed address (a DHCP reservation in the router), so the NVR always finds it.

The RTSP ports take 8 connections at once; the last one is kept for the PC over USB, so Wi-Fi clients get at most 7.
A connection that has not started playing is closed when it sends no complete request for 30 seconds. The web port
(5001) answers at most 16 connections at once (more get "503 Busy, try again"), and a request has to arrive complete
within 10 seconds.

## Defaults

1080p at 24 fps (the lowest rate of 15 fps or more the camera offers), 4 Mbps, keyframe every 2 s. A fresh install
starts with these; you can change them in the app.

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
  Paused / Hot / Protect), phone and app CPU, app memory, engine badge (Fast / Safe mode), encoder latency, and power
  (watts into or out of the battery on a 0-15 W bar). A tile turns amber or red when it needs attention: the battery
  bar goes red when the battery is draining although the phone says "Charging" (judged every second from the power
  reading: a PC USB port often can't keep up with 4K). Amber notices appear while the default login is in use and when
  the charger is insufficient (with the watts), a red one for heat.
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
screen holds **Start/Stop RTSP stream**, the **service pills** (RTSP over Wi-Fi with its address and viewers, RTSP over
USB, and the web page: green while streaming, grey when stopped; a tap opens *Network and login*), the status card,
**Show camera picture (60 s)**, **Black out the screen now**, **Return to normal mode** in safe mode, and any warning;
Video, Camera, Power, Network and login, and Details and log fold away. Tapping the black screen shows the status for
15 seconds with no restart. **Show camera picture** (or touching a camera control) shows the picture until 60 seconds
after your last touch; **Black out the screen now** ends it straight away. The preview can only start or stop while the
stream is stopped (changing it mid-stream stalled the 4K pipeline), so each of these restarts the stream once, a gap of
a second or two for the NVR. Keeping the preview running behind the black screen was measured and rejected: it doubled
the app's CPU use (12% to 24%) and raised the battery draw. The start/stop chirp is Windows' own recording sound; it
follows the phone's Ringer + notifications volume.

The page can be read without the login (a *View only* notice with a *Log in* button shows); changes need the login
(see Login below), over the USB cable too: any program or web page on the PC the phone is plugged into can reach the
USB port. The page reloads itself after an app update.

**High bandwidth mode** unlocks bitrates above 24 Mbps (up to 60). It needs 5 GHz Wi-Fi or a USB-C connection; at
60 Mbps on 2.4 GHz the stream drops frames. Auto bitrate is capped at 16 Mbps (40 Mbps in high bandwidth mode).

**Temperature and heat protection:** Windows 10 Mobile gives apps no temperature reading, and the frame rate does not
show heat (it stayed at 25 fps while Windows warned of overheating). The app uses the charging state instead: if
charging pauses ("Idle") on a charger Windows reports as adequate, with the battery below 95%, the temperature badge
reads **Paused** (amber); after 20 seconds of that it reads **Hot** and *heat protection* switches on, with a chime
and a vibration. It forces the power-saving options on (screen blackout, preview off, stabilization off, held focus),
lowers the video to 1080p at 6 Mbps (restored afterwards), locks the flashlight and shows a red warning on the phone
and the web page. The usual cause is a charger that can't keep up (a PC USB port or a small adapter; use 2 A or more).
After 30 seconds of charging again (and at least 3 minutes) your own settings come back, with a short chime; the
flashlight stays off but can be used again.
Windows can still stop the app first if it overheats quickly; the app then restarts into heat protection.

**Safe mode:** after a crash or two stalls the app uses the slower MP4 engine, limited to 1080p. The page says so and
offers **Return to normal mode**.

**Self-check:** if the app fails to start twice in a row, it clears its saved settings and starts with defaults (the
login is kept). The log says "SELF-CHECK".

## Login

One login protects the RTSP stream, every change (the settings page, `/config`) and the snapshot, over Wi-Fi and over
the USB cable alike; reading the page, `/status`, the log and the stats needs no login (view only). A fresh install
uses **admin / admin**; the phone screen and the settings page warn until you change it.

- **Settings page:** open `http://PHONE-IP:5001/camera`, press **Log in**, and use the **Login** section at the bottom
  (current password, user name, new password twice, 8-64 characters). Your browser asks you to sign in again afterwards.
- **On the phone:** tap the screen to wake it, open **Network and login**, enter a user name and password under "Login
  (stream and web page)" and tap **Save login**. An empty password resets the login to admin / admin (it is never
  turned off).
- **From the PC over USB:** run `tools\Set-Camera-Login.cmd`. It asks for the current login first, then the new one;
  an empty new password resets it to admin / admin.
- **Forgotten password:** set a new one on the phone (Network and login); it does not ask for the old one.

Since 1.2 a new password needs at least 8 characters everywhere (phone, settings page, `/config`); a shorter one set
before keeps working until you change it. After 5 wrong logins from one address within a minute, that address is
refused for 30 seconds (the web pages answer `429 Too Many Requests`, the stream 401). The phone renews the login
challenge every 5 minutes; browsers, VLC and NVRs take the new one by themselves without asking for the password.

NVRs then use `rtsp://USER:PASSWORD@PHONE-IP:8554/live`. The phone uses Digest authentication, which doesn't send the
password itself. Older NVRs that only know Basic authentication can be allowed with `basic=on` (see Settings over USB).
The login is kept in the Windows password vault, so it survives reinstalling the app. Traffic is not encrypted
(plain HTTP/RTSP), so use it on a network you trust.

## Microphone audio

Turn on **Microphone audio (AAC)** under Video on the phone, or send `audio=on` (see Settings over USB).
The camera restarts and, the first time, the phone asks whether the app may use the microphone.
Audio only works with the normal low-latency engine; in safe mode (MP4 engine) the stream is video only.
`/status` shows `"audio":"streaming"` when it is working.

## Optional extras (off by default)

| Setting | What it does | Turn on |
|---|---|---|
| Allow RTP over UDP | Lets clients that ask for UDP get it. On Wi-Fi, TCP is more reliable, which is why it's off. | App toggle, or `udp=on` |
| Snapshots | `/snapshot.jpg` returns a 1280-px still taken while the video keeps recording. **Not possible on the Lumia 950** (its camera can't take photos while recording). | App toggle, or `snapshot=on` |

## Settings over USB

Most settings can be changed from the web page after signing in. The fields below can all be sent to `/config`, which
only answers over the USB cable and needs the login like everything else. Changes (POST) also need the header
`X-Requested-With: W10MSecurityCam` and `127.0.0.1` or `localhost` as `Host`. `tools\set-camera-config.ps1` does all
that; it takes the current login with `-User`/`-Password` or the environment variables `W10M_USER`/`W10M_PASSWORD`,
asks for it otherwise, and takes form fields:

```
powershell -File tools\set-camera-config.ps1 -Body "user=admin&password=secret"
powershell -File tools\set-camera-config.ps1 -Body "format=3840x2160@30&bitrate=12"
powershell -File tools\set-camera-config.ps1 -Body "udp=on&audio=off"
powershell -File tools\set-camera-config.ps1 -Body "password="        (resets the login to admin / admin)
```

Fields: `user`, `password` (a new one needs at least 8 characters), `basic=on|off`, `stream=start|stop|restart`, `engine=normal`, `highbw=on|off`, `powersave=on|off`,
`camera=auto|manual`, `focus=auto|manual|once|0-100`, `exposure=auto|manual|MS`, `wb=auto|manual|KELVIN`, `format=WIDTHxHEIGHT@FPS`, `bitrate=MBPS|auto`, `audio=on|off`,
`udp=on|off`, `snapshot=on|off`, `savepower=on|off`, `saveois=on|off`, `savefocus=on|off`, `torch=on|off`. Changing
format or bitrate restarts the stream (viewers reconnect within a second or two). The list of formats the camera offers
is shown by `/config`. `http://127.0.0.1:8020/config` shows the current values (the browser asks for the login).

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
powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1                               (over the USB cable)
powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1 -Phone 192.168.0.50 -Every 15   (over Wi-Fi)
```

It reads `/status` without a login (the same script serves W10MSecurityCam, W10MDashcam and W10MWebCam); the phone log
is collected only while the *Log page* switch is on.

Every sample (default 30 s) goes to `tools\soak\soak-DATE.csv`: streaming, fps, Mbps, viewers, format, engine,
latency, battery %, charging, watts, heat, heat protection, phone and app CPU, memory, Wi-Fi, uptime, screen and
preview state, and the busiest GPU engine if Device Portal is paired (`tools\Monitor-Portal.ps1 -Pair PIN`). A sample
the phone does not answer is recorded as unreachable. Next to it, `soak-DATE.html` shows a graph of each metric over
time (hover for values) and a summary: phone reachable %, streaming %, app restarts, stream stops, fps average and
minimum, time with a heat warning or in heat protection, battery minimum, and the memory trend in MB per hour (a slow
leak shows there). The page reloads itself every minute while the monitor runs. The phone keeps only its last
hour or two of log, so the monitor also saves it every 5 minutes to `soak-DATE-phone-log.txt` (dated lines, the whole
run; `-LogEvery` changes the interval). Stop with Ctrl+C; `-Csv FILE`
continues an earlier run and `-ReportOnly -Csv FILE` redraws a report.

## Install a release

The release is ready-built: no compiling and nothing else to download. Download `W10MSecurityCam_<version>_ARM.zip`
from the [latest release](https://github.com/ClodeMaxxing/W10MSecurityCam/releases/latest). It holds:

- the app package, `W10MSecurityCam_<package version>_ARM.appx` (the package version has four parts, for example
  1.9.31.0 for release 1.2);
- `Dependencies\` with the three Microsoft runtime packages it needs: `Microsoft.NET.Native.Framework.1.7.appx`,
  `Microsoft.NET.Native.Runtime.1.7.appx` and `Microsoft.VCLibs.ARM.14.00.appx`;
- the test certificate the package is signed with (`.cer`), `INSTALL.txt` with the steps, and `LICENSE`.

Check the zip against `SHA256SUMS.txt` from the same release, in PowerShell:

```
Get-FileHash .\W10MSecurityCam_<version>_ARM.zip -Algorithm SHA256
```

(or `certutil -hashfile W10MSecurityCam_<version>_ARM.zip SHA256` in a command prompt).

Install with **Device Portal**: on the phone, Settings > Update & security > For developers, turn on Developer mode,
Device discovery and Device Portal. Open the address it shows in a browser on the PC, pair with the PIN the phone
shows, then **Apps > Install app**: the `.appx` plus the three files in `Dependencies`. `WinAppDeployCmd` from the
Windows 10 SDK works too. Step by step: [Installation](https://github.com/ClodeMaxxing/W10MSecurityCam/wiki/Installation)
on the [wiki](https://github.com/ClodeMaxxing/W10MSecurityCam/wiki).

**Updating:** install the newer package the same way; settings are kept. If Device Portal refuses with *same identity
but different contents*, the version number is unchanged: uninstall the app first (this resets its settings; the
login is kept in the Windows password vault).

## Build and install

Same PC setup as W10MWebCam, formerly LumiaCam (VS 2019 with UWP + C++ UWP + ARM tools, SDK 10.0.19041, IP over USB);
see [Building from Source](https://github.com/ClodeMaxxing/W10MSecurityCam/wiki/Building-from-Source) on the wiki.
Bump `Version` in `W10MSecurityCam/Package.appxmanifest` for every install, or the phone refuses it.

| Script | Use |
|---|---|
| `tools\Build-And-Deploy.cmd` | Build Release ARM and install on the phone (USB first, then Wi-Fi using the address in `tools\phone-ip.txt`). Finds Visual Studio 2019's MSBuild with vswhere (the 2019 Community path if vswhere is missing) |
| `tools\Install-Only.cmd` | Re-install the last build |
| `tools\Get-Phone-Log.cmd` | Download the phone log to `tools\phone-log.txt` |
| `tools\Set-Camera-Login.cmd` | Set or reset the stream login over USB (asks for the current login; it is never removed) |
| `tools\Test-RTSP.cmd` | Play the Wi-Fi stream for 15 s, check it, save it to `tools\rtsp-test.h264` |
| `tools\Test-USB-Video.cmd` | Same test over USB |
| `tools\test-rtsp.ps1` | The test itself: `-Seconds`, `-User`/`-Password`, `-Udp`, `-NoSave` (long runs), `-Abrupt`, `-NoAudio`. Saves video to `.h264` and audio to `.aac` |
| `tools\check-http.ps1`, `tools\check-snapshot.ps1` | Check the web endpoints / fetch snapshots |
| `tools\Soak-Monitor.ps1` | Long-run recorder: samples every status value every 30 s to `tools\soak\*.csv` and keeps an HTML report with graphs of each metric over time and a summary (availability, restarts, heat time, fps, memory trend). See *Soak testing* above |
| `tools\Monitor-Portal.ps1` | Logs whole-phone CPU, GPU and battery from Device Portal once a minute (`-Pair PIN` first) |
| `tools\Crash-Dumps.ps1` | Windows crash dumps through Device Portal: `-Pair PIN` once, `-Enable` (again after each app update), `-List`, `-Get` (to `tools\dump\`), `-Disable` |
| `tools\Enable-Crash-Dumps.cmd`, `tools\Get-Crash-Dump.cmd` | Switch crash dumps on and list them / download W10MSecurityCam's dumps |
| `tools\Sync-Core.ps1` | Compares the source files the three apps share (`-Check`) or copies one app's copy over the others (`-Fix -From APP`); needs all three repositories |
| `tools\Remove-Dead-IpOverUsb-Keys.cmd` | Optional, run as admin: deletes the unused `LumiaCam*` IP-over-USB registry entries |

Put the phone's IP address in `tools\phone-ip.txt` (one line, not committed) for the Wi-Fi fallback and the test scripts. `tools\autotest\Run-Next.cmd`
is the runner used for unattended testing; it can be ignored.

Developer notes are in [NOTES.md](NOTES.md). Release history is in [CHANGELOG.md](CHANGELOG.md).

## Credits

W10MSecurityCam started as a fork of [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam) (formerly LumiaCam)
1.0: its camera engine and low-latency encoder sink. W10MWebCam's version 1.1 took back this fork's crash fix, power
monitor, heat detection and camera controls. The RTSP/RTP server, login, audio, camera controls, settings page, status
and safety nets are new. The sister app [W10MDashcam](https://github.com/ClodeMaxxing/W10MDashcam), a 4K dashcam,
forked from W10MSecurityCam 1.1.

## Licence

MIT, see [LICENSE](LICENSE). Use it, change it, share it. It comes with no warranty, and it is a hobby-grade
camera: do not rely on it alone for anything safety-critical.
