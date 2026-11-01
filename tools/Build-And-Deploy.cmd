@echo off
setlocal
title W10MSecurityCam - Build and install on phone
set ROOT=%~dp0..
set LOG=%~dp0build-deploy-log.txt
set PHONE=
if exist "%~dp0phone-ip.txt" set /p PHONE=<"%~dp0phone-ip.txt"
if not defined PHONE echo  Tip: put the phone's IP address in tools\phone-ip.txt for the Wi-Fi fallback
echo Started %date% %time% > "%LOG%"

rem MSBuild of Visual Studio 2019 (2022 may not build these ARM .NET Native projects); the 2019 Community path if vswhere is missing
set MSBUILD=
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -version [16.0^,17.0^) -products * -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%i"
if not defined MSBUILD set MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe
echo MSBuild: %MSBUILD% >> "%LOG%"
if not exist "%MSBUILD%" (
  echo MSBuild not found >> "%LOG%"
  goto :done
)

echo [1/3] Building...
if exist "%ROOT%\out" rmdir /s /q "%ROOT%\out"
"%MSBUILD%" "%ROOT%\W10MSecurityCam.sln" -restore -m -nologo -v:minimal ^
  -p:Configuration=Release -p:Platform=ARM ^
  -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly ^
  -p:GenerateAppxPackageOnBuild=true ^
  -p:AppxPackageDir="%ROOT%\out\\" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> "%LOG%"
  goto :done
)
echo BUILD OK >> "%LOG%"

echo [2/3] Collecting package...
set APPX=
for /r "%ROOT%\out" %%f in (W10MSecurityCam_*_ARM.appx) do set "APPX=%%f"
for %%f in ("%APPX%") do set "PKGDIR=%%~dpf"
echo Package: %APPX% >> "%LOG%"
set DEP1=%PKGDIR%Dependencies\arm\Microsoft.NET.Native.Framework.1.7.appx
set DEP2=%PKGDIR%Dependencies\arm\Microsoft.NET.Native.Runtime.1.7.appx
set DEP3=%PKGDIR%Dependencies\arm\Microsoft.VCLibs.ARM.14.00.appx

set WADC=
for /f "delims=" %%i in ('where /r "%ProgramFiles(x86)%\Windows Kits\10\bin" WinAppDeployCmd.exe 2^>nul') do set "WADC=%%i"

echo [3/3] Installing on phone - trying USB first...
"%WADC%" install -file "%APPX%" -ip 127.0.0.1 -dependency "%DEP1%" "%DEP2%" "%DEP3%" > "%TEMP%\w10msecuritycam_deploy.txt" 2>&1
set RC=%errorlevel%
echo USB deploy exit %RC% >> "%LOG%"
if not %RC%==0 (
  echo  USB deploy failed, trying Wi-Fi %PHONE%...
  "%WADC%" install -file "%APPX%" -ip %PHONE% -dependency "%DEP1%" "%DEP2%" "%DEP3%" > "%TEMP%\w10msecuritycam_deploy.txt" 2>&1
  call set RC=%%errorlevel%%
)
type "%TEMP%\w10msecuritycam_deploy.txt" >> "%LOG%"
findstr /i /c:"pin" "%TEMP%\w10msecuritycam_deploy.txt" >nul
if %errorlevel%==0 (
  echo.
  echo  The phone needs pairing again. On the phone: Settings - Update and security -
  echo  For developers - Pair, then type the code shown.
  set /p PIN= Code: 
  call "%WADC%" install -file "%APPX%" -ip %PHONE% -pin %%PIN%% -dependency "%DEP1%" "%DEP2%" "%DEP3%" >> "%LOG%" 2>&1
  call set RC=%%errorlevel%%
)
echo DeployExitCode %RC% >> "%LOG%"

:done
echo Finished %date% %time% >> "%LOG%"
echo.
type "%LOG%" | findstr /i /c:"error" /c:"BUILD" /c:"Done" /c:"Exit" /c:"Finished"
echo.
echo  Window closes in 20 seconds.
timeout /t 20 >nul
