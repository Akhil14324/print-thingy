@echo off
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo Asking for administrator permission...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
  exit /b
)
set DEST=C:\PrintStation
taskkill /IM PrintStation.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul
powershell -NoProfile -Command "Expand-Archive -Force -LiteralPath '%~dp0payload.zip' -DestinationPath '%DEST%'"
if errorlevel 1 (echo Could not unpack files. & pause & exit /b 1)
for /f "tokens=2 delims=:, " %%a in ('findstr /i "\"port\"" "%DEST%\config.json"') do set PORT=%%a
if "%PORT%"=="" set PORT=8080
netsh http delete urlacl url=http://+:%PORT%/ >nul 2>&1
netsh http add urlacl url=http://+:%PORT%/ sddl=D:(A;;GX;;;WD) >nul
netsh advfirewall firewall delete rule name="PrintStation" >nul 2>&1
netsh advfirewall firewall add rule name="PrintStation" dir=in action=allow protocol=TCP localport=%PORT% profile=any >nul
schtasks /Delete /TN PrintStation /F >nul 2>&1
schtasks /Create /TN PrintStation /TR "\"%DEST%\PrintStation.exe\"" /SC ONLOGON /RL LIMITED /F >nul
powercfg /change standby-timeout-ac 0 >nul
schtasks /Run /TN PrintStation >nul
echo.
echo ===============================================
echo  PrintStation installed in %DEST%
echo  It starts automatically at every login.
echo  Open this on phones (same Wi-Fi):
echo     http://%COMPUTERNAME%:%PORT%
for /f "tokens=2 delims=:" %%i in ('ipconfig ^| findstr /c:"IPv4"') do echo     or http:/%%i:%PORT%
echo ===============================================
pause
