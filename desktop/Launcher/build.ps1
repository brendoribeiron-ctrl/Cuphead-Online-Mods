# Compila o launcher "Cuphead Online.exe" e cria o atalho na area de trabalho.
# Uso: powershell -ExecutionPolicy Bypass -File Launcher\build.ps1 [-GameDir <pasta do Cuphead>]
param([string]$GameDir = (Join-Path $PSScriptRoot "..\..\..\Cuphead"))
$GameDir = (Resolve-Path $GameDir).Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $GameDir "Cuphead Online.exe"
# O exe leva o Assembly-CSharp.dll do mod dentro (compile o mod antes com dotnet build -c Release)
$mod = Join-Path $PSScriptRoot "..\bin\Release\net35\Assembly-CSharp.dll"
if (-not (Test-Path $mod)) { throw "Compile o mod antes: $mod nao existe" }
& $csc /nologo /target:winexe /win32icon:"$PSScriptRoot\cuphead.ico" /resource:"$mod",Assembly-CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$out" "$PSScriptRoot\CupheadOnlineLauncher.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) "Cuphead Online.lnk"))
$lnk.TargetPath = $out; $lnk.WorkingDirectory = $GameDir; $lnk.IconLocation = "$out,0"; $lnk.Save()
"Launcher: $out"
