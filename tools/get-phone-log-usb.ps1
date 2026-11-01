# Saves the phone's log (/log) to tools\phone-log.txt: over USB first, then Wi-Fi (address in tools\phone-ip.txt).
# The log page is off by default: switch it on in the app (Network and login > Log page) or on the settings page.
$out = Join-Path $PSScriptRoot 'phone-log.txt'
$ip = (Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)
$urls = @('http://127.0.0.1:8020/log'); if ($ip) { $urls += "http://$($ip.Trim()):5001/log" }
$result = $null
foreach ($u in $urls) {
  try { $result = (New-Object System.Net.WebClient).DownloadString($u); break }
  catch {
    $resp = $_.Exception.InnerException.Response; if (-not $resp) { $resp = $_.Exception.Response }
    if ($resp -and [int]$resp.StatusCode -eq 403) {
      $result = "The phone answered, but its log page is switched off.`r`nIn W10MSecurityCam: Network and login > Log page (or tick Log page on http://127.0.0.1:8020/camera), then run this again."
      break
    }
  }
}
if (-not $result) { $result = "Phone log fetch failed (USB and Wi-Fi). Is W10MSecurityCam open, and the cable plugged in?" }
$result | Out-File $out -Encoding utf8
