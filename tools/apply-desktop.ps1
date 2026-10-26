# Monta o mod desktop sobre um Assembly-CSharp descompilado.
# Uso: .\tools\apply-desktop.ps1 -Base <pasta do Assembly-CSharp descompilado, na base 1b32b31>
param([Parameter(Mandatory)][string]$Base)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

git -C $Base apply --whitespace=nowarn "$root\desktop\patches\assembly-csharp.patch"
Copy-Item "$root\shared\Net\*.cs" $Base -Force
Copy-Item "$root\desktop\Mod\*.cs" $Base -Force
Write-Host "Mod aplicado em $Base. Compile com: dotnet build Assembly-CSharp.csproj -c Release"
