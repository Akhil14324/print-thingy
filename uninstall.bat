@echo off
net session >nul 2>&1
if errorlevel 1 (echo Run as administrator. & pause & exit /b 1)
taskkill /IM PrintStation.exe /F >nul 2>&1
schtasks /Delete /TN PrintStation /F >nul 2>&1
netsh advfirewall firewall delete rule name="PrintStation" >nul 2>&1
echo Removed. You can delete this folder.
pause
