param([switch]$SkipTests,[switch]$SkipInstaller)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$lock=Get-Content dependencies.lock.json -Raw|ConvertFrom-Json
# One version for the app, the MSI, the bundle and the release folder: the project file's <Version>.
$version=(Select-Xml -Path src\Launcher.csproj -XPath '//Version').Node.InnerText
if($version -notmatch '^\d+\.\d+\.\d+$'){throw "Unexpected version in Launcher.csproj: $version"}
New-Item -ItemType Directory -Force .tools,dist|Out-Null
$releaseStage=Join-Path $PSScriptRoot ('.tools\clean-release-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $releaseStage|Out-Null
$verifiedRuntimeStage=Join-Path $PSScriptRoot ('.tools\verified-runtime-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verifiedRuntimeStage|Out-Null
function Remove-ToolsDirectory([string]$Path){
    # Only ever deletes a real (non-link) directory directly inside .tools.
    if(-not(Test-Path -LiteralPath $Path)){return}
    $toolsRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.tools'))+[IO.Path]::DirectorySeparatorChar
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $full.StartsWith($toolsRoot,[StringComparison]::OrdinalIgnoreCase) -or $full.Length -le $toolsRoot.Length){throw "Refusing to remove outside .tools: $full"}
    if((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing to remove a link: $full"}
    Remove-Item -LiteralPath $full -Recurse -Force
}
function Fetch-Verified([string]$Url,[string]$Path,[string]$Sha256){
    if(-not(Test-Path -LiteralPath $Path)){Invoke-WebRequest $Url -OutFile $Path}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256){throw "Dependency hash mismatch: $Path"}
}
Fetch-Verified $lock.sdkUrl '.tools\dotnet-sdk.zip' $lock.sdkSha256
if(-not(Test-Path '.tools\dotnet\dotnet.exe')){Expand-Archive '.tools\dotnet-sdk.zip' '.tools\dotnet' -Force}
$dotnet=Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if(-not(Test-Path '.tools\wix\wix.exe')){& $dotnet tool install wix --version $lock.wixVersion --tool-path .tools\wix;if($LASTEXITCODE){throw 'WiX installation failed'}}
$wix=Join-Path $PSScriptRoot '.tools\wix\wix.exe'
foreach($extension in @('WixToolset.Bal.wixext','WixToolset.Util.wixext')){& $wix extension add "$extension/$($lock.wixVersion)";if($LASTEXITCODE){throw 'WiX extension download failed'}}
foreach($asset in $lock.engineAssets){
    $file=Join-Path '.tools' $asset.name
    Fetch-Verified $asset.browser_download_url $file ($asset.digest -replace '^sha256:','')
    $backend=if($asset.name -match 'cpu'){'cpu'}elseif($asset.name -match 'vulkan'){'vulkan'}else{'cuda'}
    Expand-Archive -LiteralPath $file -DestinationPath "$verifiedRuntimeStage\$backend" -Force
}
Fetch-Verified $lock.vcUrl '.tools\vc_redist.x64.exe' $lock.vcSha256
& $dotnet publish src\Launcher.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $releaseStage
if($LASTEXITCODE){throw 'Application build failed'}
Get-ChildItem -LiteralPath $releaseStage -File -Filter '*.pdb' | Remove-Item
foreach($backend in @('cpu','cuda','vulkan')){
    $destination="$releaseStage\runtime\bundled\$backend";New-Item -ItemType Directory -Force $destination|Out-Null
    Get-ChildItem "$verifiedRuntimeStage\$backend" -File | Where-Object {$_.Extension -eq '.dll' -or $_.Name -eq 'llama-server.exe' -or $_.Name -like 'LICENSE*'} | Copy-Item -Destination $destination -Force
}
# The extracted engine archives are only needed for the copy above; the verified zips stay in .tools.
Remove-ToolsDirectory $verifiedRuntimeStage
$experimentalSource='runtime\experimental\turboquant-cuda-b10726'
$experimentalDestination="$releaseStage\runtime\experimental\turboquant-cuda-b10726"
if(-not(Test-Path -LiteralPath "$experimentalSource\runtime-manifest.json")){throw 'Experimental TurboQuant runtime has not been built and verified.'}
New-Item -ItemType Directory -Force $experimentalDestination|Out-Null
$experimentalManifest=Get-Content "$experimentalSource\runtime-manifest.json" -Raw|ConvertFrom-Json
Get-ChildItem -LiteralPath $experimentalSource -File | Where-Object {$_.Extension -eq '.dll' -or $_.Name -eq 'llama-server.exe' -or $_.Name -like 'LICENSE*' -or $_.Name -eq 'runtime-manifest.json'} | ForEach-Object {
    if($_.Name -ne 'runtime-manifest.json'){
        $expected=@($experimentalManifest.files|Where-Object name -eq $_.Name)
        if($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne $expected[0].sha256){throw "Experimental runtime manifest mismatch: $($_.Name)"}
    }
    Copy-Item -LiteralPath $_.FullName -Destination $experimentalDestination -Force
}
Copy-Item licenses -Destination $releaseStage -Recurse -Force
Copy-Item LICENSE,README.md -Destination $releaseStage -Force
Copy-Item docs\INSTALL.md -Destination $releaseStage -Force
$manifest=[ordered]@{}
Get-ChildItem "$releaseStage\runtime\bundled" -File -Recurse | Where-Object Name -ne 'manifest.json' | ForEach-Object {$relative=[IO.Path]::GetRelativePath((Join-Path $releaseStage "runtime\bundled"),$_.FullName).Replace('\','/');$manifest[$relative]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
$manifest|ConvertTo-Json -Depth 4|Set-Content "$releaseStage\runtime\bundled\manifest.json"
& (Join-Path $PSScriptRoot 'scripts\Test-ReleasePayload.ps1') -PayloadRoot $releaseStage
if(-not $SkipTests){
    & (Join-Path $releaseStage "LlamaLauncher.exe") --self-test "$PSScriptRoot\.tools\acceptance" | Out-Null
    if($LASTEXITCODE){throw 'Application self-tests failed; inspect .tools\acceptance\test-results.json'}
}
if(-not $SkipInstaller){
    & $wix build installer\Package.wxs -arch x64 -d "PublishDir=$releaseStage" -d "Version=$version" -o '.tools\Launcher.msi' -pdbtype none -sw1076 -cc '.tools\cabcache'
    if($LASTEXITCODE){throw 'MSI build failed'}
    $bal=Join-Path $PSScriptRoot ".wix\extensions\WixToolset.Bal.wixext\$($lock.wixVersion)\wixext5\WixToolset.BootstrapperApplications.wixext.dll"
    & $wix build installer\Bundle.wxs -arch x64 -ext $bal -ext WixToolset.Util.wixext -d "Version=$version" -d "LicenseFile=$PSScriptRoot\installer\license.rtf" -d "VCRedist=$PSScriptRoot\.tools\vc_redist.x64.exe" -d "Package=$PSScriptRoot\.tools\Launcher.msi" -o '.tools\LlamaLauncherInstaller.exe' -pdbtype none
    if($LASTEXITCODE){throw 'Installer bundle build failed'}
    $hash=(Get-FileHash '.tools\LlamaLauncherInstaller.exe' -Algorithm SHA256).Hash
    "internal static class SetupHash { internal const string Value = `"$hash`"; }" | Set-Content '.tools\SetupHash.cs'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /out:dist\Setup.exe /win32manifest:src\app.manifest /win32icon:assets\launcher.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /resource:.tools\LlamaLauncherInstaller.exe,Installer installer\SetupHost.cs .tools\SetupHash.cs
    if($LASTEXITCODE){throw 'Setup host build failed'}
}
# Whitelist the archive contents; never copy local profiles, GGUFs, runtime binaries,
# user-specific snapshots, dependency caches or build directories into source.zip.
Add-Type -AssemblyName System.IO.Compression
$archive=Join-Path $PSScriptRoot 'dist\source.zip'
if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{
    $files=@(Get-Item README.md,LICENSE,.gitignore,.editorconfig,build.ps1,dependencies.lock.json,docs\INSTALL.md)
    $sourceDirectories=@('src','installer','licenses','tests','assets','scripts','patches','ollama')|Where-Object {Test-Path -LiteralPath $_}
    $files+=@(Get-ChildItem $sourceDirectories -File -Recurse | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -notin '.gguf','.pdb'})
    foreach($file in $files){$relative=[IO.Path]::GetRelativePath($PSScriptRoot,$file.FullName).Replace('\','/');[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}
}finally{$zip.Dispose()}
$installed=(Get-ChildItem $releaseStage -File -Recurse|Measure-Object Length -Sum).Sum
$report=@("llama.cpp Launcher build report","Application version: $version","Recovery engine: b10549 / b2e5e9b28; CPU, CUDA 13.3, Vulkan x64","Experimental engine: b10726 / 407f3237; Flash-Next, combined speculation, native TurboQuant, CUDA 13.3 sm_120a",".NET runtime: 8.0.28 (self-contained); SDK 8.0.425","Installer: WiX 5.0.2, SHA-256 verified .NET Framework setup host","Build: pwsh -File build.ps1","Installed payload bytes: $installed","Installed payload MiB: $([math]::Round($installed/1MB,2))")
foreach($name in @('Setup.exe','source.zip')){if(Test-Path "dist\$name"){$item=Get-Item "dist\$name";$report+="$name bytes: $($item.Length)";$report+="$name SHA-256: $((Get-FileHash $item.FullName -Algorithm SHA256).Hash)"}}
$report+='';$report+='Release packaging: fresh staging directory; allowlisted payload; no saved settings, keys, model profiles, logs or screenshots.'
$report+='Fresh launch: LAN off, update checks off, no auto-import, newly generated API key.'
$report+='Unsigned installer; Windows x64 only. Models and vendor GPU drivers are separate.'
$publishDestination=Join-Path $PSScriptRoot 'publish'
if(Test-Path -LiteralPath $publishDestination){
    $resolvedPublish=[IO.Path]::GetFullPath($publishDestination)
    if($resolvedPublish -ne (Join-Path $PSScriptRoot 'publish')){throw 'Unexpected publish path.'}
    if((Get-Item -LiteralPath $publishDestination).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publish output must not be a link.'}
    # Keep exactly one rollback copy instead of one per build.
    Remove-ToolsDirectory (Join-Path $PSScriptRoot '.tools\publish-previous')
    Move-Item -LiteralPath $publishDestination -Destination (Join-Path $PSScriptRoot '.tools\publish-previous')
}
Move-Item -LiteralPath $releaseStage -Destination $publishDestination
$report | Set-Content dist\build-report.txt
$shareStage=Join-Path $PSScriptRoot ('.tools\share-release-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $shareStage|Out-Null
Copy-Item dist\Setup.exe,dist\source.zip,dist\build-report.txt,docs\INSTALL.md -Destination $shareStage
$checksums=Get-ChildItem -LiteralPath $shareStage -File|Sort-Object Name|ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)"}
$checksums|Set-Content (Join-Path $shareStage 'SHA256SUMS.txt')
$shareDestination=Join-Path $PSScriptRoot "dist\clean-release-$version"
if(Test-Path -LiteralPath $shareDestination){
    if((Get-Item -LiteralPath $shareDestination).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Share output must not be a link.'}
    if([IO.Path]::GetFullPath($shareDestination) -ne (Join-Path $PSScriptRoot "dist\clean-release-$version")){throw 'Unexpected share path.'}
    Remove-ToolsDirectory (Join-Path $PSScriptRoot '.tools\share-previous')
    Move-Item -LiteralPath $shareDestination -Destination (Join-Path $PSScriptRoot '.tools\share-previous')
}
Move-Item -LiteralPath $shareStage -Destination $shareDestination
Write-Output "Build complete: dist\clean-release-$version (share only this folder)."
