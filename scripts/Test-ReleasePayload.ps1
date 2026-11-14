[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadRoot)
$ErrorActionPreference = 'Stop'
$payload = [IO.Path]::GetFullPath($PayloadRoot)
$payloadPrefix = $payload.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ((Get-Item -LiteralPath $payload).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The payload root must not be a link.' }
if (-not (Test-Path -LiteralPath (Join-Path $payload 'LlamaLauncher.exe'))) { throw 'Missing launcher executable.' }
$allowedJson = @('LlamaLauncher.deps.json', 'LlamaLauncher.runtimeconfig.json', 'release-manifest.json',
    'runtime/bundled/manifest.json', 'runtime/experimental/turboquant-cuda-b10726/runtime-manifest.json')
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)
$links = @(Get-ChildItem -LiteralPath $payload -Recurse -Force -Attributes ReparsePoint)
if ($links.Count) { throw 'Links are not allowed in the release payload.' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
    if ($relative -match '(?i)(^|/)(settings|engine-state|legacy-import|connection|test-results|baseline|\.private|\.tools|logs|screenshots)([./-]|$)' -or
        $file.Extension -in '.gguf', '.pdb', '.png', '.jpg', '.jpeg', '.log', '.ps1', '.wxs', '.cs') {
        throw "Private or development file in release: $relative"
    }
    if ($file.Extension -eq '.json' -and $relative -notin $allowedJson) { throw "Unexpected JSON in release: $relative" }
    $allowed = $relative -in @('LICENSE', 'README.md', 'INSTALL.md', 'ollama/ollama_shim.py') -or $relative -in $allowedJson -or
        ($relative -match '^licenses/' -and ($file.Extension -in '.txt', '.md', '.html' -or $file.Name -like '*LICENSE*')) -or
        ($relative -match '^runtime/(bundled/(cpu|cuda|vulkan)|experimental/turboquant-cuda-b10726)/[^/]+$' -and
            ($file.Extension -eq '.dll' -or $file.Name -eq 'llama-server.exe' -or $file.Name -like 'LICENSE*')) -or
        ($relative -notmatch '/' -and ($file.Extension -eq '.dll' -or $file.Name -in 'LlamaLauncher.exe', 'createdump.exe')) -or
        ($relative -match '^[a-z]{2}(-[A-Za-z]+)?/[A-Za-z.]+\.resources\.dll$')
    if (-not $allowed) { throw "File is outside the release allowlist: $relative" }
    if ($file.Extension -in '.json', '.md', '.txt', '.html' -or $file.Name -eq 'LICENSE') {
        $content = [IO.File]::ReadAllText($file.FullName)
        if ($content -match '(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/\s"<>]+' -or
            $content -match '(?i)https?://(?:192\.168\.|10\.\d+\.|172\.(?:1[6-9]|2\d|3[01])\.|100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.)' -or
            $content -match '(?i)"(?:ApiKey|LanAccess|QwenSyncSignature|DetachedServer)"\s*:') {
            throw "Personal configuration or address in release text: $relative"
        }
    }
}
Write-Output "Release payload allowlist passed: $($files.Count) files."
