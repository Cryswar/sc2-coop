# Builds SC2Coop.exe and the release zip. Usage: .\build.ps1 [-Out <folder>] [-Target winexe|exe]
# Needs the .NET Framework 4 compiler that ships with Windows, and lib\CascLib.dll and lib\steam_api64.dll (see README).
param([string]$Out="$PSScriptRoot\out",[string]$Target="winexe")
$ErrorActionPreference="Stop"
$root=$PSScriptRoot
$pkg=Join-Path $Out "SC2Coop"
if(Test-Path $pkg){Remove-Item $pkg -Recurse -Force}
New-Item -ItemType Directory -Force (Join-Path $pkg "third-party") | Out-Null

$sources=Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
& C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:$Target /platform:x64 "/out:$pkg\SC2Coop.exe" `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
  $sources
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}

Copy-Item "$root\adapters\*.galaxy" $pkg
Copy-Item "$root\package\README.txt","$root\package\bridge-calibration.json" $pkg
Copy-Item "$root\package\third-party\*" (Join-Path $pkg "third-party")
foreach($dll in "CascLib.dll","steam_api64.dll"){
  $p=Join-Path "$root\lib" $dll
  if(Test-Path $p){Copy-Item $p $pkg}else{Write-Warning "lib\$dll is missing; the zip won't have it (see README)"}
}

$zip=Join-Path $Out "SC2Coop.zip"
if(Test-Path $zip){Remove-Item $zip -Force}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($pkg,$zip)
Write-Host "Built $pkg\SC2Coop.exe and $zip"
