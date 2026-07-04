param(
    [string]$Version = "1.8.6",
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\TaskbarHero",
    [string]$BepInExTemplate = "C:\Users\xoker\Tools\BepInEx-UnityIL2CPP-x64-6.0.0-be.785",
    [string]$Unity6InteropPatch = "C:\Users\xoker\Tools\Il2CppInterop-Unity6-1.0.0\release",
    [string]$OutputDir = "$env:USERPROFILE\Documents"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path $PSScriptRoot
$packageRoot = Join-Path $OutputDir "TaskbarHeroTrainer-v$Version"
$zipPath = Join-Path $OutputDir "TaskbarHeroTrainer-v$Version.zip"
$payloadRoot = Join-Path $packageRoot "bepinex_payload"

if (-not (Test-Path (Join-Path $GameDir "TaskBarHero.exe"))) {
    throw "No encuentro TaskBarHero.exe en GameDir: $GameDir"
}

if (-not (Test-Path (Join-Path $BepInExTemplate "BepInEx\core\BepInEx.Core.dll"))) {
    throw "No encuentro plantilla BepInEx valida: $BepInExTemplate"
}

if (-not (Test-Path (Join-Path $Unity6InteropPatch "Il2CppInterop.Runtime.dll"))) {
    throw "No encuentro parche Il2CppInterop Unity 6: $Unity6InteropPatch"
}

if (Test-Path $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

dotnet build (Join-Path $repoRoot "src\bridge\TaskbarHeroModMenu.csproj") `
    -c Release `
    -p:GameDir="$GameDir" | Out-Host

dotnet publish (Join-Path $repoRoot "src\trainer\TaskbarHeroTrainer.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $packageRoot | Out-Host

Copy-Item -LiteralPath (Join-Path $repoRoot "src\bridge\bin\Release\TaskbarHeroModMenu.dll") `
    -Destination (Join-Path $packageRoot "TaskbarHeroModMenu.dll") `
    -Force

Copy-Item -LiteralPath (Join-Path $repoRoot "INSTALL.md") `
    -Destination (Join-Path $packageRoot "INSTALL.md") `
    -Force

Copy-Item -LiteralPath (Join-Path $repoRoot "install_taskbarhero_trainer.ps1") `
    -Destination (Join-Path $packageRoot "install_taskbarhero_trainer.ps1") `
    -Force

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $BepInExTemplate ".doorstop_version") -Destination $payloadRoot -Force
Copy-Item -LiteralPath (Join-Path $BepInExTemplate "winhttp.dll") -Destination $payloadRoot -Force
Copy-Item -LiteralPath (Join-Path $BepInExTemplate "doorstop_config.ini") -Destination $payloadRoot -Force
Copy-Item -LiteralPath (Join-Path $BepInExTemplate "dotnet") -Destination $payloadRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $BepInExTemplate "BepInEx") -Destination $payloadRoot -Recurse -Force

$liveBepInExConfig = Join-Path $GameDir "BepInEx\config\BepInEx.cfg"
if (Test-Path $liveBepInExConfig) {
    New-Item -ItemType Directory -Path (Join-Path $payloadRoot "BepInEx\config") -Force | Out-Null
    Copy-Item -LiteralPath $liveBepInExConfig -Destination (Join-Path $payloadRoot "BepInEx\config\BepInEx.cfg") -Force
}

New-Item -ItemType Directory -Path (Join-Path $payloadRoot "BepInEx\plugins") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $Unity6InteropPatch "*") `
    -Destination (Join-Path $payloadRoot "BepInEx\core") `
    -Recurse `
    -Force

$payloadDoorstop = Join-Path $payloadRoot "doorstop_config.ini"
$text = Get-Content -LiteralPath $payloadDoorstop -Raw
$text = $text -replace "enabled\s*=\s*(true|false)", "enabled = false"
$text = $text -replace "debug_enabled\s*=\s*true", "debug_enabled = false"
Set-Content -LiteralPath $payloadDoorstop -Value $text -Encoding ASCII

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

$hash = Get-FileHash $zipPath -Algorithm SHA256
Write-Host "Package: $zipPath"
Write-Host "SHA-256: $($hash.Hash)"
