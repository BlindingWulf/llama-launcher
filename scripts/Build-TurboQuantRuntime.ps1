[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\.private\turboquant-src'),
    [string]$ToolkitRoot = (Join-Path $PSScriptRoot '..\.private\cuda-13.3'),
    [string]$BuildRoot = (Join-Path $PSScriptRoot '..\.private\turboquant-build-407f323'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\runtime\experimental\turboquant-cuda-b10726'),
    [string]$PatchPath = (Join-Path $PSScriptRoot '..\patches\turboquant-qwen4exp-lazy-ple.patch'),
    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount - 2),
    [switch]$BootstrapCuda,
    [switch]$ConfigureOnly
)

$ErrorActionPreference = 'Stop'

$components = @(
    @{ Name = 'cuda_crt-windows-x86_64-13.3.33-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_crt/windows-x86_64/cuda_crt-windows-x86_64-13.3.33-archive.zip' },
    @{ Name = 'cuda_cudart-windows-x86_64-13.3.29-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.3.29-archive.zip' },
    @{ Name = 'cuda_nvcc-windows-x86_64-13.3.33-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_nvcc/windows-x86_64/cuda_nvcc-windows-x86_64-13.3.33-archive.zip' },
    @{ Name = 'cuda_nvrtc-windows-x86_64-13.3.33-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_nvrtc/windows-x86_64/cuda_nvrtc-windows-x86_64-13.3.33-archive.zip' },
    @{ Name = 'libcublas-windows-x86_64-13.5.1.27-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.5.1.27-archive.zip' },
    @{ Name = 'libnvvm-windows-x86_64-13.3.33-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/libnvvm/windows-x86_64/libnvvm-windows-x86_64-13.3.33-archive.zip' },
    @{ Name = 'cuda_nvtx-windows-x86_64-13.3.29-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_nvtx/windows-x86_64/cuda_nvtx-windows-x86_64-13.3.29-archive.zip' },
    @{ Name = 'cuda_profiler_api-windows-x86_64-13.3.27-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_profiler_api/windows-x86_64/cuda_profiler_api-windows-x86_64-13.3.27-archive.zip' },
    @{ Name = 'visual_studio_integration-windows-x86_64-13.3.27-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/visual_studio_integration/windows-x86_64/visual_studio_integration-windows-x86_64-13.3.27-archive.zip' },
    @{ Name = 'cccl-windows-x86_64-13.3.3.3.1-archive.zip'; Uri = 'https://developer.download.nvidia.com/compute/cuda/redist/cccl/windows-x86_64/cccl-windows-x86_64-13.3.3.3.1-archive.zip' }
)

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$ToolkitRoot = [IO.Path]::GetFullPath($ToolkitRoot)
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$PatchPath = [IO.Path]::GetFullPath($PatchPath)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($BootstrapCuda) {
    $downloadRoot = Join-Path $repoRoot '.private\cuda-13.3-packages'
    $extractRoot = Join-Path $repoRoot '.private\cuda-13.3-extract'
    New-Item -ItemType Directory -Force -Path $downloadRoot, $extractRoot, $ToolkitRoot | Out-Null
    foreach ($component in $components) {
        $zip = Join-Path $downloadRoot $component.Name
        if (!(Test-Path -LiteralPath $zip)) {
            Write-Host "Downloading $($component.Name)"
            & curl.exe -L --fail --retry 3 --output $zip $component.Uri
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $($component.Uri)" }
        }
        $folder = Join-Path $extractRoot ([IO.Path]::GetFileNameWithoutExtension($component.Name))
        if (!(Test-Path -LiteralPath $folder)) {
            Expand-Archive -LiteralPath $zip -DestinationPath $folder
        }
        $archiveRoot = Get-ChildItem -LiteralPath $folder -Directory | Select-Object -First 1
        if ($null -eq $archiveRoot) { throw "Unexpected CUDA archive layout: $zip" }
        Copy-Item -Path (Join-Path $archiveRoot.FullName '*') -Destination $ToolkitRoot -Recurse -Force
    }
}

$nvcc = Join-Path $ToolkitRoot 'bin\nvcc.exe'
$cmake = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
foreach ($required in @($nvcc, $cmake, $ninja, $vcvars, (Join-Path $SourceRoot 'CMakeLists.txt'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required build input is missing: $required" }
}
if (!(Test-Path -LiteralPath $PatchPath)) { throw "Required runtime patch is missing: $PatchPath" }

# Keep the native runtime reproducible from the pinned upstream commit. Test the
# exact changed line first because a Windows checkout may have CRLF working-tree
# bytes while the tracked patch deliberately retains portable LF content.
$qwenSource = Join-Path $SourceRoot 'src\models\qwen4exp.cpp'
$qwenText = Get-Content -LiteralPath $qwenSource -Raw
$patchedNeedle = '{ hparams.ple_head_dim, ple_rows }, TENSOR_READ_LAZY);'
$upstreamNeedle = '{ hparams.ple_head_dim, ple_rows }, 0);'
if ($qwenText.Contains($patchedNeedle)) {
    Write-Host 'Qwen4exp lazy-PLE patch is already applied.'
} elseif ($qwenText.Contains($upstreamNeedle)) {
    & git -C $SourceRoot apply $PatchPath
    if ($LASTEXITCODE -ne 0) { throw "Could not apply runtime patch: $PatchPath" }
} else {
    throw "Runtime source is neither the pinned upstream form nor already patched with $PatchPath"
}

New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
$vsEnvironment = & cmd.exe /d /s /c ('call "' + $vcvars + '" >nul && set')
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the Visual Studio x64 build environment.' }
foreach ($line in $vsEnvironment) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) {
        [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process')
    }
}
$env:CUDA_PATH = $ToolkitRoot
$env:PATH = (Join-Path $ToolkitRoot 'bin') + ';' + $env:PATH

$configure = @(
    '-S', $SourceRoot, '-B', $BuildRoot, '-G', 'Ninja Multi-Config',
    "-DCMAKE_MAKE_PROGRAM=$ninja",
    '-DCMAKE_C_COMPILER=cl.exe',
    '-DCMAKE_CXX_COMPILER=cl.exe',
    "-DCMAKE_CUDA_COMPILER=$nvcc",
    "-DCUDAToolkit_ROOT=$ToolkitRoot",
    '-DCMAKE_CUDA_ARCHITECTURES=120',
    '-DLLAMA_BUILD_SERVER=ON',
    '-DLLAMA_BUILD_TESTS=ON',
    '-DLLAMA_BUILD_EXAMPLES=ON',
    '-DLLAMA_OPENSSL=OFF',
    '-DGGML_NATIVE=OFF',
    '-DGGML_BACKEND_DL=ON',
    '-DGGML_CUDA=ON',
    '-DGGML_RPC=ON',
    '-DGGML_CUDA_CUB_3DOT2=ON'
)

& $cmake @configure
if ($LASTEXITCODE -ne 0) { throw "TurboQuant configure failed with exit code $LASTEXITCODE" }
if ($ConfigureOnly) { return }

& $cmake --build $BuildRoot --config Release --parallel $Jobs
if ($LASTEXITCODE -ne 0) { throw "TurboQuant build failed with exit code $LASTEXITCODE" }

$bin = Join-Path $BuildRoot 'bin\Release'
if (!(Test-Path -LiteralPath (Join-Path $bin 'llama-server.exe'))) {
    throw "Build completed without llama-server.exe in $bin"
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Copy-Item -Path (Join-Path $bin '*') -Destination $OutputRoot -Recurse -Force
# GGML loads its CUDA backend dynamically, so Windows does not resolve these
# dependencies until ggml-cuda.dll is opened. Keep the official redistributable
# DLLs beside the server; otherwise --list-devices silently reports no CUDA device.
$cudaRuntimeDlls = Join-Path $ToolkitRoot 'bin\x64\*.dll'
if (!(Test-Path -Path $cudaRuntimeDlls)) {
    throw "CUDA redistributable DLLs are missing from $ToolkitRoot\bin\x64"
}
Copy-Item -Path $cudaRuntimeDlls -Destination $OutputRoot -Force

$revision = (& git -C $SourceRoot rev-parse HEAD).Trim()
$patchHash = (Get-FileHash -LiteralPath $PatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$version = (& (Join-Path $OutputRoot 'llama-server.exe') --version 2>&1) -join "`n"
$files = Get-ChildItem -LiteralPath $OutputRoot -File | Sort-Object Name | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = $hash.Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    name = 'TurboQuant CUDA 13.3 / sm_120a'
    source = 'https://github.com/TheTom/llama-cpp-turboquant'
    revision = $revision
    local_patches = @(
        [ordered]@{
            name = 'qwen4exp lazy PLE table reads'
            file = 'patches/turboquant-qwen4exp-lazy-ple.patch'
            sha256 = $patchHash
            capability = 'Keeps the 28.8 GB learned PLE N-gram table mmap-backed and reads selected rows on demand.'
        }
    )
    upstream_tag = 'feature-turboquant-kv-cache-b10726-407f323'
    cuda = '13.3.33 compiler; 13.5.1.27 cuBLAS'
    cuda_architectures = @('sm_120a')
    cmake_flags = @(
        'LLAMA_BUILD_SERVER=ON', 'LLAMA_BUILD_TESTS=ON', 'LLAMA_BUILD_EXAMPLES=ON',
        'LLAMA_OPENSSL=OFF', 'GGML_NATIVE=OFF', 'GGML_BACKEND_DL=ON',
        'GGML_CUDA=ON', 'GGML_RPC=ON', 'GGML_CUDA_CUB_3DOT2=ON'
    )
    version = $version
    files = $files
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot 'runtime-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $SourceRoot 'LICENSE') -Destination (Join-Path $OutputRoot 'LICENSE-llama-cpp-turboquant') -Force
Write-Host "Runtime staged at $OutputRoot"
