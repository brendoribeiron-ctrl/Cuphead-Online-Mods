# Testa o online numa maquina so: compila um checkout, instala na copia de teste,
# liga o proxy com internet ruim e abre duas janelas do jogo (host e amigo).
#
#   .\run-local.ps1 [-Repo pasta do checkout] [-Delay 60] [-Jitter 15] [-Loss 1] [-Kbps 0]
#                   [-HostArgs "-host"] [-JoinArgs "-join 127.0.0.1:{port}"] [-NoBuild] [-NoProxy]
#
# {port} em JoinArgs vira a porta do proxy (ou a do host com -NoProxy).
# Logs: Tools\TestGame\host.log e Tools\TestGame\join.log ; proxy: Tools\TestGame\proxy.log
param(
	[string]$Repo = (Resolve-Path "$PSScriptRoot\..\..").Path,
	[int]$HostPort = 7777,
	[int]$ProxyPort = 7778,
	[double]$Delay = 60,
	[double]$Jitter = 15,
	[double]$Loss = 0,
	[double]$Kbps = 0,
	[string]$HostArgs = "-host -port {hostport}",
	[string]$JoinArgs = "-join 127.0.0.1:{port}",
	[switch]$NoBuild,
	[switch]$NoProxy
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path
$dotnet = "$root\Tools\dotnet\dotnet.exe"
$test = "$root\Tools\TestGame"

if (-not $NoBuild) {
	& $dotnet build "$Repo\Assembly-CSharp.csproj" -c Release -nologo -v q
	if ($LASTEXITCODE -ne 0) { throw "build falhou" }
}
& "$PSScriptRoot\make-testgame.ps1" -Dest $test -Dll "$Repo\bin\Release\net35\Assembly-CSharp.dll"

$netlab = "$PSScriptRoot\..\NetLab\bin\Release\net8.0-windows\NetLab.exe"
if (-not (Test-Path $netlab)) {
	& $dotnet build "$PSScriptRoot\..\NetLab\NetLab.csproj" -c Release -nologo -v q
}

Get-Process Cuphead -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$test*" } | Stop-Process -Force

$port = $HostPort
if (-not $NoProxy) {
	$port = $ProxyPort
	Start-Process $netlab -ArgumentList "proxy --listen 127.0.0.1:$ProxyPort --target 127.0.0.1:$HostPort --delay $Delay --jitter $Jitter --loss $Loss --kbps $Kbps --udp" `
		-RedirectStandardOutput "$test\proxy.log" -WindowStyle Minimized
}
$win = "-screen-fullscreen 0 -screen-width 800 -screen-height 450 -popupwindow"
$h = $HostArgs.Replace("{hostport}", "$HostPort")
$j = $JoinArgs.Replace("{port}", "$port").Replace("{hostport}", "$HostPort")
Start-Process "$test\Cuphead.exe" -WorkingDirectory $test -ArgumentList "$win $h -logFile `"$test\host.log`""
Start-Sleep -Seconds 8
Start-Process "$test\Cuphead.exe" -WorkingDirectory $test -ArgumentList "$win $j -logFile `"$test\join.log`""
Write-Host "Host: $h | Amigo: $j | proxy: $(if ($NoProxy) {'desligado'} else {"atraso $Delay ms, perda $Loss%"})"
