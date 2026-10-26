# Cria uma copia leve do jogo para testes (nao mexe na instalacao de verdade).
# Os assets (5 GB) entram como junction/hardlink; so a pasta Managed e copiada,
# entao cada copia pode ter a sua propria Assembly-CSharp.dll.
#
#   .\make-testgame.ps1 [-Dest H:\Games\Cuphead-SteamRIP.com\Tools\TestGame] [-Dll caminho\Assembly-CSharp.dll]
param(
	[string]$Game = (Resolve-Path "$PSScriptRoot\..\..\..\..\Cuphead").Path,
	[string]$Dest = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..\..").Path "Tools\TestGame"),
	[string]$Dll = ""
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path "$Dest\Cuphead.exe")) {
	Write-Host "Criando copia de teste em $Dest"
	New-Item -ItemType Directory -Force "$Dest\Cuphead_Data" | Out-Null
	foreach ($f in Get-ChildItem $Game -File) {
		if ($f.Extension -in '.bat') { continue }
		Copy-Item $f.FullName "$Dest\$($f.Name)"
	}
	foreach ($item in Get-ChildItem "$Game\Cuphead_Data") {
		$target = "$Dest\Cuphead_Data\$($item.Name)"
		if (Test-Path $target) { continue }
		if ($item.PSIsContainer) {
			if ($item.Name -eq 'Managed') { Copy-Item $item.FullName $target -Recurse }
			else { New-Item -ItemType Junction -Path $target -Target $item.FullName | Out-Null }
		} else {
			New-Item -ItemType HardLink -Path $target -Target $item.FullName | Out-Null
		}
	}
}
if ($Dll -ne "") {
	Copy-Item $Dll "$Dest\Cuphead_Data\Managed\Assembly-CSharp.dll" -Force
	Write-Host "DLL instalada na copia de teste: $Dll"
}
Write-Host "Copia de teste pronta: $Dest\Cuphead.exe"
