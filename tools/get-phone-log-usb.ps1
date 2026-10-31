param(
  [string]$User = $env:W10M_USER,      # the stream login (asked for when not given)
  [string]$Password = $env:W10M_PASSWORD
)
# Saves the phone's log (/log) to tools\phone-log.txt: over USB first, then Wi-Fi (address in tools\phone-ip.txt).
# The log needs the stream login, over USB too. Set W10M_USER / W10M_PASSWORD to skip the questions.
$out = Join-Path $PSScriptRoot 'phone-log.txt'
if (-not $User) { $User = Read-Host 'Login - user name (Enter = admin)'; if (-not $User) { $User = 'admin' } }
if (-not $Password) {
  $sec = Read-Host 'Login - password' -AsSecureString
  $Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
$ip = (Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)
$urls = @('http://127.0.0.1:8020/log'); if ($ip) { $urls += "http://$($ip.Trim()):5001/log" }
$result = $null
foreach ($u in $urls) {
  $wc = New-Object System.Net.WebClient
  $wc.Credentials = New-Object System.Net.NetworkCredential($User, $Password)
  try { $result = $wc.DownloadString($u); break }
  catch {
    $resp = $_.Exception.InnerException.Response; if (-not $resp) { $resp = $_.Exception.Response }
    if ($resp) {
      # The phone answered: wrong login, or the page is switched off. Its reply says which.
      $text = ''; try { $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
      if ([int]$resp.StatusCode -eq 401) { $text = 'The phone refused the login (wrong user name or password).' }
      $result = "The phone answered $([int]$resp.StatusCode) at ${u}:`r`n$text"
      break
    }
  }
}
if (-not $result) { $result = "Phone log fetch failed (USB and Wi-Fi). Is the app open, and the cable plugged in?" }
$result | Out-File $out -Encoding utf8
