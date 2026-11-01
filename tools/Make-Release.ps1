# Packs the last build (out\<App>_*_ARM_Test) into release\<App>_<version>_ARM.zip with its three runtime packages, the
# test certificate and INSTALL.txt, and writes release\SHA256SUMS.txt. The same script is in all three repositories'
# tools folders; it finds the app's name from the .sln next to tools.
#   powershell -ExecutionPolicy Bypass -File tools\Make-Release.ps1                 version from the package (Major.Minor[.Build])
#   powershell -ExecutionPolicy Bypass -File tools\Make-Release.ps1 -Version 1.3    the release's own number (W10MSecurityCam: the
#                                                                                   package counts 1.9.x while the releases count 1.x)
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sln = Get-ChildItem $root -Filter *.sln | Select-Object -First 1
if (-not $sln) { throw 'No .sln next to tools\' }
$app = $sln.BaseName
$appx = Get-ChildItem (Join-Path $root 'out') -Recurse -Filter "${app}_*_ARM.appx" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw 'No build found in out\ - run tools\Build-And-Deploy.cmd first' }
$ver = ($appx.BaseName -replace "^${app}_", '' -replace '_ARM$', '')
$parts = $ver -split '\.'
$short = if ($Version) { $Version } elseif ($parts.Count -ge 3 -and $parts[2] -ne '0') { $parts[0..2] -join '.' } else { $parts[0..1] -join '.' }
$dep = Join-Path $appx.DirectoryName 'Dependencies\arm'
$rel = Join-Path $root 'release'
$stage = Join-Path $rel "${app}_${short}_ARM"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'Dependencies') | Out-Null
Copy-Item $appx.FullName $stage
Get-ChildItem $dep -Filter *.appx | Copy-Item -Destination (Join-Path $stage 'Dependencies')
Copy-Item (Join-Path $root "$app.cer") $stage
Copy-Item (Join-Path $PSScriptRoot 'INSTALL.txt') $stage
$zip = Join-Path $rel "${app}_${short}_ARM.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$sums = Get-ChildItem $rel -Filter *.zip | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() + '  ' + $_.Name }
$sums | Out-File (Join-Path $rel 'SHA256SUMS.txt') -Encoding ascii
"Release: $zip (package $ver)"
$sums
