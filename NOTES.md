# W10MSecurityCam — developer notes (RTSP fork)

The sections below this one are the notes inherited from LumiaCam (names such as `LumiaCam/` and port 5000 describe the
original app, renamed [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam) in its 1.1). In this fork the app folder is `W10MSecurityCam/`, the video server is `Rtsp/` (RTSP on 8554, USB 8020),
and the HTTP log/status/settings server answers on 5001 and on both RTSP ports.

## RTSP fork: what is where

```
W10MSecurityCam/MainPage.xaml(.cs)   UI: Status / Video / Camera / Power sections, settings, blackout rules, crash guards
W10MSecurityCam/Rtsp/                RTSP 1.0 server, RTP H.264 (FU-A), AAC (RFC 3640), RTCP SR, Digest auth (PasswordVault)
W10MSecurityCam/Streaming/CameraEngine.cs   MediaCapture, formats, encoder profile, AutoBitrate (16 Mbps cap; 40 in high bandwidth mode)
W10MSecurityCam/Streaming/CameraTuning.cs   Focus / exposure / white balance: per-setting auto, step snapping, focus once
W10MSecurityCam/Streaming/HttpApi.cs        /, /status, /snapshot.jpg, /config (USB only), /camera (page), /camera/state, /camera/set
W10MSecurityCam/Streaming/CameraPage.cs     The settings page (one HTML string)
W10MSecurityCam/Streaming/PowerMonitor.cs   Net/estimated watts from the battery report, CPU use, probe=power
W10MSecurityCam.Native/                     C++/CX Media Foundation sink (exceptions must never leave its worker thread)
tools/                               Build-And-Deploy, logs, set-camera-config.ps1, test-rtsp.ps1, check-http.ps1
```

## Rules worth remembering

- **Auth:** `DigestAuth` is always on (vault; first run stores admin/admin; empty password = reset to default). `HttpApi.HandleAsync`
  gates every path for non-USB requests; `FromUsb` means the peer address is 127.0.0.1 (the IP-over-USB service), which a LAN
  client cannot fake. `/camera/set` also needs the header `X-Requested-With: W10MSecurityCam` unless from USB (CSRF guard:
  browsers resend cached Digest credentials to cross-site POSTs, but cannot add a custom header without a preflight).
  Allowed keys: camera, focus, exposure, wb, format, bitrate, highbw, powersave, stream, engine, user, password (checked),
  savepower, saveois, savefocus, probe, torch.
- **Display rules (one place):** `Adjusting` = `_phoneAdjusting`, set only by touches on the phone (Show camera picture,
  camera controls) and cleared 60 s after the last touch or by Black out; web changes never set it.
  `BlackoutWanted` = streaming && black-screen switch on && !Adjusting; `PreviewWanted` = !black-screen switch || Adjusting.
  The preview never changes mid-stream: `RestartForPreviewAsync` restarts the stream (automatic restarts at most once a
  minute; button presses always, queued via `_previewFixPending` if a restart is already running), and a 1 s check in
  `UpdateStats` repairs a preview left in the wrong state. A preview kept running behind the overlay was measured
  (4K25, on battery): app CPU 12% -> 24%, phone CPU 21% -> 37%, battery draw higher; rejected.
- **Camera tuning:** exposure must be a multiple of the camera step (range on the Lumia 950: 0.06-4000 ms; the app uses
  0.5 ms as the floor, since 0.06 ms gives a black picture); only changed controls are sent; ISO goes to auto after an
  exposure change; applies are queued, never dropped or run in parallel.
- **Safe mode** (after a crash or two stalls) uses the MP4 engine: 1080p cap, bitrate ignored. The web page shows it.
- **Crash guards:** `StartInProgress` (stream start), `TuningInProgress` (camera settings; resets to automatic),
  `LaunchInProgress`/`LaunchFailures` (two failed launches clear LocalSettings; the vault login survives).
- **Battery/heat:** battery % from `Battery.AggregateBattery`; `EnergySaverStatus` shown read-only (apps cannot switch it
  on). No temperature API exists for UWP on W10M; the heat signal is charging paused on an adequate charger (see below).
- **4K30 over 2.4 GHz Wi-Fi:** 8-24 Mbps clean; 60 Mbps overloads the link ("App fell behind", camera error, stalls).
- `Dispatcher.RunAsync` with an async lambda returns at the first await; use `RunUiAsync` when completion matters.
- UI event handlers fire during page construction; `_loadingTuningUi` starts true to stop them overwriting saved values.
- The web page's video values must follow the dropdowns (`SyncVideoKeys`), not the last stream start.

## Release workflow

Bump the version in `Package.appxmanifest` and `Properties/AssemblyInfo.cs` (the phone refuses an install that is not
higher), run `tools\Build-And-Deploy.cmd`, check `/status`, update `CHANGELOG.md`, then commit and tag (`git tag vX.Y.Z`).
An uninstall keeps the stream login (password vault) but clears the app settings.

---

# W10MSecurityCam — developer notes

> **W10MSecurityCam fork.** The sections from "Project layout" on are inherited from LumiaCam 1.0 and
> describe the shared capture pipeline (read `LumiaCam` as `W10MSecurityCam`). What this fork changed is in
> "RTSP implementation" right below. Scope: Wi-Fi LAN only, no cellular.

## RTSP implementation (this fork)

```
W10MSecurityCam/
  Rtsp/RtspServer.cs       listeners on 8554 (Wi-Fi) and 8020 (USB), connection cap (8), SPS/PPS, fan-out
  Rtsp/RtspConnection.cs   one client: request parsing, Digest check, SETUP (TCP interleaved or UDP),
                           PLAY/PAUSE/GET_PARAMETER/TEARDOWN, per-client send queue, RTCP SR every 5 s
  Rtsp/RtpPacketizer.cs    RFC 6184: single NAL or FU-A (1400-byte payloads), SPS+PPS before every IDR;
                           RFC 3640 AAC-hbr payloads for the audio track (payload type 97)
  Rtsp/Sdp.cs              DESCRIBE answer: H264/90000, packetization-mode=1, profile-level-id, sprop
  Rtsp/DigestAuth.cs       Digest (RFC 2617, no qop offered) + optional Basic; PasswordVault storage
  Streaming/HttpApi.cs     / (log), /status (JSON), /snapshot.jpg, /config (USB only); used by port 5001
                           and by HTTP requests that arrive on the RTSP ports
  Streaming/StreamServer.cs  no longer compiled (raw Annex-B server from LumiaCam); safe to delete
```

- **Frames, not NALs.** `CameraEngine.AccessUnitReady(nals, time100ns, keyframe)` replaces `NalReady`.
  Native path: one encoder sample = one frame, timestamp = MF sample time. MP4 path: NALs are grouped
  into frames (new frame at a first slice / SEI / SPS / PPS / AUD) and stamped from a Stopwatch.
  AUDs are dropped. `ParameterSetsReady` updates the SDP's SPS/PPS (the encoder's in-band copy wins
  over the cached probe copy).
- **RTP timestamps** = 90 kHz from the frame time plus a random per-client offset. When the encoder
  clock restarts (camera restart) or jumps by >10 s, the client's timeline continues one frame later
  (`RtspConnection.Timestamp`), so NVRs never see time run backwards. `RtspServer.ResetStream()` is
  called before every camera start: clients wait for the next keyframe.
- **Backlog** is time-based per client, as in LumiaCam: oldest queued frame > 1.5 s old → drop the
  queue and wait for the next keyframe; nothing sent for 6 s while data is queued → disconnect.
  No keep-alive (request or RTCP) for 90 s → disconnect. Responses are never dropped.
- **Login** covers every RTSP method except OPTIONS, and `/snapshot.jpg`. Nonces are stateless
  (timestamp + MD5 of a per-launch secret) and valid for 12 h; an expired one gets `stale=TRUE`.
- **Settings over USB** (`POST /config` on 8020): the IP-over-USB service makes the PC appear as
  127.0.0.1, which is treated as physical proof of ownership. Wi-Fi requests get 403.
- **UDP** (opt-in): SETUP `RTP/AVP;unicast;client_port=a-b` binds an even/odd pair of DatagramSockets
  (40000-49999) and answers `server_port`. MessageReceived handlers must be attached before binding.
  Clients behind a firewall must send a punch packet first (ffmpeg does; the PC test script does too).
- **Snapshots** (opt-in): `MediaCapture.CapturePhotoToStreamAsync` while recording, only if
  `ConcurrentRecordAndPhotoSupported`. The photo stream is set once to the smallest size ≥1280 wide;
  larger results are scaled to 1280 with BitmapEncoder. Cached for 2 s.
- **Audio** (opt-in, `Audio` setting): MediaCapture opens with `AudioAndVideo` (falls back to video only if
  the microphone is denied or fails). The low-latency profile gets `AudioEncodingProperties.CreateAac(48000, 1,
  96000)`; the native sink pre-creates stream 1 for it. Each stream sink decides audio vs video from its media
  type, so the engine's stream order doesn't matter. Audio frames skip the video keyframe/backlog logic in the
  native queue. The AAC encoder needs a *complete* output type (16-bit, 12000 bytes/s, payload type 0, profile
  0x29, no user data) or `StartRecordToCustomSinkAsync` fails with MF_E_INVALIDMEDIATYPE; it then fills in its own
  AudioSpecificConfig (`11 88` for 48 kHz mono AAC-LC), which goes into the SDP `config=`.
  RTP audio timestamps count samples (+1024 per frame) and only follow the media clock if they drift by more
  than one frame, because the microphone's frame times jitter. RTCP SRs for both tracks map media time to the
  same wall-clock base (`RtspServer.MediaTimeToUtc`), which is what players use for lip sync.
  The MP4 fallback engine stays video only (audio would break the mdat NAL scanner).
- **Defaults** (applied once via the `DefaultsVersion` setting): 1080p at the lowest rate ≥15 fps
  (24 on the Lumia 950), 4 Mbps, keyframe 2 s requested.

### Findings

- **Keyframes:** the Lumia's encoder sends an IDR every second at 24 fps regardless of the requested
  GOP (2 s asked, 25 IDRs in 25 s measured). Shorter intervals were not tested.
- **Bitrate** is honoured on the native path: 4 Mbps requested → 3.75–3.8 Mbps measured.
- **Phone clock** runs about 0.6 s behind the PC (seen in RTSP Date headers and RTCP SR NTP times).
- **Two viewers:** one viewer dropping abruptly (TCP reset, no TEARDOWN) did not disturb the other
  (90 s, 0 gaps).
- **Soak, Oct 29:** 14 minutes at 1080p24 with 0 gaps, then the phone vanished from Wi-Fi and USB
  at 04:00 (no ping). Battery read 19–20% "Charging" on the PC's USB port an hour earlier. Likely
  cause: the charger was too weak and the battery ran out. Version 1.3 warns in the log and `/status`
  (`batteryFalling`) when the level drops 2% or more over 20–30 minutes while "Charging".
- **Writing files to the PC from a cloud session:** re-using a staging path can write a stale copy;
  always stage from a new folder and compare checksums (only relevant to how this fork was built).
- **UDP (1.3):** 60 s over Wi-Fi lost 3 of 20,613 packets (one gap); every frame decoded. TCP had 0 loss.
- **Audio (1.4):** 30 s over TCP and UDP: 46.9 AAC frames/s as expected, 0 lost, every frame decodes
  (48 kHz mono AAC-LC, ~105 kbps on the wire). Video unchanged at the same time (24 fps, ~80 ms sensor to
  encoder, 0 gaps). Microphone frame times jitter by a few samples; fixed by counting samples (1.4.3).
- **4K30 over Wi-Fi (1.5):** 45 s each at 8/12/16/24 Mbps requested → 7.2/11.0/15.0/22.1 Mbps measured, 30.0 fps,
  0 lost packets, 45 keyframes, audio clean, every frame decodes (level 5.1). Wi-Fi signal 4 bars; band not recorded.
  App memory peaked at 567 MB at 4K (limit 1536 MB) and returned to ~180 MB at 1080p.
- **Config replies (1.5.1):** `/config` text runs on a network thread, so it must never touch XAML controls
  (RPC_E_WRONG_THREAD → HTTP 500 even though the change was applied). It uses cached strings now.
- **Snapshots (1.3):** the Lumia 950 reports `ConcurrentRecordAndPhotoSupported = false`, so
  `/snapshot.jpg` answers 404 while streaming. A future route would be decoding a keyframe from the
  stream (Media Foundation H.264 decoder) instead of using the photo pin.

### Not done, and why

- **Sub-stream (low-res second stream).** MediaCapture records one stream through the single hardware
  encoder. Frigate and Blue Iris can decode and downscale the main stream instead.
- **Thermal logging.** Windows 10 Mobile has no thermal API for apps. Use 1080p (not 4K) when enclosed.
- **Start at boot.** Not possible without interop unlock (unchanged from LumiaCam).

### Version history

| Version | Content |
|---|---|
| 1.0 | Fork of LumiaCam 1.0, renamed, new identity and certificate |
| 1.1 | RTSP server, RTP over TCP, raw stream removed |
| 1.2 | Digest login, RTCP sender reports, /status, /config over USB, security defaults, power warnings |
| 1.3 | RTP over UDP (opt-in), snapshots (opt-in), "charger too weak" warning |
| 1.4.x | Microphone audio: AAC track over RTSP (TCP and UDP), sample-counted RTP timestamps |
| 1.5.x | `format=` / `bitrate=` over USB, memory + Wi-Fi signal in `/status`, memory log every 30 min |
| 1.9.2-1.9.4 | Camera power saving (stabilization off, held focus), longer 4K auto-start settle, `probe=devices`, HDR/denoise removed |
| 1.9.5-1.9.6 | Power monitor (`PowerMonitor.cs`, `probe=power`), minute log line, Healthy/Unstable line |
| 1.9.7 | Exceptions from the frame/log/audio handlers caught in the native sink worker thread (crash fix) |
| 1.9.13-1.9.29 | Heat protection also lowers video to 1080p/6 Mbps; preview is never toggled mid-stream (stalls/crashes the 4K pipeline); blackout by default, Show camera picture (60 s) / Black out with one restart each way; folded phone layout; green theme; graphic status tiles on both screens; web notes behind (i) icons; stream-address pop-up; flashlight under Camera; empty first encoder sample ignored; `tools/Soak-Monitor.ps1` |
| 1.9.8-1.9.12 | Flashlight (30 s limit), heat protection, red thermal warnings, folding web sections |

Reference for future work (RTSP fork, dashcam fork, maintenance). User-facing setup is in README.md.

## Project layout

```
LumiaCam.sln
LumiaCam/                     C# UWP app (.NET Native, ARM), SDK 10.0.19041, min 10.0.14393
  MainPage.xaml(.cs)          UI, settings, blackout, auto-start, crash guard, stall watchdog, logging
  Streaming/CameraEngine.cs   MediaCapture setup, format list, encoder profile, engine selection
  Streaming/StreamServer.cs   TCP listeners (5000 Wi-Fi, 8020 USB), keyframe-synced broadcast, backlog control
  Streaming/LogServer.cs      HTTP log on 5001
  Streaming/Mp4TapStream.cs   Fallback engine: IRandomAccessStream the MP4 recorder writes into
  Streaming/Mp4H264Extractor.cs  Self-synchronizing NAL scanner over the MP4 mdat bytes
  Streaming/AvcConfigReader.cs   Reads SPS/PPS from avcC of a short probe recording
  Streaming/AnnexB.cs         Splits Annex-B byte streams into NAL units
LumiaCam.Native/              C++/CX WinRT component (WRL)
  EncodedMediaSink.h/.cpp     IMFMediaSink/IMFStreamSink/IMFMediaTypeHandler/IMFClockStateSink
  EncodedFrameSink.h/.cpp     Public ref class wrapping the sink for C# (MediaExtension, FrameReceived, LogMessage)
tools/                        Build/deploy/log scripts; tools/archive = one-off dev scripts
```

Build: `tools\Build-And-Deploy.cmd` (MSBuild Release|ARM → `out\` → WinAppDeployCmd, USB `-ip 127.0.0.1` then Wi-Fi).
Bump `Version` in `LumiaCam/Package.appxmanifest` for each release; the title shows Major.Minor.

## Pipeline

```
Rear camera ─► MediaCapture (record stream, H.264 hardware encoder)
   │  StartRecordToCustomSinkAsync(profile, EncodedFrameSink.MediaExtension)      ◄── "low-latency" engine
   │     ProcessSample → copy → queue (max 3, else drop until next IDR) → worker thread → FrameReceived
   │  fallback: StartRecordToStreamAsync(MP4) → Mp4TapStream → Mp4H264Extractor     ◄── "standard (MP4)" engine
   ▼
CameraEngine.OnNal ─► StreamServer.Broadcast ─► each viewer: SPS+PPS, wait for IDR, then live NALs
                                               (raw Annex-B H.264 over TCP; no container, no audio)
```

- **SPS/PPS**: the native sink gets them from MF_MT_MPEG_SEQUENCE_HEADER; for robustness a one-time
  probe recording is parsed (avcC) and cached in LocalSettings under `Enc|WxH@fps`.
  `CameraEngine.ClearEncoderCache()` resets it.
- **Encoder properties** set on the profile's video properties: MF_MT_MAX_KEYFRAME_SPACING, CODECAPI
  rate control/mean/max bitrate, GOP size, CODECAPI_AVLowLatencyMode. The Lumia honours bitrate and GOP
  on the native path (the MP4 path ignores bitrate).
- **Native sink detail**: stream 0 is pre-created from the profile properties
  (MFCreateMediaTypeFromProperties) and reused by AddStreamSink. PlaceMarker completes immediately.
  Latency per frame: DeviceReferenceSystemTime attribute {6523775A-BA2D-405F-B2C5-01FF88E2E8F6} vs MFGetSystemTime.
- **Viewer backlog**: time-based (drop and resync at next IDR if a viewer is > 1.5 s behind);
  viewers with no successful send for 6 s are closed. A size-based limit (1 MB) wrongly dropped valid 4K video.

## Measurements (Lumia 950, W10M 10.0.15254.603)

| Item | Low-latency engine | MP4 engine |
|---|---|---|
| Sensor → encoded frame | ~115 ms avg at 4K30 | n/a |
| App hand-off queue | ~0.2 ms | — |
| Viewer send queue (USB) | ~1 ms | — |
| Delivery pattern | steady per-frame | bursts with ~350 ms recorder pauses |
| Bitrate at 4K30 | ~35 Mbps (Auto) | ~68 Mbps (ignores setting) |

- USB via IP-over-USB carried ≥ 68 Mbps sustained, so 4K30 has plenty of headroom.
- Remaining end-to-end latency is mostly the phone camera/encoder (~115 ms) plus OBS/FFmpeg decode
  and buffering. The OBS FFmpeg options in README.md matter more than anything left on the phone.
- **A/B tested, no effect** (removed in 1.0): MediaCategory.Communications +
  DesiredOptimization = LatencyThenQuality ("Latency mode"). Shorter keyframe intervals do not change
  latency; they only speed up joins/recovery.

## Transport discoveries

- Windows 10 Mobile has no USB webcam (UVC) gadget mode for apps. The usable USB path is Microsoft's
  **IP over USB** service (`IpOverUsbSvc`, from the Windows 10 SDK 1709 MSI). It forwards PC
  localhost ports to whitelisted phone ports only.
- Phone port **8020** (the `VSTestFramework` entry) is forwarded and reachable by a normal app:
  PC `127.0.0.1:8020` → phone `:8020`. Custom entries added under
  `HKLM\SOFTWARE\Microsoft\Windows Phone\IpOverUsbSdk\` (LumiaCamVideo/LumiaCamLog/LumiaCamTest*) did
  **not** work and are unused; they can be deleted from an admin prompt.
- Because 8020 is the only USB port, the app also answers an HTTP-style `GET` on 8020 with the log.
- Wi-Fi works but 2.4 GHz cannot carry 4K reliably. Ethernet through a ThinkPad USB-C Dock Gen 2
  got no IP address (and would be 100 Mbps anyway).
- After a phone reboot the USB link sometimes stays down until the cable is replugged.

## Platform limits found

- **Screen lock / app switch revokes the camera**. ExtendedExecution and the restricted
  `backgroundMediaRecording` capability did not help (removed in 1.0). Workaround: DisplayRequest +
  blackout overlay (black OLED pixels + BrightnessOverride to 0).
- **No start-at-boot** for third-party apps on W10M. Possible only with root/interop unlock
  (e.g. a startup task or registry run key in the main OS) — not attempted.
- **Charging while off** isn't possible; the phone auto-boots when power is connected.
- With blackout on, the phone charges faster than it drains even at 4K30 on a good USB port.

## Stability lessons (crash causes fixed)

1. Subscribing `CameraStreamStateChanged` before `InitializeAsync` crashed at startup → subscribe after.
2. Toggling the preview on/off right after camera power-up and then starting 4K recording crashed the
   camera pipeline → never start the preview when auto-start + blackout will turn it off; turn preview
   off *before* recording starts; wait 2 s after init before auto-starting.
3. Doing work on the Media Foundation callback thread stalled 4K → copy and hand off to a worker thread.
4. Awaiting ExtendedExecution blocked the UI "busy" state → removed; a 30 s busy watchdog unlocks controls.

Safety nets kept in 1.0:
- **Crash guard**: `StartInProgress` is set before a stream start and cleared after 15 s of healthy
  streaming, on stop, and on suspend. If set at launch → SAFE MODE for that launch only (no
  auto-start, MP4 engine).
- **Stall watchdog**: no native frames for 4 s → restart stream; 2 stalls within 2 min → MP4 engine
  until the app is reopened.
- **Persistent log**: `LocalFolder\log.txt` (reset above ~2 MB), previous session kept as
  `log-previous.txt` and its last 120 lines are appended to the served log.

### Crash found with Device Portal dumps (1.9.6 -> 1.9.7)
Enabling user-mode crash dumps (`POST /api/debug/dump/usermode/crashcontrol?packageFullName=...&maxCount=3`, per package
version) gave a dump with exception 0xC0000409, parameter 7 (FAST_FAIL_FATAL_APP_EXIT) at `ucrtbase` abort: a C++
exception left the native sink's `std::thread` worker (`WorkerLoop` calls the C# `FrameReceived` handler). Fix: try/catch
around every callback in `EncodedMediaSink.cpp` and the `Raise*` functions, and in `CameraEngine.OnEncodedFrame`.
Download a dump with `GET /api/debug/dump/usermode/crashdump?packageFullName=...&fileName=...` (not `/dump`).

### Heat and power (Lumia 950, measured)
- No temperature is readable by apps. `Battery.AggregateBattery` status `Idle` while `PowerSupplyStatus` is `Adequate`
  and the level is below 95% means charging is paused (hot). The frame rate does not change when the phone is hot.
- Net power: remaining mWh change averaged over up to 5 minutes, restarted when the charging state changes; the phone's
  own draw is only known on battery or while charging is paused. Windows' instantaneous rate read 7-10 W at 4K25 on
  battery with the screen black. The start/stop chirp is Windows' recording sound (follows the ringer volume). 4K25 at 24 Mbps with
  the screen blacked out: about +2 to +5 W net while charging; 7-10 W draw once charging pauses (hot phone, flashlight).
- The torch (`MediaCapture.VideoDeviceController.TorchControl`) is on/off only on this phone; repeated use led to
  Windows' overheating warning, which closes the app. Hence the 30 s limit and heat protection.
- Device Portal login is the same PIN pairing as the deploy tool: POST the PIN to `/api/authorize/pair?pin=...` with the
  `CSRF-Token` cookie echoed in an `X-CSRF-Token` header and a `Content-Length`. `tools/Monitor-Portal.ps1` does this.

## Dev-environment pitfalls

- Batch files: a `)` inside an `if (...)` block must be escaped `^)`; `%errorlevel%` inside a block is
  expanded early — use `call set RC=%%errorlevel%%`.
- SDK 17763 wasn't installed → project targets 19041 (min 14393 still runs on 15254).
- When copying files to the PC through Claude's file bridge, always stage into a **new** folder;
  reusing a staged path delivered stale content once.
- WinAppDeployCmd over USB during a running 4K stream takes ~2.5 min; it does finish.
- The app is signed with `LumiaCam_TemporaryKey.pfx` (CN=LumiaCam); `LumiaCam.cer` is the public cert.

## Future work

### RTSP fork (separate app, latency less critical)
- Fork the solution, keep CameraEngine + native sink unchanged (they already produce NAL units with
  timestamps and keyframe flags).
- Replace StreamServer with an RTSP server: DESCRIBE (SDP with `sprop-parameter-sets` from the cached
  SPS/PPS, `profile-level-id`), SETUP (RTP/AVP/TCP interleaved first — simplest and works through the
  USB port; UDP later), PLAY, TEARDOWN. RTP packetization per RFC 6184: single NAL or FU-A fragments
  (MTU ~1400), 90 kHz timestamps from the sample time, marker bit on the last packet of a frame.
- Optional: Basic/Digest auth, ONVIF discovery is out of scope. Target clients: VLC, ffmpeg, Frigate,
  Blue Iris, Synology Surveillance.
- For security-camera use: 1080p/720p at lower bitrate, longer keyframes (2–4 s), audio (AAC) maybe.

### Dashcam fork
- Record to SD with segmenting (e.g. 3–5 min MP4 files, loop-delete oldest), plus optional live view.
- The MP4 engine path is already a good recorder; the native sink could write fragmented MP4 instead.
- Constraints: screen must stay on (blackout works), no boot start, heat at 4K in a car — prefer 1080p.
- Add GPS overlay/metadata via Geolocator if wanted.

### Possible improvements to this app
- Microphone audio (would need a container or a second stream; OBS can take PC mic instead).
- Front camera / camera switch, exposure/focus lock, torch.
- A tiny PC-side helper instead of OBS (e.g. a DirectShow/MF virtual camera that reads TCP 8020).
