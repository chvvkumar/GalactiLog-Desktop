<#
.SYNOPSIS
Cleans up, builds, packs, and either saves or installs a local GalactiLog build.

.DESCRIPTION
Runs the local publish and pack procedure from docs/packaging.md, then either copies the
Velopack setup executable to a folder (default: the Downloads folder) or installs it silently.

Optionally removes the existing installation first:

  -Clean None   Leave the existing installation alone (default).
  -Clean App    Uninstall the application through the registered Velopack uninstaller.
                The catalogue, logs, thumbnail cache and settings are kept.
  -Clean All    Uninstall the application and delete the data root named in
                %APPDATA%\GalactiLog\datapath.json (default %LOCALAPPDATA%\GalactiLogData)
                and the pointer file itself, without asking. This removes the catalogue
                database. It never touches image files.

The script sets DOTNET_ROOT only for the vpk call, inside this process, and clears it again
afterwards. It never persists the variable.

.PARAMETER Clean
None, App, or All. See the description.

.PARAMETER Install
Install the packed build silently instead of saving the setup executable.

.PARAMETER OutputPath
Folder that receives the setup executable when -Install is not given.
Defaults to the user's Downloads folder.

.PARAMETER Version
Semantic version stamped on the build and the package. Defaults to a synthetic
0.0.1-local.<yyyyMMddHHmm> that sorts below every real release.

.PARAMETER Channel
Velopack channel: alpha, rc, or stable. Defaults to alpha.

.PARAMETER Configuration
Build configuration. Defaults to Release.

.PARAMETER SeedLocation
After delivery, read the observer's position from -GpsUrl and write observer_latitude,
observer_longitude and the matching time zone into the catalogue database, so the setup wizard
opens with the Location step prefilled. Runs GalactiLog.exe scan first to create the database
when it does not exist yet (the installed build with -Install, otherwise the published one), then
tools\Seed-Location.cs through dotnet run. Requires a GPS fix.

.PARAMETER GpsUrl
Status endpoint that returns JSON with lat, lon and fixStatus.
Defaults to http://espgps.lan/api/status.

.PARAMETER Force
Accepted and ignored. -Clean All no longer asks for a typed confirmation; the switch stays so
existing command lines keep working.

.EXAMPLE
.\tools\Build-GalactiLog.ps1
Build and copy the setup executable to Downloads.

.EXAMPLE
.\tools\Build-GalactiLog.ps1 -Clean App -Install
Uninstall the current application, keep the data, build, and install the new build.

.EXAMPLE
.\tools\Build-GalactiLog.ps1 -Clean All -Install -Force
Wipe the application and its data without prompting, then build and install.
#>
[CmdletBinding()]
param(
    [ValidateSet('None', 'App', 'All')]
    [string] $Clean = 'None',

    [switch] $Install,

    [string] $OutputPath = (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'),

    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version = ('0.0.1-local.{0}' -f (Get-Date -Format 'yyyyMMddHHmm')),

    [ValidateSet('alpha', 'rc', 'stable')]
    [string] $Channel = 'alpha',

    [string] $Configuration = 'Release',

    [switch] $SeedLocation,

    [string] $GpsUrl = 'http://espgps.lan/api/status',

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------------------------

$RepoRoot      = Split-Path -Parent $PSScriptRoot
$ProjectPath   = Join-Path $RepoRoot 'src\GalactiLog.App'
$PublishDir    = Join-Path $RepoRoot 'publish\win-x64'
$ReleasesDir   = Join-Path $RepoRoot 'Releases\local'
$PackId        = 'GalactiLog'
$MainExe       = 'GalactiLog.exe'

$InstallRoot   = Join-Path $env:LOCALAPPDATA 'GalactiLog'
$UpdateExe     = Join-Path $InstallRoot 'Update.exe'
$DefaultData   = Join-Path $env:LOCALAPPDATA 'GalactiLogData'
$PointerDir    = Join-Path $env:APPDATA 'GalactiLog'
$PointerFile   = Join-Path $PointerDir 'datapath.json'
$StartupLink   = Join-Path ([Environment]::GetFolderPath('Startup')) 'GalactiLog.lnk'
$UninstallKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GalactiLog'

$UserDotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$MachineDotnet = 'C:\Program Files\dotnet'
$UserVpk       = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host ('==> ' + $Text) -ForegroundColor Cyan
}

function Write-Info([string] $Text) {
    Write-Host ('    ' + $Text)
}

function Invoke-Native([string] $Exe, [string[]] $Arguments, [string] $WorkingDirectory) {
    # Runs a native executable, streams its output, and throws on a non-zero exit code.
    Write-Info ('$ ' + $Exe + ' ' + ($Arguments -join ' '))
    $previous = Get-Location
    try {
        if ($WorkingDirectory) { Set-Location $WorkingDirectory }
        & $Exe @Arguments | Out-Host
        $code = $LASTEXITCODE
    }
    finally {
        Set-Location $previous
    }
    if ($code -ne 0) {
        throw ('{0} exited with code {1}.' -f (Split-Path -Leaf $Exe), $code)
    }
}

function Test-DotnetSdk10([string] $DotnetExe) {
    if (-not (Test-Path $DotnetExe)) { return $false }
    $sdks = & $DotnetExe --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sdks) { return $false }
    return [bool] ($sdks | Where-Object { $_ -match '^10\.0\.4\d\d' })
}

function Resolve-Dotnet {
    # Order: whatever dotnet is on PATH, then the user-profile install, then the machine install.
    # The first one carrying a 10.0.4xx SDK wins. HANDOFF section 3.1 item 2.
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $UserDotnetDir 'dotnet.exe')
    $candidates += (Join-Path $MachineDotnet 'dotnet.exe')

    foreach ($candidate in $candidates) {
        if (Test-DotnetSdk10 $candidate) { return $candidate }
    }
    throw ('No .NET SDK 10.0.4xx found. Checked: ' + ($candidates -join ', ') +
           '. See docs/packaging.md.')
}

function Resolve-Vpk {
    $onPath = Get-Command vpk -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    if (Test-Path $UserVpk) { return $UserVpk }
    throw 'vpk not found. Install it with: dotnet tool install -g vpk --version 1.2.0'
}

function Remove-DirectoryWithRetry([string] $Path, [int] $Attempts = 10) {
    # Update.exe cannot delete itself while it is still exiting, so the install root can stay
    # locked for a moment after the uninstall returns. Retry briefly before giving up.
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Seconds 1
        }
    }
}

function Stop-GalactiLog {
    $running = @(Get-Process -Name 'GalactiLog' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    foreach ($process in $running) {
        Write-Info ('Stopping GalactiLog (PID {0}).' -f $process.Id)
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    $null = $running | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}

function Get-DataRoots {
    # Every directory the data location pointer names, plus the default, de-duplicated.
    # Only the ones that exist on disk are returned.
    $roots = New-Object System.Collections.Generic.List[string]
    $roots.Add($DefaultData)
    if (Test-Path $PointerFile) {
        try {
            $document = Get-Content $PointerFile -Raw | ConvertFrom-Json
            foreach ($name in 'data_root', 'pending_root', 'previous_root') {
                $property = $document.PSObject.Properties[$name]
                if ($property -and $property.Value) { $roots.Add([string] $property.Value) }
            }
        }
        catch {
            Write-Warning ('Could not read {0}: {1}' -f $PointerFile, $_.Exception.Message)
        }
    }
    $seen = @{}
    $result = @()
    foreach ($root in $roots) {
        $full = [IO.Path]::GetFullPath($root).TrimEnd('\')
        $key = $full.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        # Refuse a drive root or a share root, whatever the pointer says.
        if ($full -match '^[A-Za-z]:$' -or $full -match '^\\\\[^\\]+\\[^\\]+$') { continue }
        if (Test-Path $full) { $result += $full }
    }
    return $result
}

# ---------------------------------------------------------------------------------------------
# 1. Cleanup
# ---------------------------------------------------------------------------------------------

function Remove-Application {
    Write-Step 'Uninstalling the existing application'
    Stop-GalactiLog

    if (Test-Path $UpdateExe) {
        Write-Info ('$ ' + $UpdateExe + ' uninstall --silent')
        & $UpdateExe uninstall --silent | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning ('Update.exe uninstall exited with code {0}; removing leftovers by hand.' -f $LASTEXITCODE)
        }
    }
    else {
        Write-Info 'No Velopack Update.exe at the install root; nothing registered to uninstall.'
    }

    # Velopack removes the install root, the shortcuts and the registry key. Sweep whatever is
    # left. The install root holds binaries only (design-spec.md 17.2), never user data.
    if (Test-Path $InstallRoot) {
        Write-Info ('Removing leftover install root {0}' -f $InstallRoot)
        Remove-DirectoryWithRetry $InstallRoot
    }
    if (Test-Path $StartupLink) {
        Write-Info ('Removing leftover Startup shortcut {0}' -f $StartupLink)
        Remove-Item $StartupLink -Force
    }
    if (Test-Path $UninstallKey) {
        Write-Info ('Removing leftover uninstall registry key {0}' -f $UninstallKey)
        Remove-Item $UninstallKey -Recurse -Force
    }
    Write-Info 'Application removed. Data root and settings were not touched.'
}

function Remove-Data {
    Write-Step 'Removing application data'
    $targets = @(Get-DataRoots)
    if (Test-Path $PointerDir) { $targets += $PointerDir }

    if ($targets.Count -eq 0) {
        Write-Info 'No data root and no data location pointer found; nothing to remove.'
        return
    }

    Write-Host ''
    Write-Host 'The following will be deleted permanently:' -ForegroundColor Yellow
    foreach ($target in $targets) { Write-Host ('    ' + $target) -ForegroundColor Yellow }
    Write-Host 'This includes the catalogue database, logs and thumbnail cache.' -ForegroundColor Yellow
    Write-Host 'Image files are never touched.' -ForegroundColor Yellow

    foreach ($target in $targets) {
        Write-Info ('Removing ' + $target)
        Remove-Item $target -Recurse -Force
    }
    Write-Info 'Application data removed.'
}

# ---------------------------------------------------------------------------------------------
# 2. Build and pack
# ---------------------------------------------------------------------------------------------

function Invoke-Publish([string] $DotnetExe) {
    Write-Step ('Publishing {0} ({1}, win-x64, self-contained)' -f $Version, $Configuration)
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    Invoke-Native $DotnetExe @(
        'publish', $ProjectPath,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained',
        '-o', $PublishDir,
        ('-p:Version=' + $Version)
    ) $RepoRoot

    $exe = Join-Path $PublishDir $MainExe
    if (-not (Test-Path $exe)) { throw ('Publish produced no {0}.' -f $exe) }
    if (-not (Test-Path (Join-Path $PublishDir 'Catalogs'))) { throw 'Publish produced no Catalogs directory.' }
}

function Invoke-Pack([string] $VpkExe, [string] $DotnetDir) {
    Write-Step ('Packing {0} on channel {1}' -f $Version, $Channel)
    if (Test-Path $ReleasesDir) { Remove-Item $ReleasesDir -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $ReleasesDir

    # vpk's apphost resolves the runtime through DOTNET_ROOT, never PATH. Scope the variable to
    # this one call and clear it afterwards. Never persist it (HANDOFF section 3.1 item 7).
    $env:DOTNET_ROOT = $DotnetDir
    try {
        Invoke-Native $VpkExe @(
            'pack',
            '--packId', $PackId,
            '--packVersion', $Version,
            '--packDir', $PublishDir,
            '--mainExe', $MainExe,
            '--channel', $Channel,
            '--outputDir', $ReleasesDir
        ) $RepoRoot
    }
    finally {
        Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
    }

    $setup = Join-Path $ReleasesDir ('{0}-{1}-Setup.exe' -f $PackId, $Channel)
    if (-not (Test-Path $setup)) { throw ('Pack produced no {0}.' -f $setup) }
    Write-Info 'Produced:'
    Get-ChildItem $ReleasesDir | ForEach-Object { Write-Info ('  ' + $_.Name) }
    return $setup
}

# ---------------------------------------------------------------------------------------------
# 3. Deliver
# ---------------------------------------------------------------------------------------------

function Install-Setup([string] $SetupExe) {
    Write-Step 'Installing'
    Stop-GalactiLog
    Write-Info ('$ ' + $SetupExe + ' --silent')
    $process = Start-Process -FilePath $SetupExe -ArgumentList '--silent' -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw ('Setup exited with code {0}.' -f $process.ExitCode)
    }

    $installed = Join-Path $InstallRoot ('current\' + $MainExe)
    if (-not (Test-Path $installed)) {
        throw ('Setup finished but {0} is missing.' -f $installed)
    }
    $registered = ''
    if (Test-Path $UninstallKey) {
        $registered = [string] (Get-ItemProperty $UninstallKey).DisplayVersion
    }
    Write-Info ('Installed to ' + $InstallRoot)
    Write-Info ('Registered version: ' + $registered)
    Write-Info ('Launch with: ' + $installed)
}

function Seed-Location([string] $DotnetExe) {
    Write-Step ('Seeding observer location from ' + $GpsUrl)
    $exeDir = if ($Install) { Join-Path $InstallRoot 'current' } else { $PublishDir }
    # A CLI verb creates the database and runs migrations; with no scan roots it exits 0 at once.
    Invoke-Native (Join-Path $exeDir $MainExe) @('scan', '--quiet') $RepoRoot

    $dataRoot = $DefaultData
    if (Test-Path $PointerFile) {
        $stored = (Get-Content $PointerFile -Raw | ConvertFrom-Json).PSObject.Properties['data_root']
        if ($stored -and $stored.Value) { $dataRoot = [string] $stored.Value }
    }
    $database = Join-Path $dataRoot 'galactilog.db'
    if (-not (Test-Path $database)) { throw ('Scan finished but {0} is missing.' -f $database) }

    Invoke-Native $DotnetExe @('run', (Join-Path $PSScriptRoot 'Seed-Location.cs'), '--', $database, $GpsUrl) $RepoRoot
}

function Save-Setup([string] $SetupExe) {
    Write-Step ('Saving the setup executable to ' + $OutputPath)
    if (-not (Test-Path $OutputPath)) {
        $null = New-Item -ItemType Directory -Path $OutputPath
    }
    $target = Join-Path $OutputPath ('{0}-{1}-{2}-Setup.exe' -f $PackId, $Version, $Channel)
    Copy-Item $SetupExe $target -Force
    Write-Info ('Saved ' + $target)
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

$dotnetExe = Resolve-Dotnet
$dotnetDir = Split-Path -Parent $dotnetExe
$vpkExe    = Resolve-Vpk

Write-Step 'Configuration'
Write-Info ('Repository:    ' + $RepoRoot)
Write-Info ('dotnet:        ' + $dotnetExe)
Write-Info ('vpk:           ' + $vpkExe)
Write-Info ('Version:       ' + $Version)
Write-Info ('Channel:       ' + $Channel)
Write-Info ('Clean:         ' + $Clean)
if ($Install) { Write-Info 'Deliver:       install' } else { Write-Info ('Deliver:       save to ' + $OutputPath) }
if ($SeedLocation) { Write-Info ('Seed location: ' + $GpsUrl) }

# Put the chosen SDK first on this process's PATH so every dotnet child process agrees on it.
# Process scope only; nothing is persisted.
$env:PATH = $dotnetDir + ';' + $env:PATH

switch ($Clean) {
    'App' { Remove-Application }
    'All' { Remove-Application; Remove-Data }
}

Invoke-Publish $dotnetExe
$setupExe = Invoke-Pack $vpkExe $dotnetDir

if ($Install) { Install-Setup $setupExe } else { Save-Setup $setupExe }
if ($SeedLocation) { Seed-Location $dotnetExe }

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
