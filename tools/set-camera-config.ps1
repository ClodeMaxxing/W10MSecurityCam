param(
  [string]$Body = '',                  # e.g. "user=admin&password=secret" or "udp=on"; empty = ask interactively
  [string]$User = $env:W10M_USER,      # the current stream login (asked for when not given)
  [string]$Password = $env:W10M_PASSWORD
)
# Changes W10MSecurityCam settings over the USB cable (the phone only accepts /config over USB, and it needs the login).
# Fields: user, password (empty = back to the default login), basic=on|off, udp=on|off, snapshot=on|off
# Set W10M_USER / W10M_PASSWORD to skip the login questions (e.g. for Test-Phase3-4.cmd).
$url = 'http://127.0.0.1:8020/config'
if (-not $User) { $User = Read-Host 'Current login - user name (Enter = admin)'; if (-not $User) { $User = 'admin' } }
if (-not $Password) {
  $sec = Read-Host 'Current login - password' -AsSecureString
  $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
$wc = New-Object System.Net.WebClient
$wc.Credentials = New-Object System.Net.NetworkCredential($User, $Password)
try {
  if (-not $Body) {
    Write-Host 'Current settings:'
    Write-Host $wc.DownloadString($url)
    Write-Host ''
    $u = Read-Host 'New user name (Enter = admin)'
    if (-not $u) { $u = 'admin' }
    $p = Read-Host 'New password (Enter = back to the default login)'
    $Body = 'user=' + [Uri]::EscapeDataString($u) + '&password=' + [Uri]::EscapeDataString($p)
  }
  $wc.Headers.Add('Content-Type', 'application/x-www-form-urlencoded')
  $wc.Headers.Add('X-Requested-With', 'W10MSecurityCam') # the phone refuses changes without it
  $r = $wc.UploadString($url, 'POST', $Body)
  Write-Host $r
  $r | Out-File (Join-Path $PSScriptRoot 'config-result.txt') -Encoding utf8
} catch {
  $resp = $_.Exception.InnerException.Response; if (-not $resp) { $resp = $_.Exception.Response }
  if ($resp -and [int]$resp.StatusCode -eq 401) { $m = 'The phone refused the login (wrong user name or password).' }
  else { $m = "Could not reach the phone over USB: $_" }
  Write-Host $m
  $m | Out-File (Join-Path $PSScriptRoot 'config-result.txt') -Encoding utf8
}
