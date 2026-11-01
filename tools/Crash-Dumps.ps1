# Windows crash dumps for W10MSecurityCam, through the phone's Device Portal (the method that found the native-sink crash in
# W10MSecurityCam). Windows only keeps dumps for a package after collection is switched on for it, and the switch is
# per package VERSION, so run -Enable again after installing an update.
#
#   powershell -ExecutionPolicy Bypass -File tools\Crash-Dumps.ps1 -Pair 123456   first time: the PIN from the phone
#                                   (Settings > Update & security > For developers > Pair)
#   ... -Enable                     switch dump collection on for the installed W10MSecurityCam (keeps the last 3)
#   ... -List                       list the dumps on the phone (all apps)
#   ... -Get                        download W10MSecurityCam's dumps to tools\dump\
#   ... -Disable                    switch collection off again
#
# The phone is reached over Wi-Fi at the address in tools\phone-ip.txt (or -Phone ADDRESS).
param([string]$Pair, [switch]$Enable, [switch]$Disable, [switch]$List, [switch]$Get, [string]$Phone)
$ErrorActionPreference = 'Stop'
if (-not $Phone) { $Phone = (Get-Content "$PSScriptRoot\phone-ip.txt" -TotalCount 1 -ErrorAction SilentlyContinue) }
if (-not $Phone) { throw 'Put the phone''s Wi-Fi address in tools\phone-ip.txt or give -Phone ADDRESS' }
$Phone = $Phone.Trim()
$base = "https://$Phone"
$dir = Join-Path $env:LOCALAPPDATA 'W10MSecurityCam'; New-Item -ItemType Directory -Force $dir | Out-Null
$jar = Join-Path $dir 'portal.jar'
# A pairing made by the sister apps' tools works too.
if (-not (Test-Path $jar)) {
  foreach ($other in 'W10MWebCam', 'W10MDashcam') {
    $j = Join-Path $env:LOCALAPPDATA "$other\portal.jar"
    if (Test-Path $j) { Copy-Item $j $jar; break }
  }
}
function Csrf { $l = Select-String -Path $jar -Pattern 'CSRF-Token' | Select-Object -Last 1; if ($l) { ($l.Line -split "`t")[-1] } else { '' } }
function Refresh { curl.exe -sk -m 15 -b $jar -c $jar -o NUL "$base/api/os/info" | Out-Null }
function Api([string]$method, [string]$path, [string]$out) {
  Refresh
  $a = @('-sk', '-m', '60', '-b', $jar, '-c', $jar, '-X', $method, '-H', "X-CSRF-Token: $(Csrf)")
  if ($method -ne 'GET') { $a += @('-H', 'Content-Length: 0') }
  if ($out) { $a += @('-o', $out, '-w', '%{http_code}') } else { $a += @('-w', "`n%{http_code}") }
  & curl.exe @a "$base$path"
}

if ($Pair) {
  Remove-Item $jar -ErrorAction SilentlyContinue
  curl.exe -sk -c $jar -o NUL "$base/authenticate.htm" | Out-Null
  curl.exe -sk -b $jar -c $jar -X POST -H "X-CSRF-Token: $(Csrf)" -H 'Content-Length: 0' "$base/api/authorize/pair?pin=$Pair&persistent=1" | Out-Null
  'Paired (if the PIN was right).'
}

$pkgs = (curl.exe -sk -m 20 -b $jar "$base/api/app/packagemanager/packages" | ConvertFrom-Json).InstalledPackages
if (-not $pkgs) { throw 'Device Portal did not answer: is Device Portal on, and paired? (run with -Pair PIN)' }
$pfn = ($pkgs | Where-Object { $_.PackageFullName -like 'W10MSecurityCam_*' } | Select-Object -First 1).PackageFullName
if (-not $pfn) { throw 'W10MSecurityCam is not installed on the phone' }
"Installed: $pfn"
$q = [uri]::EscapeDataString($pfn)

if ($Enable)  { $r = Api 'POST' "/api/debug/dump/usermode/crashcontrol?packageFullName=$q&maxCount=3"; "Crash dumps on (HTTP $(($r -split "`n")[-1])). Run -Enable again after each app update." }
if ($Disable) { $r = Api 'DELETE' "/api/debug/dump/usermode/crashcontrol?packageFullName=$q"; "Crash dumps off (HTTP $(($r -split "`n")[-1]))." }
$state = curl.exe -sk -m 20 -b $jar "$base/api/debug/dump/usermode/crashcontrol?packageFullName=$q"
"Collection setting: $state"

if ($List -or $Get) {
  $dumps = (curl.exe -sk -m 20 -b $jar "$base/api/debug/dump/usermode/dumps" | ConvertFrom-Json).CrashDumps
  if (-not $dumps) { 'No crash dumps on the phone.' }
  $dumps | Format-Table FileDate, FileName, FileSize, PackageFullName -AutoSize | Out-String -Width 220
  if ($Get) {
    $out = Join-Path $PSScriptRoot 'dump'; New-Item -ItemType Directory -Force $out | Out-Null
    foreach ($d in ($dumps | Where-Object { $_.PackageFullName -like 'W10MSecurityCam_*' })) {
      $file = Join-Path $out $d.FileName
      $code = Api 'GET' ("/api/debug/dump/usermode/crashdump?packageFullName=" + [uri]::EscapeDataString($d.PackageFullName) + "&fileName=" + [uri]::EscapeDataString($d.FileName)) $file
      "Saved $file (HTTP $code)"
    }
  }
}
