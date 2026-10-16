@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0dist
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%\web"
"%CSC%" /nologo /target:winexe /out:"%OUT%\PrintStation.exe" /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll "%~dp0src\Program.cs"
if errorlevel 1 exit /b 1
copy /y "%~dp0web\index.html" "%OUT%\web\" >nul
copy /y "%~dp0tools\SumatraPDF.exe" "%OUT%\" >nul
copy /y "%~dp0config.json" "%OUT%\" >nul
copy /y "%~dp0install.bat" "%OUT%\" >nul
copy /y "%~dp0uninstall.bat" "%OUT%\" >nul
echo Built to %OUT%
