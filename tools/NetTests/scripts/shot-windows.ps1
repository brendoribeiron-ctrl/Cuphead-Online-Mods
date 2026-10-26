# Tira um print de cada janela do jogo da copia de teste (mesmo atras de outras janelas).
#   .\shot-windows.ps1 [-Out pasta] [-Match TestGame]
param(
	[string]$Out = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..\..").Path "Tools\TestGame"),
	[string]$Match = "TestGame"
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class NetLabWin {
	[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
	[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
	public struct RECT { public int L, T, R, B; }
}
"@
$i = 0
foreach ($p in Get-Process Cuphead -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$Match*" } | Sort-Object StartTime) {
	$r = New-Object NetLabWin+RECT
	[NetLabWin]::GetClientRect($p.MainWindowHandle, [ref]$r) | Out-Null
	$w = [Math]::Max(1, $r.R - $r.L); $h = [Math]::Max(1, $r.B - $r.T)
	$bmp = New-Object System.Drawing.Bitmap $w, $h
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$dc = $g.GetHdc()
	[NetLabWin]::PrintWindow($p.MainWindowHandle, $dc, 3) | Out-Null   # 1 = so a area cliente, 2 = conteudo DirectX
	$g.ReleaseHdc($dc)
	$file = Join-Path $Out "janela$i.png"
	$bmp.Save($file)
	Write-Host "$file (pid $($p.Id))"
	$i++
}
