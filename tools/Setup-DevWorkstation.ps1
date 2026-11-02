#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Prepares a Windows 10/11 workstation to build, debug and test the WebText app.

.DESCRIPTION
    - Installs the .NET 10 SDK (needed to build and debug).
    - Enables local IIS with the same features the production server uses (Windows Authentication, WebSockets,
      Application Initialization), plus logging/tracing that help when debugging. Then installs the ASP.NET Core 10
      Hosting Bundle so the app can be hosted in IIS. Skip with -SkipIis if you only debug with 'dotnet run' / Visual Studio.
    - Unlocks the IIS authentication sections the app's web.config sets.
    - Optionally installs SQL Server Express (-IncludeSqlExpress). Not required: in Development the app uses a SQLite file.

    Run in an elevated PowerShell window *as your own account* (so the HTTPS dev certificate is trusted for you):
        Set-ExecutionPolicy -Scope Process Bypass
        .\tools\Setup-DevWorkstation.ps1

.PARAMETER SkipIis
    Don't enable IIS or install the Hosting Bundle. 'dotnet run' and Visual Studio debugging work without IIS.

.PARAMETER IncludeSqlExpress
    Also install SQL Server Express, to test against SQL Server instead of SQLite.
#>
[CmdletBinding()]
param(
    [switch]$SkipIis,
    [switch]$IncludeSqlExpress
)

$ErrorActionPreference = 'Stop'
$restartNeeded = $false

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Install-WingetPackage([string]$Id, [string]$Name) {
    Write-Host "    $Name ($Id)"
    winget install --id $Id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    # Non-zero codes that just mean "already installed / nothing to update":
    #   0x8A15002B (-1978335189) no applicable update; 0x8A150061 (-1978335135) package already installed.
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189 -and $LASTEXITCODE -ne -1978335135) {
        Write-Warning "winget exit code $LASTEXITCODE for $Id. Install it manually if it isn't already present."
    }
}

# ---------------------------------------------------------------- checks
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -ne 1) {
    Write-Warning "This looks like Windows Server. Use the server command from the README instead:"
    Write-Host   "  Install-WindowsFeature Web-Server, Web-Windows-Auth, Web-WebSockets, Web-AppInit, Web-Mgmt-Console"
    return
}
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget wasn't found. Install 'App Installer' from the Microsoft Store, then run this script again."
}

# ---------------------------------------------------------------- .NET 10 SDK
Write-Step "Installing the .NET 10 SDK"
Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK'

# ---------------------------------------------------------------- IIS (optional)
if (-not $SkipIis) {
    Write-Step "Enabling IIS features"
    # Workstation names for: Web-Server, Web-Windows-Auth, Web-WebSockets, Web-AppInit, Web-Mgmt-Console,
    # plus HTTP logging and Failed Request Tracing for debugging. -All also enables the parent features.
    $features = @(
        'IIS-WebServerRole',
        'IIS-WebServer',
        'IIS-CommonHttpFeatures',
        'IIS-StaticContent',
        'IIS-DefaultDocument',
        'IIS-HttpErrors',
        'IIS-Security',
        'IIS-RequestFiltering',
        'IIS-WindowsAuthentication',
        'IIS-ApplicationDevelopment',
        'IIS-WebSockets',
        'IIS-ApplicationInit',
        'IIS-HealthAndDiagnostics',
        'IIS-HttpLogging',
        'IIS-HttpTracing',
        'IIS-WebServerManagementTools',
        'IIS-ManagementConsole'
    )
    foreach ($f in $features) {
        $state = (Get-WindowsOptionalFeature -Online -FeatureName $f).State
        if ($state -eq 'Enabled') { Write-Host "    $f (already enabled)"; continue }
        Write-Host "    $f"
        $r = Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart
        if ($r.RestartNeeded) { $restartNeeded = $true }
    }

    # The Hosting Bundle must be installed AFTER IIS so it registers the ASP.NET Core Module with IIS.
    Write-Step "Installing the ASP.NET Core 10 Hosting Bundle"
    Install-WingetPackage 'Microsoft.DotNet.HostingBundle.10' 'ASP.NET Core 10 Hosting Bundle'

    # The app's web.config turns on Windows + Anonymous auth; IIS locks those sections by default.
    Write-Step "Unlocking IIS authentication settings"
    $appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
    if (Test-Path $appcmd) {
        & $appcmd unlock config -section:system.webServer/security/authentication/windowsAuthentication | Out-Null
        & $appcmd unlock config -section:system.webServer/security/authentication/anonymousAuthentication | Out-Null
        Write-Host "    done"
    } else {
        Write-Warning "appcmd.exe not found yet (a restart may be needed). Re-run this script after restarting."
    }

    Write-Step "Restarting IIS"
    try { iisreset /restart | Out-Null; Write-Host "    done" } catch { Write-Warning "iisreset failed: $_" }
}

# ---------------------------------------------------------------- SQL Server Express (optional)
if ($IncludeSqlExpress) {
    Write-Step "Installing SQL Server Express"
    Install-WingetPackage 'Microsoft.SQLServer.2022.Express' 'SQL Server 2022 Express'
}

# ---------------------------------------------------------------- HTTPS dev certificate
Write-Step "Trusting the ASP.NET Core HTTPS development certificate"
# Refresh PATH so a freshly installed 'dotnet' is found in this window.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    dotnet dev-certs https --trust
} else {
    Write-Warning "'dotnet' isn't on PATH yet. Open a new window and run: dotnet dev-certs https --trust"
}

# ---------------------------------------------------------------- summary
Write-Step "Summary"
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host "    SDKs:";     dotnet --list-sdks     | ForEach-Object { Write-Host "      $_" }
    Write-Host "    Runtimes:"; dotnet --list-runtimes | Where-Object { $_ -match 'AspNetCore' } | ForEach-Object { Write-Host "      $_" }
}
if (-not $SkipIis) {
    $module = Test-Path (Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll')
    Write-Host ("    IIS ASP.NET Core Module: " + ($(if ($module) { 'installed' } else { 'NOT found - run the Hosting Bundle installer and choose Repair' })))
}
# Work out the real project folder from where this script lives (tools\ is next to src\).
$projectDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\WebText.Web'
if (-not (Test-Path (Join-Path $projectDir 'WebText.Web.csproj'))) {
    $found = Get-ChildItem -Path (Split-Path $PSScriptRoot -Parent) -Filter WebText.Web.csproj -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $projectDir = $found.DirectoryName }
}
Write-Host @"

Next steps (copy and paste):
  cd "$projectDir"
  dotnet run --launch-profile https        (uses SQLite + a fake dev user in both AD groups)

To test your real Windows login and AD groups, remove "Auth:DevUser" from appsettings.Development.json.
"@
if ($restartNeeded) { Write-Warning "Windows needs a restart to finish enabling IIS features." }
