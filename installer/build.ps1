<#
.SYNOPSIS
    Builds the SabhaMujic MSI: artifacts/SabhaMujic-<Version>-x64.msi.

.DESCRIPTION
    1. Publishes src/SabhaMujic.Service and src/SabhaMujic.Desktop (self-contained win-x64, as set in their project
       files) into artifacts/service and artifacts/desktop. -SkipPublish reuses existing publish output instead.
    2. Builds installer/SabhaMujic.Installer/SabhaMujic.Installer.wixproj (WiX 5.0.2) with that output.
    3. Copies the MSI to artifacts/ and prints its path.
    Windows only (WiX does not build MSIs on Linux). Needs the .NET SDK from global.json.

.EXAMPLE
    pwsh installer/build.ps1
    pwsh installer/build.ps1 -Version 0.2.0
    pwsh installer/build.ps1 -Version 0.1.1 -SkipPublish
#>
[CmdletBinding()]
param(
    # MSI ProductVersion: major.minor.build (major and minor at most 255, build at most 65535).
    [string] $Version = '0.1.0',
    [string] $Configuration = 'Release',
    # Reuse artifacts/service and artifacts/desktop (e.g. from CI's publish step) instead of publishing again.
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$' -or [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw "Version '$Version' is not a valid MSI version (major.minor.build, e.g. 0.1.0; major/minor <= 255, build <= 65535)."
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repo 'artifacts'
$serviceDir = Join-Path $artifacts 'service'
$desktopDir = Join-Path $artifacts 'desktop'
$wixproj = Join-Path $repo 'installer/SabhaMujic.Installer/SabhaMujic.Installer.wixproj'

function Invoke-Dotnet {
    # Out-Host keeps the tool's output out of this script's output, which is only the MSI path.
    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipPublish) {
    # Start from empty folders: everything in them is harvested into the MSI.
    foreach ($dir in $serviceDir, $desktopDir) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }

    Write-Host "Publishing the service and the desktop app ($Configuration, version $Version)..."
    Invoke-Dotnet publish (Join-Path $repo 'src/SabhaMujic.Service') -c $Configuration -o $serviceDir "-p:Version=$Version"
    Invoke-Dotnet publish (Join-Path $repo 'src/SabhaMujic.Desktop') -c $Configuration -o $desktopDir "-p:Version=$Version"
}

foreach ($required in (Join-Path $serviceDir 'SabhaMujic.Service.exe'), (Join-Path $desktopDir 'SabhaMujic.Desktop.exe')) {
    if (-not (Test-Path $required)) {
        throw "Missing $required. Run without -SkipPublish, or publish first (see docs/BUILDING.md)."
    }
}

Write-Host "Building the MSI (WiX 5.0.2, version $Version)..."
# --no-incremental: the harvested publish folders and the version are not inputs MSBuild can see, so never reuse an
# earlier MSI.
Invoke-Dotnet build $wixproj -c $Configuration --no-incremental `
    "-p:SabhaMujicVersion=$Version" "-p:ServicePublishDir=$serviceDir" "-p:DesktopPublishDir=$desktopDir"

$msiName = "SabhaMujic-$Version-x64.msi"
$built = Join-Path $repo "installer/SabhaMujic.Installer/bin/$Configuration/$msiName"
if (-not (Test-Path $built)) {
    throw "The build succeeded but $built was not found."
}

$output = Join-Path $artifacts $msiName
Copy-Item $built $output -Force
Write-Host "MSI: $output"
Write-Output $output
