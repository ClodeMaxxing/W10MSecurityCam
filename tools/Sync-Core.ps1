# Keeps the source files that W10MSecurityCam, W10MDashcam and W10MWebCam share in step. The same script is in all
# three repositories' tools folders; it finds out which one it is in from the .sln next to tools (or the folder name).
#
#   powershell -ExecutionPolicy Bypass -File tools\Sync-Core.ps1            same as -Check
#   ... -Check                  compare the shared files, print a table, exit 1 when one has drifted
#   ... -Fix -From W10MDashcam  copy that app's copy of every shared file over the others (namespace renamed, each
#                               target keeps its own line endings); -Files limits it, e.g. -Files Streaming\AnnexB.cs
#   ... -SecurityCam DIR -Dashcam DIR -WebCam DIR   where the three repositories are (defaults: C:\Claude\...)
#
# Files are compared after normalising line endings and the three app names (namespaces, InspectableClass names and
# comments that name an app). The differences that are meant to be there are listed in $Expected below; -Fix never
# touches a file with such a difference (merge those by hand).
param(
  [switch]$Check, [switch]$Fix, [string]$From, [string[]]$Files,
  [string]$SecurityCam = 'C:\Claude\LumiaSecurityCam',
  [string]$Dashcam = 'C:\Claude\W10MDashcam',
  [string]$WebCam = 'C:\Claude\W10MWebCam'
)
$ErrorActionPreference = 'Stop'

$Apps = [ordered]@{ W10MSecurityCam = $SecurityCam; W10MDashcam = $Dashcam; W10MWebCam = $WebCam }
$All = @('W10MSecurityCam', 'W10MDashcam', 'W10MWebCam')
$ScDc = @('W10MSecurityCam', 'W10MDashcam')

# The shared files: path under <App>\ (C#) or <App>.Native\ (native sink), and the apps that have them.
$Shared = @(
  @{ Path = 'Streaming\AnnexB.cs';           Apps = $All }
  @{ Path = 'Streaming\AvcConfigReader.cs';  Apps = $All }
  @{ Path = 'Streaming\Mp4H264Extractor.cs'; Apps = $All }
  @{ Path = 'Streaming\Mp4TapStream.cs';     Apps = $All }
  @{ Path = 'Streaming\PowerMonitor.cs';     Apps = $All }
  @{ Path = 'Streaming\CameraTuning.cs';     Apps = $All }
  @{ Path = 'Rtsp\DigestAuth.cs';            Apps = $ScDc }
  @{ Path = 'Rtsp\RtpPacketizer.cs';         Apps = $ScDc }
  @{ Path = 'Rtsp\Sdp.cs';                   Apps = $ScDc }
  @{ Path = 'EncodedFrameSink.cpp';  Native = $true; Apps = $ScDc }
  @{ Path = 'EncodedFrameSink.h';    Native = $true; Apps = $ScDc }
  @{ Path = 'EncodedMediaSink.cpp';  Native = $true; Apps = $ScDc }
  @{ Path = 'EncodedMediaSink.h';    Native = $true; Apps = $ScDc }
)

# Differences that are meant to be there, per file and app, as normalised lines (app names read APP, compared without
# leading and trailing spaces). Extra: lines only that app has. Missing: lines the others have and that app has not.
# A @{ From = regex; To = regex } entry is a block of lines (whole lines, spaces kept), with the empty line before it.
$Expected = @{
  'Streaming\CameraTuning.cs|W10MWebCam' = @{
    # A webcam subject moves: holding focus and stabilisation off are opt-in there. And where the class came from.
    Extra = @(
      '/// Ported from APP 1.1 (Streaming/CameraTuning.cs).'
      '// ---- camera power saving (less processing and heat). Off by default in APP: a webcam subject moves,'
      '// so holding focus is opt-in, and stabilization off is only right on a fixed mount.'
      'public bool SaveOis = false, SaveFocus = false;'
      '// reading at the end stop is replaced by the last sensible one (APP: switching to manual showed'
      '// "focus 0" and a blurred picture).'
    )
    Missing = @(
      '// ---- camera power saving (less processing and heat; each part can be switched off)'
      'public bool SaveOis = true, SaveFocus = true;'
      '// reading at the end stop is replaced by the last sensible one (switching to manual showed "focus 0" and a'
      '// blurred picture; fix from APP).'
    )
  }
  'Streaming\PowerMonitor.cs|W10MWebCam' = @{
    # PowerMonitor.Dump() is only used by probe=power, which W10MWebCam does not have.
    Missing = @( @{ From = '^        /// <summary>Everything the battery report and the processor counters return'; To = '^        }$' } )
  }
}

function Find-Here {
  $root = Split-Path $PSScriptRoot -Parent
  $sln = Get-ChildItem $root -Filter *.sln -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($sln -and $Apps.Contains($sln.BaseName)) { return $sln.BaseName }
  $name = Split-Path $root -Leaf
  if ($name -eq 'LumiaSecurityCam') { return 'W10MSecurityCam' }
  if ($Apps.Contains($name)) { return $name }
  return $null
}

function File-Of([string]$app, $entry) {
  $sub = if ($entry.Native) { "$app.Native" } else { $app }
  Join-Path (Join-Path $Apps[$app] $sub) $entry.Path
}

function Normalise([string]$text) {
  $t = $text -replace "`r`n", "`n" -replace "`r", "`n"
  $t = $t -replace 'W10MSecurityCam|W10MDashcam|W10MWebCam', 'APP'
  $lines = @($t -split "`n")
  if ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = @($lines[0..($lines.Count - 2)]) }
  return ,$lines
}

# Takes the expected lines and blocks out of a copy (first match of each, in order of the list).
function Remove-Expected([string[]]$lines, $list) {
  $out = New-Object System.Collections.Generic.List[string]
  $out.AddRange($lines)
  foreach ($e in @($list)) {
    if ($e -is [hashtable]) {
      $start = -1
      for ($i = 0; $i -lt $out.Count; $i++) { if ($out[$i] -match $e.From) { $start = $i; break } }
      if ($start -lt 0) { continue }
      $end = $start
      while ($end -lt $out.Count -and -not ($out[$end] -match $e.To)) { $end++ }
      if ($end -ge $out.Count) { continue }
      if ($start -gt 0 -and $out[$start - 1].Trim() -eq '') { $start-- }
      $out.RemoveRange($start, $end - $start + 1)
    } else {
      for ($i = 0; $i -lt $out.Count; $i++) { if ($out[$i].Trim() -eq $e) { $out.RemoveAt($i); break } }
    }
  }
  return ,$out.ToArray()
}

function First-Difference([string[]]$a, [string[]]$b) {
  $n = [Math]::Max($a.Count, $b.Count)
  for ($i = 0; $i -lt $n; $i++) {
    $x = if ($i -lt $a.Count) { $a[$i] } else { '<end of file>' }
    $y = if ($i -lt $b.Count) { $b[$i] } else { '<end of file>' }
    if ($x -ne $y) { return @{ Line = $i + 1; A = $x.Trim(); B = $y.Trim() } }
  }
  return $null
}

$here = Find-Here
if ($Fix -and -not $From) { $From = $here }
if (-not $Fix) { $Check = $true }
foreach ($a in $Apps.Keys) { if (-not (Test-Path $Apps[$a])) { throw "$a not found at $($Apps[$a]) (use -SecurityCam / -Dashcam / -WebCam)" } }
"Running in: " + $(if ($here) { $here } else { 'unknown repository' })

if ($Fix) {
  if (-not $Apps.Contains($From)) { throw "-From must be one of: $($All -join ', ')" }
  foreach ($entry in $Shared) {
    if ($Files -and $Files -notcontains $entry.Path) { continue }
    if ($entry.Apps -notcontains $From) { continue }
    $srcFile = File-Of $From $entry
    $src = [System.IO.File]::ReadAllText($srcFile)
    foreach ($app in $entry.Apps) {
      if ($app -eq $From) { continue }
      if ($Expected.ContainsKey("$($entry.Path)|$app") -or $Expected.ContainsKey("$($entry.Path)|$From")) {
        "{0,-32} {1,-16} skipped: it has expected differences, merge by hand" -f $entry.Path, $app; continue
      }
      $dstFile = File-Of $app $entry
      $old = if (Test-Path $dstFile) { [System.IO.File]::ReadAllText($dstFile) } else { '' }
      if (((Normalise $old) -join "`n") -ceq ((Normalise $src) -join "`n")) { continue }
      # The source app's name becomes the target's everywhere: the namespace, and also the RTSP realm, the password
      # vault entry and the SDP session name, which carry the app name. Names of the other apps stay as they are.
      $text = $src -creplace "\b$From\b", $app
      $crlf = if ($old) { $old.Contains("`r`n") } else { $src.Contains("`r`n") }
      $text = $text -replace "`r`n", "`n"
      if ($crlf) { $text = $text -replace "`n", "`r`n" }
      [System.IO.File]::WriteAllText($dstFile, $text, (New-Object System.Text.UTF8Encoding($false)))
      "{0,-32} {1,-16} copied from {2}" -f $entry.Path, $app, $From
    }
  }
  return
}

$drift = 0
$rows = @()
$notes = @()
foreach ($entry in $Shared) {
  if ($Files -and $Files -notcontains $entry.Path) { continue }
  $ref = $entry.Apps[0]
  $refLines = Normalise ([System.IO.File]::ReadAllText((File-Of $ref $entry)))
  $row = [ordered]@{ File = $entry.Path }
  foreach ($app in $All) {
    if ($entry.Apps -notcontains $app) { $row[$app] = '-'; continue }
    $file = File-Of $app $entry
    if (-not (Test-Path $file)) { $row[$app] = 'MISSING'; $drift++; continue }
    if ($app -eq $ref) { $row[$app] = 'ref'; continue }
    $lines = Normalise ([System.IO.File]::ReadAllText($file))
    $key = "$($entry.Path)|$app"
    $a = $refLines; $b = $lines; $mark = 'same'
    if ($Expected.ContainsKey($key)) {
      $a = Remove-Expected $refLines $Expected[$key].Missing
      $b = Remove-Expected $lines $Expected[$key].Extra
      $mark = 'same*'
    }
    $d = First-Difference $a $b
    if ($d) {
      $row[$app] = 'DRIFT'; $drift++
      $notes += "{0} ({1} vs {2}), first difference near line {3}:`n    {2}: {4}`n    {1}: {5}" -f $entry.Path, $app, $ref, $d.Line, $d.A, $d.B
    } else { $row[$app] = $mark }
  }
  $rows += New-Object PSObject -Property $row
}
$rows | Format-Table File, W10MSecurityCam, W10MDashcam, W10MWebCam -AutoSize | Out-String -Width 200
"ref = compared against; same* = same apart from the expected differences listed in the script; - = not shared"
foreach ($n in $notes) { $n }
if ($drift -gt 0) { "$drift shared file(s) have drifted."; exit 1 }
'All shared files are in step.'
exit 0
