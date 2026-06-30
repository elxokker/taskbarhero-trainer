param(
    [string]$GameDir = "",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

function Find-TaskbarHeroGameDir {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\TaskbarHero",
        "C:\Program Files\Steam\steamapps\common\TaskbarHero"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "TaskBarHero.exe")) {
            return $candidate
        }
    }

    return ""
}

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = Find-TaskbarHeroGameDir
}

if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path (Join-Path $GameDir "TaskBarHero.exe"))) {
    throw "No encuentro TaskBarHero.exe. Ejecuta este script con -GameDir `"C:\ruta\a\TaskbarHero`"."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bridgeSource = Join-Path $scriptDir "TaskbarHeroModMenu.dll"
$trainerExe = Join-Path $scriptDir "TaskbarHeroTrainer.exe"
$bepInExPayload = Join-Path $scriptDir "bepinex_payload"
$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
$coreDll = Join-Path $GameDir "BepInEx\core\BepInEx.Core.dll"
$interopDll = Join-Path $GameDir "BepInEx\interop\Assembly-CSharp.dll"
$doorstopConfig = Join-Path $GameDir "doorstop_config.ini"
$gameAssemblyDll = Join-Path $GameDir "GameAssembly.dll"

function Unblock-IfPossible([string]$Path) {
    if (-not (Test-Path $Path)) {
        return
    }

    try {
        Unblock-File -LiteralPath $Path -ErrorAction Stop
    } catch {
        Write-Host "Aviso: no pude desbloquear '$Path': $($_.Exception.Message)"
    }
}

if (-not (Test-Path $bridgeSource)) {
    throw "Falta TaskbarHeroModMenu.dll en la carpeta del instalador."
}

if (-not (Test-Path $trainerExe)) {
    throw "Falta TaskbarHeroTrainer.exe en la carpeta del instalador."
}

$runningGame = Get-Process TaskBarHero -ErrorAction SilentlyContinue
if ($runningGame) {
    Write-Host "TaskBarHero.exe esta abierto. Cierra el juego antes de instalar o actualizar el bridge."
    exit 1
}

Unblock-IfPossible $bridgeSource
Unblock-IfPossible $trainerExe

function Backup-IfExists([string]$Path, [string]$BackupRoot, [string]$GameRoot) {
    if (-not (Test-Path $Path)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($GameRoot).TrimEnd("\")
    if ($fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $fullPath.Substring($fullRoot.Length).TrimStart("\")
    } else {
        $relative = Split-Path -Leaf $fullPath
    }

    $target = Join-Path $BackupRoot $relative
    $targetParent = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    Copy-Item -LiteralPath $Path -Destination $target -Recurse -Force
}

function Convert-HexToBytes([string]$Hex) {
    if (($Hex.Length % 2) -ne 0) {
        throw "Hex invalido."
    }

    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }

    return $bytes
}

function Same-Bytes([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) {
        return $false
    }

    for ($i = 0; $i -lt $Left.Length; $i++) {
        if ($Left[$i] -ne $Right[$i]) {
            return $false
        }
    }

    return $true
}

function Patch-GameAssemblyDlcCheck([string]$Path, [string]$GameRoot) {
    if (-not (Test-Path $Path)) {
        Write-Host "Aviso: no encuentro GameAssembly.dll; no se aplica parche de heroes persistentes."
        return
    }

    $offset = 0xC1A9C0
    $expected = Convert-HexToBytes "40534883ec20807938008bdac7442438"
    $patched = Convert-HexToBytes "B801000000C3"
    $buffer = New-Object byte[] $expected.Length

    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    } catch [System.IO.IOException] {
        Write-Host "Aviso: GameAssembly.dll esta en uso. Cierra TaskbarHero y vuelve a ejecutar el instalador."
        return
    }

    try {
        $stream.Position = $offset
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -ne $buffer.Length) {
            Write-Host "Aviso: GameAssembly.dll demasiado corto; no se aplica parche de heroes persistentes."
            return
        }

        $currentPatch = New-Object byte[] $patched.Length
        [Array]::Copy($buffer, 0, $currentPatch, 0, $patched.Length)
        if (Same-Bytes $currentPatch $patched) {
            Write-Host "Parche de heroes persistentes ya estaba aplicado en GameAssembly.dll."
            return
        }

        if (-not (Same-Bytes $buffer $expected)) {
            Write-Host "Aviso: GameAssembly.dll no coincide con la version esperada; no se aplica parche de heroes persistentes."
            return
        }
    } finally {
        $stream.Dispose()
    }

    $backupRoot = Join-Path $GameRoot "TaskbarHeroTrainer_Backups"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backupPath = Join-Path $backupRoot ("GameAssembly_before_dlc_hbo_patch_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".dll")
    try {
        Copy-Item -LiteralPath $Path -Destination $backupPath -Force
    } catch [System.IO.IOException] {
        Write-Host "Aviso: no pude crear backup de GameAssembly.dll. Cierra TaskbarHero y vuelve a ejecutar el instalador."
        return
    }

    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    } catch [System.IO.IOException] {
        Write-Host "Aviso: GameAssembly.dll esta en uso. Cierra TaskbarHero y vuelve a ejecutar el instalador."
        return
    }

    try {
        $verify = New-Object byte[] $expected.Length
        $stream.Position = $offset
        $read = $stream.Read($verify, 0, $verify.Length)
        if ($read -ne $verify.Length -or -not (Same-Bytes $verify $expected)) {
            Write-Host "Aviso: GameAssembly.dll cambio durante la instalacion; no se aplica parche de heroes persistentes."
            return
        }

        $stream.Position = $offset
        $stream.Write($patched, 0, $patched.Length)
        $stream.Flush()
        Write-Host "Parche de heroes persistentes aplicado. Backup: $backupPath"
    } finally {
        $stream.Dispose()
    }
}

if (Test-Path $bepInExPayload) {
    Get-ChildItem -LiteralPath $bepInExPayload -Recurse -Force -File | ForEach-Object {
        Unblock-IfPossible $_.FullName
    }

    $backupRoot = Join-Path $GameDir ("TaskbarHeroTrainer_Backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
    Backup-IfExists (Join-Path $GameDir ".doorstop_version") $backupRoot $GameDir
    Backup-IfExists (Join-Path $GameDir "winhttp.dll") $backupRoot $GameDir
    Backup-IfExists $doorstopConfig $backupRoot $GameDir
    Backup-IfExists (Join-Path $GameDir "BepInEx\core") $backupRoot $GameDir

    Get-ChildItem -LiteralPath $bepInExPayload -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $GameDir -Recurse -Force
    }

    Write-Host "BepInEx IL2CPP instalado/actualizado desde el paquete."
    if (Test-Path $backupRoot) {
        Write-Host "Backup de archivos sustituidos: $backupRoot"
    }
}

if (-not (Test-Path $coreDll)) {
    throw "BepInEx IL2CPP no esta instalado y el paquete no trae bepinex_payload."
}

New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
Copy-Item -LiteralPath $bridgeSource -Destination (Join-Path $pluginsDir "TaskbarHeroModMenu.dll") -Force
Patch-GameAssemblyDlcCheck $gameAssemblyDll $GameDir

if (Test-Path $doorstopConfig) {
    $text = Get-Content -LiteralPath $doorstopConfig -Raw
    if ($text -match "enabled\s*=") {
        $text = $text -replace "enabled\s*=\s*(true|false)", "enabled = false"
        Set-Content -LiteralPath $doorstopConfig -Value $text -Encoding ASCII
    }

    $text = Get-Content -LiteralPath $doorstopConfig -Raw
    if ($text -match "debug_enabled\s*=\s*true") {
        $text = $text -replace "debug_enabled\s*=\s*true", "debug_enabled = false"
        Set-Content -LiteralPath $doorstopConfig -Value $text -Encoding ASCII
    }
}

if (-not (Test-Path $interopDll)) {
    Write-Host "Interop aun no existe. Es normal en primera instalacion; BepInEx lo generara al abrir el juego por primera vez."
}

Write-Host "Instalado bridge en: $pluginsDir"
Write-Host "Trainer: $trainerExe"

if (-not $NoLaunch) {
    try {
        Start-Process -FilePath $trainerExe -WorkingDirectory $scriptDir -ErrorAction Stop
    } catch {
        Write-Host "Instalacion completada, pero Windows no dejo abrir el trainer automaticamente."
        Write-Host "Abre manualmente: $trainerExe"
        Write-Host "Detalle: $($_.Exception.Message)"
    }
} else {
    Write-Host "NoLaunch activo. Abre manualmente: $trainerExe"
}
