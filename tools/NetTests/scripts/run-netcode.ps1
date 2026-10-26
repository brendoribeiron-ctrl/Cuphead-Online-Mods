# Teste automatico do netcode lockstep com duas janelas do jogo, bots nos controles
# e o proxy do NetLab no meio. No fim compara os "[Netcode] snap frame F hash H" dos dois logs.
#
#   .\run-netcode.ps1 -Dll <Assembly-CSharp.dll> [-Seconds 120] [-Delay 40] [-Jitter 10] [-Loss 0] [-Level Veggies]
# -Lobby usa o lobby de verdade (-host/-join/-autostart/-autoready) em vez do transporte de teste do netcode.
# -CutAfter N derruba a conexao no proxy depois de N segundos (testa a reconexao).
# Codigo de saida 0 = os dois lados chegaram nos mesmos hashes e sem dessincronia.
param(
	[Parameter(Mandatory = $true)][string]$Dll,
	[int]$Seconds = 120,
	[double]$Delay = 40,
	[double]$Jitter = 10,
	[double]$Loss = 0,
	[string]$Level = "",
	[int]$HostPort = 17861,
	[int]$ProxyPort = 17862,
	[string]$Tag = "netcode",
	[int]$Slot = 0,
	[double]$MinFps = 45,
	[double]$CutAfter = 0,
	[switch]$Lobby,
	[string]$Mode = "sync",
	[string]$Saves = "$env:APPDATA\Cuphead",
	# argumentos extras para os dois jogos (ex.: -Extra "-netcode-testwin 600")
	[string]$Extra = ""
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path
$test = "$root\Tools\TestGame"
& "$PSScriptRoot\make-testgame.ps1" -Dest $test -Dll $Dll | Out-Null
$netlab = "$PSScriptRoot\..\NetLab\bin\Release\net8.0-windows\NetLab.exe"
if (-not (Test-Path $netlab)) {
	& "$root\Tools\dotnet\dotnet.exe" build "$PSScriptRoot\..\NetLab\NetLab.csproj" -c Release -nologo -v q | Out-Null
}
function Stop-Test {
	Get-Process Cuphead -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$test*" } | Stop-Process -Force
	Get-Process NetLab -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*NetTests*" } | Stop-Process -Force
}
Stop-Test
$hlog = "$test\$Tag-host.log"; $jlog = "$test\$Tag-join.log"; $plog = "$test\$Tag-proxy.log"
Remove-Item $hlog, $jlog, $plog, "$test\$Tag-*-unity.log" -ErrorAction SilentlyContinue
Start-Process $netlab -ArgumentList "proxy --listen 127.0.0.1:$ProxyPort --target 127.0.0.1:$HostPort --delay $Delay --jitter $Jitter --loss $Loss --cut-after $CutAfter" `
	-RedirectStandardOutput $plog -WindowStyle Hidden
# Cada instancia joga numa copia dos saves (-savedir): o save de verdade nunca e tocado
foreach ($side in "host", "join") {
	$dir = "$test\saves-$side\Cuphead"
	Remove-Item "$test\saves-$side" -Recurse -Force -ErrorAction SilentlyContinue
	New-Item -ItemType Directory -Force $dir | Out-Null
	Copy-Item "$Saves\*.sav" $dir
}
$win = "-screen-fullscreen 0 -screen-width 480 -screen-height 270"
$lvl = if ($Level -ne "") { "-netcode-testlevel $Level" } else { "" }
if ($Lobby) {
	$hostNet = "-host -port $HostPort -name Host -mode $Mode -autostart"
	$joinNet = "-join 127.0.0.1:$ProxyPort -name Amigo -autoready"
} else {
	$hostNet = "-netcode-host -netcode-port $HostPort"
	$joinNet = "-netcode-join 127.0.0.1 -netcode-port $ProxyPort"
}
Start-Process "$test\Cuphead.exe" -WorkingDirectory $test -ArgumentList "$win $hostNet -netcode-autoload $Slot -netcode-botinput $lvl $Extra -savedir `"$test\saves-host`" -onlinelog `"$hlog`" -logFile `"$test\$Tag-host-unity.log`""
Start-Sleep 4
Start-Process "$test\Cuphead.exe" -WorkingDirectory $test -ArgumentList "$win $joinNet -netcode-autoload $Slot -netcode-botinput $Extra -savedir `"$test\saves-join`" -onlinelog `"$jlog`" -logFile `"$test\$Tag-join-unity.log`""
Start-Sleep $Seconds
& "$PSScriptRoot\shot-windows.ps1" -Out $test | Out-Null
Stop-Test
Start-Sleep 1

function Get-Snaps($log) {
	$h = @{}
	foreach ($m in Select-String -Path $log -Pattern "\[Netcode\] snap frame (\d+) hash (\w+)") {
		$h[[int]$m.Matches[0].Groups[1].Value] = $m.Matches[0].Groups[2].Value
	}
	return $h
}
$hs = Get-Snaps $hlog; $js = Get-Snaps $jlog
# Velocidade: frames do lockstep por segundo de verdade (tem que ser ~60)
$sm = @(Select-String -Path $hlog -Pattern "^(\S+) \[Netcode\] snap frame (\d+)")
$fps = 0
if ($sm.Count -ge 2) {
	$t0 = [TimeSpan]::Parse($sm[0].Matches[0].Groups[1].Value); $t1 = [TimeSpan]::Parse($sm[-1].Matches[0].Groups[1].Value)
	$f0 = [int]$sm[0].Matches[0].Groups[2].Value; $f1 = [int]$sm[-1].Matches[0].Groups[2].Value
	if ($t1 -gt $t0) { $fps = [Math]::Round(($f1 - $f0) / ($t1 - $t0).TotalSeconds, 1) }
}
$common = $hs.Keys | Where-Object { $js.ContainsKey($_) } | Sort-Object
$bad = @($common | Where-Object { $hs[$_] -ne $js[$_] })
$desync = @(Select-String -Path $hlog, $jlog -Pattern "dessincronia").Count
$errors = @(Select-String -Path $hlog, $jlog, "$test\$Tag-host-unity.log", "$test\$Tag-join-unity.log" -Pattern "Exception" -Context 0,3 -ErrorAction SilentlyContinue |
	# erro do proprio Cuphead ao descarregar a camera da fase (acontece sem o mod tambem)
	Where-Object { ($_.Context.PostContext -join " ") -notmatch "CameraAnimationController.OnDisable" })
$lastFrame = if ($common.Count) { $common[-1] } else { 0 }
Write-Host "Velocidade do lockstep: $fps frames/s (normal = 60)"
Write-Host "Netcode: $($common.Count) snapshots em comum (ultimo frame $lastFrame), $($bad.Count) hashes diferentes, $desync correcoes de dessincronia, $($errors.Count) excecoes"
Select-String -Path $hlog, $jlog -Pattern "\[Netcode\] (lockstep|aviso|barreira|carregando|teste|amigo|pedindo|ressincron|cena desconhecida|falha|erro|dessincronia)|ERRO|sala criada|entrou|voltou|caiu|reconectando|partida iniciada|sessao encerrada|sincronizado iniciado" | ForEach-Object { "  $($_.Filename): $($_.Line)" } | Select-Object -First 60
$errors | Select-Object -First 5 | ForEach-Object { "  EXC $($_.Filename): $($_.Line)" }
if ($common.Count -ge 3 -and $bad.Count -eq 0 -and $desync -eq 0 -and $fps -ge $MinFps -and $fps -le 65) { exit 0 } else { exit 1 }
