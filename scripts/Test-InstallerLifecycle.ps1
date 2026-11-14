[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath, [Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$msi = [IO.Path]::GetFullPath($MsiPath)
$output = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$token = [Guid]::NewGuid().ToString('N')
$testName = "LlamaLauncherValidation-$($token.Substring(0,8))"
$upgradeCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$componentIds = @{}
$products = @()
$checks = [ordered]@{}
$installer = New-Object -ComObject WindowsInstaller.Installer
function Execute-Sql($db, [string]$sql) {
    $view = $db.OpenView($sql)
    try { $view.Execute() }
    catch { throw "Installer database query failed: $sql. $($_.Exception.Message)" }
    finally { $view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}
function New-IsolatedPackage([string]$version) {
    $copy = Join-Path $output "package-$version.msi"
    Copy-Item -LiteralPath $msi -Destination $copy
    $db = $installer.OpenDatabase($copy, 1)
    $product = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    try {
        Execute-Sql $db "UPDATE Property SET Value='$product' WHERE Property='ProductCode'"
        Execute-Sql $db "UPDATE Property SET Value='$upgradeCode' WHERE Property='UpgradeCode'"
        Execute-Sql $db "UPDATE Property SET Value='$testName' WHERE Property='ProductName'"
        Execute-Sql $db "UPDATE Property SET Value='$version' WHERE Property='ProductVersion'"
        # UpgradeCode is part of the table's primary key; replace rows rather
        # than attempting an UPDATE of a Windows Installer primary key.
        $upgradeView = $db.OpenView('SELECT UpgradeCode, VersionMin, VersionMax, Language, Attributes, Remove, ActionProperty FROM Upgrade')
        $upgradeView.Execute()
        $upgradeRows = @()
        while ($record = $upgradeView.Fetch()) {
            $values = @()
            for ($column = 2; $column -le 7; $column++) {
                if ($column -eq 5) { $values += [string]$record.IntegerData($column) }
                else { $value = $record.StringData($column); $values += $(if ($value -eq '') { 'NULL' } else { "'" + $value.Replace("'", "''") + "'" }) }
            }
            $upgradeRows += ,$values
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
        $upgradeView.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($upgradeView) | Out-Null
        Execute-Sql $db 'DELETE FROM Upgrade'
        foreach ($values in $upgradeRows) {
            Execute-Sql $db "INSERT INTO Upgrade (UpgradeCode, VersionMin, VersionMax, Language, Attributes, Remove, ActionProperty) VALUES ('$upgradeCode', $($values -join ', '))"
        }
        Execute-Sql $db "UPDATE Directory SET DefaultDir='$testName' WHERE Directory='INSTALLFOLDER'"
        Execute-Sql $db "UPDATE Directory SET DefaultDir='$testName' WHERE Directory='AppMenuFolder'"
        Execute-Sql $db "UPDATE Shortcut SET Name='$testName'"
        Execute-Sql $db (('UPDATE `Registry` SET `Key`=''Software\{0}''') -f $testName)
        $view = $db.OpenView('SELECT Component, ComponentId FROM Component')
        $view.Execute()
        $names = @()
        while ($record = $view.Fetch()) { $names += $record.StringData(1); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        $view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        foreach ($name in $names) {
            if (-not $componentIds.ContainsKey($name)) { $componentIds[$name] = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}' }
            Execute-Sql $db "UPDATE Component SET ComponentId='$($componentIds[$name])' WHERE Component='$name'"
        }
        $db.Commit()
        $summary = $db.SummaryInformation(1)
        $summary.Property(9) = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
        $summary.Persist()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
        $db.Commit()
    } finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    }
    return [pscustomobject]@{ Product=$product; Package=$copy; Version=$version }
}
function Run-Msi([string[]]$arguments) {
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -notin 0,3010) { throw "Installer returned $($process.ExitCode); inspect the isolated MSI log." }
}
try {
    $first = New-IsolatedPackage '1.2.1'; $products += $first.Product
    $second = New-IsolatedPackage '1.2.2'; $products += $second.Product
    $installed = Join-Path $env:LOCALAPPDATA "Programs\$testName"
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$testName\$testName.lnk"
    Run-Msi @('/i', ('"'+$first.Package+'"'), '/qn', '/norestart', '/L*v', ('"'+(Join-Path $output 'install.log')+'"'))
    $checks.Install = Test-Path -LiteralPath (Join-Path $installed 'LlamaLauncher.exe')
    $checks.Shortcut = Test-Path -LiteralPath $shortcut
    $profileRoot = Join-Path $output 'fresh-profile'
    $init = Start-Process -FilePath (Join-Path $installed 'LlamaLauncher.exe') -ArgumentList @('--data-dir', ('"'+$profileRoot+'"'), '--initialize-only') -WindowStyle Hidden -PassThru
    $init.WaitForExit()
    if ($init.ExitCode) { throw 'Installed launcher failed to initialize.' }
    $settings = Get-Content (Join-Path $profileRoot 'settings.json') -Raw | ConvertFrom-Json
    $checks.CleanDefaults = !$settings.Lan -and !$settings.CheckUpdatesOnOpen -and $settings.Models.Count -eq 0 -and $settings.ApiKey.Length -eq 64 -and $settings.LegacyCommon -eq '' -and $null -eq $settings.DetachedServer
    $keyBefore = $settings.ApiKey
    $modelSentinel = Join-Path $output 'user-model-sentinel.gguf'
    Set-Content -LiteralPath $modelSentinel 'synthetic model preservation sentinel'
    Run-Msi @('/i', ('"'+$second.Package+'"'), '/qn', '/norestart', '/L*v', ('"'+(Join-Path $output 'upgrade.log')+'"'))
    $checks.Upgrade = Test-Path -LiteralPath (Join-Path $installed 'LlamaLauncher.exe')
    $checks.OldProductRemoved = $installer.ProductState($first.Product) -eq -1
    $checks.SettingsSurviveUpgrade = (Get-Content (Join-Path $profileRoot 'settings.json') -Raw | ConvertFrom-Json).ApiKey -eq $keyBefore
    Run-Msi @('/x', $second.Product, '/qn', '/norestart', '/L*v', ('"'+(Join-Path $output 'uninstall.log')+'"'))
    $checks.Uninstall = !(Test-Path -LiteralPath (Join-Path $installed 'LlamaLauncher.exe'))
    $checks.ShortcutRemoved = !(Test-Path -LiteralPath $shortcut)
    $checks.SettingsAndModelsPreserved = (Test-Path -LiteralPath (Join-Path $profileRoot 'settings.json')) -and (Test-Path -LiteralPath $modelSentinel)
    if (@($checks.Values | Where-Object { $_ -ne $true }).Count) { throw 'An installer lifecycle check failed.' }
} finally {
    foreach ($product in $products) {
        if ($installer.ProductState($product) -eq 5) { Run-Msi @('/x', $product, '/qn', '/norestart') }
    }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
    $checks | ConvertTo-Json | Set-Content (Join-Path $output 'lifecycle-results.json')
}
$checks | ConvertTo-Json
