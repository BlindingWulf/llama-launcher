param([switch]$Legacy)
$ErrorActionPreference = 'Stop'
# The original UI is retained for rollback. Runtime, profiles and Qwen scripts
# remain in their existing locations; the installed launcher imports profiles.
$launcher = Join-Path $env:LOCALAPPDATA 'Programs\LlamaLauncher\LlamaLauncher.exe'
$legacyUi = Join-Path $PSScriptRoot 'Qwen3.8-Backups\Qwen3.8-Settings.before-llama-launcher-20260914.ps1'
if (-not $Legacy -and (Test-Path -LiteralPath $launcher)) {
    Start-Process -FilePath $launcher
} elseif (Test-Path -LiteralPath $legacyUi) {
    # Preserve the legacy UI's elevation requirement for its managed server.
    $quoted = '"' + $legacyUi.Replace('"','\"') + '"'
    Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-WindowStyle','Hidden','-File',$quoted) -Verb RunAs -WindowStyle Hidden
} else {
    throw 'Install llama.cpp Launcher, or restore the original settings UI from backup.'
}
