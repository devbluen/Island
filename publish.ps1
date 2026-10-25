# Gera o app em .\dist (pasta com Island.exe + bibliotecas; copie a pasta inteira)
#   .\publish.ps1            -> precisa do ".NET 8 Desktop Runtime" instalado
#   .\publish.ps1 -Portable  -> inclui o .NET, roda em qualquer Windows 10/11 x64
# Não é um .exe único porque o Whisper (voz) carrega bibliotecas nativas da pasta "runtimes".
param([switch]$Portable)

$ErrorActionPreference = "Stop"
$selfContained = if ($Portable) { "true" } else { "false" }
$out = "$PSScriptRoot\dist"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$PSScriptRoot\Island.csproj" -c Release -r win-x64 `
    --self-contained $selfContained `
    -p:DebugType=none `
    -o $out

Write-Host "`nPronto: $out\Island.exe" -ForegroundColor Green
