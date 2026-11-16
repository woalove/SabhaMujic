<#
.SYNOPSIS
    Silent install / uninstall / reinstall / upgrade smoke test for the SabhaMujic MSI.

.DESCRIPTION
    Run elevated on a disposable Windows machine (GitHub Actions windows-latest, or a test VM) that has no
    %ProgramData%\SabhaMujic yet: it creates and deletes a temporary local user, installs and removes SabhaMujic, and
    leaves %ProgramData%\SabhaMujic behind.

    0. A temporary standard user "pre-creates" %ProgramData%\SabhaMujic and its library folder and owns them
       (the attack from decision #34).
    1. Installs -Msi silently, then checks the service (account, start mode, failure actions, Running, still running
       15 s later), the desktop pipe, the remote setup site, the owner and exact DACLs of the data root and its folders
       (taken over by the installer), the Event Log source, the firewall rules, shortcuts and the Programs and Features
       entry, and prints the service log.
       1b. With library replaced by a junction, the service must refuse to start and log a critical entry; the folder
       and the service are then restored.
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
$script:HealthText = ''

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
    # Get-WinEvent throws "The parameter is incorrect" (ignoring -ErrorAction) when the provider isn't registered yet,
    # e.g. before the first install: diagnostics must never be what fails the run.
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $EventSource } -MaxEvents 30 -ErrorAction SilentlyContinue |
            ForEach-Object { "{0:u} {1} {2}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Message } | Write-Host
    } catch { Write-Host "(no events from ${EventSource}: $($_.Exception.Message))" }
    Write-Host '--- System log, Service Control Manager, mentioning SabhaMujic'
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $script:StartedAt } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -like '*SabhaMujic*' } |
            ForEach-Object { "{0:u} {1} {2}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Message } | Write-Host
    } catch { Write-Host "(no Service Control Manager events: $($_.Exception.Message))" }
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

# Enables a privilege the elevated token holds but has disabled (SeRestorePrivilege: set another account as owner).
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SmokeTestPrivilege
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivilege { public int Count; public Luid Luid; public int Attributes; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returned);

    public static void Enable(string name)
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) throw new Win32Exception();
        try
        {
            Luid luid;
            if (!LookupPrivilegeValue(null, name, out luid)) throw new Win32Exception();
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "Privilege " + name + " is not held.");
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
'@

$SidSystem = 'S-1-5-18'
$SidAdministrators = 'S-1-5-32-544'
$SidUsers = 'S-1-5-32-545'
# Write-type rights a read-only Users entry must not have: WriteData, AppendData, WriteExtendedAttributes,
# DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, ChangePermissions, TakeOwnership.
$WriteRights = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000
$ReadAndExecute = [int] [System.Security.AccessControl.FileSystemRights]::ReadAndExecute
$FullControl = [int] [System.Security.AccessControl.FileSystemRights]::FullControl

# Decision #34: owner Administrators; protected DACLs; SYSTEM, Administrators and the service full control everywhere;
# Users read & execute on the root folder itself and read-only on logs; nobody else.
function Assert-FolderSecurity([string] $Path, [string] $Label, [string] $ServiceSid, [bool] $UsersRead) {
    if (-not (Wait-Until { Test-Path -LiteralPath $Path } 30)) {
        Test-Check $false "$Label exists ($Path)"
        return
    }
    Test-Check (-not (Test-ReparsePoint $Path)) "$Label is a real folder, not a junction or link"
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    Test-Check ($owner -eq $SidAdministrators) "$Label is owned by Administrators (owner: $owner)"
    Test-Check $acl.AreAccessRulesProtected "$Label does not inherit permissions"

    $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    $expected = @($SidSystem, $SidAdministrators, $ServiceSid) + $(if ($UsersRead) { @($SidUsers) } else { @() })
    $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Test-Check ((($sids | Sort-Object) -join ',') -eq (($expected | Sort-Object) -join ',')) "$Label grants exactly $(if ($UsersRead) { 'SYSTEM, Administrators, the service and Users (read)' } else { 'SYSTEM, Administrators and the service' }) (has: $($sids -join ', '))"
    foreach ($sid in $SidSystem, $SidAdministrators, $ServiceSid) {
        $rights = Get-AllowedRights $rules $sid
        Test-Check (($rights -band $FullControl) -eq $FullControl) "$Label gives $sid full control"
    }
    if ($UsersRead) {
        $rights = Get-AllowedRights $rules $SidUsers
        Test-Check ((($rights -band $ReadAndExecute) -eq $ReadAndExecute) -and (($rights -band $WriteRights) -eq 0)) "$Label gives Users read & execute only (rights: 0x$('{0:X}' -f $rights))"
    }
    $denies = @($rules | Where-Object { $_.AccessControlType -ne 'Allow' })
    Test-Check ($denies.Count -eq 0) "$Label has no deny entries"
}

function Test-ReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return [bool] ($item -and ([int] ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) -ne 0)
}

function Get-AllowedRights($Rules, [string] $Sid) {
    $rights = 0
    foreach ($rule in $Rules) {
        if ($rule.IdentityReference.Value -eq $Sid -and $rule.AccessControlType -eq 'Allow') {
            $rights = $rights -bor [int] $rule.FileSystemRights
        }
    }
    return $rights
}

function Assert-DataRootSecurity {
    $serviceSid = ([System.Security.Principal.NTAccount] $ServiceAccount).Translate([System.Security.Principal.SecurityIdentifier]).Value
    Assert-FolderSecurity $DataRoot 'data root' $serviceSid $true
    foreach ($name in 'data', 'library', 'uploads', 'certs') {
        Assert-FolderSecurity (Join-Path $DataRoot $name) $name $serviceSid $false
    }
    Assert-FolderSecurity (Join-Path $DataRoot 'logs') 'logs' $serviceSid $true
}

# Simulates the attack from decision #34: a standard user creates the data root and library before SabhaMujic is
# installed, owns them and gives themselves full control. The installer's prepare step must take both over.
function New-SquattedDataRoot {
    $script:TempUser = 'smtest' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $password = 'Sm!' + [guid]::NewGuid().ToString('N') + 'a1Z'
    # /y answers net.exe's interactive "password longer than 14 characters, continue?" prompt (CI run 26 hung on it).
    & net.exe user $script:TempUser $password /add /y /comment:"SabhaMujic smoke test (temporary)" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not create the temporary user $script:TempUser (net user exit code $LASTEXITCODE)." }
    $userSid = ([System.Security.Principal.NTAccount] $script:TempUser).Translate([System.Security.Principal.SecurityIdentifier])
    [SmokeTestPrivilege]::Enable('SeRestorePrivilege')
    foreach ($dir in $DataRoot, (Join-Path $DataRoot 'library')) {
        New-Item -ItemType Directory -Path $dir | Out-Null
        $acl = Get-Acl -LiteralPath $dir
        $acl.SetOwner($userSid)
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($userSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $dir -AclObject $acl
        $owner = (Get-Acl -LiteralPath $dir).GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        if ($owner -ne $userSid.Value) { throw "Could not make $script:TempUser the owner of $dir (owner: $owner)." }
        Write-Host "  prepared $dir owned by $script:TempUser ($owner) with full control for that user"
    }
}

function Remove-TempUser {
    if ($script:TempUser) {
        & net.exe user $script:TempUser /delete | Out-Host
        $script:TempUser = $null
    }
}

# Decision #34: with library replaced by a junction, the service must refuse to start and log a critical (Event Log
# level Error) entry. Restores the folder and the service afterwards. Start type is set to Disabled while restoring, so
# failure-action restarts can't race with the swap back.
function Test-JunctionRefused {
    $library = Join-Path $DataRoot 'library'
    $real = Join-Path $DataRoot 'library-smoke-real'
    Stop-Service -Name $ServiceName -Force
    if (-not (Wait-Until { (Get-ServiceState) -eq 'Stopped' } 60)) { throw "Service did not stop (state: $(Get-ServiceState))." }
    Rename-Item -LiteralPath $library -NewName (Split-Path $real -Leaf)
    try {
        New-Item -ItemType Junction -Path $library -Target $real | Out-Null
        $since = Get-Date
        try { Start-Service -Name $ServiceName -ErrorAction Stop } catch { Write-Host "  Start-Service failed as expected: $($_.Exception.Message)" }
        $logged = Wait-Until {
            try {
                [bool] (Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $EventSource; StartTime = $since } -ErrorAction SilentlyContinue |
                    Where-Object { $_.Level -le 2 -and $_.Message -like '*junction*' } | Select-Object -First 1)
            } catch { $false }
        } 60
        Test-Check $logged "with library as a junction, '$EventSource' logs a critical entry about the junction"
        Test-Check ((Get-ServiceState) -ne 'Running') "with library as a junction, the service does not run (state: $(Get-ServiceState))"
    }
    finally {
        Set-Service -Name $ServiceName -StartupType Disabled
        Wait-Until { (Get-ServiceState) -eq 'Stopped' } 60 | Out-Null
        if (Test-ReparsePoint $library) {
            [System.IO.Directory]::Delete($library)   # removes the junction only, never its target
        }
        if (-not (Test-Path -LiteralPath $library)) {
            Rename-Item -LiteralPath $real -NewName 'library'
        }
        Set-Service -Name $ServiceName -StartupType AutomaticDelayedStart
        Start-Service -Name $ServiceName
    }
    $running = Wait-Until { (Get-ServiceState) -eq 'Running' } 60
    $pipe = Wait-Until { [bool](Test-PipeExists) } 60
    if (-not ($running -and $pipe)) { throw "Service did not come back after the junction test (state: $(Get-ServiceState))." }
    Write-Host '  ok   library restored; the service runs again'
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

    # The iPhone remote is on by default (SabhaMujic:Remote:Enabled); its plain-HTTP setup site answers /health.
    $healthy = Wait-Until {
        try {
            $response = Invoke-WebRequest -Uri 'http://localhost:8080/health' -TimeoutSec 5 -UseBasicParsing
            $script:HealthText = "$($response.StatusCode) $($response.Content)"
            return $response.StatusCode -eq 200 -and "$($response.Content)".Trim() -eq 'OK'
        }
        catch {
            $script:HealthText = $_.Exception.Message
            return $false
        }
    } 30
    $healthText = $script:HealthText
    Test-Check $healthy "remote setup site http://localhost:8080/health answers OK ($healthText)"

    Assert-DataRootSecurity

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
if (Test-Path -LiteralPath $DataRoot) {
    throw "$DataRoot already exists. The smoke test creates it itself (as a squatting standard user); remove it first."
}
$script:TempUser = $null

$version = $Version
$upgradeVersion = $UpgradeVersion
Write-Host "MSI $Msi (version $version), upgrade MSI $UpgradeMsi (version $upgradeVersion)"
if ([version] $upgradeVersion -le [version] $version) {
    throw "-UpgradeVersion ($upgradeVersion) must be higher than -Version ($version)."
}

try {
    Write-Step '0. A standard user pre-creates the data root and library (decision #34 attack)'
    New-SquattedDataRoot

    Write-Step "1. Install $version"
    Assert-MsiSucceeded (Invoke-Msiexec '/i' $Msi '1-install') 'Install'
    Assert-Installed $version

    Write-Step '1b. A junction in place of library must stop the service'
    Test-JunctionRefused
    Assert-DataRootSecurity

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
    Remove-TempUser
    exit 1
}
Remove-TempUser

if ($script:Failures.Count -gt 0) {
    Show-Diagnostics
    Write-Host "`n$($script:Failures.Count) check(s) failed:`n  - $($script:Failures -join "`n  - ")" -ForegroundColor Red
    exit 1
}

Write-Host "`nInstaller smoke test passed." -ForegroundColor Green
