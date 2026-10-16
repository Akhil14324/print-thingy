# Builds one self-installing Setup .exe for a PC.
# Usage: powershell -File package.ps1 -Name PC1 -Title "Printer 1 - M126a" -Printer "M126" [-Pin 1234] [-Port 8080]
param(
  [Parameter(Mandatory)][string]$Name,
  [Parameter(Mandatory)][string]$Title,
  [string]$Printer = "",
  [string]$Pin = "",
  [int]$Port = 8080,
  [string]$PeerName = "",
  [string]$PeerUrl = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$work = Join-Path $root "build\$Name"
$out = Join-Path $root "out"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$work\app\web", $out | Out-Null

& $csc /nologo /target:winexe /out:"$work\app\PrintStation.exe" /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll "$root\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Copy-Item "$root\web\index.html" "$work\app\web\"
Copy-Item "$root\tools\SumatraPDF.exe" "$work\app\"
Copy-Item "$root\uninstall.bat" "$work\app\"

$cfg = [ordered]@{ title = $Title; printer = $Printer; port = $Port; pin = $Pin; maxMb = 50; dryRun = $false; peers = @() }
if ($PeerUrl) { $cfg.peers = @(@{ name = $PeerName; url = $PeerUrl }) }
($cfg | ConvertTo-Json -Depth 5) | Set-Content "$work\app\config.json" -Encoding ascii

Compress-Archive -Path "$work\app\*" -DestinationPath "$work\payload.zip" -Force
Copy-Item "$root\install.bat" "$work\install.bat"

$exe = Join-Path $out "PrintStation-$Name-Setup.exe"
Remove-Item $exe -ErrorAction SilentlyContinue
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$exe
FriendlyName=PrintStation Setup ($Name)
AppLaunched=cmd /c install.bat
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$work\
[SourceFiles0]
%FILE0%=
%FILE1%=
[Strings]
FILE0="install.bat"
FILE1="payload.zip"
"@
$sedPath = Join-Path $work "setup.sed"
Set-Content $sedPath $sed -Encoding ascii
$p = Start-Process iexpress.exe -ArgumentList "/N /Q setup.sed" -WorkingDirectory $work -Wait -PassThru
for ($i=0; $i -lt 30 -and -not (Test-Path $exe); $i++) { Start-Sleep 1 }
if (-not (Test-Path $exe)) { throw "IExpress did not produce $exe" }
Write-Host "Built $exe"
