param(
  [string]$Target = 'wifi',          # 'wifi' or 'usb'
  [string]$PhoneIp = '',
  [int]$Seconds = 15,
  [string]$User = '',
  [string]$Password = '',
  [switch]$Udp,                      # ask for RTP over UDP instead of TCP
  [switch]$NoSave,                   # don't write the video file (long soak tests)
  [string]$Tag = '',                 # suffix for log/video names, so several tests can run at once
  [switch]$Abrupt,                   # drop the connection at the end without TEARDOWN (like a crashed viewer)
  [switch]$NoAudio                   # don't set up the audio track even if the phone offers one
)
if (-not $PhoneIp) { $PhoneIp = (Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1) }
# Plays the phone's RTSP stream without any other software:
# OPTIONS, DESCRIBE, SETUP, PLAY, then reads RTP (and RTCP) packets, checks them and rebuilds
# the H.264 video into tools\rtsp-test.h264 (playable with VLC/ffplay).

$ErrorActionPreference = 'Stop'
if ($Target -eq 'usb') { $ip = '127.0.0.1'; $port = 8020 } else { $ip = $PhoneIp; $port = 8554 }
$url = "rtsp://${ip}:$port/live"
$log = Join-Path $PSScriptRoot "rtsp-test$Tag-log.txt"
$out = Join-Path $PSScriptRoot "rtsp-test$Tag.h264"
$outAudio = Join-Path $PSScriptRoot "rtsp-test$Tag.aac"
"RTSP test started $(Get-Date) - $url - $(if ($Udp) { 'UDP' } else { 'TCP' }) - $Seconds s" | Out-File $log -Encoding utf8
function L($m) { Write-Host $m; $m | Add-Content $log }

$script:buf = New-Object byte[] (64MB)
$script:len = 0
$script:stream = $null
$script:cseq = 0
$script:auth = $null   # @{ Realm; Nonce } once the phone asked for a login
$md5 = [Security.Cryptography.MD5]::Create()
function Md5Hex([string]$s) { ([BitConverter]::ToString($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)))).Replace('-', '').ToLower() }

function AuthHeader([string]$method, [string]$uri) {
  if ($null -eq $script:auth) { return $null }
  $ha1 = Md5Hex "${User}:$($script:auth.Realm):$Password"
  $ha2 = Md5Hex "${method}:$uri"
  $resp = Md5Hex "${ha1}:$($script:auth.Nonce):$ha2"
  return "Authorization: Digest username=`"$User`", realm=`"$($script:auth.Realm)`", nonce=`"$($script:auth.Nonce)`", uri=`"$uri`", response=`"$resp`""
}

function Fill([int]$timeoutMs) {
  # wait for data without using socket timeouts (a timed-out read can leave the socket unusable)
  $w = [Diagnostics.Stopwatch]::StartNew()
  while (-not $script:stream.DataAvailable) {
    if ($w.ElapsedMilliseconds -ge $timeoutMs) { return $false }
    Start-Sleep -Milliseconds 5
  }
  $n = $script:stream.Read($script:buf, $script:len, 65536)
  if ($n -le 0) { throw 'phone closed the connection' }
  $script:len += $n
  return $true
}

function Send([string]$method, [string]$reqUrl, [string[]]$extra) {
  $script:cseq++
  $text = "$method $reqUrl RTSP/1.0`r`nCSeq: $($script:cseq)`r`nUser-Agent: W10MSecurityCam-test`r`n"
  foreach ($h in $extra) { $text += "$h`r`n" }
  $a = AuthHeader $method $reqUrl
  if ($a) { $text += "$a`r`n" }
  $text += "`r`n"
  $b = [Text.Encoding]::ASCII.GetBytes($text)
  $script:stream.Write($b, 0, $b.Length)
}

function Request([string]$method, [string]$reqUrl, [string[]]$extra, [switch]$NoRetry) {
  Send $method $reqUrl $extra
  # read until a full response (skipping any interleaved packets that arrive first)
  $deadline = (Get-Date).AddSeconds(10)
  while ($true) {
    while ($script:len -gt 0 -and $script:buf[0] -eq 36) {
      if ($script:len -lt 4) { break }
      $plen = $script:buf[2] * 256 + $script:buf[3]
      if ($script:len -lt 4 + $plen) { break }
      [Array]::Copy($script:buf, 4 + $plen, $script:buf, 0, $script:len - 4 - $plen); $script:len -= 4 + $plen
    }
    if ($script:len -gt 0 -and $script:buf[0] -ne 36) {
      $s = [Text.Encoding]::ASCII.GetString($script:buf, 0, [Math]::Min($script:len, 65536))
      $end = $s.IndexOf("`r`n`r`n")
      if ($end -ge 0) {
        $head = $s.Substring(0, $end)
        $cl = 0
        if ($head -match '(?im)^Content-Length:\s*(\d+)') { $cl = [int]$Matches[1] }
        if ($script:len -ge $end + 4 + $cl) {
          $body = [Text.Encoding]::UTF8.GetString($script:buf, $end + 4, $cl)
          $used = $end + 4 + $cl
          [Array]::Copy($script:buf, $used, $script:buf, 0, $script:len - $used); $script:len -= $used
          L "--- $method -> $($head.Split("`n")[0].Trim())"
          L $head
          if ($body) { L ''; L $body }
          if ($head -match '^RTSP/1.0 401' -and -not $NoRetry) {
            if (-not $Password) { throw 'the phone wants a login: run with -User and -Password' }
            if ($head -match '(?i)Digest realm="([^"]*)",\s*nonce="([^"]*)"') {
              $script:auth = @{ Realm = $Matches[1]; Nonce = $Matches[2] }
              return Request $method $reqUrl $extra -NoRetry
            }
            throw 'no Digest challenge in the 401 answer'
          }
          return @{ Head = $head; Body = $body }
        }
      }
    }
    if ((Get-Date) -gt $deadline) { throw "no answer to $method" }
    [void](Fill 2000)
  }
}

# ---------------------------------------------------------------- packet checks
$sc = [byte[]](0, 0, 0, 1)
$Stats = @{ packets = 0; bytes = 0; frames = 0; idr = 0; gaps = 0; lost = 0; firstMs = -1; lastSeq = -1; firstTs = $null; lastTs = $null
        back = 0; ssrcs = @{}; nal = @{}; fuOpen = $false; psIdr = 0; sps = $false; pps = $false; sr = 0; srMaxOffsetMs = 0.0
        minuteFrames = 0; minuteGaps = 0
        apackets = 0; aframes = 0; abytes = 0; agaps = 0; aLastSeq = -1; aLastTs = $null; aBadStep = 0; aBack = 0; asr = 0 }
$fs = $null; $afs = $null
$audioRate = 0; $audioChannels = 0; $audioFreqIdx = 0

function OnAudioRtp([byte[]]$a, [int]$o, [int]$plen) {
  $Stats.apackets++; $Stats.abytes += $plen
  $seq = $a[$o + 2] * 256 + $a[$o + 3]
  $ts = [double]$a[$o + 4] * 16777216 + $a[$o + 5] * 65536 + $a[$o + 6] * 256 + $a[$o + 7]
  if ($Stats.aLastSeq -ge 0 -and $seq -ne (($Stats.aLastSeq + 1) % 65536)) { $Stats.agaps++ }
  $Stats.aLastSeq = $seq
  if ($null -ne $Stats.aLastTs) {
    $d = ($ts - $Stats.aLastTs + 4294967296) % 4294967296
    if ($d -gt 2147483648) { $Stats.aBack++ } elseif ($d -ne 1024) { $Stats.aBadStep++ }
  }
  $Stats.aLastTs = $ts
  $cc = $a[$o] -band 0x0F
  $po = $o + 12 + 4 * $cc
  $end = $o + $plen
  # RFC 3640 AAC-hbr: AU-headers-length in bits, then 16-bit AU headers (13-bit size + 3-bit index)
  $hdrBits = $a[$po] * 256 + $a[$po + 1]
  $n = [int]($hdrBits / 16)
  $dataPos = $po + 2 + 2 * $n
  for ($i = 0; $i -lt $n; $i++) {
    $size = ($a[$po + 2 + 2 * $i] * 256 + $a[$po + 3 + 2 * $i]) -shr 3
    if ($dataPos + $size -gt $end) { break }
    $Stats.aframes++
    if ($afs) {
      $len = $size + 7   # ADTS header so the .aac file plays anywhere
      $h = [byte[]](0xFF, 0xF1, (64 -bor ($audioFreqIdx -shl 2) -bor ($audioChannels -shr 2)),
                    ((($audioChannels -band 3) -shl 6) -bor ($len -shr 11)), (($len -shr 3) -band 0xFF), ((($len -band 7) -shl 5) -bor 0x1F), 0xFC)
      $afs.Write($h, 0, 7); $afs.Write($a, $dataPos, $size)
    }
    $dataPos += $size
  }
}

function OnRtp([byte[]]$a, [int]$o, [int]$plen) {
  if ($Stats.firstMs -lt 0) { $Stats.firstMs = $sw.ElapsedMilliseconds }
  $Stats.packets++; $Stats.bytes += $plen
  $marker = ($a[$o + 1] -band 0x80) -ne 0
  $seq = $a[$o + 2] * 256 + $a[$o + 3]
  $ts = [double]$a[$o + 4] * 16777216 + $a[$o + 5] * 65536 + $a[$o + 6] * 256 + $a[$o + 7]
  $Stats.ssrcs[[BitConverter]::ToString($a, $o + 8, 4)] = 1
  if ($Stats.lastSeq -ge 0 -and $seq -ne (($Stats.lastSeq + 1) % 65536)) {
    $Stats.gaps++; $Stats.minuteGaps++
    $Stats.lost += (($seq - $Stats.lastSeq - 1 + 65536) % 65536)
  }
  $Stats.lastSeq = $seq
  if ($null -eq $Stats.firstTs) { $Stats.firstTs = $ts }
  if ($null -ne $Stats.lastTs -and $ts -ne $Stats.lastTs) {
    $d = ($ts - $Stats.lastTs + 4294967296) % 4294967296
    if ($d -gt 2147483648) { $Stats.back++ }
  }
  $Stats.lastTs = $ts
  $cc = $a[$o] -band 0x0F
  $po = $o + 12 + 4 * $cc
  $pl = $plen - 12 - 4 * $cc
  $nt = $a[$po] -band 0x1F
  if ($nt -ge 1 -and $nt -le 23) {
    $Stats.nal["$nt"] = 1 + [int]$Stats.nal["$nt"]
    if ($nt -eq 7) { $Stats.sps = $true }
    if ($nt -eq 8) { $Stats.pps = $true }
    if ($nt -eq 5) { if ($Stats.sps -and $Stats.pps) { $Stats.psIdr++ }; $Stats.idr++; $Stats.sps = $false; $Stats.pps = $false }
    if ($fs) { $fs.Write($sc, 0, 4); $fs.Write($a, $po, $pl) }
  } elseif ($nt -eq 28) {
    $fuh = $a[$po + 1]
    $orig = $fuh -band 0x1F
    if ($fuh -band 0x80) {
      $Stats.nal["$orig"] = 1 + [int]$Stats.nal["$orig"]
      if ($orig -eq 5) { if ($Stats.sps -and $Stats.pps) { $Stats.psIdr++ }; $Stats.idr++; $Stats.sps = $false; $Stats.pps = $false }
      if ($fs) { $fs.Write($sc, 0, 4); $fs.WriteByte([byte](($a[$po] -band 0xE0) -bor $orig)) }
      $Stats.fuOpen = $true
    }
    if ($Stats.fuOpen -and $fs) { $fs.Write($a, $po + 2, $pl - 2) }
    if ($fuh -band 0x40) { $Stats.fuOpen = $false }
  } else {
    $Stats.nal["other-$nt"] = 1 + [int]$Stats.nal["other-$nt"]
  }
  if ($marker) { $Stats.frames++; $Stats.minuteFrames++ }
}

function OnRtcp([byte[]]$a, [int]$o, [int]$plen, [switch]$Audio) {
  if ($plen -lt 28 -or $a[$o + 1] -ne 200) { return }
  if ($Audio) { $Stats.asr++; return }
  $Stats.sr++
  $sec = [double]$a[$o + 8] * 16777216 + $a[$o + 9] * 65536 + $a[$o + 10] * 256 + $a[$o + 11]
  $frac = [double]$a[$o + 12] * 16777216 + $a[$o + 13] * 65536 + $a[$o + 14] * 256 + $a[$o + 15]
  $phone = (New-Object DateTime 1900, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc)).AddSeconds($sec + $frac / 4294967296)
  $offset = [Math]::Abs(($phone - [DateTime]::UtcNow).TotalMilliseconds)
  if ($offset -gt $Stats.srMaxOffsetMs) { $Stats.srMaxOffsetMs = $offset }
}

try {
  $c = New-Object System.Net.Sockets.TcpClient
  $c.NoDelay = $true
  $t = $c.ConnectAsync($ip, $port)
  if (-not $t.Wait(5000)) { throw "could not connect to ${ip}:$port within 5 s" }
  $script:stream = $c.GetStream()

  [void](Request 'OPTIONS' $url @())
  $d = Request 'DESCRIBE' $url @('Accept: application/sdp')
  if ($d.Head -notmatch 'RTSP/1.0 200') { throw 'DESCRIBE failed' }
  $base = $url + '/'
  if ($d.Head -match '(?im)^Content-Base:\s*(\S+)') { $base = $Matches[1] }
  $control = 'trackID=0'
  if ($d.Body -match '(?im)^m=video[\s\S]*?^a=control:(\S+)') { $control = $Matches[1] }
  $trackUrl = if ($control -match '^rtsp://') { $control } else { $base + $control }

  $udpRtp = $null; $udpRtcp = $null
  if ($Udp) {
    $cp = 50000 + 2 * (Get-Random -Maximum 5000)
    $udpRtp = New-Object System.Net.Sockets.UdpClient $cp
    $udpRtcp = New-Object System.Net.Sockets.UdpClient ($cp + 1)
    $udpRtp.Client.ReceiveBufferSize = 8MB
    $s = Request 'SETUP' $trackUrl @("Transport: RTP/AVP;unicast;client_port=$cp-$($cp + 1)")
    if ($s.Head -notmatch 'RTSP/1.0 200') { throw 'UDP SETUP failed (is "Allow UDP" on?)' }
    # Punch through the PC firewall/NAT the way ffmpeg does: send one small packet to each phone port first.
    if ($s.Head -match 'server_port=(\d+)-(\d+)') {
      $punch = [byte[]](0x80, 96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
      [void]$udpRtp.Send($punch, $punch.Length, $ip, [int]$Matches[1])
      [void]$udpRtcp.Send($punch, $punch.Length, $ip, [int]$Matches[2])
    } else { L 'NOTE: no server_port in the SETUP answer' }
  } else {
    $u = Request 'SETUP' $trackUrl @('Transport: RTP/AVP;unicast;client_port=50000-50001')
    $udpRefused = $u.Head -match 'RTSP/1.0 461'
    if ($u.Head -match 'RTSP/1.0 200') {
      L 'NOTE: phone accepted UDP (Allow UDP is on); asking for TCP on a new connection'
      $c.Close(); $script:len = 0
      $c = New-Object System.Net.Sockets.TcpClient; $c.NoDelay = $true
      if (-not $c.ConnectAsync($ip, $port).Wait(5000)) { throw 'reconnect failed' }
      $script:stream = $c.GetStream()
    }
    $s = Request 'SETUP' $trackUrl @('Transport: RTP/AVP/TCP;unicast;interleaved=0-1')
    if ($s.Head -notmatch 'RTSP/1.0 200') { throw 'SETUP failed' }
  }
  $session = ''
  if ($s.Head -match '(?im)^Session:\s*([^;\r\n]+)') { $session = $Matches[1].Trim() }

  # ---- audio track (if the phone offers one)
  $audioOn = $false; $udpARtp = $null; $udpARtcp = $null
  if (-not $NoAudio -and $d.Body -match '(?im)^m=audio[\s\S]*?^a=control:(\S+)') {
    $aControl = $Matches[1]
    $aUrl = if ($aControl -match '^rtsp://') { $aControl } else { $base + $aControl }
    if ($d.Body -match 'mpeg4-generic/(\d+)/(\d+)') { $audioRate = [int]$Matches[1]; $audioChannels = [int]$Matches[2] }
    $rates = @(96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000)
    $audioFreqIdx = [Array]::IndexOf($rates, $audioRate); if ($audioFreqIdx -lt 0) { $audioFreqIdx = 3 }
    if ($Udp) {
      $udpARtp = New-Object System.Net.Sockets.UdpClient ($cp + 2)
      $udpARtcp = New-Object System.Net.Sockets.UdpClient ($cp + 3)
      $as = Request 'SETUP' $aUrl @("Transport: RTP/AVP;unicast;client_port=$($cp + 2)-$($cp + 3)", "Session: $session")
      if ($as.Head -match 'server_port=(\d+)-(\d+)') {
        $punch = [byte[]](0x80, 97, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        [void]$udpARtp.Send($punch, $punch.Length, $ip, [int]$Matches[1])
        [void]$udpARtcp.Send($punch, $punch.Length, $ip, [int]$Matches[2])
      }
    } else {
      $as = Request 'SETUP' $aUrl @('Transport: RTP/AVP/TCP;unicast;interleaved=2-3', "Session: $session")
    }
    if ($as.Head -match 'RTSP/1.0 200') { $audioOn = $true } else { L 'NOTE: audio SETUP failed' }
  } elseif (-not $NoAudio) { L 'NOTE: the phone offers no audio track' }
  $p = Request 'PLAY' $base @("Session: $session", 'Range: npt=0.000-')
  if ($p.Head -notmatch 'RTSP/1.0 200') { throw 'PLAY failed' }

  # ---- capture
  $sw = [Diagnostics.Stopwatch]::StartNew()
  if (-not $NoSave) { $fs = [IO.File]::Create($out); if ($audioOn) { $afs = [IO.File]::Create($outAudio) } }
  $nextKeepAlive = 20; $nextMinute = 60
  $ep = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0
  while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    if ($sw.Elapsed.TotalSeconds -ge $nextKeepAlive) {
      $nextKeepAlive += 20
      Send 'GET_PARAMETER' $base @("Session: $session")
    }
    if ($sw.Elapsed.TotalSeconds -ge $nextMinute) {
      L ("{0:HH:mm:ss}  minute {1}: {2} frames, {3} sequence gaps" -f (Get-Date), ($nextMinute / 60), $Stats.minuteFrames, $Stats.minuteGaps)
      $nextMinute += 60; $Stats.minuteFrames = 0; $Stats.minuteGaps = 0
    }
    if ($Udp) {
      $got = $false
      while ($udpRtp.Available -gt 0) { $dg = $udpRtp.Receive([ref]$ep); OnRtp $dg 0 $dg.Length; $got = $true }
      while ($udpRtcp.Available -gt 0) { $dg = $udpRtcp.Receive([ref]$ep); OnRtcp $dg 0 $dg.Length }
      if ($udpARtp) {
        while ($udpARtp.Available -gt 0) { $dg = $udpARtp.Receive([ref]$ep); OnAudioRtp $dg 0 $dg.Length }
        while ($udpARtcp.Available -gt 0) { $dg = $udpARtcp.Receive([ref]$ep); OnRtcp $dg 0 $dg.Length -Audio }
      }
    }
    # the TCP connection carries RTSP answers (and, in TCP mode, the video)
    $pos = 0
    while ($script:len - $pos -ge 4) {
      if ($script:buf[$pos] -ne 36) {
        $s2 = [Text.Encoding]::ASCII.GetString($script:buf, $pos, [Math]::Min($script:len - $pos, 4096))
        $e2 = $s2.IndexOf("`r`n`r`n")
        if ($e2 -lt 0) { break }
        $first = $s2.Split("`n")[0].Trim()
        if ($first -match '^RTSP/1.0 401' -and $s2.Substring(0, $e2) -match '(?i)Digest realm="([^"]*)",\s*nonce="([^"]*)",\s*stale=TRUE') {
          # nonces last 5 minutes: take the new one for the next keep-alive
          $script:auth = @{ Realm = $Matches[1]; Nonce = $Matches[2] }; L 'Keep-alive: login nonce renewed'
        } elseif ($first -notmatch '200 OK') { L "Unexpected answer during play: $first" }
        $pos += $e2 + 4
        continue
      }
      $ch = $script:buf[$pos + 1]
      $plen = $script:buf[$pos + 2] * 256 + $script:buf[$pos + 3]
      if ($script:len - $pos -lt 4 + $plen) { break }
      $o = $pos + 4
      $pos += 4 + $plen
      if ($ch -eq 0) { OnRtp $script:buf $o $plen } elseif ($ch -eq 1) { OnRtcp $script:buf $o $plen }
      elseif ($ch -eq 2) { OnAudioRtp $script:buf $o $plen } elseif ($ch -eq 3) { OnRtcp $script:buf $o $plen -Audio }
    }
    if ($pos -gt 0) { [Array]::Copy($script:buf, $pos, $script:buf, 0, $script:len - $pos); $script:len -= $pos }
    if (-not (Fill $(if ($Udp) { 5 } else { 1000 }))) { if (-not $Udp -and $sw.Elapsed.TotalSeconds -gt 10 -and $Stats.packets -eq 0) { throw 'no video' } }
  }
  if ($fs) { $fs.Close() }
  if ($afs) { $afs.Close() }
  if ($Abrupt) {
    $c.Client.LingerState = New-Object System.Net.Sockets.LingerOption($true, 0)  # reset, no goodbye
    $c.Close(); L 'Dropped the connection abruptly (no TEARDOWN)'
  } else {
    try { [void](Request 'TEARDOWN' $base @("Session: $session")) } catch { L "TEARDOWN: $_" }
    $c.Close()
  }
  if ($udpRtp) { $udpRtp.Close(); $udpRtcp.Close() }
  if ($udpARtp) { $udpARtp.Close(); $udpARtcp.Close() }

  $secs = [Math]::Max(0.001, ($sw.ElapsedMilliseconds - [Math]::Max(0, $Stats.firstMs)) / 1000.0)
  $span = if ($null -ne $Stats.firstTs) { (($Stats.lastTs - $Stats.firstTs + 4294967296) % 4294967296) / 90000.0 } else { 0 }
  L ''
  L '=== RESULT ==='
  L ("Transport: {0}" -f $(if ($Udp) { 'UDP' } else { 'TCP (interleaved)' }))
  L ("First video after PLAY: {0} ms" -f $Stats.firstMs)
  L ("RTP packets: {0}, frames (marker bits): {1}, keyframes: {2}" -f $Stats.packets, $Stats.frames, $Stats.idr)
  L ("Average: {0:0.0} fps, {1:0.00} Mbps over {2:0.0} s" -f ($Stats.frames / $secs), ($Stats.bytes * 8 / $secs / 1e6), $secs)
  L ("RTP timestamp span: {0:0.00} s (should be close to the time above); went backwards: {1}" -f $span, $Stats.back)
  L ("Sequence gaps: {0} ({1} packets lost); SSRCs seen: {2}" -f $Stats.gaps, $Stats.lost, $Stats.ssrcs.Count)
  L ("Keyframes with SPS+PPS in front: {0} of {1}" -f $Stats.psIdr, $Stats.idr)
  L ("RTCP sender reports: {0}; largest phone-vs-PC clock difference: {1:0} ms" -f $Stats.sr, $Stats.srMaxOffsetMs)
  L ("NAL types: " + (($Stats.nal.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '))
  if ($fs) { L "Video saved to $out" }
  $ok = $Stats.frames -gt 0 -and $Stats.idr -gt 0 -and $Stats.gaps -eq 0 -and $Stats.back -eq 0 -and $Stats.psIdr -eq $Stats.idr -and ($Stats.sr -gt 0 -or $secs -lt 6)
  if ($audioOn) {
    $expected = $audioRate / 1024.0
    $afps = $Stats.aframes / $secs
    L ("Audio: AAC {0} Hz, {1} ch; {2} frames ({3:0.0}/s, expected {4:0.0}/s), {5:0.0} kbps" -f $audioRate, $audioChannels, $Stats.aframes, $afps, $expected, ($Stats.abytes * 8 / $secs / 1000))
    L ("Audio: sequence gaps {0}; timestamp steps other than 1024: {1}; went backwards: {2}; RTCP sender reports: {3}" -f $Stats.agaps, $Stats.aBadStep, $Stats.aBack, $Stats.asr)
    if ($afs) { L "Audio saved to $outAudio" }
    $aok = $Stats.aframes -gt 0 -and [Math]::Abs($afps - $expected) -lt $expected * 0.1 -and $Stats.aBack -eq 0 -and ($Udp -or $Stats.agaps -eq 0) -and ($Stats.asr -gt 0 -or $secs -lt 6)
    $ok = $ok -and $aok
  }
  L ($(if ($ok) { 'PASS' } else { 'CHECK THE NUMBERS ABOVE' }))
} catch {
  L "ERROR: $_"
}

try {
  L ("Status: " + (New-Object System.Net.WebClient).DownloadString("http://${PhoneIp}:5001/status"))
} catch { L "Status fetch failed: $_" }
try {
  (New-Object System.Net.WebClient).DownloadString("http://${PhoneIp}:5001/") | Out-File (Join-Path $PSScriptRoot 'phone-log.txt') -Encoding utf8
  L 'Phone log saved to tools\phone-log.txt'
} catch { L "Phone log fetch failed: $_" }
L "Done $(Get-Date)"
