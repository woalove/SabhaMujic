<#
.SYNOPSIS
    Silent install / uninstall / reinstall / upgrade smoke test for the SabhaMujic MSI.

.DESCRIPTION
    Run elevated on a disposable Windows machine (GitHub Actions windows-latest, or a test VM): it installs and removes
    SabhaMujic and leaves %ProgramData%\SabhaMujic behind.

    1. Installs -Msi silently, then checks the service (account, start mode, failure actions, Running, still running
       15 s later), the desktop pipe, the protected folder ACLs, the Event Log source, the firewall rules, shortcuts and
       the Programs and Features entry, and prints the service log.
    2. Uninstalls: the service, firewall rules and program files are gone; %ProgramData%\SabhaMujic is kept.
    3. Reinstalls -Msi over the kept data, then upgrades to -UpgradeMsi (a higher version): checks run again and the
       data is still there.
    4. Tries to install -Msi over the newer version: must be refused, with the newer version still running.
    5. Uninstalls the upgraded version.
    Configuration checks are collected and reported together; a missing or stopped service stops the test at once.
    Verbose msiexec logs go to -LogDir; a failed msiexec run prints the relevant part of its log.

.EXAMPLE
    pwsh installer/smoke-test.ps1 -Msi artifacts/SabhaMujic-0.1.0-x64.msi -Version 0.1.0 `
        -UpgradeMsi artifacts/SabhaMujic-0.1.1-x64.msi -UpgradeVersion 0.1.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msi,
    # The ProductVersion of -Msi (as passed to installer/build.ps1).
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $UpgradeMsi,
    # The ProductVersion of -UpgradeMsi; must be higher than -Version.
    [Parameter(Mandatory)] [string] $UpgradeVersion,
    [string] $LogDir = (Join-Path (Get-Location) 'artifacts/install-logs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'SabhaMujic'
$ServiceAccount = 'NT SERVICE\SabhaMujic'
$PipeName = 'SabhaMujic.v1'                    # SabhaMujic.Contracts.Endpoints.PipeName
$EventSource = 'SabhaMujic.Service'            # .NET host: Event Log source = entry assembly name
$DataRoot = Join-Path $env:ProgramData 'SabhaMujic'
$ServiceExe = Join-Path $env:ProgramFiles 'SabhaMujic\Service\SabhaMujic.Service.exe'
$AppExe = Join-Path $env:ProgramFiles 'SabhaMujic\App\SabhaMujic.Desktop.exe'
$StartMenuShortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SabhaMujic.lnk'
$DesktopShortcut = Join-Path $env:PUBLIC 'Desktop\SabhaMujic.lnk'
$FirewallRules = @(
    @{ Name = 'SabhaMujic Remote (HTTPS)'; Port = '8443' },
    @{ Name = 'SabhaMujic Remote setup (HTTP)'; Port = '8080' }
)
$Marker = Join-Path $DataRoot 'smoke-test-marker.txt'

$Msi = (Resolve-Path $Msi).Path
$UpgradeMsi = (Resolve-Path $UpgradeMsi).Path
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:StartedAt = Get-Date

# ---------------------------------------------------------------------------------------------------------- helpers

function Write-Step([string] $Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }

# Records a failed configuration check and carries on, so one CI run reports every problem.
function Test-Check([bool] $Condition, [string] $Description) {
    if ($Condition) {
        Write-Host "  ok   $Description"
    }
    else {
        Write-Host "  FAIL $Description" -ForegroundColor Red
        $script:Failures.Add($Description)
    }
}

function Wait-Until([scriptblock] $Condition, [int] $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $Condition)
}

function Show-MsiLog([string] $Log) {
    if (-not (Test-Path $Log)) { Write-Host "(no log at $Log)"; return }
    Write-Host "--- First failing action ('Return value 3') in $Log"
    Select-String -LiteralPath $Log -Pattern 'Return value 3' -Context 60, 5 | Select-Object -First 1 | ForEach-Object { $_.ToString() } | Write-Host
    Write-Host "--- Errors (MSI errors 1xxx/2xxx/27xx) in $Log"
    Select-String -LiteralPath $Log -Pattern 'Error \d{4}|error code \d+|CustomAction .* returned actual error' | Select-Object -First 40 | ForEach-Object { $_.Line } | Write-Host
    Write-Host "::group::Full msiexec log $Log"
    Get-Content -LiteralPath $Log | Write-Host
    Write-Host '::endgroup::'
}

function Show-Diagnostics {
    Write-Step 'Diagnostics'
    $logs = Join-Path $DataRoot 'logs'
    if (Test-Path $logs) {
        foreach ($file in Get-ChildItem -LiteralPath $logs -Filter 'sabhamujic-*.log' | Sort-Object LastWriteTime) {
            Write-Host "--- $($file.FullName)"
            try { Get-Content -LiteralPath $file.FullName | Write-Host } catch { Write-Host "(could not read: $_)" }
        }
    }
    else {
        Write-Host "(no service log folder at $logs)"
    }

    Write-Host "--- Application log, source $EventSource"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $EventSource } -MaxEvents 30 -ErrorAction SilentlyContinue |
        ForEach-Object { "{0:u} {1} {2}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Message } | Write-Host
    Write-Host '--- System log, Service Control Manager, mentioning SabhaMujic'
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $script:StartedAt } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like '*SabhaMujic*' } |
        ForEach-Object { "{0:u} {1} {2}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Message } | Write-Host
    Write-Host '--- sc.exe queryex / qc'
    & sc.exe queryex $ServiceName | Write-Host
    & sc.exe qc $ServiceName | Write-Host
}

# Runs msiexec silently with a verbose log. Returns the exit code.
function Invoke-Msiexec([string] $Action, [string] $Package, [string] $LogName) {
    $log = Join-Path $LogDir "$LogName.log"
    Write-Host "msiexec $Action `"$Package`" /qn /norestart /l*v `"$log`""
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList @($Action, "`"$Package`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
    Write-Host "msiexec exit code: $($process.ExitCode)"
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Log = $log }
}

# 0 = success, 3010 = success but a reboot is required (accepted with a warning). Anything else fails the test.
function Assert-MsiSucceeded($Result, [string] $What) {
    if ($Result.ExitCode -eq 3010) {
        Write-Warning "$What succeeded but asks for a reboot (3010)."
        return
    }
    if ($Result.ExitCode -ne 0) {
        Show-MsiLog $Result.Log
        throw "$What failed: msiexec exit code $($Result.ExitCode). Log: $($Result.Log)"
    }
}

function Get-SabhaMujicService { Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" }

function Get-ServiceState {
    $service = Get-SabhaMujicService
    if ($service) { return [string] $service.State }
    return '(not installed)'
}

function Get-UninstallEntries {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'SabhaMujic' }
}

function Test-PipeExists {
    [System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -ieq "\\.\pipe\$PipeName" } | Select-Object -First 1
}

# --------------------------------------------------------------------------------------------- installed-state checks

function Assert-Installed([string] $ExpectedVersion) {
    Write-Step "Checking the installed product (expected version $ExpectedVersion)"

    $service = Get-SabhaMujicService
    if (-not $service) { throw "Service '$ServiceName' is not installed." }
    Test-Check ($service.StartName -eq $ServiceAccount) "service runs as $ServiceAccount (is '$($service.StartName)')"
    Test-Check ($service.StartMode -eq 'Auto') "service start mode is Auto (is '$($service.StartMode)')"
    Test-Check ($service.PathName -like "*$ServiceExe*") "service binary is $ServiceExe (is $($service.PathName))"
    $serviceKey = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $delayed = $serviceKey.PSObject.Properties['DelayedAutostart']
    Test-Check ($delayed -and $delayed.Value -eq 1) 'service uses delayed automatic start'

    $failure = (& sc.exe qfailure $ServiceName) -join "`n"
    Write-Host $failure
    Test-Check ($failure -match 'RESET_PERIOD \(in seconds\)\s*:\s*86400') 'failure count resets after 1 day (86400 s)'
    Test-Check (([regex]::Matches($failure, 'RESTART -- Delay = 5000 milliseconds')).Count -eq 3) 'three restart actions, 5 s apart'
    $flag = (& sc.exe qfailureflag $ServiceName) -join "`n"
    Test-Check ($flag -match 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES\s*:\s*TRUE') 'failure actions also apply when the service stops with an error'

    $running = Wait-Until { (Get-ServiceState) -eq 'Running' } 60
    if (-not $running) { throw "Service is not Running within 60 s (state: $(Get-ServiceState))." }
    Write-Host '  ok   service is Running'

    $pipe = Wait-Until { [bool](Test-PipeExists) } 60
    if (-not $pipe) { throw "Named pipe \\.\pipe\$PipeName does not exist within 60 s." }
    Write-Host "  ok   named pipe \\.\pipe\$PipeName exists"

    # Informational only: whether the service hosts the iPhone remote depends on the build and its configuration.
    try {
        $health = Invoke-WebRequest -Uri 'http://localhost:8080/health' -TimeoutSec 5 -UseBasicParsing
        Write-Host "  info remote setup site http://localhost:8080/health answered $($health.StatusCode): $($health.Content)"
    }
    catch {
        Write-Host "  info remote setup site http://localhost:8080/health not reachable ($($_.Exception.Message))"
    }

    # Protected folders: exactly SYSTEM, Administrators and the service SID, inheritance off (WindowsStoreAcl).
    $serviceSid = ([System.Security.Principal.NTAccount] $ServiceAccount).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $expectedSids = @('S-1-5-18', 'S-1-5-32-544', $serviceSid) | Sort-Object
    $forbidden = @{ 'S-1-1-0' = 'Everyone'; 'S-1-5-11' = 'Authenticated Users'; 'S-1-5-32-545' = 'BUILTIN\Users' }
    foreach ($name in 'data', 'library', 'uploads') {
        $path = Join-Path $DataRoot $name
        if (-not (Wait-Until { Test-Path -LiteralPath $path } 30)) {
            Test-Check $false "$path exists"
            continue
        }
        $acl = Get-Acl -LiteralPath $path
        $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
        $found = @($sids | Where-Object { $forbidden.ContainsKey($_) } | ForEach-Object { $forbidden[$_] })
        Test-Check ($found.Count -eq 0) "$name has no Users / Authenticated Users / Everyone entries (found: $($found -join ', '))"
        Test-Check $acl.AreAccessRulesProtected "$name does not inherit permissions"
        Test-Check (($sids -join ',') -eq ($expectedSids -join ',')) "$name grants exactly SYSTEM, Administrators and $ServiceAccount (has: $($sids -join ', '))"
    }

    $eventKey = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$EventSource"
    Test-Check (Test-Path -LiteralPath $eventKey) "Event Log source '$EventSource' is registered"
    if (Test-Path -LiteralPath $eventKey) {
        $messageFile = (Get-ItemProperty -LiteralPath $eventKey).EventMessageFile
        Test-Check (Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables($messageFile))) "Event Log message file exists ($messageFile)"
    }

    foreach ($expected in $FirewallRules) {
        $rules = @(Get-NetFirewallRule -DisplayName $expected.Name -ErrorAction SilentlyContinue)
        Test-Check ($rules.Count -eq 1) "one firewall rule '$($expected.Name)' (found $($rules.Count))"
        if ($rules.Count -ne 1) { continue }
        $rule = $rules[0]
        Test-Check ($rule.Profile.ToString() -eq 'Private') "'$($expected.Name)' applies to the Private profile only (is $($rule.Profile))"
        Test-Check ($rule.Direction.ToString() -eq 'Inbound' -and $rule.Action.ToString() -eq 'Allow' -and $rule.Enabled.ToString() -eq 'True') "'$($expected.Name)' is an enabled inbound allow rule"
        $ports = $rule | Get-NetFirewallPortFilter
        Test-Check ($ports.Protocol -eq 'TCP' -and "$($ports.LocalPort)" -eq $expected.Port) "'$($expected.Name)' is TCP $($expected.Port) (is $($ports.Protocol) $($ports.LocalPort))"
        $program = ($rule | Get-NetFirewallApplicationFilter).Program
        Test-Check ($program -ieq $ServiceExe) "'$($expected.Name)' is limited to $ServiceExe (is $program)"
    }

    Test-Check (Test-Path -LiteralPath $AppExe) "desktop app installed at $AppExe"
    Test-Check (Test-Path -LiteralPath $StartMenuShortcut) "Start menu shortcut $StartMenuShortcut"
    Test-Check (Test-Path -LiteralPath $DesktopShortcut) "desktop shortcut $DesktopShortcut (default feature selection)"

    $entries = @(Get-UninstallEntries)
    Test-Check ($entries.Count -eq 1) "exactly one 'SabhaMujic' entry in Programs and Features (found $($entries.Count))"
    if ($entries.Count -ge 1) {
        Test-Check ($entries[0].DisplayVersion -eq $ExpectedVersion) "installed version is $ExpectedVersion (is $($entries[0].DisplayVersion))"
    }

    # A crash would restart the service 5 s later with a new process id; it must keep running without that.
    $processId = (Get-SabhaMujicService).ProcessId
    Start-Sleep -Seconds 15
    $after = Get-SabhaMujicService
    if (-not $after -or $after.State -ne 'Running' -or $after.ProcessId -ne $processId) {
        $afterText = if ($after) { "state $($after.State), process $processId -> $($after.ProcessId)" } else { 'the service was removed' }
        throw "Service did not stay running: $afterText."
    }
    Write-Host "  ok   service still running 15 s later (process $processId)"

    Show-ServiceLog
}

function Show-ServiceLog {
    $logs = Join-Path $DataRoot 'logs'
    Write-Host "::group::Service log ($logs)"
    if (Test-Path $logs) {
        foreach ($file in Get-ChildItem -LiteralPath $logs -Filter 'sabhamujic-*.log' | Sort-Object LastWriteTime) {
            Write-Host "--- $($file.FullName)"
            try { Get-Content -LiteralPath $file.FullName | Write-Host } catch { Write-Host "(could not read: $_)" }
        }
    }
    else {
        Write-Host '(no log folder)'
    }
    Write-Host '::endgroup::'
}

function Assert-Uninstalled {
    Write-Step 'Checking the uninstalled state'
    Test-Check (Wait-Until { -not (Get-SabhaMujicService) } 30) "service '$ServiceName' is removed"
    foreach ($expected in $FirewallRules) {
        Test-Check (@(Get-NetFirewallRule -DisplayName $expected.Name -ErrorAction SilentlyContinue).Count -eq 0) "firewall rule '$($expected.Name)' is removed"
    }
    Test-Check (-not (Test-Path -LiteralPath $ServiceExe)) "$ServiceExe is removed"
    Test-Check (-not (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'SabhaMujic'))) "$env:ProgramFiles\SabhaMujic is removed"
    Test-Check (-not (Test-Path -LiteralPath $StartMenuShortcut)) 'Start menu shortcut is removed'
    Test-Check (-not (Test-Path -LiteralPath $DesktopShortcut)) 'desktop shortcut is removed'
    Test-Check (@(Get-UninstallEntries).Count -eq 0) 'no SabhaMujic entry in Programs and Features'
    Test-Check (-not (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$EventSource")) 'Event Log source is unregistered'
    Assert-DataKept
}

function Assert-DataKept {
    Test-Check (Test-Path -LiteralPath $DataRoot) "$DataRoot is kept"
    Test-Check (Test-Path -LiteralPath $Marker) 'marker file written before is still there'
    Test-Check (Test-Path -LiteralPath (Join-Path $DataRoot 'data\sabhamujic.db')) 'database data\sabhamujic.db is kept'
    Test-Check (Test-Path -LiteralPath (Join-Path $DataRoot 'library')) 'library folder is kept'
}

# ------------------------------------------------------------------------------------------------------------- run

$identity = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test elevated (as Administrator).'
}
if (Get-SabhaMujicService) {
    throw "Service '$ServiceName' already exists. Run the smoke test on a machine without SabhaMujic."
}

$version = $Version
$upgradeVersion = $UpgradeVersion
Write-Host "MSI $Msi (version $version), upgrade MSI $UpgradeMsi (version $upgradeVersion)"
if ([version] $upgradeVersion -le [version] $version) {
    throw "-UpgradeVersion ($upgradeVersion) must be higher than -Version ($version)."
}

try {
    Write-Step "1. Install $version"
    Assert-MsiSucceeded (Invoke-Msiexec '/i' $Msi '1-install') 'Install'
    Assert-Installed $version
    Set-Content -LiteralPath $Marker -Value "Written by installer/smoke-test.ps1 at $(Get-Date -Format o)"

    Write-Step "2. Uninstall $version"
    Assert-MsiSucceeded (Invoke-Msiexec '/x' $Msi '2-uninstall') 'Uninstall'
    Assert-Uninstalled

    Write-Step "3. Reinstall $version over the kept data"
    Assert-MsiSucceeded (Invoke-Msiexec '/i' $Msi '3-reinstall') 'Reinstall'
    Assert-Installed $version
    Assert-DataKept

    Write-Step "4. Upgrade $version -> $upgradeVersion"
    Assert-MsiSucceeded (Invoke-Msiexec '/i' $UpgradeMsi '4-upgrade') 'Upgrade'
    Assert-Installed $upgradeVersion
    Assert-DataKept

    Write-Step "5. Downgrade to $version must be refused"
    $downgrade = Invoke-Msiexec '/i' $Msi '5-downgrade'
    Test-Check ($downgrade.ExitCode -ne 0 -and $downgrade.ExitCode -ne 3010) "installing $version over $upgradeVersion is refused (exit code $($downgrade.ExitCode))"
    $entries = @(Get-UninstallEntries)
    Test-Check ($entries.Count -eq 1 -and $entries[0].DisplayVersion -eq $upgradeVersion) "$upgradeVersion is still the installed version"
    Test-Check ((Get-ServiceState) -eq 'Running') 'service is still running'

    Write-Step "6. Uninstall $upgradeVersion"
    Assert-MsiSucceeded (Invoke-Msiexec '/x' $UpgradeMsi '6-uninstall') 'Uninstall after upgrade'
    Assert-Uninstalled
}
catch {
    Write-Host "`nSmoke test stopped: $_" -ForegroundColor Red
    Show-Diagnostics
    if ($script:Failures.Count -gt 0) {
        Write-Host "Failed checks before that:`n  - $($script:Failures -join "`n  - ")" -ForegroundColor Red
    }
    exit 1
}

if ($script:Failures.Count -gt 0) {
    Show-Diagnostics
    Write-Host "`n$($script:Failures.Count) check(s) failed:`n  - $($script:Failures -join "`n  - ")" -ForegroundColor Red
    exit 1
}

Write-Host "`nInstaller smoke test passed." -ForegroundColor Green
