[CmdletBinding()]
param([switch]$Execute)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

# Only generated, superseded copies. Keep source, the CUDA toolkit used to
# rebuild the native engine, the current runtime, publish output, and dist.
$relativeTargets = @(
    '.private\preview-publish',
    '.private\flashnext-preview-final',
    '.private\flashnext-preview-final2',
    '.private\flashnext-preview-final3',
    '.private\cuda-13.3-extract',
    '.private\cuda-13.3-packages',
    '.private\bundled-stage-test',
    '.private\turboquant-build-407f323-v3',
    '.tools\Launcher.msi',
    '.tools\LlamaLauncherInstaller.exe',
    '.tools\cabcache'
)

foreach ($required in @(
    'src\Launcher.csproj',
    'publish\LlamaLauncher.exe',
    'runtime\bundled\cpu\llama-server.exe',
    'runtime\experimental\turboquant-cuda-b10726\llama-server.exe',
    '.private\cuda-13.3\bin\nvcc.exe',
    '.private\turboquant-src\CMakeLists.txt',
    'dist\Setup.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $required))) {
        throw "Required preserved build/runtime output is missing: $required"
    }
}

$nativeBuildCopy = Join-Path $projectRoot '.private\turboquant-build-407f323-v3\bin\Release\llama-server.exe'
if (Test-Path -LiteralPath $nativeBuildCopy) {
    $retainedEngine = Join-Path $projectRoot 'runtime\experimental\turboquant-cuda-b10726\llama-server.exe'
    if ((Get-FileHash -LiteralPath $nativeBuildCopy -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $retainedEngine -Algorithm SHA256).Hash) {
        throw 'The native build copy differs from the retained runtime; review it before cleanup.'
    }
}

$processes = @(Get-CimInstance Win32_Process | Where-Object ExecutablePath)
$candidates = @(
    foreach ($relative in $relativeTargets) {
        $full = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if (-not $full.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Cleanup target escaped the project: $relative"
        }
        if (-not ($relative.StartsWith('.private\') -or $relative.StartsWith('.tools\'))) {
            throw "Unexpected cleanup target: $relative"
        }
        $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cleanup target is a link: $relative"
        }
        if ($item.PSIsContainer) {
            $links = @(Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint)
            if ($links.Count -gt 0) { throw "Cleanup target contains a link: $relative" }
            $files = @(Get-ChildItem -LiteralPath $full -File -Recurse -Force)
            $bytes = ($files | Measure-Object Length -Sum).Sum
            $inUse = @($processes | Where-Object {
                $_.ExecutablePath.StartsWith($full + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)
            })
        } else {
            $files = @($item)
            $bytes = $item.Length
            $inUse = @($processes | Where-Object {
                $_.ExecutablePath.Equals($full, [StringComparison]::OrdinalIgnoreCase)
            })
        }
        if ($inUse.Count -gt 0) { throw "A process is using $relative" }
        [pscustomobject]@{ RelativePath = $relative; FullPath = $full; Bytes = [long]$bytes; Files = $files.Count }
    }
)

$totalBytes = ($candidates | Measure-Object Bytes -Sum).Sum
$candidates | Select-Object RelativePath, Files, Bytes | Format-Table -AutoSize
Write-Host ("Candidate savings: {0:N0} bytes ({1:N2} GiB)" -f $totalBytes, ($totalBytes / 1GB))

if (-not $Execute) {
    Write-Host 'Dry run only. Run this script with -Execute to remove exactly the listed targets.'
    return
}

foreach ($candidate in $candidates) {
    Remove-Item -LiteralPath $candidate.FullPath -Recurse -Force
    Write-Host "Removed $($candidate.RelativePath)"
}

Write-Host 'Cleanup complete. Current runtime, source, publish, dist, CUDA toolkit, and Qwen user data were not targets.'
